using Premagentic.Core;

namespace Premagentic.Tests;

public sealed partial class McpHttpTests
{
    [Fact]
    public async Task The_server_names_the_build_it_is_rather_than_a_typed_version()
    {
        await using var w = await ApiWorld.NewAsync(server);
        await using var client = await ConnectAsync(w, w.BotToken);

        Assert.Equal("premagentic", client.ServerInfo.Name);
        Assert.Equal(BuildVersion.Informational, client.ServerInfo.Version);
        Assert.StartsWith(typeof(BuildVersion).Assembly.GetName().Version!.ToString(3), client.ServerInfo.Version);
    }
}
