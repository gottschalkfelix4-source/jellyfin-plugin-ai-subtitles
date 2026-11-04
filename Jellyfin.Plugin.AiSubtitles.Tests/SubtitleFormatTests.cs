using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Jellyfin.Plugin.AiSubtitles.Translation;
using Xunit;

namespace Jellyfin.Plugin.AiSubtitles.Tests;

public class SubtitleFormatTests
{
    [Fact]
    public void ParsesSrtAndStripsTags()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i> {\\an8}there\r\nsecond line\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nTom &amp; Jerry\r\n\r\n";
        var cues = SubtitleFormat.Parse(srt);

        Assert.Equal(2, cues.Count);
        Assert.Equal(1000, cues[0].StartMs);
        Assert.Equal(2500, cues[0].EndMs);
        Assert.Equal("Hello there\nsecond line", cues[0].Text);
        Assert.Equal("Tom & Jerry", cues[1].Text);
        Assert.Equal(1, cues[1].Index);
    }

    [Fact]
    public void ParsesVttWithShortTimestampsAndSorts()
    {
        const string vtt = "WEBVTT\n\n00:05.000 --> 00:06.000 line:90%\nLater\n\n00:01.000 --> 00:02.000\nEarlier\n";
        var cues = SubtitleFormat.Parse(vtt);

        Assert.Equal(new[] { "Earlier", "Later" }, cues.Select(c => c.Text));
        Assert.Equal(5000, cues[1].StartMs);
    }

    [Fact]
    public void RoundTripsThroughSrt()
    {
        var cues = new List<SubtitleCue> { new(0, 3_723_456, 3_725_000, "A\nB") };
        var srt = SubtitleFormat.ToSrt(cues);

        Assert.Contains("01:02:03,456 --> 01:02:05,000", srt, StringComparison.Ordinal);
        Assert.Equal(cues[0], SubtitleFormat.Parse(srt)[0]);
    }

    [Fact]
    public void BuildsBatches()
    {
        var cues = Enumerable.Range(0, 95).Select(i => new SubtitleCue(i, i * 1000, (i * 1000) + 800, "x")).ToList();
        var batches = TranslationService.BuildBatches(cues, 40);

        Assert.Equal(3, batches.Count);
        Assert.Equal(15, batches[2].Count);
        Assert.Equal(80, batches[2].FirstCue);
        Assert.Equal(80_000, batches[2].StartMs);
        Assert.Equal(94_800, batches[2].EndMs);
    }
}
