using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.AppTracks;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Api;

/// <summary>
/// Delivers the translated tracks that are offered to all apps.
/// Requests for <c>/Videos/{id}/{source}/Subtitles/{virtual index}/…/Stream.srt</c> are routed here by <see cref="AppTrackMiddleware"/>.
/// </summary>
[ApiController]
[Route("AiSubtitles/AppTrack")]
public class AppTrackController : ControllerBase
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(4);

    private readonly AppTrackService _service;
    private readonly SubtitleSourceService _sourceService;
    private readonly ILogger<AppTrackController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppTrackController"/> class.
    /// </summary>
    /// <param name="service">Track service.</param>
    /// <param name="sourceService">Subtitle source service.</param>
    /// <param name="logger">Logger.</param>
    public AppTrackController(AppTrackService service, SubtitleSourceService sourceService, ILogger<AppTrackController> logger)
    {
        _service = service;
        _sourceService = sourceService;
        _logger = logger;
    }

    /// <summary>
    /// Streams a translated track. Batches are written in order as soon as they are ready; while waiting, blank
    /// lines keep the connection alive so players with short read timeouts do not give up.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="index">Virtual stream index.</param>
    /// <param name="format">srt or vtt.</param>
    /// <returns>Task.</returns>
    [HttpGet("{itemId}/{mediaSourceId}/{index}/Stream.{format}")]
    [Authorize]
    public async Task GetTrack([FromRoute] Guid itemId, [FromRoute] string mediaSourceId, [FromRoute] int index, [FromRoute] string format)
    {
        if (!Plugin.CurrentConfiguration.EnableAppTracks || !AppTrackInjector.IsVirtual(index))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (position, sourceIndex) = AppTrackInjector.Decode(index);
        var language = _service.GetLanguages().FirstOrDefault(l => l.Position == position);
        if (language is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        AppTrackJob job;
        try
        {
            var item = _sourceService.GetItem(itemId);
            job = await _service.GetOrStartAsync(item, mediaSourceId, sourceIndex, language, Plugin.CurrentConfiguration.PrefetchNextEpisode).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Übersetzte Spur für {Item} konnte nicht erstellt werden", itemId);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var vtt = format.Contains("vtt", StringComparison.OrdinalIgnoreCase);
        var ct = HttpContext.RequestAborted;
        Response.ContentType = vtt ? "text/vtt; charset=utf-8" : "application/x-subrip; charset=utf-8";
        Response.Headers.CacheControl = "no-store";

        try
        {
            await Response.StartAsync(ct).ConfigureAwait(false);
            if (vtt)
            {
                await WriteAsync("WEBVTT\n\n", ct).ConfigureAwait(false);
            }

            var number = 1;
            foreach (var batch in job.Batches)
            {
                while (!batch.IsCompleted)
                {
                    var finished = await Task.WhenAny(batch, Task.Delay(KeepAliveInterval, ct)).ConfigureAwait(false);
                    if (finished != batch)
                    {
                        ct.ThrowIfCancellationRequested();
                        await WriteAsync("\n", ct).ConfigureAwait(false);
                    }
                }

                var sb = new StringBuilder();
                foreach (var cue in await batch.ConfigureAwait(false))
                {
                    sb.Append(SubtitleFormat.FormatCue(cue, number++, vtt));
                }

                await WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Player closed the connection; the translation keeps running in the background.
        }
        catch (IOException)
        {
            // Connection lost.
        }
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        await Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
