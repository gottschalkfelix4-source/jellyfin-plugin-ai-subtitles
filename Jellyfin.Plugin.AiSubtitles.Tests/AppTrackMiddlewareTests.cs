using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.AppTracks;
using Jellyfin.Plugin.AiSubtitles.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AiSubtitles.Tests;

public class AppTrackMiddlewareTests
{
    private const string ItemId = "0123456789abcdef0123456789abcdef";

    private const string PlaybackInfoJson = """
        {"MediaSources":[{"Id":"ms1","DefaultSubtitleStreamIndex":-1,"MediaStreams":[
          {"Type":"Video","Index":0},
          {"Type":"Audio","Index":1},
          {"Type":"Audio","Index":2},
          {"Type":"Subtitle","Index":3,"Language":"eng","IsTextSubtitleStream":true}
        ]}]}
        """;

    public AppTrackMiddlewareTests()
    {
        ConfigOverride.Set(new PluginConfiguration { AppTrackLanguages = "de" });
    }

    private static AppTrackService CreateService() => new FakeService();

    /// <summary>
    /// Item with one English text subtitle (index 3) and streams 0..3, so the German track gets index 4.
    /// </summary>
    private sealed class FakeService : AppTrackService
    {
        public FakeService()
            : base(
                null!,
                null!,
                NullLocalization.Create(),
                DispatchProxy.Create<ILibraryManager, NullLocalization>(),
                DispatchProxy.Create<IUserManager, NullLocalization>(),
                NullLogger<AppTrackService>.Instance)
        {
        }

        public override TrackLayout? GetLayout(System.Guid itemId, string? mediaSourceId)
            => itemId == System.Guid.Parse(ItemId)
                ? new TrackLayout(null!, "ms1", [new AppTrack(4, 3, GetLanguages()[0])])
                : null;
    }

    private static DefaultHttpContext Context(string method, string path, string query = "", string? body = null, string client = "Android TV")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers.Authorization = $"MediaBrowser Client=\"{client}\", Device=\"TV\", Token=\"secret\"";
        if (body is not null)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static RequestDelegate Respond(string json, System.Action<HttpRequest>? inspect = null) => async ctx =>
    {
        inspect?.Invoke(ctx.Request);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json);
    };

    [Fact]
    public async Task PlaybackInfoHidesVirtualIndexFromJellyfinAndAddsTrack()
    {
        var virtualIndex = 4;
        string? seenBody = null;
        string? seenQuery = null;

        var middleware = new AppTrackMiddleware(
            Respond(PlaybackInfoJson, req =>
            {
                seenQuery = req.QueryString.Value;
                seenBody = new StreamReader(req.Body).ReadToEnd();
            }),
            CreateService(),
            NullLogger<AppTrackMiddleware>.Instance);

        var context = Context(
            "POST",
            $"/jellyfin/Items/{ItemId}/PlaybackInfo",
            $"?SubtitleStreamIndex={virtualIndex}&SubtitleMethod=External&MaxStreamingBitrate=1",
            "{\"SubtitleStreamIndex\":" + virtualIndex + ",\"DeviceProfile\":{\"SubtitleProfiles\":[{\"Format\":\"vtt\",\"Method\":\"External\"}]}}");

        await middleware.InvokeAsync(context);

        Assert.Equal("?SubtitleStreamIndex=-1&MaxStreamingBitrate=1", seenQuery);
        Assert.Equal(-1, JsonNode.Parse(seenBody!)!["SubtitleStreamIndex"]!.GetValue<int>());

        var response = JsonNode.Parse(await ReadResponse(context))!;
        var source = response["MediaSources"]![0]!;
        Assert.Equal(virtualIndex, source["DefaultSubtitleStreamIndex"]!.GetValue<int>());
        var track = source["MediaStreams"]!.AsArray()[virtualIndex]!;
        Assert.Equal($"/Videos/{ItemId}/ms1/Subtitles/{virtualIndex}/0/Stream.vtt?ApiKey=secret", track["DeliveryUrl"]!.GetValue<string>());
        Assert.Equal(context.Response.ContentLength, context.Response.Body.Length);
    }

    [Fact]
    public async Task SubtitleRequestForVirtualIndexGoesToPlugin()
    {
        var virtualIndex = 4;
        string? seenPath = null;
        var middleware = new AppTrackMiddleware(
            ctx => { seenPath = ctx.Request.Path.Value; return Task.CompletedTask; },
            CreateService(),
            NullLogger<AppTrackMiddleware>.Instance);

        await middleware.InvokeAsync(Context("GET", $"/jellyfin/Videos/{ItemId}/ms1/Subtitles/{virtualIndex}/0/Stream.srt", "?ApiKey=secret"));
        Assert.Equal($"/jellyfin/AiSubtitles/AppTrack/{ItemId}/ms1/{virtualIndex}/Stream.srt", seenPath);

        await middleware.InvokeAsync(Context("GET", $"/Videos/{ItemId}/ms1/Subtitles/3/0/Stream.srt"));
        Assert.Equal($"/Videos/{ItemId}/ms1/Subtitles/3/0/Stream.srt", seenPath);
    }

    [Fact]
    public async Task ItemDetailsListTrackForAppsAndBrowser()
    {
        var middleware = new AppTrackMiddleware(Respond(PlaybackInfoJson), CreateService(), NullLogger<AppTrackMiddleware>.Instance);

        var app = Context("GET", $"/Users/{ItemId}/Items/{ItemId}");
        await middleware.InvokeAsync(app);
        Assert.Equal(5, JsonNode.Parse(await ReadResponse(app))!["MediaSources"]![0]!["MediaStreams"]!.AsArray().Count);

        var browser = Context("GET", $"/Items/{ItemId}", client: "Jellyfin Web");
        await middleware.InvokeAsync(browser);
        Assert.Equal(5, JsonNode.Parse(await ReadResponse(browser))!["MediaSources"]![0]!["MediaStreams"]!.AsArray().Count);
    }
}
