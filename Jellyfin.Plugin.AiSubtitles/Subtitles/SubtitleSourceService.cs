using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Subtitles;

/// <summary>
/// Describes a text subtitle stream that can be translated.
/// </summary>
/// <param name="Index">Stream index.</param>
/// <param name="Language">Language code (may be null).</param>
/// <param name="Title">Display title.</param>
/// <param name="Codec">Codec.</param>
/// <param name="IsExternal">Whether it is a sidecar file.</param>
/// <param name="IsForced">Whether it is a forced track.</param>
/// <param name="IsDefault">Whether it is the default track.</param>
public sealed record SourceStream(int Index, string? Language, string Title, string? Codec, bool IsExternal, bool IsForced, bool IsDefault);

/// <summary>
/// Loads the original subtitles of an item through Jellyfin's own subtitle pipeline.
/// </summary>
public class SubtitleSourceService
{
    private const int MaxCachedTracks = 24;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ILogger<SubtitleSourceService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Used, Task<List<SubtitleCue>> Cues)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSourceService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="subtitleEncoder">Subtitle encoder.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleSourceService(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ISubtitleEncoder subtitleEncoder,
        ILogger<SubtitleSourceService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _subtitleEncoder = subtitleEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets an item or throws.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The item.</returns>
    public BaseItem GetItem(Guid itemId)
        => _libraryManager.GetItemById(itemId) ?? throw new FileNotFoundException("Element nicht gefunden: " + itemId);

    /// <summary>
    /// Resolves the media source (falls back to the first one).
    /// </summary>
    /// <param name="item">Item.</param>
    /// <param name="mediaSourceId">Requested media source id.</param>
    /// <returns>The media source.</returns>
    public MediaSourceInfo GetMediaSource(BaseItem item, string? mediaSourceId)
    {
        var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
        var source = sources.FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault();
        return source ?? throw new FileNotFoundException("Keine Medienquelle für " + item.Name);
    }

    /// <summary>
    /// Gets all media sources of an item, or an empty list on error.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <returns>Media sources.</returns>
    public IReadOnlyList<MediaSourceInfo> GetMediaSourcesSafe(BaseItem item)
    {
        try
        {
            return _mediaSourceManager.GetStaticMediaSources(item, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Medienquellen von {Name} konnten nicht gelesen werden", item.Name);
            return [];
        }
    }

    /// <summary>
    /// Lists the text-based subtitle streams of a media source.
    /// </summary>
    /// <param name="source">Media source.</param>
    /// <returns>Streams.</returns>
    public static List<SourceStream> GetTextStreams(MediaSourceInfo source)
    {
        return source.MediaStreams
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
            .Select(s => new SourceStream(
                s.Index,
                s.Language,
                s.DisplayTitle ?? s.Title ?? s.Language ?? ("#" + s.Index),
                s.Codec,
                s.IsExternal,
                s.IsForced,
                s.IsDefault))
            .ToList();
    }

    /// <summary>
    /// Loads and parses the cues of a subtitle stream (cached in memory).
    /// </summary>
    /// <param name="item">Item.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="streamIndex">Subtitle stream index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cues.</returns>
    public Task<List<SubtitleCue>> GetCuesAsync(BaseItem item, string mediaSourceId, int streamIndex, CancellationToken cancellationToken)
    {
        var key = $"{item.Id:N}|{mediaSourceId}|{streamIndex}|{item.DateModified.Ticks}";

        if (_cache.TryGetValue(key, out var entry) && !entry.Cues.IsFaulted && !entry.Cues.IsCanceled)
        {
            _cache[key] = (DateTime.UtcNow, entry.Cues);
            return entry.Cues.WaitAsync(cancellationToken);
        }

        var task = LoadAsync(item, mediaSourceId, streamIndex);
        _cache[key] = (DateTime.UtcNow, task);
        Trim();
        return task.WaitAsync(cancellationToken);
    }

    private async Task<List<SubtitleCue>> LoadAsync(BaseItem item, string mediaSourceId, int streamIndex)
    {
        // Not bound to the caller's token: the result is shared between requests.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _logger.LogInformation("Lade Untertitel-Stream {Index} von {Name}", streamIndex, item.Name);
        await using var stream = await _subtitleEncoder
            .GetSubtitles(item, mediaSourceId, streamIndex, "srt", 0, 0, true, cts.Token)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        var cues = SubtitleFormat.Parse(content);
        _logger.LogInformation("{Count} Untertitelzeilen aus Stream {Index} von {Name} gelesen", cues.Count, streamIndex, item.Name);
        return cues;
    }

    private void Trim()
    {
        if (_cache.Count <= MaxCachedTracks)
        {
            return;
        }

        foreach (var old in _cache.OrderBy(e => e.Value.Used).Take(_cache.Count - MaxCachedTracks).ToList())
        {
            _cache.TryRemove(old.Key, out _);
        }
    }
}
