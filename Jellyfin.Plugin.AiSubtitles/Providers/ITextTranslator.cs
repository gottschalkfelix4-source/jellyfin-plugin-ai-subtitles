using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.Configuration;

namespace Jellyfin.Plugin.AiSubtitles.Providers;

/// <summary>
/// A chat backend that turns a system + user prompt into a JSON answer.
/// </summary>
public interface ITextTranslator
{
    /// <summary>
    /// Sends the prompt and returns the raw text of the answer (expected to contain JSON).
    /// </summary>
    /// <param name="config">Current configuration.</param>
    /// <param name="systemPrompt">System prompt.</param>
    /// <param name="userPrompt">User prompt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model answer.</returns>
    Task<string> CompleteAsync(PluginConfiguration config, string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when the AI backend fails in a way the user should see.
/// </summary>
public class TranslatorException : System.Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TranslatorException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="inner">Inner exception.</param>
    public TranslatorException(string message, System.Exception? inner = null)
        : base(message, inner)
    {
    }
}
