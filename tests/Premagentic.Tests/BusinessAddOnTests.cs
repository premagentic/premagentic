using System.Text.Json;
using Premagentic.Cli.Admin;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Profiles;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A deployment without the business add-on: a command, a setting or a page
/// the add-on brings is answered with one sentence naming it, for whichever
/// state the deployment is in, and nothing is written. Requires a running
/// Docker daemon for the tests that use a database.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class BusinessAddOnTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string Named = "'prem audit prune' comes with the PremAgentic business add-on";

    private async Task<(PremagenticDatabase Db, Guid Tenant, string ConnectionString)> EmptyAsync()
    {
        var connectionString = await server.CreateDatabaseAsync();
        var db = new PremagenticDatabase(connectionString);
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"), connectionString);
    }

    [Fact]
    public void The_sentence_names_the_add_on_in_each_state_a_deployment_can_be_in()
    {
        string[] prune = ["audit", "prune"];

        Assert.Equal($"{Named}, and no extension loads until a database is configured.", BusinessAddOn.Refusal(prune, host: null));
        Assert.Equal($"{Named}, which is not installed here.", BusinessAddOn.Refusal(prune, ExtensionHost.BuiltIn));

        // An extension folder under the add-on's name that nobody allowed.
        var folder = Directory.CreateTempSubdirectory("prem-addon-refused-").FullName;
        var pair = GreetingSample.InstallInto(folder, BusinessAddOn.ExtensionName);
        Assert.Equal(
            $"{Named}, which is installed here and was refused (not allowed); prem extensions list says why.",
            BusinessAddOn.Refusal(prune, ExtensionHost.Load(folder, [])));

        // Loaded, in a version that does not have the command.
        var loaded = ExtensionHost.Load(folder, [pair]);
        Assert.Equal($"{Named}, and the version loaded here (0.1.0) does not have it.", BusinessAddOn.Refusal(prune, loaded));

        // The portal's audit trail address, loaded and not: loaded, it names
        // the command that gives the same rows, and never says the add-on lacks it.
        Assert.Equal(
            "The audit trail download is not offered in the portal in this version; 'prem audit export' gives the same rows.",
            BusinessAddOn.PageRefusal("/portal/export/audit.jsonl", loaded));
        Assert.Equal(
            "'/portal/export/audit.jsonl' comes with the PremAgentic business add-on, which is not installed here.",
            BusinessAddOn.PageRefusal("/portal/export/audit.jsonl", ExtensionHost.BuiltIn));
    }

    [Fact]
    public void Only_what_the_add_on_brings_is_named()
    {
        var host = ExtensionHost.BuiltIn;

        Assert.StartsWith("'prem audit' comes with", BusinessAddOn.Refusal(["audit"], host));
        Assert.StartsWith("'prem audit' comes with", BusinessAddOn.Refusal(["help", "audit"], host));
        Assert.StartsWith("'prem audit' comes with", BusinessAddOn.Refusal(["audit", "--help"], host));
        Assert.StartsWith("'prem audit' comes with", BusinessAddOn.Refusal(["audit", "rotate"], host));
        Assert.StartsWith(Named, BusinessAddOn.Refusal(["audit", "prune", "--plan"], host));
        Assert.StartsWith("'prem audit export' comes with", BusinessAddOn.Refusal(["audit", "export", "--hosted"], host));
        // Directory group mapping, under the built-in groups: its three
        // subcommands are named, and groups itself and its own are not.
        Assert.StartsWith("'prem groups map' comes with", BusinessAddOn.Refusal(["groups", "map", "S-1-5-21-7", "Staff"], host));
        Assert.StartsWith("'prem groups unmap' comes with", BusinessAddOn.Refusal(["groups", "unmap", "S-1-5-21-7"], host));
        Assert.StartsWith("'prem groups mappings' comes with", BusinessAddOn.Refusal(["groups", "mappings"], host));
        foreach (var args in new[] { new[] { "groups" }, ["groups", "list"], ["help", "groups"], ["groups", "--help"] })
            Assert.Null(BusinessAddOn.Refusal(args, host));
        foreach (var args in new[] { new[] { "frobnicate" }, ["groups", "tally"], ["users", "list"], ["help"], [] })
            Assert.Null(BusinessAddOn.Refusal(args, host));

        Assert.StartsWith("The setting audit.retention_days comes with", BusinessAddOn.SettingRefusal("audit.retention_days", host));
        Assert.Null(BusinessAddOn.SettingRefusal("retrieval.rrf_k", host));

        Assert.StartsWith("'/portal/export/audit.jsonl' comes with", BusinessAddOn.PageRefusal("/portal/export/audit.jsonl", host));
        Assert.StartsWith("'/portal/export/audit.jsonl' comes with", BusinessAddOn.PageRefusal("/Portal/Export/Audit.JSONL", host));
        // The export page and its other downloads are the core's backup.
        foreach (var path in new[] { "/portal/export", "/portal/export/config.json", "/portal/export/changes.jsonl", "/portal/audit" })
            Assert.Null(BusinessAddOn.PageRefusal(path, host));
    }

    [Fact]
    public void Nothing_the_add_on_is_named_for_is_built_in()
    {
        Assert.Empty(BusinessAddOn.Nouns.Intersect(BuiltInCommands.Names));
        Assert.All(BusinessAddOn.Verbs, v => Assert.True(
            !BuiltInCommands.Names.Contains(v.Noun)
            || BuiltInCommands.Open.TryGetValue(v.Noun, out var builtIn) && !builtIn.Contains(v.Verb),
            $"prem {v.Noun} {v.Verb} is built in"));
        Assert.All(BusinessAddOn.Settings, key => Assert.False(SettingsCatalog.Contains(key), $"{key} is built in"));
    }

    [Fact]
    public async Task Prem_settings_names_the_add_on_for_its_setting_and_writes_nothing()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;

        foreach (var args in new[] { new[] { "settings", "set", "audit.retention_days", "365" }, ["settings", "get", "audit.retention_days"], ["settings", "unset", "audit.retention_days"] })
        {
            var refused = await ConsoleCapture.RunAsync(() => SettingsCommands.RunAsync(args, db, tenant));
            Assert.Equal(1, refused.Exit);
            Assert.Equal(
                "The setting audit.retention_days comes with the PremAgentic business add-on, which is not installed here.",
                refused.Err.Trim());
        }

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT (SELECT count(*) FROM prem_config.setting WHERE key = 'audit.retention_days') + (SELECT count(*) FROM prem_config.admin_event)");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_profile_naming_the_add_ons_setting_is_told_where_it_is_set()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;
        var folder = Directory.CreateTempSubdirectory("premagentic-profile-").FullName;
        File.WriteAllText(Path.Combine(folder, "profile.json"), """{ "name": "office", "version": "1" }""");
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{ "audit.retention_days": 365 }""");
        Assert.True(ProfileReader.TryRead(folder, out var profile, out var read), string.Join(" | ", read));

        var (plan, problems) = await ProfilePlanner.BuildAsync(
            profile!, db, tenant, Directory.CreateTempSubdirectory("premagentic-golden-").FullName);

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Problem.StartsWith(
            "'audit.retention_days' is a setting of the PremAgentic business add-on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_command_line_names_the_add_on_for_its_commands_and_writes_nothing()
    {
        var (db, _, connectionString) = await EmptyAsync();
        await using var _ = db;
        var extensions = Directory.CreateTempSubdirectory("prem-addon-cli-").FullName;

        foreach (var args in new[] { new[] { "audit", "prune" }, ["audit", "export", "--from", "2026-01-01", "--to", "2026-01-02"], ["audit"], ["help", "audit"] })
        {
            var run = await ExtensionCommandTests.CliAsync(connectionString, extensions, args);
            Assert.Equal(1, run.Exit);
            Assert.EndsWith("comes with the PremAgentic business add-on, which is not installed here.", run.Err.Trim());
            Assert.Equal("", run.Out);
        }

        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.admin_event");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }
}
