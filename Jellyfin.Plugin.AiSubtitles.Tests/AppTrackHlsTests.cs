using System.Linq;
using Jellyfin.Plugin.AiSubtitles.AppTracks;
using Xunit;

namespace Jellyfin.Plugin.AiSubtitles.Tests;

public class AppTrackHlsTests
{
    private static readonly HlsRendition German = new("Deutsch (KI)", "de", "ms1/Subtitles/100003/subtitles.m3u8?SegmentLength=30&ApiKey=k&VttTimestampMapMpegts=900000", false);

    private const string MasterWithoutSubtitles =
        "#EXTM3U\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=1000,CODECS=\"avc1.640028,mp4a.40.2\"\n" +
        "main.m3u8?MediaSourceId=ms1&ApiKey=k\n";

    [Fact]
    public void AddsGroupAndRendition()
    {
        var result = AppTrackHls.InjectIntoMaster(MasterWithoutSubtitles, [German])!;
        var lines = result.Split('\n');

        Assert.Equal("#EXTM3U", lines[0]);
        Assert.StartsWith("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Deutsch (KI)\",DEFAULT=NO", lines[1]);
        Assert.Contains("URI=\"ms1/Subtitles/100003/subtitles.m3u8?", lines[1]);
        Assert.Contains("LANGUAGE=\"de\"", lines[1]);
        Assert.EndsWith(",SUBTITLES=\"subs\"", lines[2]);
        Assert.Equal("main.m3u8?MediaSourceId=ms1&ApiKey=k", lines[3]);
    }

    [Fact]
    public void JoinsExistingGroupAndTakesOverDefault()
    {
        var master =
            "#EXTM3U\n" +
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",DEFAULT=YES,FORCED=NO,AUTOSELECT=YES,URI=\"ms1/Subtitles/3/subtitles.m3u8\",LANGUAGE=\"eng\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=1000,SUBTITLES=\"subs\"\n" +
            "main.m3u8\n";

        var result = AppTrackHls.InjectIntoMaster(master, [German with { IsDefault = true }])!;
        var lines = result.Split('\n');

        Assert.Contains("NAME=\"English\",DEFAULT=NO", lines[1]);
        Assert.Contains("NAME=\"Deutsch (KI)\",DEFAULT=YES", lines[2]);
        Assert.Equal(1, lines.Count(l => l.Contains("SUBTITLES=\"subs\"") && l.StartsWith("#EXT-X-STREAM-INF")));
        Assert.DoesNotContain("SUBTITLES=\"subs\",SUBTITLES", result);
    }

    [Fact]
    public void LeavesOtherPlaylistsAlone()
    {
        Assert.Null(AppTrackHls.InjectIntoMaster("#EXTM3U\n#EXTINF:3,\nhls1/main/0.ts\n", [German]));
        var once = AppTrackHls.InjectIntoMaster(MasterWithoutSubtitles, [German])!;
        Assert.Null(AppTrackHls.InjectIntoMaster(once, [German]));
    }

    [Fact]
    public void BuildsSubtitlePlaylistLikeJellyfin()
    {
        var playlist = AppTrackHls.BuildSubtitlePlaylist(runtimeTicks: 650_000_000, segmentLengthSeconds: 30, accessToken: "k", timestampMapMpegts: 900000);
        var lines = playlist.Split('\n');

        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#EXT-X-TARGETDURATION:30", lines[1]);
        Assert.Equal("#EXTINF:30,", lines[5]);
        Assert.Equal("stream.vtt?CopyTimestamps=true&AddVttTimeMap=true&StartPositionTicks=0&EndPositionTicks=300000000&ApiKey=k&VttTimestampMapMpegts=900000", lines[6]);
        Assert.Equal("#EXTINF:5,", lines[9]);
        Assert.Contains("StartPositionTicks=600000000&EndPositionTicks=650000000", lines[10]);
        Assert.Equal("#EXT-X-ENDLIST", lines[11]);
    }

    [Fact]
    public void VttHeaderHasTimestampMap()
    {
        Assert.Equal("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n", AppTrackHls.VttHeader(900000));
        Assert.Equal("WEBVTT\n\n", AppTrackHls.VttHeader(null));
    }
}
