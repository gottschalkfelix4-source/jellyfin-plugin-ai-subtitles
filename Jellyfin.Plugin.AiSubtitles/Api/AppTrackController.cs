using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.AppTracks;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Jellyfin.Plugin.AiSubtitles.Translation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSubtitles.Api;

/// <summary>
/// Delivers the translated tracks that are offered to all apps.
/// Requests for <c>/Videos/{id}/{source}/Subtitles/{virtual index}/…</c> are routed here by <see cref="AppTrackMiddleware"/>.
/// </summary>
[ApiController]
[Route("AiSubtitles/AppTrack")]
public class AppTrackController : ControllerBase
{
    private const int LookAheadBatches = 3;

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

    private enum OutputFormat
    {
        Srt,
        Vtt,
        Json
    }

    /// <summary>
    /// Streams a translated track: the whole track (srt, vtt, or "js" = Jellyfin's JSON format used by the web
    /// player), or with start and end ticks one HLS segment. Whole tracks are written batch by batch as they are
    /// ready; while waiting, whitespace keeps the connection alive.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="index">Virtual stream index.</param>
    /// <param name="format">srt, vtt or js.</param>
    /// <param name="startPositionTicks">Segment start (HLS).</param>
    /// <param name="endPositionTicks">Segment end (HLS).</param>
    /// <param name="addVttTimeMap">Whether to write X-TIMESTAMP-MAP (HLS).</param>
    /// <param name="vttTimestampMapMpegts">MPEG-TS value for X-TIMESTAMP-MAP.</param>
    /// <returns>Task.</returns>
    [HttpGet("{itemId}/{mediaSourceId}/{index}/Stream.{format}")]
    [Authorize]
    public async Task GetTrack(
        [FromRoute] Guid itemId,
        [FromRoute] string mediaSourceId,
        [FromRoute] int index,
        [FromRoute] string format,
        [FromQuery] long? startPositionTicks,
        [FromQuery] long? endPositionTicks,
        [FromQuery] bool addVttTimeMap = false,
        [FromQuery] long? vttTimestampMapMpegts = null)
    {
        var resolved = Resolve(itemId, mediaSourceId, index);
        if (resolved is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (item, language, sourceIndex) = resolved.Value;
        var output = format.ToLowerInvariant() switch
        {
            "js" or "json" => OutputFormat.Json,
            "vtt" or "webvtt" => OutputFormat.Vtt,
            _ => OutputFormat.Srt
        };
        var ct = HttpContext.RequestAborted;

        try
        {
            if (endPositionTicks is long end && end > 0)
            {
                await WriteSegmentAsync(item, mediaSourceId, sourceIndex, language, startPositionTicks ?? 0, end, addVttTimeMap ? vttTimestampMapMpegts ?? AppTrackHls.DefaultTimestampMapMpegts : null, ct).ConfigureAwait(false);
                return;
            }

            AppTrackJob job;
            try
            {
                job = await _service.GetOrStartAsync(item, mediaSourceId, sourceIndex, language, Plugin.CurrentConfiguration.PrefetchNextEpisode).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Übersetzte Spur für {Item} konnte nicht erstellt werden", itemId);
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            await WriteWholeTrackAsync(job, output, ct).ConfigureAwait(false);
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

    /// <summary>
    /// HLS subtitle playlist of a translated track (one WebVTT segment per window), as used by Apple's player.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">Media source id.</param>
    /// <param name="index">Virtual stream index.</param>
    /// <param name="segmentLength">Segment length in seconds.</param>
    /// <param name="vttTimestampMapMpegts">MPEG-TS value for X-TIMESTAMP-MAP.</param>
    /// <returns>Playlist.</returns>
    [HttpGet("{itemId}/{mediaSourceId}/{index}/subtitles.m3u8")]
    [Authorize]
    public ActionResult GetPlaylist(
        [FromRoute] Guid itemId,
        [FromRoute] string mediaSourceId,
        [FromRoute] int index,
        [FromQuery] int segmentLength = 30,
        [FromQuery] long? vttTimestampMapMpegts = null)
    {
        var resolved = Resolve(itemId, mediaSourceId, index);
        if (resolved is null)
        {
            return NotFound();
        }

        var item = resolved.Value.Item;
        long runtime;
        try
        {
            runtime = _sourceService.GetMediaSource(item, mediaSourceId).RunTimeTicks ?? item.RunTimeTicks ?? 0;
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }

        if (runtime <= 0)
        {
            return NotFound();
        }

        var playlist = AppTrackHls.BuildSubtitlePlaylist(runtime, segmentLength, GetAccessToken(), vttTimestampMapMpegts ?? AppTrackHls.DefaultTimestampMapMpegts);
        return Content(playlist, "application/x-mpegURL; charset=utf-8");
    }

    private (MediaBrowser.Controller.Entities.BaseItem Item, AppLanguage Language, int SourceIndex)? Resolve(Guid itemId, string mediaSourceId, int index)
    {
        if (!Plugin.CurrentConfiguration.EnableAppTracks || _service.Resolve(itemId, mediaSourceId, index) is not { } found)
        {
            return null;
        }

        return (found.Layout.Item, found.Track.Language, found.Track.SourceIndex);
    }

    private async Task WriteWholeTrackAsync(AppTrackJob job, OutputFormat output, CancellationToken ct)
    {
        Response.ContentType = output switch
        {
            OutputFormat.Json => "application/json; charset=utf-8",
            OutputFormat.Vtt => "text/vtt; charset=utf-8",
            _ => "application/x-subrip; charset=utf-8"
        };
        Response.Headers.CacheControl = "no-store";
        await Response.StartAsync(ct).ConfigureAwait(false);

        // Whitespace is allowed between JSON tokens and before SRT/VTT cues, so it serves as keep-alive.
        await WriteAsync(output switch { OutputFormat.Json => "{\"TrackEvents\":[", OutputFormat.Vtt => "WEBVTT\n\n", _ => string.Empty }, ct).ConfigureAwait(false);

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
                if (output == OutputFormat.Json)
                {
                    if (number > 1)
                    {
                        sb.Append(',');
                    }

                    sb.Append(JsonCue(cue, number++));
                }
                else
                {
                    sb.Append(SubtitleFormat.FormatCue(cue, number++, output == OutputFormat.Vtt));
                }
            }

            await WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
        }

        if (output == OutputFormat.Json)
        {
            await WriteAsync("]}", ct).ConfigureAwait(false);
        }
    }

    private async Task WriteSegmentAsync(
        MediaBrowser.Controller.Entities.BaseItem item,
        string mediaSourceId,
        int sourceIndex,
        AppLanguage language,
        long startTicks,
        long endTicks,
        long? timestampMapMpegts,
        CancellationToken ct)
    {
        var cues = await _sourceService.GetCuesAsync(item, mediaSourceId, sourceIndex, ct).ConfigureAwait(false);
        var batches = TranslationService.BuildBatches(cues, TranslationService.GetBatchSize());
        var startMs = startTicks / TimeSpan.TicksPerMillisecond;
        var endMs = endTicks / TimeSpan.TicksPerMillisecond;

        var hit = batches.Where(b => b.StartMs < endMs && b.EndMs > startMs).ToList();
        var tasks = hit.Select(b => _service.TranslateSafeAsync(cues, b, language.Target, item.Name)).ToList();

        // Prepare what comes next so later segments are ready when the player asks for them.
        var next = hit.Count > 0 ? hit[^1].Index + 1 : batches.FindIndex(b => b.StartMs >= endMs);
        if (next >= 0)
        {
            foreach (var b in batches.Skip(next).Take(LookAheadBatches))
            {
                _ = _service.TranslateSafeAsync(cues, b, language.Target, item.Name);
            }
        }

        Response.ContentType = "text/vtt; charset=utf-8";
        await Response.StartAsync(ct).ConfigureAwait(false);
        await WriteAsync(AppTrackHls.VttHeader(timestampMapMpegts), ct).ConfigureAwait(false);

        var all = Task.WhenAll(tasks);
        while (!all.IsCompleted)
        {
            var finished = await Task.WhenAny(all, Task.Delay(KeepAliveInterval, ct)).ConfigureAwait(false);
            if (finished != all)
            {
                ct.ThrowIfCancellationRequested();
                await WriteAsync("\n", ct).ConfigureAwait(false);
            }
        }

        var sb = new StringBuilder();
        foreach (var cue in (await all.ConfigureAwait(false)).SelectMany(c => c).Where(c => c.EndMs > startMs && c.StartMs < endMs))
        {
            sb.Append(SubtitleFormat.FormatCue(cue, 0, vtt: true));
        }

        await WriteAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private static string JsonCue(SubtitleCue cue, int id)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Text"] = cue.Text,
            ["StartPositionTicks"] = cue.StartMs * TimeSpan.TicksPerMillisecond,
            ["EndPositionTicks"] = cue.EndMs * TimeSpan.TicksPerMillisecond
        });

    private string? GetAccessToken()
    {
        foreach (var name in new[] { "ApiKey", "api_key" })
        {
            var value = Request.Query[name].FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return Request.Headers["X-Emby-Token"].FirstOrDefault();
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        if (text.Length == 0)
        {
            return;
        }

        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        await Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
