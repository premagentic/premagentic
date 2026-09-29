using System.Text.Json;
using Premagentic.Api.Callers;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.McpServer;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Premagentic.Tests;

public sealed partial class McpHttpTests
{
    private const string HouseRules = "Cite the file and heading of every passage.\nNever guess at a path.";

    /// <summary>Sets the instructions, then reads them as the server does when it starts.</summary>
    private static async Task SetInstructionsAndRestartAsync(ApiWorld w, string text)
    {
        await new TuningSettingsStore(w.Db, w.Tenant).SetAsync(
            McpSettings.Instructions, JsonSerializer.SerializeToElement(text), new AdminActor("cli", "an-admin"));
        await McpInstructions.ApplyAsync(w.Host.Services, NullLogger.Instance);
    }

    [Fact]
    public async Task An_assistant_is_given_the_instructions_at_connect_and_can_read_them_in_full()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await SetInstructionsAndRestartAsync(w, HouseRules);
        await using var client = await ConnectAsync(w, w.BotToken);

        Assert.Equal(HouseRules, client.ServerInstructions);
        var resource = Assert.Single(await client.ListResourcesAsync());
        Assert.Equal(McpInstructions.ResourceUri, resource.Uri);
        var read = await client.ReadResourceAsync(McpInstructions.ResourceUri);
        Assert.Equal(HouseRules, Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text);
    }

    [Fact]
    public async Task With_nothing_set_an_assistant_is_told_nothing_and_there_is_no_resource()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await using var client = await ConnectAsync(w, w.BotToken);

        Assert.Null(client.ServerInstructions);
        Assert.Empty(await client.ListResourcesAsync());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync(McpInstructions.ResourceUri).AsTask());
    }

    [Fact]
    public async Task The_stdio_bridge_passes_the_instructions_and_only_that_resource_through()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await SetInstructionsAndRestartAsync(w, HouseRules);
        await using var bridge = new ApiBridge(new Uri("https://localhost"), w.AssistantToken, w.Host.Client());

        Assert.Equal(HouseRules, await bridge.InstructionsAsync());
        Assert.Equal([ApiBridge.InstructionsUri], (await bridge.ListResourcesAsync()).Resources.Select(r => r.Uri));
        var read = await bridge.ReadResourceAsync(new ReadResourceRequestParams { Uri = ApiBridge.InstructionsUri });
        Assert.Equal(HouseRules, Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text);
        await Assert.ThrowsAsync<McpProtocolException>(() =>
            bridge.ReadResourceAsync(new ReadResourceRequestParams { Uri = "file:///etc/passwd" }));
    }
}
