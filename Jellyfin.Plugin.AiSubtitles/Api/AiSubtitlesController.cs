using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.Configuration;
using Jellyfin.Plugin.AiSubtitles.Providers;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Jellyfin.Plugin.AiSubtitles.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Subtitles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Api;

/// <summary>
/// HTTP API used by the player script and the settings page.
/// </summary>
[ApiController]
[Route("AiSubtitles")]
public class AiSubtitlesController : ControllerBase
{
    private readonly SubtitleSourceService _sourceService;
    private readonly TranslationService _translationService;
    private readonly TranslationCache _cache;
    private readonly ISubtitleManager _subtitleManager;
    private readonly ILogger<AiSubtitlesController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiSubtitlesController"/> class.
    /// </summary>
    /// <param name="sourceService">Subtitle source service.</param>
    /// <param name="translationService">Translation service.</param>
    /// <param name="cache">Translation cache.</param>
    /// <param name="subtitleManager">Subtitle manager.</param>
    /// <param name="logger">Logger.</param>
    public AiSubtitlesController(
        SubtitleSourceService sourceService,
        TranslationService translationService,
        TranslationCache cache,
        ISubtitleManager subtitleManager,
        ILogger<AiSubtitlesController> logger)
    {
        _sourceService = sourceService;
        _translationService = translationService;
        _cache = cache;
        _subtitleManager = subtitleManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.CurrentConfiguration;

    private bool IsAdmin => User.IsInRole("Administrator");

    /// <summary>
    /// Lists translatable (text) subtitle streams.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <returns>Streams.</returns>
    [HttpGet("Streams/{itemId}")]
    [Authorize]
    public ActionResult<StreamsDto> GetStreams([FromRoute] Guid itemId, [FromQuery] string? mediaSourceId)
    {
        return Guarded(() =>
        {
            var item = _sourceService.GetItem(itemId);
            var source = _sourceService.GetMediaSource(item, mediaSourceId);
            var streams = SubtitleSourceService.GetTextStreams(source)
                .Select(s => new StreamDto(s.Index, s.Language, s.Title, s.Codec, s.IsExternal, s.IsForced, s.IsDefault))
                .ToList();
            return new StreamsDto(source.Id, streams);
        });
    }

    /// <summary>
    /// Translates the whole stream and returns it as a file (SRT or VTT).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="streamIndex">Subtitle stream index.</param>
    /// <param name="lang">Target language.</param>
    /// <param name="format">srt or vtt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Subtitle file.</returns>
    [HttpGet("File/{itemId}")]
    [Authorize]
    public async Task<ActionResult> GetFile(
        [FromRoute] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] int streamIndex,
        [FromQuery] string? lang,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        try
        {
            var (item, _, cues) = await LoadCuesAsync(itemId, mediaSourceId, streamIndex, cancellationToken).ConfigureAwait(false);
            var language = _translationService.ResolveLanguage(lang);
            var translated = await _translationService.TranslateAllAsync(cues, language, cancellationToken).ConfigureAwait(false);
            var vtt = string.Equals(format, "vtt", StringComparison.OrdinalIgnoreCase);
            var text = vtt ? SubtitleFormat.ToVtt(translated) : SubtitleFormat.ToSrt(translated);
            var fileName = $"{SafeFileName(item.Name)}.{language.Key}.ai.{(vtt ? "vtt" : "srt")}";
            return File(Encoding.UTF8.GetBytes(text), vtt ? "text/vtt" : "application/x-subrip", fileName);
        }
        catch (Exception ex) when (ToError(ex) is { } error)
        {
            return error;
        }
    }

    /// <summary>
    /// Translates the whole stream and stores it as a regular subtitle of the item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="streamIndex">Subtitle stream index.</param>
    /// <param name="lang">Target language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result.</returns>
    [HttpPost("Save/{itemId}")]
    [Authorize]
    public Task<ActionResult<ResultDto>> Save(
        [FromRoute] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] int streamIndex,
        [FromQuery] string? lang,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin && !Config.AllowNonAdminSave)
        {
            return Task.FromResult<ActionResult<ResultDto>>(StatusCode(StatusCodes.Status403Forbidden, new ErrorDto("Nur Administratoren dürfen Übersetzungen speichern.")));
        }

        return GuardedAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var (item, _, cues) = await LoadCuesAsync(itemId, mediaSourceId, streamIndex, cancellationToken).ConfigureAwait(false);
            if (item is not Video video)
            {
                throw new ArgumentException("Nur Videos können Untertitel erhalten.");
            }

            var language = _translationService.ResolveLanguage(lang);
            var translated = await _translationService.TranslateAllAsync(cues, language, cancellationToken).ConfigureAwait(false);

            await _subtitleManager.UploadSubtitle(video, new SubtitleResponse
            {
                Format = "srt",
                Language = language.Iso3,
                IsForced = false,
                Stream = new MemoryStream(Encoding.UTF8.GetBytes(SubtitleFormat.ToSrt(translated)))
            }).ConfigureAwait(false);

            _logger.LogInformation("KI-Untertitel ({Lang}) für {Name} gespeichert", language.Key, item.Name);
            return new ResultDto(true, $"Untertitel „{language.DisplayName}“ gespeichert ({translated.Count} Zeilen).", null, sw.ElapsedMilliseconds);
        });
    }

    /// <summary>
    /// Sends a short sample to the configured backend.
    /// </summary>
    /// <param name="lang">Target language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result.</returns>
    [HttpPost("Test")]
    [Authorize]
    public async Task<ActionResult<ResultDto>> Test([FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorDto("Nur für Administratoren."));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var lines = await _translationService.TestAsync(_translationService.ResolveLanguage(lang), cancellationToken).ConfigureAwait(false);
            return new ResultDto(true, "Verbindung funktioniert.", lines, sw.ElapsedMilliseconds);
        }
        catch (TranslatorException ex)
        {
            return new ResultDto(false, ex.Message, null, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Cache statistics.
    /// </summary>
    /// <returns>Statistics.</returns>
    [HttpGet("CacheInfo")]
    [Authorize]
    public ActionResult<CacheInfoDto> GetCacheInfo()
    {
        if (!IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorDto("Nur für Administratoren."));
        }

        var (files, bytes) = _cache.GetStats();
        return new CacheInfoDto(files, bytes);
    }

    /// <summary>
    /// Deletes all cached translations.
    /// </summary>
    /// <returns>Result.</returns>
    [HttpPost("ClearCache")]
    [Authorize]
    public ActionResult<ResultDto> ClearCache()
    {
        if (!IsAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorDto("Nur für Administratoren."));
        }

        _cache.Clear();
        return new ResultDto(true, "Cache geleert.");
    }

    private async Task<(BaseItem Item, string SourceId, List<SubtitleCue> Cues)> LoadCuesAsync(
        Guid itemId,
        string? mediaSourceId,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var item = _sourceService.GetItem(itemId);
        var source = _sourceService.GetMediaSource(item, mediaSourceId);
        if (!SubtitleSourceService.GetTextStreams(source).Any(s => s.Index == streamIndex))
        {
            throw new ArgumentException($"Stream {streamIndex} ist kein Text-Untertitel (Bild-Untertitel wie PGS/VobSub lassen sich nicht übersetzen).");
        }

        var cues = await _sourceService.GetCuesAsync(item, source.Id, streamIndex, cancellationToken).ConfigureAwait(false);
        if (cues.Count == 0)
        {
            throw new ArgumentException("Der Untertitel enthält keine lesbaren Zeilen.");
        }

        return (item, source.Id, cues);
    }

    private ActionResult<T> Guarded<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ToError(ex) is { } error)
        {
            return error;
        }
    }

    private async Task<ActionResult<T>> GuardedAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ToError(ex) is { } error)
        {
            return error;
        }
    }

    private ObjectResult? ToError(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return null; // client went away; let ASP.NET handle it
            case FileNotFoundException:
                return NotFound(new ErrorDto(ex.Message));
            case ArgumentException:
                return BadRequest(new ErrorDto(ex.Message));
            case TranslatorException:
                _logger.LogWarning("KI-Übersetzung fehlgeschlagen: {Message}", ex.Message);
                return StatusCode(StatusCodes.Status502BadGateway, new ErrorDto(ex.Message));
            default:
                _logger.LogError(ex, "Fehler in AiSubtitles-API");
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorDto(ex.Message));
        }
    }

    private static string SafeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "subtitle").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length == 0 ? "subtitle" : clean;
    }
}
