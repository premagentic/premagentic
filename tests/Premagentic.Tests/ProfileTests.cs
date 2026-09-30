using System.Security.AccessControl;
using System.Text.Json;
using Premagentic.Cli;
using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Okf;
using Premagentic.Core.Profiles;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The profile mechanism: a folder of plain files that configures a whole
/// deployment through the administrator paths, and can reach no further than
/// an administrator can. Every file and folder here is invented. Requires a
/// running Docker daemon.
/// </summary>
// GoldenSetRun's gate is one per process: never beside another class that runs it.
[Collection(GoldenSetRunGate.Name)]
public sealed class ProfileTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly AdminActor Tester = new("cli", "test-account");

    private sealed class Env(PremagenticDatabase db, Guid tenant) : IAsyncDisposable
    {
        public PremagenticDatabase Db => db;
        public Guid Tenant => tenant;
        public TuningSettingsStore Tuning { get; } = new(db, tenant);
        public TrustSettingsStore Trust { get; } = new(db, tenant);
        public SourceRegistry Sources { get; } = new(db, tenant);
        public AclStore Rules { get; } = new(db, tenant);
        public ChangeRecord Record { get; } = new(db, tenant);

        public Task<(ProfilePlan? Plan, IReadOnlyList<ProfileProblem> Problems)> PlanAsync(
            string folder, ChunkerRegistry? chunkers = null)
        {
            Assert.True(ProfileReader.TryRead(folder, out var profile, out var read), Problems(read));
            return ProfilePlanner.BuildAsync(profile!, db, tenant, GoldenSetFolder, chunkers);
        }

        /// <summary>Reads, plans and applies, asserting that each step was allowed to happen.</summary>
        public async Task<ProfileApplyReport> ApplyAsync(string folder, ChunkerRegistry? chunkers = null)
        {
            var (plan, problems) = await PlanAsync(folder, chunkers);
            Assert.True(plan is not null, Problems(problems));
            var report = await ProfileApply.RunAsync(plan!, db, tenant, Tester, chunkers);
            Assert.True(report.Complete, report.Problem);
            return report;
        }

        /// <summary>Every problem on one line, so a failing assertion says what was wrong.</summary>
        public static string Problems(IReadOnlyList<ProfileProblem> problems) =>
            problems.Count == 0 ? "no problems" : string.Join(" | ", problems);

        public string GoldenSetFolder { get; } = Directory.CreateTempSubdirectory("premagentic-golden-").FullName;

        public async Task<IReadOnlyList<string>> KindsAsync() =>
            [.. (await Record.ListAsync(200)).Select(e => e.Kind)];

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private async Task<Env> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return new Env(db, await db.EnsureTenantAsync("t", "T"));
    }

    /// <summary>A profile folder holding the given files.</summary>
    private static string Profile(params (string File, string Text)[] files)
    {
        var root = Directory.CreateTempSubdirectory("premagentic-profile-").FullName;
        foreach (var (file, text) in files) File.WriteAllText(Path.Combine(root, file), text);
        return root;
    }

    private const string Manifest = """
        { "name": "office", "version": "1", "description": "An invented office." }
        """;

    private static string Sources(
        string folder, string rule = """["allow everyone"]""", string chunker = "markdown") => $$"""
        [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(folder)}}, "prefix": "handbook",
            "okfBundle": false, "undeclaredAsMachine": false, "chunker": "{{chunker}}", "rule": {{rule}} } ]
        """;

    private static string Corpus() =>
        SourcesTests.Folder(("rota.md", "# Rota\n\n## Weekend\nTwo people open the shop on Saturdays.\n"));

    [Fact]
    public async Task A_profile_applies_its_settings_sources_and_rules_through_the_administrator_paths()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40, "trust.stale": "hidden-from-everyone" }"""),
            ("sources.json", Sources(Corpus())));

        var report = await e.ApplyAsync(folder);

        Assert.Equal(5, report.Applied.Count);
        Assert.Equal(40, (await e.Tuning.ReadAsync(RetrievalSettings.RrfK)).Value.GetInt32());
        Assert.Equal("hidden-from-everyone", (await e.Trust.ReadAsync(TrustSettingsStore.Stale)).Value);

        var source = await e.Sources.FindAsync("handbook");
        Assert.Equal("handbook", source!.PathPrefix);
        Assert.Equal("markdown", source.Chunker);

        // The rule exactly as the profile stated it, and the folder held back
        // beside it: a source a profile adds does not leave the network unless
        // the profile says it may.
        var rule = Assert.Single(await e.Rules.ListRulesAsync());
        Assert.Equal(["allow everyone"], rule.Rule.Acl.Entries.Select(x => x.ToString()));
        Assert.Equal(SourceExposureState.NeverLeaves, await new SourceExposureStore(e.Db, e.Tenant).ReadAsync("handbook"));

        // The state row and the audit entry both exist, and the rule is
        // audited even though the rules store writes no entry of its own.
        var applied = await ProfileApply.LatestAsync(e.Db, e.Tenant);
        Assert.Equal(("office", "1"), (applied!.Name, applied.Version));
        Assert.Contains("profile.applied", await e.KindsAsync());
        Assert.Contains("rule.set", await e.KindsAsync());
        Assert.Contains(SourceExposureStore.ChangeKind, await e.KindsAsync());
        Assert.Contains("source.add", await e.KindsAsync());
        Assert.Contains("setting.set", await e.KindsAsync());
    }

    [Fact]
    public async Task A_profile_cannot_set_what_an_administrator_cannot_set()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40, "search.unrestricted": true }"""));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Problem.Contains("'search.unrestricted' is not a setting an administrator can change"));
        // Refused as a whole: the good key beside the bad one was not applied.
        Assert.Equal(60, (await e.Tuning.ReadAsync(RetrievalSettings.RrfK)).Value.GetInt32());
        Assert.Empty(await e.KindsAsync());
    }

    private const string FlowAddress = "https://prem.example.internal:8443";

    [Theory]
    [InlineData("""{ "mcp.oauth.enabled": true, "mcp.oauth.public_url": "https://prem.example.internal:8443", "mcp.oauth.code_seconds": 30 }""")]
    [InlineData("""{ "mcp.oauth.public_url": "https://prem.example.internal:8443", "mcp.oauth.code_seconds": 30, "mcp.oauth.enabled": true }""")]
    public async Task A_profile_that_turns_the_flow_on_and_sets_its_address_is_valid_whatever_the_order_of_its_lines(string settings)
    {
        await using var e = await EmptyAsync();
        var folder = Profile(("profile.json", Manifest), ("settings.json", settings));

        var (plan, problems) = await e.PlanAsync(folder);

        // Judged as the deployment would stand once applied, not key by key
        // against an empty database, where "enabled" alone has no address.
        Assert.True(plan is not null, Env.Problems(problems));
        var keys = plan!.Changes.OfType<SettingChange>().Select(c => c.Key).ToList();
        Assert.Equal([OAuthSettings.CodeSeconds, OAuthSettings.PublicUrl, OAuthSettings.Enabled], keys);
        Assert.Contains($"{OAuthSettings.Enabled}: false becomes true", plan.Changes.Select(c => c.ToString()));

        var report = await ProfileApply.RunAsync(plan, e.Db, e.Tenant, Tester);
        Assert.True(report.Complete, report.Problem);
        var stored = new SettingsStore(e.Db, e.Tenant);
        Assert.Equal(FlowAddress, (await stored.GetAsync(OAuthSettings.PublicUrl))!.Value.GetString());
        Assert.True((await stored.GetAsync(OAuthSettings.Enabled))!.Value.GetBoolean());
        Assert.Equal(3, (await e.KindsAsync()).Count(k => k == TuningSettingsStore.SetKind));
    }

    [Theory]
    [InlineData("""{ "retrieval.rrf_k": 40, "mcp.oauth.enabled": true }""")]
    [InlineData("""{ "retrieval.rrf_k": 40, "mcp.oauth.enabled": true, "mcp.oauth.public_url": null }""")]
    public async Task A_profile_that_turns_the_flow_on_without_an_address_is_refused_with_the_settings_verb_sentence(string settings)
    {
        await using var e = await EmptyAsync();
        var folder = Profile(("profile.json", Manifest), ("settings.json", settings));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.Null(plan);
        var problem = Assert.Single(problems);
        Assert.Equal(ProfileReader.SettingsFile, problem.File);
        Assert.Equal(OAuthSettingRules.EnableNeedsAddress, problem.Problem);
        Assert.StartsWith("mcp.oauth.enabled cannot be turned on until mcp.oauth.public_url holds", problem.Problem);
        // Refused whole: the good key beside it was not applied either.
        Assert.Equal(60, (await e.Tuning.ReadAsync(RetrievalSettings.RrfK)).Value.GetInt32());
        Assert.Empty(await e.KindsAsync());
    }

    [Fact]
    public async Task A_profile_that_unsets_the_address_while_the_flow_is_on_is_refused_the_same_way_and_one_that_also_turns_it_off_is_not()
    {
        await using var e = await EmptyAsync();
        await e.Tuning.SetAsync(OAuthSettings.PublicUrl, JsonSerializer.SerializeToElement(FlowAddress), Tester);
        await e.Tuning.SetAsync(OAuthSettings.Enabled, JsonSerializer.SerializeToElement(true), Tester);
        var before = (await e.KindsAsync()).Count;

        var (refused, problems) = await e.PlanAsync(Profile(
            ("profile.json", Manifest), ("settings.json", """{ "mcp.oauth.public_url": null }""")));

        Assert.Null(refused);
        Assert.Equal(OAuthSettingRules.AddressNeededWhileOn, Assert.Single(problems).Problem);
        Assert.StartsWith("mcp.oauth.public_url cannot be unset while mcp.oauth.enabled is true", Assert.Single(problems).Problem);
        Assert.Equal(before, (await e.KindsAsync()).Count);

        // The same unset beside turning the flow off is a whole that holds.
        var report = await e.ApplyAsync(Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "mcp.oauth.public_url": null, "mcp.oauth.enabled": false }""")));

        Assert.Equal(
            [$"{OAuthSettings.Enabled}: true becomes false", $"{OAuthSettings.PublicUrl}: {FlowAddress} becomes unset"],
            report.Applied.Select(c => c.ToString()));
        var stored = new SettingsStore(e.Db, e.Tenant);
        Assert.Null(await stored.GetAsync(OAuthSettings.PublicUrl));
        Assert.False((await stored.GetAsync(OAuthSettings.Enabled))!.Value.GetBoolean());
        Assert.Contains(TuningSettingsStore.UnsetKind, await e.KindsAsync());
    }

    [Fact]
    public async Task A_profile_cannot_create_a_user_or_grant_a_role_because_no_file_says_so()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("users.json", """[ { "name": "mallory", "role": "administrator" } ]"""));

        Assert.False(ProfileReader.TryRead(folder, out var profile, out var problems));

        Assert.Null(profile);
        var problem = Assert.Single(problems);
        Assert.Equal("users.json", problem.File);
        Assert.Contains("refuses rather than ignore it", problem.Problem);
    }

    [Fact]
    public async Task A_rule_naming_somebody_who_does_not_exist_is_refused_and_the_rest_is_not_applied()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40 }"""),
            ("sources.json", Sources(Corpus(), """["allow group:finance"]""")));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.Null(plan);
        Assert.Contains(problems, p => p.File == "sources.json" && p.Problem.Contains("finance"));
        // Nothing at all: not the setting, not the source that was fine.
        Assert.Equal(60, (await e.Tuning.ReadAsync(RetrievalSettings.RrfK)).Value.GetInt32());
        Assert.Null(await e.Sources.FindAsync("handbook"));
        Assert.Empty(await e.KindsAsync());
    }

    [Fact]
    public async Task Validating_a_profile_changes_nothing()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40 }"""),
            ("sources.json", Sources(Corpus())));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.True(plan is not null, Env.Problems(problems));
        // The setting, the source, its rule, and its hold.
        Assert.Equal(4, plan!.Changes.Count);
        Assert.Equal(60, (await e.Tuning.ReadAsync(RetrievalSettings.RrfK)).Value.GetInt32());
        Assert.Null(await e.Sources.FindAsync("handbook"));
        Assert.Empty(await e.Rules.ListRulesAsync());
        Assert.Empty(await e.KindsAsync());
        Assert.Null(await ProfileApply.LatestAsync(e.Db, e.Tenant));
    }

    [Fact]
    public async Task Every_problem_is_reported_and_not_only_the_first()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", """{ "name": "office", "version": "1", "seams": { "reader": 99 } }"""),
            ("settings.json", """{ "retrieval.rrf_k": 0, "trust.stale": "whenever", "nothing.here": 1 }"""),
            ("sources.json", Sources(Path.Combine(Path.GetTempPath(), "premagentic-not-a-folder"))));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.Null(plan);
        Assert.Equal(5, problems.Count);
        Assert.Contains(problems, p => p.Problem.Contains("version 99 of the reader seam"));
        Assert.Contains(problems, p => p.Problem.Contains("'retrieval.rrf_k'"));
        Assert.Contains(problems, p => p.Problem.Contains("'whenever' is not a value"));
        Assert.Contains(problems, p => p.Problem.Contains("'nothing.here' is not a setting"));
        Assert.Contains(problems, p => p.Problem.Contains("not a folder on this machine"));
    }

    [Fact]
    public async Task A_seam_this_build_does_not_have_that_far_is_refused_and_an_older_one_is_fine()
    {
        await using var e = await EmptyAsync();

        var (ahead, problems) = await e.PlanAsync(Profile(
            ("profile.json", """{ "name": "office", "version": "2", "seams": { "chunker": 2 } }""")));
        Assert.Null(ahead);
        Assert.Contains(problems, p => p.Problem.Contains("version 2 of the chunker seam and this build has version 1"));

        var (behind, _) = await e.PlanAsync(Profile(
            ("profile.json", """{ "name": "office", "version": "2", "seams": { "chunker": 1 } }""")));
        Assert.NotNull(behind);
    }

    [Fact]
    public async Task Show_names_exactly_what_drifted_and_nothing_else()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40, "trust.stale": "hidden-from-everyone" }"""),
            ("sources.json", Sources(Corpus())));
        await e.ApplyAsync(folder);

        // One setting moved by hand, the way an operator would.
        await e.Tuning.SetAsync(RetrievalSettings.RrfK, JsonSerializer.SerializeToElement(55), Tester);

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.True(plan is not null, Env.Problems(problems));
        var drift = Assert.Single(plan!.Changes);
        Assert.Equal(RetrievalSettings.RrfK, drift.Target);
        Assert.Equal("55 becomes 40", drift.Describe());
    }

    [Fact]
    public async Task Reapplying_a_profile_at_a_new_version_records_it_and_changes_nothing_else()
    {
        await using var e = await EmptyAsync();
        var corpus = Corpus();
        var settings = """{ "retrieval.rrf_k": 40 }""";
        await e.ApplyAsync(Profile(
            ("profile.json", Manifest),
            ("settings.json", settings),
            ("sources.json", Sources(corpus))));
        var after = (await e.KindsAsync()).Count;

        // The same configuration, published again under a new version: the
        // deployment already matches it, so there is nothing to change.
        var again = await e.ApplyAsync(Profile(
            ("profile.json", """{ "name": "office", "version": "2" }"""),
            ("settings.json", settings),
            ("sources.json", Sources(corpus))));

        Assert.Empty(again.Applied);
        // One more entry, the apply itself, and no second setting, source or rule.
        Assert.Equal(after + 1, (await e.KindsAsync()).Count);
        // Both applies are kept, and the newest is the one in force.
        Assert.Equal(2, await CountAppliedAsync(e));
        Assert.Equal("2", (await ProfileApply.LatestAsync(e.Db, e.Tenant))!.Version);
    }

    [Fact]
    public async Task A_golden_set_in_a_profile_is_copied_where_the_server_can_read_it_and_the_path_is_set()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("golden-set.json", """
                [ { "id": "rota", "question": "Who opens on Saturday?", "category": "operations",
                    "expectedSourcePaths": ["handbook/rota.md"], "expectedHeading": null,
                    "forbiddenSourcePaths": [], "archiveAllowed": false, "expectNoAnswer": false,
                    "notes": "Invented." } ]
                """));

        await e.ApplyAsync(folder);

        var running = await e.Tuning.ReadGoldenSetPathAsync();
        Assert.Equal(Path.Combine(e.GoldenSetFolder, "office.json"), running.Path);
        Assert.True(File.Exists(running.Path));
        Assert.Single(EvalRunner.LoadCases(running.Path!));
    }

    [Fact]
    public async Task A_profile_applied_to_the_default_folder_leaves_the_service_credentials_folder_to_setup()
    {
        await using var e = await EmptyAsync();
        const string OneCase = """
            { "id": "rota", "question": "Who opens on Saturday?", "category": "operations",
              "expectedSourcePaths": ["handbook/rota.md"], "expectedHeading": null,
              "forbiddenSourcePaths": [], "archiveAllowed": false, "expectNoAnswer": false, "notes": "Invented." }
            """;
        var folder = Profile(("profile.json", Manifest), ("golden-set.json", $"[ {OneCase} ]"));

        // A stand-in for the shared folder. On Windows it is made the ordinary
        // way in C:\ProgramData, so what is made in it takes the rules that let
        // every user add files, as the folders made in C:\ProgramData itself do.
        var shared = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "premagentic-profile-tests-" + Guid.NewGuid().ToString("N")[..8])
            : Directory.CreateTempSubdirectory("premagentic-shared-").FullName;
        Directory.CreateDirectory(shared);
        InstallFolders.SharedForTests.Value = shared;
        try
        {
            // The folder setup --windows-service makes and keeps private.
            var credentials = InstallFolders.Credentials(windowsService: true);
            Assert.Equal(Path.Combine(shared, "Premagentic"), credentials);

            // No --golden-set-dir: the default, as INSTALL.txt applies the starter profile.
            Assert.Equal(0, await ProfileCommands.RunAsync(["profile", "apply", folder], e.Db, e.Tenant));

            // Setup finds no folder there, so it makes it private rather than
            // refusing one another account could have filled.
            Assert.False(Directory.Exists(credentials), $"profile apply made {credentials}");
            var copied = (await e.Tuning.ReadGoldenSetPathAsync()).Path!;
            Assert.Equal(Path.Combine(shared, "Premagentic-golden-sets", "office.json"), copied);
            Assert.Single(EvalRunner.LoadCases(copied));
            if (!OperatingSystem.IsWindows()) return;

            // The service's account reads the copy as a member of Users, by the
            // rules the shared folder gives what is made in it.
            Assert.True(AccessRules.UsersMayRead(copied), $"{copied} cannot be read by {AccessRules.UsersName}");

            // Once setup has made the folder, applying again leaves it as setup made it.
            InstallAccess.CreatePrivateFolder(credentials);
            var made = new DirectoryInfo(credentials).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            File.WriteAllText(Path.Combine(folder, "golden-set.json"), $"[ {OneCase}, {OneCase.Replace("\"rota\"", "\"rota-2\"")} ]");

            Assert.Equal(0, await ProfileCommands.RunAsync(["profile", "apply", folder], e.Db, e.Tenant));

            Assert.Equal(2, EvalRunner.LoadCases(copied).Count);
            Assert.Empty(Directory.EnumerateFileSystemEntries(credentials));
            Assert.Equal(made, new DirectoryInfo(credentials).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.Null(InstallAccess.Untrusted(credentials));
        }
        finally
        {
            InstallFolders.SharedForTests.Value = null;
            Directory.Delete(shared, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("golden/escape")]
    [InlineData("two words")]
    [InlineData(".hidden")]
    public void A_profile_name_that_could_name_a_folder_is_refused_when_the_profile_is_read(string name)
    {
        var folder = Profile(("profile.json", JsonSerializer.Serialize(new { name, version = "1" })));

        Assert.False(ProfileReader.TryRead(folder, out var profile, out var problems));

        Assert.Null(profile);
        var problem = Assert.Single(problems);
        Assert.Equal(ProfileReader.ManifestFile, problem.File);
        Assert.Equal(
            $"The profile's name is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter or digit, not '{name}'.",
            problem.Problem);
    }

    [Fact]
    public async Task A_profile_whose_name_leads_out_of_the_golden_set_folder_is_refused_before_anything_is_copied()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("golden-set.json", """
                [ { "id": "rota", "question": "Who opens on Saturday?", "category": "operations",
                    "expectedSourcePaths": ["handbook/rota.md"], "expectedHeading": null,
                    "forbiddenSourcePaths": [], "archiveAllowed": false, "expectNoAnswer": false,
                    "notes": "Invented." } ]
                """));
        Assert.True(ProfileReader.TryRead(folder, out var read, out _));
        // Made without the reader, as a host or an extension could make one.
        var escaping = read! with { Manifest = read.Manifest with { Name = "../escape" } };

        var (plan, problems) = await ProfilePlanner.BuildAsync(escaping, e.Db, e.Tenant, e.GoldenSetFolder);

        Assert.Null(plan);
        var problem = Assert.Single(problems);
        Assert.Equal(ProfileReader.ManifestFile, problem.File);
        var outside = Path.GetFullPath(Path.Combine(e.GoldenSetFolder, "..", "escape.json"));
        Assert.Equal(
            $"The golden set would be copied to {outside}, outside {Path.TrimEndingDirectorySeparator(Path.GetFullPath(e.GoldenSetFolder))}. " +
            "A profile's name cannot name another folder.",
            problem.Problem);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task A_golden_question_a_search_would_refuse_is_refused_before_the_golden_set_is_copied()
    {
        await using var e = await EmptyAsync();
        var question = string.Join(' ', Enumerable.Repeat("rota", 801));
        var folder = Profile(
            ("profile.json", Manifest),
            ("golden-set.json", JsonSerializer.Serialize(new[]
            {
                new { id = "long", question, category = "operations", expectedSourcePaths = new[] { "handbook/rota.md" },
                      forbiddenSourcePaths = Array.Empty<string>(), archiveAllowed = false, expectNoAnswer = false },
            })));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.Null(plan);
        var problem = Assert.Single(problems);
        Assert.Equal(ProfileReader.GoldenSetFile, problem.File);
        Assert.Equal($"The golden set at {Path.Combine(folder, ProfileReader.GoldenSetFile)} has a question over 4,000 characters, 'long', which a search refuses.", problem.Problem);
        Assert.False(File.Exists(Path.Combine(e.GoldenSetFolder, "office.json")));
    }

    [Fact]
    public async Task A_profile_cannot_move_a_source_that_is_already_indexed()
    {
        await using var e = await EmptyAsync();
        var first = Corpus();
        var moved = Corpus();
        await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", Sources(first))));

        // The same source name, pointed at another folder. Applying that would
        // mean removing the source and indexing a whole corpus again, which a
        // configuration file must never imply.
        var (plan, problems) = await e.PlanAsync(Profile(
            ("profile.json", Manifest), ("sources.json", Sources(moved))));

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Problem.Contains("A source's folder cannot be changed"));
        Assert.Equal(first, (await e.Sources.FindAsync("handbook"))!.Folder);
    }

    [Fact]
    public async Task The_starter_profile_sets_up_the_quick_start_on_a_fresh_server_in_one_apply_and_its_golden_set_passes()
    {
        await using var e = await EmptyAsync();
        // Nothing made by hand first: the profile carries the groups its rule
        // and its golden set name, hr because the rule reads "allow group:hr",
        // engineering because a golden case asks as somebody in it and
        // expects to be told nothing.
        Assert.Null(await new IdentityStore(e.Db, e.Tenant).FindGroupByNameAsync("hr"));

        var report = await e.ApplyAsync(Path.Combine(RepoRoot(), "samples", "profiles", "starter"));

        Assert.Equal(["engineering", "hr"], report.Applied.OfType<GroupChange>().Select(g => g.Name).Order());

        // The trust values the starter profile carries are the defaults written
        // down, so applying it moves nobody's trust posture.
        Assert.DoesNotContain(report.Applied, c => c.Target.StartsWith("trust.", StringComparison.Ordinal));
        Assert.Equal(2, report.Applied.OfType<SourceChange>().Count());
        Assert.Equal(2, report.Applied.OfType<RuleChange>().Count());

        // What the quick start then does by hand: ingest each source it set up.
        var embedder = new HashEmbeddingProvider();
        foreach (var source in await e.Sources.ListAsync())
        {
            var summary = await new IngestPipeline(e.Db, embedder).RunAsync(e.Tenant, source.ToFileSystemSource());
            Assert.True(summary.Ingested > 0, $"{source.Name} indexed nothing");
            Assert.Equal(0, summary.DeniedToEveryone);
        }

        var golden = (await e.Tuning.ReadGoldenSetPathAsync()).Path;
        Assert.NotNull(golden);
        var runner = new EvalRunner(
            new HybridSearch(e.Db, embedder), e.Tenant, new PrincipalNames(new IdentityStore(e.Db, e.Tenant)));
        var reportPath = Path.Combine(Path.GetTempPath(), $"prem-profile-eval-{Guid.NewGuid():N}.md");
        try
        {
            Assert.Equal(0, await runner.RunAsync(golden!, reportPath));
            Assert.Contains("5/5 passed", await File.ReadAllTextAsync(reportPath));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    /// <summary>
    /// The switch is a hold beside the rule, so a profile may set it with no
    /// rule at all, and writes none for it.
    /// </summary>
    [Fact]
    public async Task A_profile_may_hold_a_source_back_without_stating_a_rule()
    {
        await using var e = await EmptyAsync();
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                "hosted": false } ]
            """;

        await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", sources)));

        Assert.Empty(await e.Rules.ListRulesAsync());
        Assert.Equal(SourceExposureState.NeverLeaves,
            await new SourceExposureStore(e.Db, e.Tenant).ReadAsync("handbook"));
    }

    [Fact]
    public async Task A_profile_that_says_hosted_no_holds_the_folder_and_writes_the_rule_as_stated()
    {
        await using var e = await EmptyAsync();
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                "rule": ["allow everyone"], "hosted": false } ]
            """;

        await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", sources)));

        var rule = Assert.Single(await e.Rules.ListRulesAsync());
        Assert.Equal(["allow everyone"], rule.Rule.Acl.Entries.Select(x => x.ToString()));
        Assert.Contains(new HostedHold("filesystem", "handbook"), await HostedHolds.ListAsync(e.Db, e.Tenant));
        Assert.Equal(SourceExposureState.NeverLeaves,
            await new SourceExposureStore(e.Db, e.Tenant).ReadAsync("handbook"));
    }

    /// <summary>
    /// A profile's own rule for a held source, rewritten by a later profile
    /// that says nothing about hosted models, and a rule written beneath the
    /// folder in between: the folder stays held through both, the second plan
    /// holds a rule change and no change to the switch, and the indexed
    /// document stores the rewritten rule's list with the denial first.
    /// </summary>
    [Fact]
    public async Task Rules_written_after_a_profile_held_a_source_do_not_release_it()
    {
        await using var e = await EmptyAsync();
        var corpus = Corpus();
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(corpus)}}, "prefix": "handbook",
                "rule": ["allow everyone"], "hosted": false } ]
            """;
        await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", sources)));
        var source = (await e.Sources.FindAsync("handbook"))!;
        Assert.Equal(1, (await new IngestPipeline(e.Db, new HashEmbeddingProvider())
            .RunAsync(e.Tenant, source.ToFileSystemSource())).Ingested);

        await e.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook/weekend", AclSet.Of(AclEntry.Allow(Principal.Everyone))));
        var (plan, problems) = await e.PlanAsync(Profile(
            ("profile.json", Manifest), ("sources.json", Sources(corpus, rule: """["deny everyone"]"""))));

        Assert.True(plan is not null, Env.Problems(problems));
        Assert.Single(plan!.Changes.OfType<RuleChange>());
        Assert.Empty(plan.Changes.OfType<HostedChange>());
        await ProfileApply.RunAsync(plan, e.Db, e.Tenant, Tester);

        Assert.Contains(new HostedHold("filesystem", "handbook"), await HostedHolds.ListAsync(e.Db, e.Tenant));
        Assert.Equal(SourceExposureState.NeverLeaves,
            await new SourceExposureStore(e.Db, e.Tenant).ReadAsync("handbook"));
        var reserved = (await new IdentityStore(e.Db, e.Tenant).FindSystemGroupAsync(SystemGroups.HostedModelAgents))!.Id;
        Assert.Equal($"deny group:{CallerResolver.IdText(reserved)}\ndeny everyone\n",
            await SourceExposureTests.StoredListAsync(e.Db, e.Tenant, "handbook/rota.md"));
    }

    /// <summary>
    /// A folder under a legacy hold whose rule still opens with the entry the
    /// switch wrote before migration 0141: the profile that states the rule
    /// without it has not drifted, because the entry is the hold's, not the
    /// rule's.
    /// </summary>
    [Fact]
    public async Task A_legacy_held_rule_still_carrying_the_switchs_old_entry_is_not_drift()
    {
        await using var e = await EmptyAsync();
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                "rule": ["allow everyone"], "hosted": false } ]
            """;
        var folder = Profile(("profile.json", Manifest), ("sources.json", sources));
        await e.ApplyAsync(folder);
        await SourceExposureTests.MarkLegacyAsync(e.Db, e.Tenant, "filesystem", "handbook");
        var reserved = (await new IdentityStore(e.Db, e.Tenant).FindSystemGroupAsync(SystemGroups.HostedModelAgents))!.Id;
        await e.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook",
            AclSet.Of(SourceExposure.Denial(reserved), AclEntry.Allow(Principal.Everyone))));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.True(plan is not null, Env.Problems(problems));
        Assert.Empty(plan!.Changes.Select(c => c.ToString()));
    }

    /// <summary>
    /// The same entry at the top of the rule of a folder whose hold is not a
    /// legacy one was written by a person, not by the switch, so a profile
    /// that states the rule without it has drifted and writes its rule.
    /// </summary>
    [Fact]
    public async Task Under_a_hold_that_is_not_legacy_a_top_denial_the_profile_does_not_state_is_drift()
    {
        await using var e = await EmptyAsync();
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                "rule": ["allow everyone"], "hosted": false } ]
            """;
        var folder = Profile(("profile.json", Manifest), ("sources.json", sources));
        await e.ApplyAsync(folder);
        var reserved = (await new IdentityStore(e.Db, e.Tenant).FindSystemGroupAsync(SystemGroups.HostedModelAgents))!.Id;
        await e.Rules.SetRuleAsync(new FolderRule("filesystem", "handbook",
            AclSet.Of(SourceExposure.Denial(reserved), AclEntry.Allow(Principal.Everyone))));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.True(plan is not null, Env.Problems(problems));
        Assert.Single(plan!.Changes.OfType<RuleChange>());
    }

    /// <summary>
    /// A profile whose rule opens with the denial of hosted-model agents, on a
    /// held folder, legacy or not: applied once, it has nothing left to plan,
    /// and the entry it states stays when the hold is released.
    /// </summary>
    [Fact]
    public async Task A_profile_whose_rule_opens_with_the_hosted_denial_converges()
    {
        await using var e = await EmptyAsync();
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                "rule": ["deny group:{{SystemGroups.HostedModelAgentsName}}", "allow everyone"], "hosted": false } ]
            """;
        var folder = Profile(("profile.json", Manifest), ("sources.json", sources));
        await e.ApplyAsync(folder);

        var (plan, problems) = await e.PlanAsync(folder);
        Assert.True(plan is not null, Env.Problems(problems));
        Assert.Empty(plan!.Changes.Select(c => c.ToString()));

        await SourceExposureTests.MarkLegacyAsync(e.Db, e.Tenant, "filesystem", "handbook");
        (plan, problems) = await e.PlanAsync(folder);
        Assert.True(plan is not null, Env.Problems(problems));
        Assert.Empty(plan!.Changes.Select(c => c.ToString()));
    }

    /// <summary>
    /// Holds are applied before anything else a profile writes, and releases
    /// after, so no source or rule it writes is ever served unheld in between.
    /// </summary>
    [Fact]
    public async Task A_profile_applies_holds_first_and_releases_last()
    {
        await using var e = await EmptyAsync();
        var corpus = Corpus();
        var held = await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", Sources(corpus))));
        Assert.Equal(new HostedChange("handbook", "handbook", MayBeServed: false, Held: false), held.Applied[0]);

        var released = await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(corpus)}}, "prefix": "handbook",
                "okfBundle": false, "undeclaredAsMachine": false, "chunker": "markdown",
                "rule": ["deny everyone"], "hosted": true } ]
            """)));
        Assert.IsType<RuleChange>(released.Applied[0]);
        Assert.Equal(new HostedChange("handbook", "handbook", MayBeServed: true, Held: true), released.Applied[^1]);
    }

    /// <summary>
    /// A folder held by a hold above it cannot be released by a profile that
    /// names the source beneath: releasing its own folder would change
    /// nothing, and the plan would ask for it again on every run. It is
    /// refused, naming the hold.
    /// </summary>
    [Fact]
    public async Task A_profile_cannot_serve_a_source_a_hold_above_its_folder_covers()
    {
        await using var e = await EmptyAsync();
        await e.Rules.HoldAsync("filesystem", "");
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                "rule": ["allow everyone"], "hosted": true } ]
            """;

        var (plan, problems) = await e.PlanAsync(Profile(("profile.json", Manifest), ("sources.json", sources)));

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Problem.Contains("held back by the switch on filesystem:(whole source)"));
    }

    [Fact]
    public async Task A_profile_adding_a_source_holds_it_back_from_hosted_models_unless_it_says_otherwise()
    {
        await using var e = await EmptyAsync();

        // No hosted key at all: silence about a source being added means the
        // documents do not leave the network.
        await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", Sources(Corpus()))));

        Assert.Equal(SourceExposureState.NeverLeaves,
            await new SourceExposureStore(e.Db, e.Tenant).ReadAsync("handbook"));
    }

    [Fact]
    public async Task A_profile_can_name_the_person_answerable_for_a_source()
    {
        await using var e = await EmptyAsync();
        var dana = await new IdentityStore(e.Db, e.Tenant).CreateUserAsync("dana", "Dana", Role.Member);
        var sources = $$"""
            [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                "rule": ["allow everyone"], "owner": "dana" } ]
            """;

        await e.ApplyAsync(Profile(("profile.json", Manifest), ("sources.json", sources)));

        Assert.Equal(dana.Id, (await e.Sources.FindAsync("handbook"))!.OwnerUserId);

        // A name nobody signs in as is refused, like any other principal a
        // profile names.
        var (plan, problems) = await e.PlanAsync(Profile(
            ("profile.json", Manifest),
            ("sources.json", sources.Replace("\"dana\"", "\"nobody-here\""))));
        Assert.Null(plan);
        Assert.Contains(problems, p => p.Problem.Contains("no user signs in as that"));
    }

    [Fact]
    public async Task A_profile_naming_an_extensions_chunker_applies_only_where_that_extension_is_loaded()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("sources.json", Sources(Corpus(), chunker: SampleExtension.ChunkerName)));

        // With only the built-in chunkers, the name is one this deployment does
        // not have, and the profile is refused before anything is written.
        var (none, problems) = await e.PlanAsync(folder);
        Assert.Null(none);
        Assert.Contains(problems, p => p.Problem.Contains($"names the chunker '{SampleExtension.ChunkerName}'"));
        Assert.Null(await e.Sources.FindAsync("handbook"));

        // The same profile, on a deployment whose extension registers that
        // chunker, applies. Validating against the built-in set alone would
        // have refused a profile this deployment can in fact apply.
        var host = SampleExtension.LoadedOnce();
        Assert.Contains(SampleExtension.ChunkerName, host.Chunkers.Names);

        await e.ApplyAsync(folder, host.Chunkers);

        Assert.Equal(SampleExtension.ChunkerName, (await e.Sources.FindAsync("handbook"))!.Chunker);
    }

    [Fact]
    public async Task The_profile_verb_validates_then_applies_then_shows_no_drift()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40 }"""),
            ("sources.json", Sources(Corpus())));
        string[] dir = ["--golden-set-dir", e.GoldenSetFolder];

        // Validating changes nothing, so applying afterwards still has work.
        Assert.Equal(0, await ProfileCommands.RunAsync(["profile", "validate", folder, .. dir], e.Db, e.Tenant));
        Assert.Null(await ProfileApply.LatestAsync(e.Db, e.Tenant));

        Assert.Equal(0, await ProfileCommands.RunAsync(["profile", "apply", folder, .. dir], e.Db, e.Tenant));
        Assert.Equal("office", (await ProfileApply.LatestAsync(e.Db, e.Tenant))!.Name);

        // show with a folder is the drift list, and with none it is what is applied.
        Assert.Equal(0, await ProfileCommands.RunAsync(["profile", "show", folder, .. dir], e.Db, e.Tenant));
        Assert.Equal(0, await ProfileCommands.RunAsync(["profile", "show", .. dir], e.Db, e.Tenant));
    }

    [Fact]
    public async Task The_profile_verb_refuses_and_says_so_without_changing_anything()
    {
        await using var e = await EmptyAsync();
        var bad = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40, "nothing.here": 1 }"""));

        Assert.Equal(1, await ProfileCommands.RunAsync(
            ["profile", "apply", bad, "--golden-set-dir", e.GoldenSetFolder], e.Db, e.Tenant));

        Assert.Equal(60, (await e.Tuning.ReadAsync(RetrievalSettings.RrfK)).Value.GetInt32());
        Assert.Empty(await e.KindsAsync());
        Assert.Null(await ProfileApply.LatestAsync(e.Db, e.Tenant));

        // A subcommand nobody has, and a folder that is not one.
        Assert.Equal(1, await ProfileCommands.RunAsync(["profile", "reticulate", bad], e.Db, e.Tenant));
        Assert.Equal(1, await ProfileCommands.RunAsync(
            ["profile", "validate", Path.Combine(Path.GetTempPath(), "premagentic-no-profile-here")], e.Db, e.Tenant));
    }

    [Fact]
    public async Task Every_kind_of_change_renders_as_a_sentence_rather_than_as_its_record()
    {
        await using var e = await EmptyAsync();
        await new IdentityStore(e.Db, e.Tenant).CreateUserAsync("dana", "Dana", Role.Member);
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40 }"""),
            ("groups.json", """[ "floor" ]"""),
            ("sources.json", $$"""
                [ { "name": "handbook", "folder": {{JsonSerializer.Serialize(Corpus())}}, "prefix": "handbook",
                    "rule": ["allow everyone"], "owner": "dana" } ]
                """),
            ("golden-set.json", """
                [ { "id": "rota", "question": "Who opens on Saturday?", "category": "operations",
                    "expectedSourcePaths": ["handbook/rota.md"], "expectedHeading": null,
                    "forbiddenSourcePaths": [], "archiveAllowed": false, "expectNoAnswer": false,
                    "notes": "Invented." } ]
                """));

        var (plan, problems) = await e.PlanAsync(folder);

        Assert.True(plan is not null, Env.Problems(problems));
        // One of every kind, so this cannot pass by covering fewer than all of
        // them: a record that synthesizes its own ToString beats the base's, and
        // it would do so one kind at a time.
        Assert.Equal(7, plan!.Changes.Select(c => c.GetType()).Distinct().Count());
        foreach (var change in plan.Changes)
        {
            var line = change.ToString();
            Assert.StartsWith(change.Target + ": ", line);
            // A record's own ToString reads "SettingChange { Key = ... }".
            Assert.DoesNotContain("{", line);
        }
    }

    [Fact]
    public async Task The_profile_verb_prints_each_change_as_a_sentence()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("settings.json", """{ "retrieval.rrf_k": 40 }"""),
            ("sources.json", Sources(Corpus())));

        // Through the command, because the sentence an operator reads is the
        // join of Target and Describe, and asserting the two halves separately
        // is what let every change print as its record text.
        var (exit, output, _) = await ConsoleCapture.RunAsync(() => ProfileCommands.RunAsync(
            ["profile", "validate", folder, "--golden-set-dir", e.GoldenSetFolder], e.Db, e.Tenant));

        Assert.Equal(0, exit);
        Assert.Contains("retrieval.rrf_k: 60 becomes 40", output);
        Assert.Contains("source handbook: added, reading", output);
        Assert.DoesNotContain("Change {", output);
    }

    [Fact]
    public async Task Applying_the_starter_profile_a_second_time_changes_nothing_and_says_so()
    {
        await using var e = await EmptyAsync();
        var starter = Path.Combine(RepoRoot(), "samples", "profiles", "starter");
        string[] dir = ["--golden-set-dir", e.GoldenSetFolder];
        Assert.Equal(0, await ProfileCommands.RunAsync(["profile", "apply", starter, .. dir], e.Db, e.Tenant));
        var after = (await e.KindsAsync()).Count;

        var (plan, problems) = await e.PlanAsync(starter);
        Assert.True(plan is not null, Env.Problems(problems));
        // Every kind the starter holds, the groups and the golden set among
        // them, is in force, so there is no change left to make.
        Assert.Empty(plan!.Changes.Select(c => c.ToString()));

        var (exit, output, _) = await ConsoleCapture.RunAsync(() =>
            ProfileCommands.RunAsync(["profile", "apply", starter, .. dir], e.Db, e.Tenant));

        Assert.Equal(0, exit);
        Assert.Contains("already matched it, so nothing was changed", output);
        // One more entry, the apply itself, and no second group, rule or copy.
        Assert.Equal(after + 1, (await e.KindsAsync()).Count);
        Assert.Equal(2, (await new IdentityStore(e.Db, e.Tenant).ListGroupsAsync())
            .Count(g => g.Name is "hr" or "engineering"));
    }

    [Fact]
    public async Task A_group_the_profile_creates_is_applied_before_the_rule_that_names_it()
    {
        await using var e = await EmptyAsync();
        var folder = Profile(
            ("profile.json", Manifest),
            ("groups.json", """[ "floor" ]"""),
            ("sources.json", Sources(Corpus(), """["allow group:floor"]""")));

        var report = await e.ApplyAsync(folder);

        var floor = await new IdentityStore(e.Db, e.Tenant).FindGroupByNameAsync("floor");
        Assert.NotNull(floor);
        // The rule names the group's real id, not the placeholder the plan
        // carried while the group did not exist.
        var rule = Assert.Single(await e.Rules.ListRulesAsync());
        Assert.Equal($"allow group:{CallerResolver.IdText(floor!.Id)}", rule.Rule.Acl.Entries[^1].ToString());
        var planned = Assert.Single(report.Applied.OfType<GroupChange>());
        Assert.DoesNotContain(rule.Rule.Acl.Entries, x => x.Principal.Value == CallerResolver.IdText(planned.Placeholder));

        // Recorded as the groups page records one.
        var entry = Assert.Single(await e.Record.ListAsync(200), x => x.Kind == "group.add");
        Assert.Equal("floor", entry.Target);
        Assert.Equal("floor", entry.NewValue!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_group_that_exists_is_left_alone_and_a_group_listed_twice_is_refused()
    {
        await using var e = await EmptyAsync();
        await new IdentityStore(e.Db, e.Tenant).CreateGroupAsync("floor");

        var (plan, problems) = await e.PlanAsync(Profile(
            ("profile.json", Manifest),
            ("groups.json", """[ "floor" ]"""),
            ("sources.json", Sources(Corpus(), """["allow group:floor"]"""))));
        Assert.True(plan is not null, Env.Problems(problems));
        Assert.Empty(plan!.Changes.OfType<GroupChange>());

        var (twice, refused) = await e.PlanAsync(Profile(
            ("profile.json", Manifest),
            ("groups.json", """[ "desk", "Desk", " padded" ]""")));
        Assert.Null(twice);
        Assert.Contains(refused, p => p.File == "groups.json" && p.Problem.Contains("listed twice"));
        Assert.Contains(refused, p => p.File == "groups.json" && p.Problem.Contains("white space"));
        Assert.Null(await new IdentityStore(e.Db, e.Tenant).FindGroupByNameAsync("desk"));
    }

    [Fact]
    public async Task Validating_a_profile_with_groups_creates_no_group()
    {
        await using var e = await EmptyAsync();
        var (plan, problems) = await e.PlanAsync(Profile(
            ("profile.json", Manifest),
            ("groups.json", """[ "floor" ]"""),
            ("sources.json", Sources(Corpus(), """["allow group:floor"]"""))));

        Assert.True(plan is not null, Env.Problems(problems));
        Assert.Single(plan!.Changes.OfType<GroupChange>());
        Assert.Null(await new IdentityStore(e.Db, e.Tenant).FindGroupByNameAsync("floor"));
        Assert.Empty(await e.KindsAsync());
    }

    [Fact]
    public async Task A_golden_set_run_pointed_at_a_profiles_own_file_writes_nothing_into_the_profile_folder()
    {
        await using var e = await EmptyAsync();
        // A copy of the starter profile, so the run cannot touch the repository.
        var folder = Directory.CreateTempSubdirectory("premagentic-profile-").FullName;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "samples", "profiles", "starter")))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        var before = Directory.EnumerateFileSystemEntries(folder).Order().ToArray();
        Assert.Contains(Path.Combine(folder, ProfileReader.GoldenSetFile), before);

        await e.Tuning.SetAsync(TuningSettingsStore.GoldenSetPath,
            JsonSerializer.SerializeToElement(Path.Combine(folder, ProfileReader.GoldenSetFile)), Tester);
        var result = await new GoldenSetRun(e.Db, e.Tenant, new HybridSearch(e.Db, new HashEmbeddingProvider()),
            new PrincipalNames(new IdentityStore(e.Db, e.Tenant))).RunAsync(Tester);

        // The run happened, so the absence below is the run writing nothing
        // rather than a run that never started.
        Assert.Equal(GoldenRunOutcome.Completed, result.Outcome);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(folder).Order().ToArray());
        Assert.True(ProfileReader.TryRead(folder, out _, out var problems), Env.Problems(problems));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Premagentic.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No repository root above the test output.");
    }

    private static async Task<long> CountAppliedAsync(Env e)
    {
        await using var cmd = e.Db.DataSource.CreateCommand(
            "SELECT count(*) FROM prem_config.profile_applied WHERE tenant_id = @tenant");
        cmd.Parameters.AddWithValue("tenant", e.Tenant);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_profile_that_turns_the_flow_on_over_a_stored_address_that_fails_the_one_spelling_is_refused()
    {
        await using var e = await EmptyAsync();
        await new SettingsStore(e.Db, e.Tenant).SetAsync(OAuthSettings.PublicUrl, JsonSerializer.SerializeToElement("https://Host:8443/"));

        var (plan, problems) = await e.PlanAsync(Profile(("profile.json", Manifest), ("settings.json", """{ "mcp.oauth.enabled": true }""")));

        Assert.Null(plan);
        Assert.Equal(OAuthSettingRules.EnableNeedsAddress, Assert.Single(problems).Problem);
    }
}
