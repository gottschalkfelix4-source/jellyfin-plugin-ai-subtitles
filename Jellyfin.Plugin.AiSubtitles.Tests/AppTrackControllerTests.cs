using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.Api;
using Jellyfin.Plugin.AiSubtitles.AppTracks;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AiSubtitles.Tests;

public class AppTrackControllerTests
{
    [Theory]
    [InlineData("Json")]
    [InlineData("Vtt")]
    [InlineData("Srt")]
    public async Task WritesWholeTrackInRequestedFormat(string format)
    {
        var slow = new TaskCompletionSource<System.Collections.Generic.List<SubtitleCue>>();
        var job = new AppTrackJob(
        [
            Task.FromResult(new System.Collections.Generic.List<SubtitleCue> { new(0, 1000, 2000, "Hallo \"du\""), new(1, 3000, 4000, "Zeile 1\nZeile 2") }),
            slow.Task
        ]);

        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        var controller = new AppTrackController(null!, null!, NullLogger<AppTrackController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var method = typeof(AppTrackController).GetMethod("WriteWholeTrackAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var outputType = typeof(AppTrackController).GetNestedType("OutputFormat", BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(controller, [job, Enum.Parse(outputType, format), CancellationToken.None])!;

        // Second batch arrives after a keep-alive interval.
        await Task.Delay(4500);
        slow.SetResult([new(2, 5000, 6000, "Ende")]);
        await task;

        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        switch (format)
        {
            case "Json":
                var events = JsonNode.Parse(text)!["TrackEvents"]!.AsArray();
                Assert.Equal(3, events.Count);
                Assert.Equal("Hallo \"du\"", events[0]!["Text"]!.GetValue<string>());
                Assert.Equal(10_000_000, events[0]!["StartPositionTicks"]!.GetValue<long>());
                Assert.Equal("3", events[2]!["Id"]!.GetValue<string>());
                break;
            case "Vtt":
                Assert.StartsWith("WEBVTT\n\n", text);
                Assert.Contains("00:00:05.000 --> 00:00:06.000\nEnde", text);
                break;
            default:
                Assert.Equal(3, SubtitleFormat.Parse(text).Count);
                break;
        }
    }
}
