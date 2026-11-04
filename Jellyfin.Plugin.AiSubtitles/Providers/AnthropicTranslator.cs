using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Jellyfin.Plugin.AiSubtitles.Configuration;

namespace Jellyfin.Plugin.AiSubtitles.Providers;

/// <summary>
/// Talks to the Anthropic Messages API through the official SDK.
/// </summary>
public sealed class AnthropicTranslator : ITextTranslator, IDisposable
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "translations": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "integer" },
                  "text": { "type": "string" }
                },
                "required": ["id", "text"],
                "additionalProperties": false
              }
            }
          },
          "required": ["translations"],
          "additionalProperties": false
        }
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> Schema =
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaJson)!;

    private readonly object _lock = new();
    private AnthropicClient? _client;
    private string? _clientKey;

    /// <inheritdoc />
    public async Task<string> CompleteAsync(PluginConfiguration config, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.AnthropicApiKey))
        {
            throw new TranslatorException("Anthropic: Es ist kein API-Schlüssel hinterlegt.");
        }

        var client = GetClient(config);

        var parameters = new MessageCreateParams
        {
            Model = string.IsNullOrWhiteSpace(config.AnthropicModel) ? "claude-opus-5-5" : config.AnthropicModel.Trim(),
            MaxTokens = 16000,
            System = systemPrompt,
            Messages = [new BetaUserMessageParam { Content = userPrompt }],
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(config.AnthropicEffort),
                Format = new BetaJsonOutputFormat { Schema = Schema }
            }
        };

        if (config.AnthropicServerFallback)
        {
            parameters = parameters with
            {
                Betas = [FallbackBeta],
                Fallbacks = new Default()
            };
        }

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (AnthropicApiException ex)
        {
            throw new TranslatorException("Anthropic-API-Fehler: " + ex.Message, ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslatorException($"Anthropic: Zeitüberschreitung nach {config.RequestTimeoutSeconds} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TranslatorException)
        {
            throw new TranslatorException("Anthropic: " + ex.Message, ex);
        }

        if (response.StopReason == BetaStopReason.Refusal)
        {
            throw new TranslatorException("Claude hat die Übersetzung dieses Abschnitts abgelehnt (stop_reason=refusal).");
        }

        var text = string.Concat(response.Content
            .Select(block => block.Value)
            .OfType<BetaTextBlock>()
            .Select(block => block.Text));

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new TranslatorException($"Anthropic: Leere Antwort (stop_reason={response.StopReason}).");
        }

        return text;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            (_client as IDisposable)?.Dispose();
            _client = null;
        }
    }

    private static Effort ParseEffort(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Low
    };

    private AnthropicClient GetClient(PluginConfiguration config)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.AnthropicBaseUrl) ? "https://api.anthropic.com" : config.AnthropicBaseUrl.Trim().TrimEnd('/');
        var timeout = TimeSpan.FromSeconds(Math.Max(10, config.RequestTimeoutSeconds));
        var key = string.Join('|', config.AnthropicApiKey.Trim(), baseUrl, timeout.TotalSeconds);

        lock (_lock)
        {
            if (_client is null || _clientKey != key)
            {
                (_client as IDisposable)?.Dispose();
                _client = new AnthropicClient
                {
                    ApiKey = config.AnthropicApiKey.Trim(),
                    BaseUrl = baseUrl,
                    Timeout = timeout,
                    MaxRetries = 2
                };
                _clientKey = key;
            }

            return _client;
        }
    }
}
