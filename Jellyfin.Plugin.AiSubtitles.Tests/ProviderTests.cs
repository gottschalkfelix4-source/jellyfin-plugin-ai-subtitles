using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AiSubtitles.Configuration;
using Jellyfin.Plugin.AiSubtitles.Providers;
using Jellyfin.Plugin.AiSubtitles.Subtitles;
using Jellyfin.Plugin.AiSubtitles.Translation;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AiSubtitles.Tests;

/// <summary>
/// Runs both backends against a local fake HTTP server and checks what goes over the wire.
/// </summary>
public sealed class ProviderTests : IDisposable
{
    private readonly FakeServer _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task AnthropicSendsStructuredRequestWithFallbackAndParsesAnswer()
    {
        _server.Respond = _ => """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"text","text":"{\"translations\":[{\"id\":0,\"text\":\"Hallo\"}]}"}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":12,"output_tokens":7}}
            """;

        var config = new PluginConfiguration
        {
            Provider = AiProviderType.Anthropic,
            AnthropicApiKey = "test-key",
            AnthropicBaseUrl = _server.Url,
            AnthropicModel = "claude-opus-5-5",
            AnthropicEffort = "low",
            AnthropicServerFallback = true
        };

        using var translator = new AnthropicTranslator();
        var answer = await translator.CompleteAsync(config, "SYSTEM", "USER", CancellationToken.None);

        Assert.Contains("Hallo", answer, StringComparison.Ordinal);
        var request = Assert.Single(_server.Requests);
        Assert.EndsWith("/v1/messages", request.Path, StringComparison.Ordinal);
        // SDK 13.x sends the key as a bearer token (older releases used x-api-key).
        var sentKey = request.Headers.TryGetValue("x-api-key", out var k) ? k : request.Headers["Authorization"].Replace("Bearer ", string.Empty, StringComparison.Ordinal);
        Assert.Equal("test-key", sentKey);
        Assert.Contains("server-side-fallback-2026-07-01", request.Headers["anthropic-beta"], StringComparison.Ordinal);

        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.NotNull(body["output_config"]!["format"]!["schema"]!["properties"]!["translations"]);
        Assert.Null(body["thinking"]);
        Assert.Null(body["temperature"]);
    }

    [Fact]
    public async Task AnthropicWithoutFallbackSendsNoBeta()
    {
        _server.Respond = _ => """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5",
             "content":[{"type":"text","text":"{\"translations\":[]}"}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
            """;
        var config = new PluginConfiguration { AnthropicApiKey = "k", AnthropicBaseUrl = _server.Url, AnthropicServerFallback = false };

        using var translator = new AnthropicTranslator();
        await translator.CompleteAsync(config, "S", "U", CancellationToken.None);

        var request = Assert.Single(_server.Requests);
        Assert.False(request.Headers.ContainsKey("anthropic-beta"));
        Assert.Null(JsonNode.Parse(request.Body)!["fallbacks"]);
    }

    [Fact]
    public async Task AnthropicRefusalBecomesTranslatorException()
    {
        _server.Respond = _ => """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],
             "stop_reason":"refusal","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}
            """;
        var config = new PluginConfiguration { AnthropicApiKey = "k", AnthropicBaseUrl = _server.Url };

        using var translator = new AnthropicTranslator();
        var ex = await Assert.ThrowsAsync<TranslatorException>(() => translator.CompleteAsync(config, "S", "U", CancellationToken.None));
        Assert.Contains("refusal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatibleSendsChatCompletion()
    {
        _server.Respond = _ => """{"choices":[{"message":{"role":"assistant","content":"{\"translations\":[{\"id\":0,\"text\":\"Bonjour\"}]}"}}]}""";
        var config = new PluginConfiguration
        {
            Provider = AiProviderType.OpenAiCompatible,
            OpenAiBaseUrl = _server.Url + "/v1/",
            OpenAiApiKey = "sk-test",
            OpenAiModel = "local-model",
            OpenAiTemperature = "0,3",
            OpenAiJsonMode = true
        };

        var translator = new OpenAiCompatibleTranslator(new SimpleHttpClientFactory());
        var answer = await translator.CompleteAsync(config, "SYS", "USR", CancellationToken.None);

        Assert.Contains("Bonjour", answer, StringComparison.Ordinal);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("Bearer sk-test", request.Headers["Authorization"]);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("local-model", body["model"]!.GetValue<string>());
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>(), 3);
        Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAiCompatibleReportsHttpErrors()
    {
        _server.Status = 401;
        _server.Respond = _ => """{"error":{"message":"bad key"}}""";
        var config = new PluginConfiguration { Provider = AiProviderType.OpenAiCompatible, OpenAiBaseUrl = _server.Url + "/v1", OpenAiModel = "m" };

        var translator = new OpenAiCompatibleTranslator(new SimpleHttpClientFactory());
        var ex = await Assert.ThrowsAsync<TranslatorException>(() => translator.CompleteAsync(config, "S", "U", CancellationToken.None));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bad key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslationServiceTranslatesBatchesWithContextRetriesAndCache()
    {
        // The fake model answers each line with "[DE] <text>" but drops one line the first time.
        var dropped = false;
        _server.Respond = body =>
        {
            var system = JsonNode.Parse(body)!["messages"]![1]!["content"]!.GetValue<string>();
            var payload = JsonNode.Parse(system)!;
            var lines = payload["lines"]!.AsArray();
            var translations = new JsonArray();
            foreach (var line in lines)
            {
                var id = line!["id"]!.GetValue<int>();
                var text = line["text"]!.GetValue<string>();
                if (!dropped && text == "line 3")
                {
                    dropped = true;
                    continue;
                }

                translations.Add(new JsonObject { ["id"] = id, ["text"] = "[DE] " + text });
            }

            var content = new JsonObject { ["translations"] = translations }.ToJsonString();
            return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) }.ToJsonString();
        };

        var service = CreateService(new PluginConfiguration
        {
            Provider = AiProviderType.OpenAiCompatible,
            OpenAiBaseUrl = _server.Url + "/v1",
            OpenAiModel = "m",
            ContextLines = 2
        });

        var cues = Enumerable.Range(0, 12).Select(i => new SubtitleCue(i, i * 1000, (i * 1000) + 900, "line " + i)).ToList();
        var batches = TranslationService.BuildBatches(cues, 5);
        var language = new TargetLanguage("test-" + Guid.NewGuid().ToString("N"), "German", "Deutsch", "ger");

        var first = await service.TranslateBatchAsync(cues, batches[0], language, CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => "[DE] line " + i), first.Select(c => c.Text));
        Assert.Equal(2, _server.Requests.Count); // one retry for the dropped line

        var second = await service.TranslateBatchAsync(cues, batches[1], language, CancellationToken.None);
        Assert.Equal("[DE] line 5", second[0].Text);
        var context = JsonNode.Parse(JsonNode.Parse(_server.Requests[^1].Body)!["messages"]![1]!["content"]!.GetValue<string>())!["context"]!.AsArray();
        Assert.Equal(new[] { "line 3", "line 4" }, context.Select(n => n!.GetValue<string>()));

        var before = _server.Requests.Count;
        var again = await service.TranslateBatchAsync(cues, batches[0], language, CancellationToken.None);
        Assert.Equal(first.Select(c => c.Text), again.Select(c => c.Text));
        Assert.Equal(before, _server.Requests.Count); // served from cache
    }

    private static TranslationService CreateService(PluginConfiguration config)
    {
        // Plugin.Instance is null in tests, so the service falls back to a default config.
        // Inject the test config through the backing field.
        ConfigOverride.Set(config);
        var factory = new TranslatorFactory(new OpenAiCompatibleTranslator(new SimpleHttpClientFactory()), new AnthropicTranslator());
        return new TranslationService(factory, new TranslationCache(NullLogger<TranslationCache>.Instance), NullLocalization.Create(), NullLogger<TranslationService>.Instance);
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public FakeServer()
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public int Status { get; set; } = 200;

        public Func<string, string> Respond { get; set; } = _ => "{}";

        public List<(string Path, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var headers = ctx.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => ctx.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                lock (Requests)
                {
                    Requests.Add((ctx.Request.Url!.AbsolutePath, headers, body));
                }

                var bytes = Encoding.UTF8.GetBytes(Respond(body));
                ctx.Response.StatusCode = Status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers["request-id"] = "req_test";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }
    }
}

/// <summary>
/// Lets tests supply a configuration while <see cref="Plugin.Instance"/> is null.
/// </summary>
internal static class ConfigOverride
{
    public static void Set(PluginConfiguration config) => TestHooks.ConfigurationOverride = config;
}

/// <summary>
/// ILocalizationManager stub that knows no languages.
/// </summary>
public class NullLocalization : DispatchProxy
{
    public static ILocalizationManager Create() => DispatchProxy.Create<ILocalizationManager, NullLocalization>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod?.ReturnType;
        return type is null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
    }
}
