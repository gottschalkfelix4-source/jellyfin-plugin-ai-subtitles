using Jellyfin.Plugin.AiSubtitles.Providers;
using Jellyfin.Plugin.AiSubtitles.Translation;
using Xunit;

namespace Jellyfin.Plugin.AiSubtitles.Tests;

public class ParseAnswerTests
{
    [Fact]
    public void ParsesPlainJson()
    {
        var result = TranslationService.ParseAnswer("{\"translations\":[{\"id\":1,\"text\":\"Zwei\"},{\"id\":0,\"text\":\"Eins\"}]}", 2);
        Assert.Equal(new[] { "Eins", "Zwei" }, result);
    }

    [Fact]
    public void HandlesThinkTagsFencesAndMissingLines()
    {
        const string answer = "<think>let me see {not json}</think>\n```json\n{\"translations\":[{\"id\":\"0\",\"text\":\"Hallo\\nWelt\"},{\"id\":2,\"text\":\"Drei\"}]}\n```";
        var result = TranslationService.ParseAnswer(answer, 3);

        Assert.Equal("Hallo\nWelt", result[0]);
        Assert.Null(result[1]);
        Assert.Equal("Drei", result[2]);
    }

    [Fact]
    public void AcceptsBareArrayOfStrings()
    {
        var result = TranslationService.ParseAnswer("[\"a\",\"b\"]", 2);
        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void ThrowsOnGarbage()
    {
        Assert.Throws<TranslatorException>(() => TranslationService.ParseAnswer("Sorry, I can't.", 2));
    }
}
