using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AiSubtitles.AppTracks;
using Jellyfin.Plugin.AiSubtitles.Translation;
using Xunit;

namespace Jellyfin.Plugin.AiSubtitles.Tests;

public class AppTrackInjectorTests
{
    private const string ItemId = "0123456789abcdef0123456789abcdef";

    private static readonly AppLanguage German = new(0, new TargetLanguage("de", "German (Deutsch)", "Deutsch", "ger"));
    private static readonly AppLanguage French = new(1, new TargetLanguage("fr", "French", "Français", "fre"));

    private static string? Key(string? code) => code switch
    {
        "eng" => "en",
        "ger" or "deu" => "de",
        "fre" => "fr",
        null => null,
        _ => code
    };

    private static InjectOptions Options(string? format = "srt", int? requested = null, string? preferred = null, bool auto = false, params AppLanguage[] languages)
        => new()
        {
            ItemId = ItemId,
            Languages = languages.Length == 0 ? [German] : languages,
            LanguageKey = Key,
            LanguageName = c => c == "eng" ? "English" : c,
            Format = format,
            ApiKey = "tok en",
            RequestedIndex = requested,
            PreferredKey = preferred,
            AutoSelect = auto
        };

    private const string PlaybackInfo = """
        {
          "MediaSources": [{
            "Id": "ms1",
            "DefaultSubtitleStreamIndex": 3,
            "MediaStreams": [
              { "Type": "Video", "Index": 0 },
              { "Type": "Audio", "Index": 1, "Language": "eng" },
              { "Type": "Subtitle", "Index": 2, "Language": "eng", "IsForced": true, "IsTextSubtitleStream": true },
              { "Type": "Subtitle", "Index": 3, "Language": "eng", "IsTextSubtitleStream": true, "DeliveryMethod": "External" },
              { "Type": "Subtitle", "Index": 4, "Language": "spa", "IsTextSubtitleStream": false }
            ]
          }],
          "PlaySessionId": "x"
        }
        """;

    [Fact]
    public void AddsTrackWithDeliveryUrl()
    {
        var root = JsonNode.Parse(PlaybackInfo)!;
        var added = AppTrackInjector.Inject(root, Options(), out var selected);

        var track = Assert.Single(added);
        Assert.Null(selected);
        Assert.Equal(3, track.SourceIndex); // forced track is skipped
        Assert.Equal(5, track.Index); // directly after the five existing streams

        // The web player looks up a preselected subtitle by position: MediaStreams[index] must be the track.
        var atPosition = root["MediaSources"]![0]!["MediaStreams"]![track.Index]!;
        Assert.Equal(track.Index, atPosition["Index"]!.GetValue<int>());

        var stream = root["MediaSources"]![0]!["MediaStreams"]!.AsArray().Last()!;
        Assert.Equal("Subtitle", stream["Type"]!.GetValue<string>());
        Assert.Equal("ger", stream["Language"]!.GetValue<string>());
        Assert.Equal("External", stream["DeliveryMethod"]!.GetValue<string>());
        Assert.Equal($"/Videos/{ItemId}/ms1/Subtitles/{track.Index}/0/Stream.srt?ApiKey=tok%20en", stream["DeliveryUrl"]!.GetValue<string>());
        Assert.Equal("Deutsch (KI) – aus English", stream["DisplayTitle"]!.GetValue<string>());
        Assert.Equal(3, root["MediaSources"]![0]!["DefaultSubtitleStreamIndex"]!.GetValue<int>());
    }

    [Fact]
    public void SkipsLanguagesThatAlreadyExist()
    {
        var root = JsonNode.Parse(PlaybackInfo.Replace("\"spa\", \"IsTextSubtitleStream\": false", "\"deu\", \"IsTextSubtitleStream\": true"))!;
        var added = AppTrackInjector.Inject(root, Options(languages: [German, French]), out _);
        Assert.Equal("fr", Assert.Single(added).Language.Target.Key);
    }

    [Fact]
    public void IsIdempotent()
    {
        var root = JsonNode.Parse(PlaybackInfo)!;
        AppTrackInjector.Inject(root, Options(), out _);
        Assert.Empty(AppTrackInjector.Inject(root, Options(), out _));
    }

    [Fact]
    public void KeepsRequestedVirtualTrackSelected()
    {
        var root = JsonNode.Parse(PlaybackInfo.Replace("\"DefaultSubtitleStreamIndex\": 3", "\"DefaultSubtitleStreamIndex\": -1"))!;
        var index = 5;
        AppTrackInjector.Inject(root, Options(requested: index), out var selected);
        Assert.NotNull(selected);
        Assert.Equal(index, root["MediaSources"]![0]!["DefaultSubtitleStreamIndex"]!.GetValue<int>());
    }

    [Fact]
    public void PreselectsTranslationForPreferredLanguage()
    {
        var root = JsonNode.Parse(PlaybackInfo)!;
        AppTrackInjector.Inject(root, Options(preferred: "de", auto: true), out var selected);
        Assert.NotNull(selected);
        Assert.Equal(selected!.Index, root["MediaSources"]![0]!["DefaultSubtitleStreamIndex"]!.GetValue<int>());
    }

    [Fact]
    public void DoesNotPreselectWhenServerBurnsIn()
    {
        var root = JsonNode.Parse(PlaybackInfo.Replace("\"DeliveryMethod\": \"External\"", "\"DeliveryMethod\": \"Encode\""))!;
        AppTrackInjector.Inject(root, Options(preferred: "de", auto: true), out var selected);
        Assert.Null(selected);
        Assert.Equal(3, root["MediaSources"]![0]!["DefaultSubtitleStreamIndex"]!.GetValue<int>());
    }

    [Fact]
    public void DoesNotPreselectWhenClientChoseTrack()
    {
        var root = JsonNode.Parse(PlaybackInfo)!;
        AppTrackInjector.Inject(root, Options(requested: 3, preferred: "de", auto: true), out var selected);
        Assert.Null(selected);
    }

    [Fact]
    public void HandlesCamelCaseItemDetails()
    {
        var root = JsonNode.Parse("""
            {
              "id": "abc",
              "mediaSources": [{ "id": "ms1", "mediaStreams": [
                { "type": "Subtitle", "index": 5, "language": "eng", "isTextSubtitleStream": true }
              ]}],
              "mediaStreams": [
                { "type": "Subtitle", "index": 5, "language": "eng", "isTextSubtitleStream": true }
              ]
            }
            """)!;

        var added = AppTrackInjector.Inject(root, Options(format: null), out _);

        Assert.Single(added);
        var top = root["mediaStreams"]!.AsArray().Last()!;
        Assert.Equal(6, top["index"]!.GetValue<int>()); // after the highest existing index (5)
        Assert.Null(top["deliveryUrl"]);
        Assert.Equal(2, root["mediaSources"]![0]!["mediaStreams"]!.AsArray().Count);
    }

    [Fact]
    public void IgnoresItemsWithoutTextSubtitles()
    {
        var root = JsonNode.Parse("""{ "MediaSources": [{ "Id": "a", "MediaStreams": [{ "Type": "Video", "Index": 0 }] }] }""")!;
        Assert.Empty(AppTrackInjector.Inject(root, Options(), out _));
    }

    [Fact]
    public void FirstFreeIndexFollowsExistingStreams()
    {
        Assert.Equal(0, AppTrackInjector.FirstFreeIndex([]));
        Assert.Equal(4, AppTrackInjector.FirstFreeIndex([0, 1, 2, 3]));
        Assert.Equal(8, AppTrackInjector.FirstFreeIndex([0, 1, 7]));
    }

    [Fact]
    public void NumbersSeveralLanguagesConsecutively()
    {
        var root = JsonNode.Parse(PlaybackInfo)!;
        var added = AppTrackInjector.Inject(root, Options(languages: [German, French]), out _);
        Assert.Equal([5, 6], added.Select(a => a.Index).ToArray());
    }

    [Theory]
    [InlineData("""{"DeviceProfile":{"SubtitleProfiles":[{"Format":"ass","Method":"Embed"},{"Format":"vtt","Method":"External"}]}}""", "vtt/External")]
    [InlineData("""{"deviceProfile":{"subtitleProfiles":[{"format":"subrip","method":"External"}]}}""", "srt/External")]
    [InlineData("""{"DeviceProfile":{"SubtitleProfiles":[{"Format":"srt","Method":"Encode"}]}}""", null)]
    [InlineData("""{"DeviceProfile":{"SubtitleProfiles":[{"Format":"vtt","Method":"Hls"},{"Format":"srt","Method":"Encode"}]}}""", "vtt/Hls")]
    [InlineData("""{"DeviceProfile":{"SubtitleProfiles":[{"Format":"vtt","Method":"Hls"},{"Format":"vtt","Method":"External"}]}}""", "vtt/External")]
    [InlineData("""{"UserId":"x"}""", "srt/External")]
    public void ChoosesDeliveryFromDeviceProfile(string body, string? expected)
    {
        var node = JsonNode.Parse(body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
        var result = AppTrackMiddleware.ChooseDelivery(node);
        Assert.Equal(expected, result is null ? null : result.Value.Format + "/" + result.Value.Method);
    }

    [Fact]
    public void HlsTrackPointsToSubtitlePlaylist()
    {
        var root = JsonNode.Parse(PlaybackInfo)!;
        var options = new InjectOptions
        {
            ItemId = ItemId,
            Languages = [German],
            LanguageKey = Key,
            Format = "vtt",
            Method = "Hls",
            ApiKey = "abc"
        };
        var track = Assert.Single(AppTrackInjector.Inject(root, options, out _));
        var stream = root["MediaSources"]![0]!["MediaStreams"]!.AsArray().Last()!;
        Assert.Equal("Hls", stream["DeliveryMethod"]!.GetValue<string>());
        Assert.Equal($"/Videos/{ItemId}/ms1/Subtitles/{track.Index}/subtitles.m3u8?SegmentLength=30&ApiKey=abc", stream["DeliveryUrl"]!.GetValue<string>());
    }
}
