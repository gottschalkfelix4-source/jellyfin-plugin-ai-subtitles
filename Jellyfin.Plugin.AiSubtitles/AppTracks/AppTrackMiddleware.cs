using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.AiSubtitles.AppTracks;

/// <summary>
/// Offers the translated tracks to every client (TV, mobile and desktop apps) by extending Jellyfin's
/// PlaybackInfo and item responses and by answering subtitle requests for the virtual stream indexes.
/// Any failure leaves the original response untouched.
/// </summary>
public partial class AppTrackMiddleware
{
    private static readonly JsonNodeOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private readonly RequestDelegate _next;
    private readonly AppTrackService _service;
    private readonly ILogger<AppTrackMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppTrackMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next delegate.</param>
    /// <param name="service">Track service.</param>
    /// <param name="logger">Logger.</param>
    public AppTrackMiddleware(RequestDelegate next, AppTrackService service, ILogger<AppTrackMiddleware> logger)
    {
        _next = next;
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>Task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var config = Plugin.CurrentConfiguration;
        if (!config.EnableAppTracks)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var request = context.Request;
        var path = request.Path.Value ?? string.Empty;

        // 1. Subtitle file of a translated track -> plugin endpoint.
        var subtitle = SubtitlePathRegex().Match(path);
        if (subtitle.Success && int.TryParse(subtitle.Groups["idx"].Value, out var subtitleIndex) && AppTrackInjector.IsVirtual(subtitleIndex))
        {
            request.Path = new PathString(subtitle.Groups["prefix"].Value
                + $"/AiSubtitles/AppTrack/{subtitle.Groups["item"].Value}/{subtitle.Groups["ms"].Value}/{subtitleIndex}/Stream.{subtitle.Groups["fmt"].Value}");
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 2. Jellyfin itself must never see a virtual index (it would try to burn in a stream that does not exist).
        var requested = StripVirtualIndexFromQuery(request);

        if (!config.AppTracksInWebClient && IsBrowserClient(request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var playbackInfo = PlaybackInfoRegex().Match(path);
        if (playbackInfo.Success && (HttpMethods.IsPost(request.Method) || HttpMethods.IsGet(request.Method)))
        {
            await HandlePlaybackInfoAsync(context, playbackInfo.Groups["item"].Value, requested).ConfigureAwait(false);
            return;
        }

        var details = ItemRegex().Match(path);
        if (details.Success && HttpMethods.IsGet(request.Method))
        {
            var itemId = details.Groups["item"].Value;
            await RewriteJsonAsync(context, root =>
            {
                var added = AppTrackInjector.Inject(root, BuildOptions(itemId, null, null, null, null, false), out _);
                return added.Count > 0;
            }).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private async Task HandlePlaybackInfoAsync(HttpContext context, string itemId, int? requested)
    {
        var request = context.Request;
        JsonObject? body = null;

        if (HttpMethods.IsPost(request.Method) && (request.ContentType ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                request.EnableBuffering();
                using var reader = new StreamReader(request.Body, Encoding.UTF8, false, 4096, leaveOpen: true);
                var text = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
                request.Body.Position = 0;
                body = JsonNode.Parse(text, CaseInsensitive) as JsonObject;

                if (body is not null && AsInt(body["SubtitleStreamIndex"]) is int bodyIndex)
                {
                    requested = bodyIndex;
                    if (AppTrackInjector.IsVirtual(bodyIndex))
                    {
                        body["SubtitleStreamIndex"] = -1;
                        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                        request.Body = new MemoryStream(bytes);
                        request.ContentLength = bytes.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "PlaybackInfo-Anfrage nicht lesbar");
                request.Body.Position = 0;
                body = null;
            }
        }

        var format = ChooseFormat(body);
        if (format is null)
        {
            // This client cannot load external text subtitles.
            await _next(context).ConfigureAwait(false);
            return;
        }

        var userId = AsString(body?["UserId"]) ?? request.Query["userId"].FirstOrDefault() ?? request.Query["UserId"].FirstOrDefault();
        var config = Plugin.CurrentConfiguration;
        string? preferredKey = null;
        if (config.AutoSelectAppTrack && Guid.TryParse(userId, out var userGuid))
        {
            preferredKey = _service.GetPreferredKey(userGuid);
        }

        InjectedTrack? selected = null;
        await RewriteJsonAsync(context, root =>
        {
            var options = BuildOptions(itemId, format, GetAccessToken(request), requested, preferredKey, config.AutoSelectAppTrack);
            var added = AppTrackInjector.Inject(root, options, out selected);
            return added.Count > 0;
        }).ConfigureAwait(false);

        if (selected is not null && Guid.TryParse(itemId, out var itemGuid))
        {
            // The client will ask for the file right away; start translating now.
            _service.Prepare(itemGuid, selected.MediaSourceId, selected.SourceIndex, selected.Language);
        }
    }

    private InjectOptions BuildOptions(string itemId, string? format, string? apiKey, int? requested, string? preferredKey, bool autoSelect)
        => new()
        {
            ItemId = itemId,
            Languages = _service.GetLanguages(),
            LanguageKey = _service.LanguageKey,
            LanguageName = _service.LanguageName,
            Format = format,
            ApiKey = apiKey,
            RequestedIndex = requested,
            PreferredKey = preferredKey,
            AutoSelect = autoSelect
        };

    private async Task RewriteJsonAsync(HttpContext context, Func<JsonNode, bool> mutate)
    {
        // Ask for an uncompressed, complete response so it can be rewritten.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = buffer.ToArray();
        if (context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase)
            && !context.Response.Headers.ContainsKey("Content-Encoding")
            && bytes.Length > 0)
        {
            try
            {
                var root = JsonNode.Parse(bytes);
                if (root is not null && mutate(root))
                {
                    bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Übersetzte Spuren konnten nicht eingetragen werden ({Path})", context.Request.Path);
            }
        }

        if (!context.Response.HasStarted)
        {
            context.Response.ContentLength = bytes.Length;
        }

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks srt or vtt from the client's device profile; null if it cannot load external text subtitles.
    /// </summary>
    /// <param name="body">PlaybackInfo request body.</param>
    /// <returns>Format or null.</returns>
    internal static string? ChooseFormat(JsonObject? body)
    {
        if (body?["DeviceProfile"] is not JsonObject profile)
        {
            return "srt";
        }

        if (profile["SubtitleProfiles"] is not JsonArray profiles)
        {
            return null;
        }

        foreach (var entry in profiles.OfType<JsonObject>())
        {
            var method = entry["Method"]?.ToString();
            if (!string.Equals(method, "External", StringComparison.OrdinalIgnoreCase) && method != "2")
            {
                continue;
            }

            switch (entry["Format"]?.ToString().ToLowerInvariant())
            {
                case "srt":
                case "subrip":
                    return "srt";
                case "vtt":
                case "webvtt":
                    return "vtt";
            }
        }

        return null;
    }

    private static int? StripVirtualIndexFromQuery(HttpRequest request)
    {
        if (!request.QueryString.HasValue)
        {
            return null;
        }

        int? requested = null;
        var changed = false;
        var builder = new QueryBuilder();
        foreach (var (key, values) in request.Query)
        {
            if (key.Equals("SubtitleStreamIndex", StringComparison.OrdinalIgnoreCase) && int.TryParse(values.FirstOrDefault(), out var index))
            {
                requested = index;
                if (AppTrackInjector.IsVirtual(index))
                {
                    builder.Add(key, "-1");
                    changed = true;
                    continue;
                }
            }

            foreach (var value in values)
            {
                builder.Add(key, value ?? string.Empty);
            }
        }

        if (changed)
        {
            // The method belonged to the virtual track; without it Jellyfin plays without subtitles.
            var rebuilt = new QueryBuilder();
            foreach (var pair in builder.Where(p => !p.Key.Equals("SubtitleMethod", StringComparison.OrdinalIgnoreCase)))
            {
                rebuilt.Add(pair.Key, pair.Value);
            }

            request.QueryString = rebuilt.ToQueryString();
        }

        return requested;
    }

    private static bool IsBrowserClient(HttpRequest request)
    {
        var client = AuthValue(request, "Client");
        return string.Equals(client, "Jellyfin Web", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetAccessToken(HttpRequest request)
    {
        foreach (var name in new[] { "ApiKey", "api_key" })
        {
            var value = request.Query[name].FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        foreach (var name in new[] { "X-Emby-Token", "X-MediaBrowser-Token" })
        {
            if (request.Headers.TryGetValue(name, out var value) && !StringValues.IsNullOrEmpty(value))
            {
                return value.ToString();
            }
        }

        return AuthValue(request, "Token");
    }

    private static string? AuthValue(HttpRequest request, string field)
    {
        foreach (var header in new[] { "Authorization", "X-Emby-Authorization" })
        {
            if (!request.Headers.TryGetValue(header, out var value))
            {
                continue;
            }

            var match = Regex.Match(value.ToString(), field + "=\"([^\"]*)\"", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return Uri.UnescapeDataString(match.Groups[1].Value);
            }
        }

        return null;
    }

    private static string? AsString(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static int? AsInt(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        return v.GetValueKind() switch
        {
            JsonValueKind.Number when v.TryGetValue<int>(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetValue<string>(), out var s) => s,
            _ => null
        };
    }

    [GeneratedRegex(@"^(?<prefix>.*?)/Videos/(?<item>[0-9a-fA-F-]{32,36})/(?<ms>[^/]+)/Subtitles/(?<idx>\d+)(?:/\d+)?/Stream\.(?<fmt>[A-Za-z0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubtitlePathRegex();

    [GeneratedRegex(@"^(?<prefix>.*?)/Items/(?<item>[0-9a-fA-F-]{32,36})/PlaybackInfo$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaybackInfoRegex();

    [GeneratedRegex(@"^(?<prefix>.*?)(?:/Users/[0-9a-fA-F-]{32,36})?/Items/(?<item>[0-9a-fA-F-]{32,36})$", RegexOptions.IgnoreCase)]
    private static partial Regex ItemRegex();
}
