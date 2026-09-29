using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Mcp;

namespace Premagentic.Tests;

/// <summary>
/// What an agent receives when a call to /mcp goes wrong: a tool that throws,
/// a method nobody serves, a body that is not JSON-RPC, and a body larger than
/// the server takes. Each answer holds no stack frame, no file path, no
/// assembly version, no type name and no connection string, and the operator's
/// log keeps the real exception. Requires a running Docker daemon.
/// </summary>
public sealed class McpErrorTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    // Planted in the exception the tool throws, so an answer that carried any
    // of it would be seen.
    private const string Planted = "Host=db.internal;Password=zq-planted-secret at C:\\srv\\zq-models\\model.onnx";

    private static readonly string[] Leaks =
    [
        "   at ", ".cs:line", ":\\", "C:/", "/srv/", "Version=", "PublicKeyToken", "Culture=",
        "Exception", "System.", "Premagentic.", "ModelContextProtocol.", "Npgsql", "Host=", "Password=", "zq-",
    ];

    /// <summary>An embedder that fails the way a broken model or database does, with its own details in the message.</summary>
    private sealed class FailingEmbedder : IEmbeddingProvider
    {
        public string Name => "failing";
        public int Dimensions => 64;
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException(Planted);
    }

    /// <summary>Records each log entry's exception in full, which the host's other test log leaves out.</summary>
    private sealed class ExceptionLog(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);
        public void Dispose() { }

        private sealed class Logger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception is not null) lock (lines) lines.Add(exception.ToString());
            }
        }
    }

    private async Task<(ApiTestHost Host, string Bot, List<string> Exceptions)> StartAsync()
    {
        var w = await ApiWorld.NewAsync(server);
        var (connection, clock, bot) = (w.ConnectionString, w.Clock, w.BotToken);
        await w.DisposeAsync();
        var exceptions = new List<string>();
        var host = ApiTestHost.Start(connection, clock, new FailingEmbedder(),
            new ApiHostOptions { Services = services => services.AddSingleton<ILoggerProvider>(new ExceptionLog(exceptions)) });
        return (host, bot, exceptions);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(ApiTestHost host, string bot, string body)
    {
        using var http = host.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bot);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The JSON-RPC message in an answer, whether it came as JSON or as one server-sent event.</summary>
    private static JsonElement Message(string body)
    {
        var json = body.StartsWith("event:", StringComparison.Ordinal)
            ? body.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]
            : body;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void AssertNothingOfTheServer(string body)
    {
        foreach (var leak in Leaks)
            Assert.False(body.Contains(leak, StringComparison.Ordinal), $"the answer holds '{leak}': {body[..Math.Min(body.Length, 600)]}");
    }

    private static string Call(string tool, string arguments, int id = 1) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""";

    [Fact]
    public async Task A_tool_that_throws_answers_a_fixed_sentence_and_the_log_keeps_the_exception()
    {
        var (host, bot, exceptions) = await StartAsync();
        await using var _ = host;

        var (status, body) = await PostAsync(host, bot, Call(McpToolDescriptions.SearchKnowledge, """{"query":"rota"}"""));

        Assert.Equal(HttpStatusCode.OK, status);
        var result = Message(body).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        AssertNothingOfTheServer(body);
        string logged;
        lock (exceptions) logged = string.Join("\n", exceptions);
        Assert.Contains("zq-planted-secret", logged, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_method_nobody_serves_answers_the_specification_code_and_nothing_else()
    {
        var (host, bot, _) = await StartAsync();
        await using var __ = host;

        var (_, body) = await PostAsync(host, bot, """{"jsonrpc":"2.0","id":2,"method":"prem/nothing","params":{}}""");

        Assert.Equal(-32601, Message(body).GetProperty("error").GetProperty("code").GetInt32());
        AssertNothingOfTheServer(body);
    }

    [Fact]
    public async Task A_body_that_is_not_json_rpc_answers_a_request_error_and_nothing_else()
    {
        var (host, bot, _) = await StartAsync();
        await using var __ = host;

        var (status, body) = await PostAsync(host, bot, """{"jsonrpc":"2.0","id":3,"method":"tools/list" """);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(Message(body).GetProperty("error").GetProperty("code").GetInt32(), new[] { -32600, -32700 });
        AssertNothingOfTheServer(body);
    }

    // The server's own bound refuses it before the tool sees it
    // (McpRequestLimitTests); what the agent reads says nothing of the server.
    [Fact]
    public async Task A_body_larger_than_the_server_takes_answers_nothing_of_the_server()
    {
        var (host, bot, _) = await StartAsync();
        await using var __ = host;
        var query = JsonSerializer.Serialize(new string('q', Premagentic.Core.Identity.McpSettings.MaxRequestBytesDefault));

        var (status, body) = await PostAsync(host, bot, Call(McpToolDescriptions.SearchKnowledge, $$"""{"query":{{query}}}"""));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal(-32600, Message(body).GetProperty("error").GetProperty("code").GetInt32());
        AssertNothingOfTheServer(body);
    }
}
