using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSubtitles.Configuration;

namespace Jellyfin.Plugin.AiSubtitles.Providers;

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint.
/// </summary>
public class OpenAiCompatibleTranslator : ITextTranslator
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAiCompatibleTranslator"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public OpenAiCompatibleTranslator(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(PluginConfiguration config, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.OpenAiBaseUrl) || string.IsNullOrWhiteSpace(config.OpenAiModel))
        {
            throw new TranslatorException("OpenAI-kompatibler Endpunkt: Basis-URL und Modell müssen gesetzt sein.");
        }

        var url = config.OpenAiBaseUrl.TrimEnd('/') + "/chat/completions";

        var body = new JsonObject
        {
            ["model"] = config.OpenAiModel.Trim(),
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            },
            ["stream"] = false
        };

        if (!string.IsNullOrWhiteSpace(config.OpenAiTemperature)
            && double.TryParse(config.OpenAiTemperature.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            body["temperature"] = temperature;
        }

        if (config.OpenAiJsonMode)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAiApiKey.Trim());
        }

        var client = _httpClientFactory.CreateClient(nameof(OpenAiCompatibleTranslator));
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, config.RequestTimeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslatorException($"Zeitüberschreitung nach {config.RequestTimeoutSeconds} s ({url}).");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslatorException($"Endpunkt nicht erreichbar ({url}): {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslatorException($"HTTP {(int)response.StatusCode} von {url}: {Truncate(text, 400)}");
            }

            try
            {
                var node = JsonNode.Parse(text);
                var content = node?["choices"]?[0]?["message"]?["content"];
                if (content is null)
                {
                    throw new TranslatorException("Antwort enthält keine choices[0].message.content: " + Truncate(text, 400));
                }

                return content.GetValueKind() == JsonValueKind.String ? content.GetValue<string>() : content.ToJsonString();
            }
            catch (JsonException ex)
            {
                throw new TranslatorException("Antwort ist kein gültiges JSON: " + Truncate(text, 400), ex);
            }
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
