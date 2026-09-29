using Premagentic.Core.Admin;
using Premagentic.Portal.Pages;

namespace Premagentic.Tests;

/// <summary>
/// The health page names the embedding model this process loaded, with the
/// folder a local one came from and its revision. Requires a running Docker
/// daemon for the page test.
/// </summary>
public sealed class PortalHealthModelTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public void A_local_model_shows_its_name_folder_and_revision_and_a_missing_part_says_so()
    {
        var local = HealthPages.Model(new LoadedModel("all-MiniLM-L6-v2", @"C:\Invented\models\minilm", "c9745ed1")).ToString();
        Assert.Contains("all-MiniLM-L6-v2", local);
        Assert.Contains(@"from <code>C:\Invented\models\minilm</code>", local);
        Assert.Contains("revision c9745ed1", local);

        var bare = HealthPages.Model(new LoadedModel("hash", null, null)).ToString();
        Assert.Contains("no folder: this provider runs without one", bare);
        Assert.Contains("revision not recorded", bare);

        Assert.Contains("not known", HealthPages.Model(null).ToString());
    }

    [Fact]
    public async Task The_health_page_names_the_model_this_process_loaded()
    {
        await using var p = await PortalWorld.NewAsync(server);

        var page = await p.TextAsync(await p.GetAsync("/portal/health", p.Auditor));

        Assert.Contains("<dt>Model</dt>", page);
        Assert.DoesNotContain("not known: this process registered no embedding model", page);
        // The test host embeds with its seeded provider, which has no folder.
        Assert.Contains("no folder: this provider runs without one", page);
    }
}
