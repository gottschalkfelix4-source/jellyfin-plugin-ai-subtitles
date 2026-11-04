using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AiSubtitles.Configuration;

/// <summary>
/// Which AI backend is used.
/// </summary>
public enum AiProviderType
{
    /// <summary>
    /// Any endpoint speaking the OpenAI chat completions protocol (OpenAI, Ollama, LM Studio, OpenRouter, vLLM, ...).
    /// </summary>
    OpenAiCompatible = 0,

    /// <summary>
    /// Anthropic Messages API (Claude).
    /// </summary>
    Anthropic = 1
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the active provider.
    /// </summary>
    public AiProviderType Provider { get; set; } = AiProviderType.Anthropic;

    // ---------- OpenAI-compatible ----------

    /// <summary>
    /// Gets or sets the base URL (up to and including /v1).
    /// </summary>
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Gets or sets the API key (optional for local servers).
    /// </summary>
    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model name.
    /// </summary>
    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Gets or sets a value indicating whether response_format=json_object is sent.
    /// </summary>
    public bool OpenAiJsonMode { get; set; } = true;

    /// <summary>
    /// Gets or sets the temperature. Empty = parameter not sent (some reasoning models reject it).
    /// </summary>
    public string OpenAiTemperature { get; set; } = "0.2";

    // ---------- Anthropic ----------

    /// <summary>
    /// Gets or sets the Anthropic base URL.
    /// </summary>
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// Gets or sets the Anthropic API key.
    /// </summary>
    public string AnthropicApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Claude model id.
    /// </summary>
    public string AnthropicModel { get; set; } = "claude-opus-5-5";

    /// <summary>
    /// Gets or sets the effort level (low, medium, high, xhigh, max).
    /// </summary>
    public string AnthropicEffort { get; set; } = "low";

    /// <summary>
    /// Gets or sets a value indicating whether the server-side refusal fallback (beta) is requested.
    /// </summary>
    public bool AnthropicServerFallback { get; set; } = true;

    // ---------- Translation ----------

    /// <summary>
    /// Gets or sets the number of subtitle cues per AI request.
    /// </summary>
    public int BatchSize { get; set; } = 40;

    /// <summary>
    /// Gets or sets how many preceding cues are sent along as context.
    /// </summary>
    public int ContextLines { get; set; } = 6;

    /// <summary>
    /// Gets or sets the maximum number of concurrent AI requests (server-wide).
    /// </summary>
    public int MaxParallelRequests { get; set; } = 3;

    /// <summary>
    /// Gets or sets the request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Gets or sets additional instructions appended to the system prompt.
    /// </summary>
    public string ExtraInstructions { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language list shown in the player, one "code=Name" per line.
    /// </summary>
    public string Languages { get; set; } =
        "de=Deutsch\nen=English\nfr=Français\nes=Español\nit=Italiano\nnl=Nederlands\npl=Polski\npt=Português\ntr=Türkçe\nru=Русский\nuk=Українська\nja=日本語\nko=한국어\nzh=中文\nar=العربية";

    /// <summary>
    /// Gets or sets the preselected target language code.
    /// </summary>
    public string DefaultTargetLanguage { get; set; } = "de";

    // ---------- Integration ----------

    /// <summary>
    /// Gets or sets a value indicating whether the player script is injected into the web client.
    /// </summary>
    public bool EnableWebPlayerIntegration { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin shows up in the "search subtitles" dialog.
    /// </summary>
    public bool EnableSubtitleProvider { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether non-admin users may save translations as subtitle files.
    /// </summary>
    public bool AllowNonAdminSave { get; set; }

    // ---------- Tracks for all apps ----------

    /// <summary>
    /// Gets or sets a value indicating whether translated tracks are offered as regular subtitle tracks to every client.
    /// </summary>
    public bool EnableAppTracks { get; set; } = true;

    /// <summary>
    /// Gets or sets the languages offered as tracks in all apps (codes, separated by comma or space).
    /// </summary>
    public string AppTrackLanguages { get; set; } = "de";

    /// <summary>
    /// Gets or sets a value indicating whether the tracks are also listed in the browser (which has its own live menu).
    /// </summary>
    public bool AppTracksInWebClient { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the translated track is preselected when the server would otherwise
    /// show subtitles in another language than the user's preferred subtitle language.
    /// </summary>
    public bool AutoSelectAppTrack { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the next episode is translated in the background.
    /// </summary>
    public bool PrefetchNextEpisode { get; set; } = true;
}
