using Premagentic.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// The stdio MCP server, as a bridge to the Premagentic API: it presents one
// registered agent's token to the API's /mcp and passes the read-only tools
// through. It holds no database credentials. See ApiBridge.
var builder = Host.CreateApplicationBuilder(args);

// stdio transport: stdout carries the protocol, so all logging goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// A configuration the bridge cannot start with ends it with the sentence that
// says what to set and exit code 2, as the API and the CLI do.
ApiBridge bridge;
try
{
    bridge = ApiBridge.FromEnvironment(Environment.GetEnvironmentVariable);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Premagentic MCP bridge cannot start: {ex.Message}");
    return 2;
}

await using (bridge)
{
    // What the API tells an assistant at connect is given to this bridge's
    // client too, so it has to be known before the client connects. An API
    // that cannot be reached yet leaves the bridge serving without it, as
    // before; the tools connect when they are first used.
    string? instructions = null;
    try
    {
        using var reach = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        instructions = await bridge.InstructionsAsync(reach.Token);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"Premagentic MCP bridge: the API could not be reached at start, so no instructions are passed on: {ex.Message}");
    }

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "premagentic", Version = Premagentic.McpServer.BridgeVersion.Informational };
            options.ServerInstructions = instructions;
        })
        .WithStdioServerTransport()
        .WithListToolsHandler(async (request, ct) => await bridge.ListToolsAsync(ct))
        .WithCallToolHandler(async (request, ct) => await bridge.CallToolAsync(request.Params, ct))
        .WithListResourcesHandler(async (request, ct) => await bridge.ListResourcesAsync(ct))
        .WithReadResourceHandler(async (request, ct) => await bridge.ReadResourceAsync(request.Params, ct));

    await builder.Build().RunAsync();
}
return 0;
