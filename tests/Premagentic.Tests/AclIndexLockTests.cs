using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The advisory locks between ingest and folder-rule changes, in both
/// directions, and the one run per source.
/// Requires a running Docker daemon.
/// </summary>
public sealed class AclIndexLockTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant, PrincipalNames Names)> NewAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        return (db, tenant, new PrincipalNames(new IdentityStore(db, tenant)));
    }

    private static async Task<FolderRule> EveryoneRuleAsync(PrincipalNames names, string prefix) =>
        new("filesystem", prefix, await names.ToAclSetAsync(["allow everyone"]));

    [Fact]
    public async Task A_second_run_of_the_same_source_is_refused_while_the_first_holds_it()
    {
        var (db, tenant, _) = await NewAsync();
        await using var _ = db;

        await using (await IndexLocks.ForIngestAsync(db, tenant, "filesystem", "hr"))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => IndexLocks.ForIngestAsync(db, tenant, "filesystem", "hr"));
            Assert.Contains("filesystem:hr is running", refused.Message);

            // Another prefix, another connector, and another tenant each run.
            await using (await IndexLocks.ForIngestAsync(db, tenant, "filesystem", "open")) { }
            await using (await IndexLocks.ForIngestAsync(db, tenant, "other", "hr")) { }
            await using (await IndexLocks.ForIngestAsync(db, await db.EnsureTenantAsync("u", "U"), "filesystem", "hr")) { }
        }

        // Released with the first run: the same source runs again.
        await using (await IndexLocks.ForIngestAsync(db, tenant, "filesystem", "hr")) { }
    }

    [Fact]
    public async Task A_rule_change_waits_for_a_running_ingest_and_refuses_when_it_does_not_finish()
    {
        var (db, tenant, names) = await NewAsync();
        await using var _ = db;
        var rules = new AclStore(db, tenant) { RuleChangeWait = TimeSpan.FromMilliseconds(300) };

        await using (await IndexLocks.ForIngestAsync(db, tenant, "filesystem", "hr"))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await rules.SetRuleAsync(await EveryoneRuleAsync(names, "hr")));
            Assert.Contains("while an ingest is running", refused.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rules.RemoveRuleAsync("filesystem", "hr"));
            Assert.Empty(await rules.ListRulesAsync());

            // Another tenant's ingest does not hold this tenant's rules.
            await using (await IndexLocks.ForIngestAsync(db, await db.EnsureTenantAsync("u", "U"), "filesystem", "x")) { }
        }

        // The control: with the run finished, the same change goes through.
        await rules.SetRuleAsync(await EveryoneRuleAsync(names, "hr"));
        Assert.Single(await rules.ListRulesAsync());
    }

    [Fact]
    public async Task An_ingest_waits_for_a_rule_change_in_progress_to_commit()
    {
        var (db, tenant, names) = await NewAsync();
        await using var _ = db;

        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await new AclStore(db, tenant, tx).SetRuleAsync(await EveryoneRuleAsync(names, "hr"));

        var ingest = IndexLocks.ForIngestAsync(db, tenant, "filesystem", "hr");
        var first = await Task.WhenAny(ingest, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(ingest, first);

        await tx.CommitAsync();
        await using var lease = await ingest.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_recorded_run_of_a_source_already_running_is_refused_and_records_nothing()
    {
        var (db, tenant, names) = await NewAsync();
        await using var _ = db;
        await new AclStore(db, tenant).SetRuleAsync(await EveryoneRuleAsync(names, "notes"));
        var folder = Directory.CreateTempSubdirectory("premagentic-lock-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "a.md"), "## A\nthe lock note");
            var source = new FileSystemSource(folder.FullName, DocumentAccess.FolderRules, "notes");
            var runs = new IngestRuns(db, tenant);
            var pipeline = new IngestPipeline(db, new SeededEmbeddingProvider());

            await using (await IndexLocks.ForIngestAsync(db, tenant, source.Name, source.PathPrefix))
                await Assert.ThrowsAsync<InvalidOperationException>(() => runs.RunAsync(pipeline, source, sourceId: null));
            Assert.Empty(await runs.ListAsync());

            // The control: once the other run is done, this one runs and is recorded.
            var result = await runs.RunAsync(pipeline, source, sourceId: null);
            Assert.Equal(1, result.Summary.Ingested);
            Assert.Single(await runs.ListAsync());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
