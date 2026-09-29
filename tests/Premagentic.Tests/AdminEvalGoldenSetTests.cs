using Premagentic.Cli.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The README quick start end to end on the database, down to the sample golden
/// set: the two groups, the two folders ingested under their rules, and every
/// golden case passing. The hashing embedder keeps it deterministic and needs no
/// model download. Requires a running Docker daemon.
/// </summary>
public sealed class AdminEvalGoldenSetTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Premagentic.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No repository root above the test output.");
    }

    /// <summary>
    /// <c>prem groups add hr</c>, <c>prem groups add engineering</c>, then the
    /// two quick start ingests.
    /// </summary>
    private async Task<(PremagenticDatabase Db, Guid Tenant, EvalRunner Runner)> QuickStartAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("default", "Premagentic deployment");
        Assert.Equal(0, await AdminCommands.RunAsync(["groups", "add", "hr"], db, tenant));
        Assert.Equal(0, await AdminCommands.RunAsync(["groups", "add", "engineering"], db, tenant));

        var embedder = new HashEmbeddingProvider();
        var names = new PrincipalNames(new IdentityStore(db, tenant));
        var rules = new AclStore(db, tenant);
        await IngestAsync(db, tenant, embedder, names, rules, "open", "--public");
        await IngestAsync(db, tenant, embedder, names, rules, "hr", "--principals", "group:hr");

        return (db, tenant, new EvalRunner(new HybridSearch(db, embedder), tenant, names));
    }

    /// <summary>
    /// What <c>prem ingest ./sample-docs/&lt;folder&gt; &lt;flags&gt; --prefix &lt;folder&gt;</c>
    /// does: set the folder's rule from the flags, then ingest it under the rules.
    /// </summary>
    private static async Task IngestAsync(
        PremagenticDatabase db, Guid tenant, IEmbeddingProvider embedder, PrincipalNames names, AclStore rules,
        string folder, params string[] flags)
    {
        var source = new FileSystemSource(Path.Combine(RepoRoot(), "sample-docs", folder), DocumentAccess.FolderRules, folder);
        await AdminCommands.SetRuleAsync(db, tenant, source.Name, source.PathPrefix, AdminCommands.RuleEntries(flags)!);
        var summary = await new IngestPipeline(db, embedder).RunAsync(tenant, source);
        Assert.True(summary.Ingested > 0);
        Assert.Equal(0, summary.DeniedToEveryone);
    }

    private static async Task<(int Exit, string Report)> RunAsync(EvalRunner runner, string goldenPath)
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"prem-eval-{Guid.NewGuid():N}.md");
        try
        {
            var exit = await runner.RunAsync(goldenPath, reportPath);
            return (exit, await File.ReadAllTextAsync(reportPath));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task Every_case_of_the_sample_golden_set_passes_after_the_quick_start()
    {
        var (db, _, runner) = await QuickStartAsync();
        await using var _ = db;

        var (exit, report) = await RunAsync(runner, Path.Combine(RepoRoot(), "samples", "profiles", "starter", "golden-set.json"));

        Assert.True(exit == 0, report);
        Assert.Contains("**Result: 5/5 passed.", report);
        // q04 is the control that proves q03's denial is the gate at work: HR
        // reaches the salary bands through the name it was written with.
        Assert.Contains("## q04, PASS (access-allowed)", report);
        Assert.Contains("## q03, PASS (access-denied)", report);
    }

    [Fact]
    public async Task An_as_name_that_does_not_resolve_fails_its_case_without_running_it()
    {
        var (db, tenant, runner) = await QuickStartAsync();
        await using var _ = db;

        // Run as a caller holding nothing, this denial case would pass: nothing
        // is visible, so no answer and no forbidden path.
        var golden = Path.Combine(Path.GetTempPath(), $"prem-golden-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(golden, """
            [
              {
                "id": "x1",
                "question": "What is the compensation review cycle for band four engineers?",
                "category": "access-denied",
                "expectedSourcePaths": [],
                "expectedHeading": null,
                "forbiddenSourcePaths": [ "hr/salary-bands.md" ],
                "archiveAllowed": false,
                "expectNoAnswer": true,
                "notes": "Names a group that does not exist.",
                "as": [ "group:contractors" ]
              }
            ]
            """);
        try
        {
            var (exit, report) = await RunAsync(runner, golden);

            Assert.Equal(1, exit);
            Assert.Contains("## x1, FAIL (access-denied)", report);
            Assert.Contains("'as' name 'group:contractors' does not resolve: There is no group named 'contractors'.", report);
            Assert.Contains("**Result: 0/1 passed.", report);
        }
        finally
        {
            File.Delete(golden);
        }

        // Not run means not searched: no retrieval event was written for it.
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM prem_config.retrieval_event WHERE tenant_id = @t AND access_label = 'eval:x1'");
        cmd.Parameters.AddWithValue("t", tenant);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }
}
