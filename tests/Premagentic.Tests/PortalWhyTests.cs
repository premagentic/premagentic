using Premagentic.Core;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The "why" tool must say what the gates do. For every caller and document in
/// the test world, the explanation's verdict is held to what a section fetch
/// by that caller, under that caller's policy, actually returns. A disagreement
/// is a tool that misleads an administrator, so it fails here.
/// Requires a running Docker daemon.
/// </summary>
public sealed class PortalWhyTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly string[] Paths =
    [
        ApiWorld.Handbook, ApiWorld.Plan, ApiWorld.AuditLog, ApiWorld.Pay, ApiWorld.Draft,
        "okf/care/pest-scouting.md", "okf/care/humidity.md", "okf/care/watering.md",
        "okf/supplies/fertilizer-prices.md", "okf/procedures/old-closing.md", "okf/misc/broken.md",
    ];

    [Fact]
    public async Task Every_explanation_agrees_with_what_the_gates_return()
    {
        await using var world = await ApiWorld.NewAsync(server, okfBundle: true);
        var identity = world.Identity;
        var catalog = new DocumentCatalog(world.Db, world.Tenant);
        var matcher = await new AclStore(world.Db, world.Tenant).LoadMatcherAsync();
        var sections = new SectionFetcher(world.Db);
        var now = world.Clock.GetUtcNow();

        var callers = new List<(string Name, Caller Caller)>();
        foreach (var user in new[] { world.Alice, world.Bob, world.Carol, world.Eve, world.Dan })
            callers.Add(("user " + user.Name, await CallerAccess.ResolveUserAsync(identity, user.Id)));
        callers.Add(("agent assistant", await CallerAccess.ResolveAgentTokenAsync(identity, world.AssistantToken)));
        callers.Add(("agent report-bot", await CallerAccess.ResolveAgentTokenAsync(identity, world.BotToken)));

        var readable = 0;
        var held = 0;
        foreach (var (name, caller) in callers)
        {
            var policy = await CallerPolicy.TrustAsync(world.Db, world.Tenant, caller);
            foreach (var path in Paths)
                foreach (var historical in new[] { false, true })
                {
                    var document = await catalog.FindAsync(path);
                    Assert.NotNull(document);
                    var why = AccessExplainer.Explain(document, matcher.Match(document.SourceName ?? "", path), caller.Scope, policy, now, historical);

                    var fetched = await sections.GetAsync(world.Tenant,
                        new SearchOptions(caller.Scope, IncludeHistorical: historical, Trust: policy, AsOf: now), path, heading: null);
                    var served = fetched is { LifecycleGated: false, Chunks.Count: > 0 };

                    Assert.True(why.Readable == served,
                        $"{name} on {path} (historical {historical}): the tool says {(why.Readable ? "readable" : "not readable")}, the gates {(served ? "served it" : "did not")}. " +
                        $"Access: {why.Access.Reason} Trust: {why.Trust.Reason} Freshness: {why.Freshness.Reason} Lifecycle: {why.Lifecycle.Reason}");
                    if (served) readable++; else held++;
                }
        }

        // The matrix covers both outcomes, many times over.
        Assert.True(readable > 10 && held > 10, $"readable {readable}, held back {held}");
    }
}
