using System.Net;
using System.Text;
using System.Text.Json;
using Premagentic.Api.Callers;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The bound on a request to /mcp: a body at it is served, one byte over it
/// is refused with 413 and a JSON-RPC error whether its length was declared
/// or not, and the bound is the catalog's setting as the server read it at
/// start. Requires a running Docker daemon.
/// </summary>
public sealed class McpRequestLimitTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    /// <summary>A tools/list request padded with trailing white space, which JSON allows, to exactly <paramref name="bytes"/> bytes.</summary>
    private static byte[] Body(int bytes)
    {
        var body = Encoding.UTF8.GetBytes(ToolsList);
        return body.Concat(Enumerable.Repeat((byte)' ', bytes - body.Length)).ToArray();
    }

    /// <summary>A stream that cannot say its length, so the request goes without Content-Length.</summary>
    private sealed class UnknownLength(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hands a request on untouched. A client built with one has no redirect
    /// handler, which buffers a request's body to send it again and so gives
    /// it a length.
    /// </summary>
    private sealed class PassThrough : DelegatingHandler;

    private static async Task<(HttpStatusCode Status, string? Type, string Body)> PostAsync(ApiWorld w, byte[] body, bool declared = true)
    {
        using var http = declared ? w.Host.Client() : w.Host.Client(true, new PassThrough());
        HttpContent content = declared ? new ByteArrayContent(body) : new StreamContent(new UnknownLength(body));
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + w.BotToken);
        using var response = await http.SendAsync(request);
        Assert.Equal(declared, request.Content.Headers.ContentLength is not null);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The JSON-RPC message in an answer, whether it came as JSON or as one server-sent event.</summary>
    private static JsonElement Message(string body)
    {
        var json = body.StartsWith("event:", StringComparison.Ordinal)
            ? body.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]
            : body;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void AssertServed((HttpStatusCode Status, string? Type, string Body) answer)
    {
        Assert.True(answer.Status == HttpStatusCode.OK, $"answered {answer.Status}: {answer.Body[..Math.Min(answer.Body.Length, 300)]}");
        Assert.True(Message(answer.Body).GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
    }

    private static void AssertRefused((HttpStatusCode Status, string? Type, string Body) answer, int bound)
    {
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, answer.Status);
        Assert.Equal("application/json", answer.Type);
        var message = Message(answer.Body);
        Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("id").ValueKind);
        Assert.Equal(-32600, message.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal($"Content Too Large: the request body is over this server's limit of {bound} bytes.",
            message.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_body_at_the_default_bound_is_served_and_one_byte_over_it_is_refused_with_413_and_a_json_rpc_error()
    {
        await using var w = await ApiWorld.NewAsync(server);
        const int Bound = McpSettings.MaxRequestBytesDefault;

        AssertServed(await PostAsync(w, Body(Bound)));
        AssertRefused(await PostAsync(w, Body(Bound + 1)), Bound);
    }

    [Fact]
    public async Task A_body_of_unknown_length_is_bounded_the_same_way()
    {
        await using var w = await ApiWorld.NewAsync(server);
        const int Bound = McpSettings.MaxRequestBytesDefault;

        AssertServed(await PostAsync(w, Body(Bound), declared: false));
        AssertRefused(await PostAsync(w, Body(Bound + 1), declared: false), Bound);
    }

    [Fact]
    public async Task The_bound_is_the_catalog_setting_as_the_server_read_it_at_start()
    {
        var definition = SettingsCatalog.Find(McpSettings.MaxRequestBytes);
        Assert.NotNull(definition);
        Assert.Equal(McpSettings.MaxRequestBytesDefault, definition.Default!.Value.GetInt32());
        var logs = new List<string>();
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions { Logs = logs }, beforeStart: (db, tenant) =>
            new SettingsStore(db, tenant).SetAsync(McpSettings.MaxRequestBytes, JsonSerializer.SerializeToElement(20_000)));

        AssertServed(await PostAsync(w, Body(20_000)));
        AssertRefused(await PostAsync(w, Body(20_001)), 20_000);
        AssertRefused(await PostAsync(w, Body(20_001), declared: false), 20_000);
        lock (logs)
        {
            Assert.Contains(logs, l => l.Contains("A request to /mcp may carry at most 20000 bytes (mcp.max_request_bytes).", StringComparison.Ordinal));
            Assert.Contains(logs, l => l.Contains("Refused POST /mcp: the body is over mcp.max_request_bytes, 20000 bytes.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_stored_bound_the_setting_does_not_take_leaves_the_default_with_one_warning()
    {
        var logs = new List<string>();
        await using var w = await ApiWorld.NewAsync(server, new ApiHostOptions { Logs = logs }, beforeStart: (db, tenant) =>
            new SettingsStore(db, tenant).SetAsync(McpSettings.MaxRequestBytes, JsonSerializer.SerializeToElement(100)));

        AssertServed(await PostAsync(w, Body(McpSettings.MaxRequestBytesDefault)));
        AssertRefused(await PostAsync(w, Body(McpSettings.MaxRequestBytesDefault + 1)), McpSettings.MaxRequestBytesDefault);
        lock (logs)
            Assert.Single(logs, l => l.StartsWith("Warning", StringComparison.Ordinal)
                                     && l.Contains($"{McpSettings.MaxRequestBytes} takes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("16383", false)]
    [InlineData("16384", true)]
    [InlineData("30000000", true)]
    [InlineData("30000001", false)]
    [InlineData("\"262144\"", false)]
    [InlineData("262144.5", false)]
    public void The_setting_takes_a_whole_number_of_bytes_within_its_range(string json, bool taken)
    {
        using var value = JsonDocument.Parse(json);
        Assert.Equal(taken, McpSettings.MaxRequestBytesProblem(value.RootElement) is null);
    }

    [Fact]
    public void The_endpoint_carries_the_bound_for_the_web_servers_own_body_size_feature()
    {
        var limit = new McpRequestSizeLimit(20_000);
        Assert.Equal(20_000, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
        Assert.False(McpRequestSizeLimit.Over(20_000, 20_000));
        Assert.True(McpRequestSizeLimit.Over(20_001, 20_000));
    }
}
