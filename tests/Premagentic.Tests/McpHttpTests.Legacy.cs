using ModelContextProtocol;
using ModelContextProtocol.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Premagentic.Tests;

public sealed partial class McpHttpTests
{
    /// <summary>
    /// What this stateless 2026-07-28 server does for a client that still
    /// speaks 2025-11-25 and opens with the initialize handshake, which
    /// 2026-07-28 removed. Pinned from a run, so a change in the server's
    /// answer, or in the SDK's, fails here by name.
    /// </summary>
    [Fact]
    public async Task A_client_pinned_to_2025_11_25_is_answered_at_initialize_and_given_the_instructions()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await SetInstructionsAndRestartAsync(w, HouseRules);
        var recorder = new RecordingHandler();

        var http = w.Host.Client(https: true, recorder);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("https://localhost/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + w.BotToken },
            },
            http, NullLoggerFactory.Instance, ownsHttpClient: true);

        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(transport,
                new McpClientOptions { ProtocolVersion = "2025-11-25" }, NullLoggerFactory.Instance);
        }
        catch (Exception ex)
        {
            Assert.Fail($"The server refused a 2025-11-25 initialize: {ex.GetType().Name} " +
                $"{(ex as McpProtocolException)?.ErrorCode} {ex.Message} (methods sent: {string.Join(", ", recorder.RequestMethods)})");
            return;
        }

        await using (client)
        {
            Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
            Assert.Equal(HouseRules, client.ServerInstructions);
            Assert.Contains("initialize", recorder.RequestMethods);
            Assert.NotEmpty(await client.ListToolsAsync());
        }
    }
}
