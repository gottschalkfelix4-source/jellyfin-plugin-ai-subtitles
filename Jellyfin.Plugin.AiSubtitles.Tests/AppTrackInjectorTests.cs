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
        Assert.Equal(AppTrackInjector.Encode(0, 3), track.Index);

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
        var index = AppTrackInjector.Encode(0, 3);
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
        Assert.Equal(AppTrackInjector.Encode(0, 5), top["index"]!.GetValue<int>());
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
    public void EncodeDecodeRoundTrip()
    {
        var index = AppTrackInjector.Encode(3, 17);
        Assert.True(AppTrackInjector.IsVirtual(index));
        Assert.Equal((3, 17), AppTrackInjector.Decode(index));
        Assert.False(AppTrackInjector.IsVirtual(17));
    }

    [Theory]
    [InlineData("""{"DeviceProfile":{"SubtitleProfiles":[{"Format":"ass","Method":"Embed"},{"Format":"vtt","Method":"External"}]}}""", "vtt")]
    [InlineData("""{"deviceProfile":{"subtitleProfiles":[{"format":"subrip","method":"External"}]}}""", "srt")]
    [InlineData("""{"DeviceProfile":{"SubtitleProfiles":[{"Format":"srt","Method":"Encode"}]}}""", null)]
    [InlineData("""{"UserId":"x"}""", "srt")]
    public void ChoosesFormatFromDeviceProfile(string body, string? expected)
    {
        var node = JsonNode.Parse(body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
        Assert.Equal(expected, AppTrackMiddleware.ChooseFormat(node));
    }
}
