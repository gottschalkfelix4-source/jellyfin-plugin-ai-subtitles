using Jellyfin.Plugin.AiSubtitles.Configuration;

namespace Jellyfin.Plugin.AiSubtitles.Providers;

/// <summary>
/// Picks the backend that matches the configuration.
/// </summary>
public class TranslatorFactory
{
    private readonly OpenAiCompatibleTranslator _openAi;
    private readonly AnthropicTranslator _anthropic;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslatorFactory"/> class.
    /// </summary>
    /// <param name="openAi">OpenAI-compatible backend.</param>
    /// <param name="anthropic">Anthropic backend.</param>
    public TranslatorFactory(OpenAiCompatibleTranslator openAi, AnthropicTranslator anthropic)
    {
        _openAi = openAi;
        _anthropic = anthropic;
    }

    /// <summary>
    /// Gets the backend for the given configuration.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <returns>The backend.</returns>
    public ITextTranslator Get(PluginConfiguration config) => config.Provider switch
    {
        AiProviderType.Anthropic => _anthropic,
        _ => _openAi
    };
}
