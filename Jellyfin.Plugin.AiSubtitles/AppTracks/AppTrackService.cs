using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiSubtitles.Configuration;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Jellyfin.Plugin.AiSubtitles.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.AppTracks;

/// <summary>
/// A running (or finished) translation of a whole track, readable batch by batch in order.
/// </summary>
/// <param name="Batches">Translated cues per batch, in playback order.</param>
public sealed record AppTrackJob(IReadOnlyList<Task<List<SubtitleCue>>> Batches);

/// <summary>
/// Languages, user preferences and background translations for the tracks offered to all apps.
/// </summary>
public class AppTrackService
{
    private readonly SubtitleSourceService _sourceService;
    private readonly TranslationService _translationService;
    private readonly ILocalizationManager _localization;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<AppTrackService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<AppTrackJob>>> _jobs = new();
    private readonly ConcurrentDictionary<string, (string? Key, string? Name)> _languageInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="AppTrackService"/> class.
    /// </summary>
    /// <param name="sourceService">Subtitle source service.</param>
    /// <param name="translationService">Translation service.</param>
    /// <param name="localization">Localization manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="logger">Logger.</param>
    public AppTrackService(
        SubtitleSourceService sourceService,
        TranslationService translationService,
        ILocalizationManager localization,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<AppTrackService> logger)
    {
        _sourceService = sourceService;
        _translationService = translationService;
        _localization = localization;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.CurrentConfiguration;

    /// <summary>
    /// Gets the languages offered as tracks.
    /// </summary>
    /// <returns>Languages in configured order.</returns>
    public IReadOnlyList<AppLanguage> GetLanguages()
    {
        var config = Config;
        return (config.AppTrackLanguages ?? string.Empty)
            .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Take(20)
            .Select((code, position) => new AppLanguage(position, LanguageResolver.Resolve(code, config, _localization)))
            .ToList();
    }

    /// <summary>
    /// Maps a stream language code ("ger", "eng", "de") to the key used for target languages ("de", "en").
    /// </summary>
    /// <param name="code">Language code.</param>
    /// <returns>Key or null.</returns>
    public string? LanguageKey(string? code) => string.IsNullOrWhiteSpace(code) ? null : Lookup(code).Key;

    /// <summary>
    /// Gets a readable name for a stream language code.
    /// </summary>
    /// <param name="code">Language code.</param>
    /// <returns>Name or null.</returns>
    public string? LanguageName(string? code) => string.IsNullOrWhiteSpace(code) ? null : Lookup(code).Name;

    /// <summary>
    /// Gets the key of a user's preferred subtitle language.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <returns>Key or null.</returns>
    public string? GetPreferredKey(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return LanguageKey(_userManager.GetUserById(userId)?.SubtitleLanguagePreference);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Untertitelsprache des Benutzers {User} nicht lesbar", userId);
            return null;
        }
    }

    /// <summary>
    /// Starts translating a track in the background (if not already running).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="sourceIndex">Original stream index.</param>
    /// <param name="language">Target language.</param>
    public void Prepare(Guid itemId, string mediaSourceId, int sourceIndex, AppLanguage language)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var item = _sourceService.GetItem(itemId);
                var job = await GetOrStartAsync(item, mediaSourceId, sourceIndex, language, Config.PrefetchNextEpisode).ConfigureAwait(false);
                await Task.WhenAll(job.Batches).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vorbereitung der Übersetzung von {Item} fehlgeschlagen", itemId);
            }
        });
    }

    /// <summary>
    /// Gets the running translation of a track or starts it. The translation keeps running when the caller goes away.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="sourceIndex">Original stream index.</param>
    /// <param name="language">Target language.</param>
    /// <param name="prefetchNext">Whether the next episode is translated afterwards.</param>
    /// <returns>The job.</returns>
    public Task<AppTrackJob> GetOrStartAsync(BaseItem item, string mediaSourceId, int sourceIndex, AppLanguage language, bool prefetchNext)
    {
        var key = $"{item.Id:N}|{mediaSourceId}|{sourceIndex}|{language.Target.Key}";
        var lazy = _jobs.GetOrAdd(key, k => new Lazy<Task<AppTrackJob>>(() => CreateAsync(k, item, mediaSourceId, sourceIndex, language, prefetchNext)));
        return lazy.Value;
    }

    private async Task<AppTrackJob> CreateAsync(string key, BaseItem item, string mediaSourceId, int sourceIndex, AppLanguage language, bool prefetchNext)
    {
        try
        {
            var cues = await _sourceService.GetCuesAsync(item, mediaSourceId, sourceIndex, CancellationToken.None).ConfigureAwait(false);
            var batches = TranslationService.BuildBatches(cues, TranslationService.GetBatchSize());
            var tasks = batches.Select(b => TranslateSafeAsync(cues, b, language.Target, item.Name)).ToList();
            _logger.LogInformation(
                "Übersetze {Name} (Stream {Index}) nach {Lang} für die Apps: {Count} Abschnitte",
                item.Name,
                sourceIndex,
                language.Target.Key,
                tasks.Count);

            _ = Task.WhenAll(tasks).ContinueWith(
                _ =>
                {
                    _jobs.TryRemove(key, out var _);
                    if (prefetchNext)
                    {
                        PrefetchNextEpisode(item, mediaSourceId, sourceIndex, language);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return new AppTrackJob(tasks);
        }
        catch
        {
            _jobs.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<List<SubtitleCue>> TranslateSafeAsync(List<SubtitleCue> cues, BatchInfo batch, TargetLanguage language, string name)
    {
        try
        {
            return await _translationService.TranslateBatchAsync(cues, batch, language, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Abschnitt {Batch} von {Name} konnte nicht übersetzt werden, Original wird verwendet", batch.Index, name);
            return cues.Skip(batch.FirstCue).Take(batch.Count).ToList();
        }
    }

    private void PrefetchNextEpisode(BaseItem item, string mediaSourceId, int sourceIndex, AppLanguage language)
    {
        try
        {
            if (item is not Episode episode || episode.SeriesId == Guid.Empty)
            {
                return;
            }

            var previousSource = _sourceService.GetMediaSourcesSafe(item).FirstOrDefault(s => s.Id == mediaSourceId);
            var previousLanguage = previousSource?.MediaStreams.FirstOrDefault(s => s.Index == sourceIndex)?.Language;

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = [episode.SeriesId],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsVirtualItem = false
                })
                .OfType<Episode>()
                .OrderBy(e => e.ParentIndexNumber ?? 0)
                .ThenBy(e => e.IndexNumber ?? 0)
                .ToList();

            var position = episodes.FindIndex(e => e.Id == episode.Id);
            if (position < 0 || position + 1 >= episodes.Count)
            {
                return;
            }

            var next = episodes[position + 1];
            var source = _sourceService.GetMediaSourcesSafe(next).FirstOrDefault();
            if (source?.Id is null)
            {
                return;
            }

            var streams = SubtitleSourceService.GetTextStreams(source);
            if (AppTrackInjector.HasNativeTrack(streams, language.Target.Key, LanguageKey))
            {
                return;
            }

            var pick = AppTrackInjector.PickSource(streams, language.Target.Key, LanguageKey, LanguageKey(previousLanguage));
            if (pick is null)
            {
                return;
            }

            _logger.LogInformation("Übersetze die nächste Folge {Name} im Voraus", next.Name);
            _ = GetOrStartAsync(next, source.Id, pick.Index, language, prefetchNext: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nächste Folge nach {Name} konnte nicht vorbereitet werden", item.Name);
        }
    }

    private (string? Key, string? Name) Lookup(string code)
    {
        return _languageInfo.GetOrAdd(code.Trim(), c =>
        {
            try
            {
                var culture = _localization.FindLanguageInfo(c);
                if (culture is not null)
                {
                    return ((culture.TwoLetterISOLanguageName ?? culture.ThreeLetterISOLanguageName ?? c).ToLowerInvariant(), culture.DisplayName);
                }
            }
            catch (ArgumentException)
            {
                // unknown code
            }

            return (c.ToLowerInvariant(), null);
        });
    }
}
