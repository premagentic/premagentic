using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Premagentic.Cli;
using Premagentic.Cli.Admin;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Profiles;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Lays out the built greeting-command sample in a folder, the way an
/// administrator would, with a manifest carrying the hash of those bytes and
/// the seams it is told to declare. Nothing here references the sample's
/// types, so it is loaded only the way a real extension is.
/// </summary>
internal static class GreetingSample
{
    public const string Name = "greeting-command";
    public const string AssemblyFile = "GreetingCommand.dll";
    public const string SettingKey = "greeting.text";
    public const string ChangeKind = "greeting.set";
    public const string Declared = """{ "command": 1, "setting": 1 }""";

    public static string BuiltPath
    {
        get
        {
            var path = typeof(GreetingSample).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "Premagentic.Sample.GreetingCommand.Path")
                .Value;
            Assert.True(File.Exists(path), "the greeting sample was not built where the test project recorded it: " + path);
            return path!;
        }
    }

    /// <summary>One extension folder under <paramref name="extensionsFolder"/>, and the pair an allow list needs for it.</summary>
    public static (string Name, string Sha256) InstallInto(
        string extensionsFolder, string name = Name, string seams = Declared)
    {
        var directory = Directory.CreateDirectory(Path.Combine(extensionsFolder, name));
        var assembly = Path.Combine(directory.FullName, AssemblyFile);
        File.Copy(BuiltPath, assembly, overwrite: true);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly)));
        File.WriteAllText(Path.Combine(directory.FullName, ExtensionManifest.FileName), $$"""
            {
              "name": "{{name}}",
              "version": "0.1.0",
              "assemblyFile": "{{AssemblyFile}}",
              "sha256": "{{sha256}}",
              "seams": {{seams}}
            }
            """);
        return (name, sha256);
    }

    /// <summary>A host with the sample installed and allowed, in a folder of its own.</summary>
    public static ExtensionHost LoadedOnce()
    {
        var folder = Directory.CreateTempSubdirectory("prem-greeting-loaded-");
        var allowed = InstallInto(folder.FullName);
        var host = ExtensionHost.Load(folder.FullName, [allowed]);
        Assert.True(host.Refused.Count == 0, string.Join(" | ", host.Refused.Select(r => r.Detail)));
        return host;
    }
}

/// <summary>
/// A command and a setting an extension adds, through the real host: what
/// the host takes and refuses, the one dispatch the command line uses, the
/// flags a usage names, the change record, <c>prem settings</c> and a
/// profile, and the command line itself as a process. Requires a running
/// Docker daemon for the tests that use a database.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ExtensionCommandTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static readonly ExtensionHost Host = GreetingSample.LoadedOnce();

    private async Task<(PremagenticDatabase Db, Guid Tenant, string ConnectionString)> EmptyAsync()
    {
        var connectionString = await server.CreateDatabaseAsync();
        var db = new PremagenticDatabase(connectionString);
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"), connectionString);
    }

    private static async Task<(int? Exit, string Out, string Err)> RunAsync(
        ExtensionHost host, PremagenticDatabase db, Guid tenant, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await ExtensionCommands.RunAsync(host, args, db, tenant, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static async Task<List<(string Kind, string Surface)>> RecordAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT kind, actor_surface FROM prem_config.admin_event ORDER BY id");
        var rows = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    // --- What the host takes ---

    [Fact]
    public void The_host_holds_the_commands_and_the_setting_the_sample_adds()
    {
        Assert.Equal(
            new[] { "greeting show", "greeting set", "groups count" },
            Host.Commands.SelectMany(c => c.Command.Verbs.Select(v => $"{c.Command.Noun} {v.Name}")));
        Assert.All(Host.Commands, c => Assert.Equal(GreetingSample.Name, c.Extension));
        Assert.Equal(new[] { ("greeting", "set") }, ExtensionCommands.WriteVerbs(Host));

        Assert.NotNull(Host.Settings.Find(GreetingSample.SettingKey));
        Assert.Equal(GreetingSample.Name, Host.Settings.ExtensionOf(GreetingSample.SettingKey));
        Assert.Null(Host.Settings.ExtensionOf(SettingsCatalog.ConnectSnippets));
        Assert.False(ExtensionHost.BuiltIn.Settings.Contains(GreetingSample.SettingKey));
        Assert.Empty(ExtensionHost.BuiltIn.Commands);
    }

    [Theory]
    [InlineData("""{ "setting": 1 }""", "\"command\" seam")]
    [InlineData("""{ "command": 1 }""", "\"setting\" seam")]
    [InlineData("{}", "\"command\" seam")]
    public void An_extension_that_adds_a_command_or_a_setting_without_declaring_its_seam_is_refused(string seams, string names)
    {
        var folder = Directory.CreateTempSubdirectory("prem-greeting-undeclared-").FullName;
        var allowed = GreetingSample.InstallInto(folder, seams: seams);

        var host = ExtensionHost.Load(folder, [allowed]);

        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.DidNotLoad, refused.Reason);
        Assert.Contains(names, refused.Detail);
        Assert.Empty(host.Commands);
        Assert.False(host.Settings.Contains(GreetingSample.SettingKey));
    }

    [Fact]
    public void An_older_version_refuses_the_seams_by_name()
    {
        Assert.True(ExtensionManifest.TryRead(Path.GetDirectoryName(GreetingSample.BuiltPath)!, out var manifest, out var problem), problem);

        var older = manifest!.SeamTooNew(seam => seam is SeamVersions.CommandName or SeamVersions.SettingName ? null : SeamVersions.Of(seam));

        Assert.NotNull(older);
        Assert.Contains("which this version does not have", older);
        Assert.Null(manifest.SeamTooNew(SeamVersions.Of));
        Assert.Equal(1, SeamVersions.Of("command"));
        Assert.Equal(1, SeamVersions.Of("setting"));
    }

    [Fact]
    public void A_second_extension_that_brings_the_same_command_or_setting_is_refused_and_the_first_keeps_them()
    {
        var folder = Directory.CreateTempSubdirectory("prem-greeting-twice-").FullName;
        var first = GreetingSample.InstallInto(folder, "a-greeting");
        var second = GreetingSample.InstallInto(folder, "b-greeting");

        var host = ExtensionHost.Load(folder, [first, second]);

        Assert.Equal("a-greeting", Assert.Single(host.Loaded).Name);
        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.NameTaken, refused.Reason);
        Assert.Contains("prem greeting is already a command of the extension \"a-greeting\"", refused.Detail);
        Assert.Equal("a-greeting", host.Settings.ExtensionOf(GreetingSample.SettingKey));
    }

    private static ExtensionVerb Verb(string name, bool writes = false) => new(name, writes, (_, _) => Task.FromResult(0));

    private static ExtensionCommand Command(string noun, string usage, params string[] verbs) =>
        new(noun, "A made-up command.", usage, [.. verbs.Select(v => Verb(v))]);

    [Fact]
    public void A_command_that_would_stand_in_for_a_built_in_one_or_disagree_with_its_usage_is_refused_as_it_registers()
    {
        var registrations = new ExtensionRegistrations();

        // A built-in command that takes no extension's subcommands, a built-in subcommand, and help.
        Assert.Contains("is built in", Assert.Throws<ArgumentException>(() =>
            registrations.AddCommand(Command("users", "prem users rename <name>", "rename"))).Message);
        Assert.Contains("is built in", Assert.Throws<ArgumentException>(() =>
            registrations.AddCommand(Command("extensions", "prem extensions trust <name>", "trust"))).Message);
        Assert.Contains("prem groups add is built in", Assert.Throws<ArgumentException>(() =>
            registrations.AddCommand(Command("groups", "prem groups add <name>", "add"))).Message);
        Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command("help", "prem help me", "me")));

        // Words a command line cannot be called by.
        foreach (var bad in new[] { "", "Yard", "two words", "-yard", "yard/", new string('y', 33) })
            Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command(bad, $"prem {bad} run", "run")));
        Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command("yard", "prem yard Run", "Run")));

        // The usage names exactly the subcommands registered.
        Assert.Contains("names the subcommands close and the extension registers close, open", Assert.Throws<ArgumentException>(() =>
            registrations.AddCommand(Command("yard", "prem yard close", "close", "open"))).Message);
        Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command("yard", "prem yard open|close", "open")));
        Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command("yard", "prem yard open", "open", "open")));
        Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command("yard", "no command here")));
        Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command("yard", new string('x', 4_001), "open")));
        Assert.Throws<ArgumentException>(() =>
            registrations.AddCommand(new ExtensionCommand("yard", "two\nlines", "prem yard open", [Verb("open")])));

        Assert.True(registrations.IsEmpty);

        // What is allowed: a command of its own, and a new subcommand under groups, each once.
        registrations.AddCommand(Command("yard", "prem yard open|close\r\n\r\n  --gate g   open: the gate", "open", "close"));
        registrations.AddCommand(Command("groups", "prem groups tally", "tally"));

        // What was checked is what is kept: a verb added to the list afterwards is not.
        var verbs = new List<ExtensionVerb> { Verb("open") };
        registrations.AddCommand(new ExtensionCommand("gate", "A made-up command.", "prem gate open", verbs));
        verbs.Add(Verb("close"));
        Assert.Equal(new[] { "open" }, registrations.Commands.Single(c => c.Noun == "gate").Verbs.Select(v => v.Name));
        Assert.Throws<ArgumentException>(() => registrations.AddCommand(Command("yard", "prem yard lock", "lock")));
        Assert.False(registrations.IsEmpty);
    }

    private static SettingDefinition Setting(string key, JsonElement? fallback = null, bool trust = false) =>
        new(key, "What it means.", "a small number", fallback, fallback is null ? "nothing is kept" : null,
            v => v.ValueKind == JsonValueKind.Number ? null : $"{key} takes a small number.", IsTrust: trust);

    [Fact]
    public void A_setting_that_would_stand_in_for_a_built_in_or_decide_trust_or_loading_is_refused_as_it_registers()
    {
        var registrations = new ExtensionRegistrations();

        foreach (var key in new[] { "trust.agents.minimum_tier", "trust.yard", "extensions.folder", "extensions.yard", SettingsCatalog.ConnectSnippets })
            Assert.Throws<ArgumentException>(() => registrations.AddSetting(Setting(key)));
        Assert.Throws<ArgumentException>(() => registrations.AddSetting(Setting("yard.size", trust: true)));
        foreach (var key in new[] { "", "Yard.size", "yard size", "yard-size" })
            Assert.Throws<ArgumentException>(() => registrations.AddSetting(Setting(key)));
        Assert.Contains("refuses its own default", Assert.Throws<ArgumentException>(() =>
            registrations.AddSetting(Setting("yard.size", JsonSerializer.SerializeToElement("large")))).Message);
        Assert.True(registrations.IsEmpty);

        registrations.AddSetting(Setting("yard.size", JsonSerializer.SerializeToElement(3)));
        Assert.Throws<ArgumentException>(() => registrations.AddSetting(Setting("yard.size")));
        Assert.False(registrations.IsEmpty);
    }

    [Fact]
    public void The_built_in_lists_the_host_checks_against_are_the_command_lines_own()
    {
        Assert.Equal(
            Help.Commands.Select(c => c.Verb).Concat(["help", "init-db"]).Order(StringComparer.Ordinal),
            BuiltInCommands.Names.Order(StringComparer.Ordinal));
        foreach (var (noun, subcommands) in BuiltInCommands.Open)
            Assert.Equal(
                Help.SubcommandsIn(noun, Help.For(noun)!).Order(StringComparer.Ordinal),
                subcommands.Order(StringComparer.Ordinal));
    }

    // --- The one dispatch ---

    [Fact]
    public async Task A_command_an_extension_adds_runs_and_its_change_is_in_the_record_by_the_command_lines_actor()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;

        var set = await RunAsync(Host, db, tenant, "greeting", "set", "Good morning");
        Assert.True(set.Exit == 0, set.Err);
        Assert.Equal("The greeting is now 'Good morning'.", set.Out.Trim());
        Assert.Equal(new[] { (GreetingSample.ChangeKind, "cli") }, await RecordAsync(db));

        var shown = await RunAsync(Host, db, tenant, "greeting", "show");
        Assert.Equal(0, shown.Exit);
        Assert.Equal("Good morning", shown.Out.Trim());

        var again = await RunAsync(Host, db, tenant, "greeting", "set", "Good morning");
        Assert.Equal(0, again.Exit);
        Assert.Single(await RecordAsync(db));

        var counted = await RunAsync(Host, db, tenant, "groups", "count");
        Assert.Equal(0, counted.Exit);
        Assert.EndsWith("group(s).", counted.Out.Trim());
    }

    [Fact]
    public async Task A_flag_the_usage_does_not_name_is_refused_with_the_usage_and_nothing_runs()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;

        var refused = await RunAsync(Host, db, tenant, "greeting", "set", "Hi", "--loud");
        Assert.Equal(1, refused.Exit);
        Assert.StartsWith("prem greeting does not take --loud.", refused.Err);
        Assert.Contains("prem greeting set <text> [--quiet]", refused.Err);
        Assert.Empty(await RecordAsync(db));

        var quiet = await RunAsync(Host, db, tenant, "greeting", "set", "Hi", "--quiet");
        Assert.Equal(0, quiet.Exit);
        Assert.Equal("", quiet.Out);
        Assert.Single(await RecordAsync(db));
    }

    [Fact]
    public async Task A_verb_that_refuses_its_arguments_says_so_in_one_line_and_exits_1()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;

        var none = await RunAsync(Host, db, tenant, "greeting", "set");
        Assert.Equal(1, none.Exit);
        Assert.Equal("Give one greeting: prem greeting set <text>.", none.Err.Trim());

        var tooLong = await RunAsync(Host, db, tenant, "greeting", "set", new string('h', 81));
        Assert.Equal(1, tooLong.Exit);
        Assert.Equal("greeting.text takes a text of 1 to 80 characters.", tooLong.Err.Trim());
        Assert.Empty(await RecordAsync(db));
    }

    [Theory]
    [InlineData(new[] { "greeting" }, 0, "prem greeting show")]
    [InlineData(new[] { "greeting", "--help" }, 0, "prem greeting show")]
    [InlineData(new[] { "greeting", "nod", "-h" }, 0, "prem greeting show")]
    [InlineData(new[] { "help", "greeting" }, 0, "prem greeting show")]
    [InlineData(new[] { "greeting", "set", "--help" }, 0, "prem greeting show")]
    [InlineData(new[] { "greeting", "nod" }, 1, "prem greeting has no subcommand 'nod'.")]
    [InlineData(new[] { "greeting", "--plan" }, 1, "prem greeting has no subcommand '--plan'.")]
    public async Task A_command_an_extension_adds_describes_itself_like_a_built_in_one(string[] args, int exit, string firstLine)
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;

        var run = await RunAsync(Host, db, tenant, args);

        Assert.Equal(exit, run.Exit);
        var printed = exit == 0 ? run.Out : run.Err;
        Assert.StartsWith(firstLine, printed.ReplaceLineEndings("\n").Split('\n')[0]);
        Assert.Contains("Added by the extension greeting-command 0.1.0.", printed);
        Assert.Empty(await RecordAsync(db));
    }

    [Theory]
    [InlineData("groups tally")]
    [InlineData("frobnicate")]
    [InlineData("help frobnicate")]
    [InlineData("help groups count")]
    public async Task A_call_no_extension_added_is_left_to_the_command_line_to_refuse(string call)
    {
        var args = call.Split(' ');
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;

        var run = await RunAsync(Host, db, tenant, args);

        Assert.Null(run.Exit);
        Assert.Equal("", run.Out + run.Err);
    }

    // --- prem settings and a profile ---

    private static Task<(int Exit, string Out, string Err)> Settings(
        PremagenticDatabase db, Guid tenant, ExtensionHost? host, params string[] args) =>
        ConsoleCapture.RunAsync(() => SettingsCommands.RunAsync(["settings", .. args], db, tenant, host));

    [Fact]
    public async Task Prem_settings_lists_checks_and_records_an_extensions_setting_while_it_is_loaded()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;
        var key = GreetingSample.SettingKey;

        var listed = await Settings(db, tenant, Host, "list");
        Assert.Contains(listed.Out.Split(Environment.NewLine),
            l => l.StartsWith(key + " ", StringComparison.Ordinal) && l.Contains("Hello") && l.EndsWith("(added by greeting-command)"));
        var got = await Settings(db, tenant, Host, "get", key);
        Assert.Contains("Default: Hello. It takes a text of 1 to 80 characters.", got.Out);
        Assert.Contains("Added by the extension greeting-command", got.Out);

        var refused = await Settings(db, tenant, Host, "set", key, new string('h', 81));
        Assert.Equal(1, refused.Exit);
        Assert.Equal("greeting.text takes a text of 1 to 80 characters.", refused.Err.Trim());
        Assert.Empty(await RecordAsync(db));

        var set = await Settings(db, tenant, Host, "set", key, "Evening");
        Assert.True(set.Exit == 0, set.Err);
        Assert.Equal(new[] { ("setting.set", "cli") }, await RecordAsync(db));
        Assert.Equal("Evening", (await RunAsync(Host, db, tenant, "greeting", "show")).Out.Trim());

        var unset = await Settings(db, tenant, Host, "unset", key);
        Assert.True(unset.Exit == 0, unset.Err);
        Assert.Equal(2, (await RecordAsync(db)).Count);
    }

    [Fact]
    public async Task Without_the_extension_its_setting_is_accepted_nowhere()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;
        var key = GreetingSample.SettingKey;

        foreach (var args in new[] { new[] { "set", key, "Hi" }, ["get", key], ["unset", key] })
        {
            var refused = await Settings(db, tenant, null, args);
            Assert.Equal(1, refused.Exit);
            Assert.StartsWith($"There is no setting '{key}'.", refused.Err);
        }
        Assert.DoesNotContain(key, (await Settings(db, tenant, null, "list")).Out);

        await Assert.ThrowsAsync<ArgumentException>(() => new SettingsStore(db, tenant).GetAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new Premagentic.Core.Evaluation.TuningSettingsStore(db, tenant).SetAsync(key, JsonSerializer.SerializeToElement("Hi"), Premagentic.Core.Admin.AdminActor.Cli()));
        Assert.Empty(await RecordAsync(db));
    }

    [Fact]
    public async Task A_profile_naming_an_extensions_setting_is_told_to_use_prem_settings()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;
        var folder = Directory.CreateTempSubdirectory("premagentic-profile-").FullName;
        File.WriteAllText(Path.Combine(folder, "profile.json"), """{ "name": "office", "version": "1" }""");
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{ "greeting.text": "Hi" }""");
        Assert.True(ProfileReader.TryRead(folder, out var profile, out var read), string.Join(" | ", read));
        var goldenSets = Directory.CreateTempSubdirectory("premagentic-golden-").FullName;

        var (plan, problems) = await ProfilePlanner.BuildAsync(profile!, db, tenant, goldenSets, settings: Host.Settings);

        Assert.Null(plan);
        Assert.Contains(problems, p => p.ToString().Contains(
            "'greeting.text' is a setting the extension greeting-command adds, and a profile does not set an extension's settings in this build. Set it with 'prem settings set'.",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prem_extensions_list_names_what_each_loaded_extension_adds()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;

        var listed = await ConsoleCapture.RunAsync(() => ExtensionsCommands.RunAsync(["extensions", "list"], db, tenant, Host));

        Assert.Equal(0, listed.Exit);
        Assert.Contains("    adds prem greeting show, prem greeting set (changes), prem groups count", listed.Out);
        Assert.Contains("    adds the setting(s) greeting.text", listed.Out);
    }

    [Fact]
    public async Task The_store_a_command_is_handed_refuses_the_keys_that_decide_trust_and_loading_and_checks_every_value()
    {
        var (db, tenant, _) = await EmptyAsync();
        await using var _ = db;
        var store = new CommandContext(["greeting", "set"], db, tenant, Premagentic.Core.Admin.AdminActor.Cli(), Host,
            TextWriter.Null, TextWriter.Null).SettingsStore();
        var allowed = ExtensionSettings.ToStored([("greeting-command", new string('a', 64))]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(ExtensionSettings.Allowed, allowed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(ExtensionSettings.Folder));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetAsync("trust.stale", JsonSerializer.SerializeToElement("shown-to-everyone")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetAsync(GreetingSample.SettingKey, JsonSerializer.SerializeToElement(new string('h', 81))));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetAsync("retrieval.rrf_k", JsonSerializer.SerializeToElement("forty")));
        await using (var count = db.DataSource.CreateCommand(
                         "SELECT count(*) FROM prem_config.setting WHERE key IN ('extensions.allowed', 'extensions.folder', 'greeting.text', 'retrieval.rrf_k')"))
            Assert.Equal(0L, await count.ExecuteScalarAsync());

        // The controls: a value the definition takes is written, and a store
        // made directly, as the core's own code makes one, still writes the
        // allow list, so the refusal above is the handed store's own.
        await store.SetAsync(GreetingSample.SettingKey, JsonSerializer.SerializeToElement("Hi"));
        Assert.NotNull(await store.GetAsync(GreetingSample.SettingKey));
        await new SettingsStore(db, tenant).SetAsync(ExtensionSettings.Allowed, allowed);
    }

    // --- The command line as a process ---

    [Fact]
    public async Task A_call_that_waits_for_the_extensions_on_a_database_it_cannot_use_stops_with_one_line_and_exit_2()
    {
        var extensions = Directory.CreateTempSubdirectory("prem-greeting-nodb-").FullName;

        // Nothing listens on port 1.
        var unreachable = await CliAsync("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=5", extensions, "greeting", "show");

        // A role that may connect and may not create the schema the migration needs.
        var name = "t_" + Guid.NewGuid().ToString("N");
        var role = "r_" + Guid.NewGuid().ToString("N");
        const string password = "an invented password for a test role";
        await using (var admin = Npgsql.NpgsqlDataSource.Create(server.AdminConnectionString))
        {
            await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
            await using (var login = admin.CreateCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}'")) await login.ExecuteNonQueryAsync();
        }
        var limited = new Npgsql.NpgsqlConnectionStringBuilder(server.AdminConnectionString)
        {
            Database = name, Username = role, Password = password,
        }.ConnectionString;
        var unmigrated = await CliAsync(limited, extensions, "greeting", "show");

        foreach (var run in new[] { unreachable, unmigrated })
        {
            Assert.True(run.Exit == 2, $"exit {run.Exit}: {run.Err}");
            var lines = run.Err.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("prem greeting stopped: The database cannot be used: ", Assert.Single(lines));
            Assert.DoesNotContain("   at ", run.Err);
        }
    }

    [Fact]
    public async Task The_command_line_runs_an_extensions_command_once_it_is_allowed_and_refuses_what_nobody_added()
    {
        var (db, tenant, connectionString) = await EmptyAsync();
        await using var _ = db;
        var extensions = Directory.CreateTempSubdirectory("prem-greeting-cli-").FullName;
        GreetingSample.InstallInto(extensions);

        var before = await CliAsync(connectionString, extensions, "greeting", "show");
        Assert.Equal(1, before.Exit);
        Assert.Contains("Unknown command 'greeting'. prem --help lists every command.", before.Err);

        var allowed = await CliAsync(connectionString, extensions, "extensions", "allow", Path.Combine(extensions, GreetingSample.Name));
        Assert.True(allowed.Exit == 0, allowed.Err);

        var set = await CliAsync(connectionString, extensions, "greeting", "set", "From the console");
        Assert.True(set.Exit == 0, set.Err);
        Assert.Equal("From the console", (await CliAsync(connectionString, extensions, "greeting", "show")).Out.Trim());
        Assert.Contains((GreetingSample.ChangeKind, "cli"), await RecordAsync(db));

        var settingSet = await CliAsync(connectionString, extensions, "settings", "set", GreetingSample.SettingKey, "Set as a setting");
        Assert.True(settingSet.Exit == 0, settingSet.Err);

        var tally = await CliAsync(connectionString, extensions, "groups", "tally");
        Assert.Equal(1, tally.Exit);
        Assert.StartsWith("prem groups has no subcommand 'tally'.", tally.Err.ReplaceLineEndings("\n").Split('\n').First(l => l.Length > 0));
        Assert.Contains("prem groups count", tally.Err);

        var counted = await CliAsync(connectionString, extensions, "groups", "count");
        Assert.True(counted.Exit == 0, counted.Err);
        Assert.EndsWith("group(s).", counted.Out.Trim());
    }

    /// <summary>
    /// The CLI's own build output on the given database, with the given
    /// extensions folder, and a hard limit, so a call that waits on anything
    /// fails the test by name instead of holding the run.
    /// </summary>
    internal static async Task<(int Exit, string Out, string Err)> CliAsync(string connectionString, string extensions, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connectionString;
        start.Environment["PREM_EXTENSIONS_DIR"] = extensions;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"'prem {string.Join(' ', args)}' did not end within 2 minutes.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string CliPath()
    {
        var path = typeof(ExtensionCommandTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}
