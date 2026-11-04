using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Subtitles;

/// <summary>
/// Makes AI translations appear in Jellyfin's "Search subtitles" dialog (works for every client that offers it).
/// </summary>
public class AiSubtitleProvider : ISubtitleProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleSourceService _sourceService;
    private readonly TranslationService _translationService;
    private readonly ILogger<AiSubtitleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiSubtitleProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sourceService">Subtitle source service.</param>
    /// <param name="translationService">Translation service.</param>
    /// <param name="logger">Logger.</param>
    public AiSubtitleProvider(
        ILibraryManager libraryManager,
        SubtitleSourceService sourceService,
        TranslationService translationService,
        ILogger<AiSubtitleProvider> logger)
    {
        _libraryManager = libraryManager;
        _sourceService = sourceService;
        _translationService = translationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Plugin.PluginName;

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        IEnumerable<RemoteSubtitleInfo> empty = [];
        var config = Plugin.Instance?.Configuration;

        // Never run on the scheduled "download missing subtitles" task: that would translate whole libraries.
        if (config is null || !config.EnableSubtitleProvider || request.IsAutomated || string.IsNullOrEmpty(request.MediaPath))
        {
            return Task.FromResult(empty);
        }

        var item = _libraryManager.FindByPath(request.MediaPath, false);
        if (item is null)
        {
            return Task.FromResult(empty);
        }

        var language = _translationService.ResolveLanguage(request.Language);
        var results = new List<RemoteSubtitleInfo>();

        foreach (var source in _sourceService.GetMediaSourcesSafe(item))
        {
            foreach (var stream in SubtitleSourceService.GetTextStreams(source))
            {
                if (string.Equals(stream.Language, language.Iso3, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // already in the target language
                }

                var id = string.Join(
                    '~',
                    item.Id.ToString("N", CultureInfo.InvariantCulture),
                    source.Id,
                    stream.Index.ToString(CultureInfo.InvariantCulture),
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(language.Key)).Replace('/', '_').Replace('+', '-'));

                results.Add(new RemoteSubtitleInfo
                {
                    Id = id,
                    ProviderName = Name,
                    Name = $"KI-Übersetzung {stream.Title} → {language.DisplayName}",
                    Format = "srt",
                    ThreeLetterISOLanguageName = language.Iso3,
                    Comment = "Wird beim Herunterladen per KI übersetzt; das kann bei Spielfilmen ein bis zwei Minuten dauern.",
                    Forced = stream.IsForced
                });
            }
        }

        return Task.FromResult<IEnumerable<RemoteSubtitleInfo>>(results);
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var parts = id.Split('~');
        if (parts.Length != 4 || !Guid.TryParse(parts[0], out var itemId) || !int.TryParse(parts[2], out var streamIndex))
        {
            throw new ArgumentException("Ungültige Untertitel-ID: " + id, nameof(id));
        }

        var langKey = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3].Replace('_', '/').Replace('-', '+')));
        var language = _translationService.ResolveLanguage(langKey);
        var item = _sourceService.GetItem(itemId);
        var cues = await _sourceService.GetCuesAsync(item, parts[1], streamIndex, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Übersetze {Count} Zeilen von {Name} nach {Lang} (Untertitelsuche)", cues.Count, item.Name, language.Key);
        var translated = await _translationService.TranslateAllAsync(cues, language, cancellationToken).ConfigureAwait(false);

        return new SubtitleResponse
        {
            Format = "srt",
            Language = language.Iso3,
            IsForced = false,
            Stream = new MemoryStream(Encoding.UTF8.GetBytes(SubtitleFormat.ToSrt(translated)))
        };
    }
}
