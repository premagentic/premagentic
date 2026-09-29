using System.Diagnostics;
using System.Reflection;
using Premagentic.Cli.Admin;
using Premagentic.Core.Admin;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Every administrator verb at the command line that changes something
/// leaves its row in the change record, by the command line's actor, as the
/// portal's forms always did. The theory runs over the CLI's own table of
/// verbs, and over the verbs a sample extension adds through the real host,
/// so a verb added either way without a case here fails by name. Requires
/// a running Docker daemon.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class AdminVerbRecordTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    /// <summary>
    /// A small world, made through the stores directly so none of it is in
    /// the record: Alice and Bob (Bob disabled), the groups Staff (Alice in
    /// it), Old and Crew, the service agent svc (granted Crew) with one token,
    /// the disabled agent off, a mapping and a rule; and for the authorization
    /// flow, the client Desk Tool with one live grant of Alice's, the
    /// disabled client Old Tool, and the client Invented Desk, stored from a
    /// metadata document.
    /// </summary>
    private sealed record World(PremagenticDatabase Db, Guid Tenant, string TokenId, string ConnectionString) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private const string DeskTool = "prem_cli_0123456789abcdef01234567";
    private const string OldTool = "prem_cli_89abcdef0123456789abcdef";
    private const string DeskGrant = "0123456789abcdef01234567";
    private const string DocumentTool = "https://app.example/client.json";

    private async Task<World> NewAsync()
    {
        var connectionString = await server.CreateDatabaseAsync();
        var db = new PremagenticDatabase(connectionString);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var store = new IdentityStore(db, tenant);
        var alice = await store.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await store.CreateUserAsync("bob", "Bob", Role.Member);
        await store.SetUserDisabledAsync(bob.Id, true);
        var staff = await store.CreateGroupAsync("Staff");
        await store.AddMemberAsync(staff.Id, alice.Id);
        await store.CreateGroupAsync("Old");
        var crew = await store.CreateGroupAsync("Crew");
        var svc = await store.CreateAgentAsync("svc", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        await store.GrantGroupAsync(svc.Id, crew.Id);
        var off = await store.CreateAgentAsync("off", alice.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        await store.SetAgentDisabledAsync(off.Id, true);
        var token = await store.IssueTokenAsync(svc.Id, TimeSpan.FromDays(30));
        // The flow's rows written as they are stored, since every path that
        // writes them also writes the record.
        var desk = await store.CreateAgentAsync("oauth-" + DeskGrant, alice.Id, AgentMode.ActsForUser, 60, null,
            ModelLocation.Hosted, "A Vendor", AgentOrigin.OAuth(DeskTool));
        await using (var flow = db.DataSource.CreateCommand("""
            INSERT INTO prem_config.oauth_client(id, tenant_id, name, redirect_uris, registered_by, created_at, disabled)
            VALUES (@desk, @tenant, 'Desk Tool', ARRAY['http://127.0.0.1/callback'], 'cli:test', now(), false),
                   (@old, @tenant, 'Old Tool', ARRAY['http://127.0.0.1/callback'], 'cli:test', now(), true);
            INSERT INTO prem_config.oauth_client(id, tenant_id, name, redirect_uris, registered_by, created_at, disabled,
                                                 document_sha256, document_stored_at)
            VALUES (@document, @tenant, 'Invented Desk', ARRAY['http://127.0.0.1:3000/callback'], 'cli:test', now(), false,
                    repeat('0', 64), now());
            INSERT INTO prem_config.oauth_grant(id, tenant_id, client_id, user_id, agent_id, user_generation, agent_generation,
                                                scope, resource, created_at, activated_at, expires_at)
            VALUES (@grant, @tenant, @desk, @alice, @agent, 0, 0, 'read', 'https://prem.test/mcp', now(), now(), now() + interval '90 days');
            """))
        {
            flow.Parameters.AddWithValue("desk", DeskTool);
            flow.Parameters.AddWithValue("old", OldTool);
            flow.Parameters.AddWithValue("document", DocumentTool);
            flow.Parameters.AddWithValue("grant", DeskGrant);
            flow.Parameters.AddWithValue("tenant", tenant);
            flow.Parameters.AddWithValue("alice", alice.Id);
            flow.Parameters.AddWithValue("agent", desk.Id);
            await flow.ExecuteNonQueryAsync();
        }
        await new AclStore(db, tenant).SetRuleAsync(
            new FolderRule(AdminCommands.FileSystemSource, "old", await new PrincipalNames(store).ToAclSetAsync(["allow everyone"])));
        return new World(db, tenant, token.Record.Id, connectionString);
    }

    /// <summary>One call of each verb that writes, in the world above.</summary>
    private static readonly Dictionary<string, Func<World, string[]>> Cases = new(StringComparer.Ordinal)
    {
        ["users add"] = _ => ["users", "add", "carol", "--password-file", PasswordFile()],
        ["users disable"] = _ => ["users", "disable", "alice"],
        ["users enable"] = _ => ["users", "enable", "bob"],
        ["users set-password"] = _ => ["users", "set-password", "alice", "--password-file", PasswordFile()],
        ["groups add"] = _ => ["groups", "add", "Night"],
        ["groups rename"] = _ => ["groups", "rename", "Old", "Older"],
        ["groups remove"] = _ => ["groups", "remove", "Old"],
        ["groups members"] = _ => ["groups", "members", "Staff", "--add", "bob"],
        ["agents add"] = _ => ["agents", "add", "helper", "--owner", "alice", "--mode", "service", "--model", "local"],
        ["agents set"] = _ => ["agents", "set", "svc", "--model", "hosted", "--vendor", "a vendor"],
        ["agents disable"] = _ => ["agents", "disable", "svc"],
        ["agents enable"] = _ => ["agents", "enable", "off"],
        ["agents remove"] = _ => ["agents", "remove", "off"],
        ["agents grant"] = _ => ["agents", "grant", "svc", "Staff"],
        ["agents ungrant"] = _ => ["agents", "ungrant", "svc", "Crew"],
        ["tokens issue"] = _ => ["tokens", "issue", "svc"],
        ["tokens revoke"] = w => ["tokens", "revoke", w.TokenId],
        ["tokens reissue"] = w => ["tokens", "reissue", w.TokenId],
        ["rules set"] = _ => ["rules", "set", "--prefix", "handbook", "--public"],
        ["rules remove"] = _ => ["rules", "remove", "--prefix", "old"],
        ["oauth clients add"] = _ => ["oauth", "clients", "add", "Night Tool", "--redirect", "http://127.0.0.1/callback"],
        ["oauth clients replace"] = _ => ["oauth", "clients", "replace", "--metadata-file", DocumentFile(), "--id", DocumentTool],
        ["oauth clients disable"] = _ => ["oauth", "clients", "disable", DeskTool],
        ["oauth clients enable"] = _ => ["oauth", "clients", "enable", OldTool],
        ["oauth clients remove"] = _ => ["oauth", "clients", "remove", DeskTool],
        ["oauth grants revoke"] = _ => ["oauth", "grants", "revoke", DeskGrant],
        ["greeting set"] = _ => ["greeting", "set", "Good evening"],
    };

    private const string Password = "a long enough password";

    /// <summary>
    /// A password in a file, for the verbs that read one. In process, a verb
    /// reads a piped secret from <see cref="Console.In"/> only while the test
    /// host's own standard input is redirected. When that input is a console,
    /// as under a hidden cmd window, the verb reads keys from the console
    /// instead and waits for a key nobody can press, so the cases give the file
    /// and nothing in process waits at a console. The piped way is proven by
    /// its own test, through the CLI's own process.
    /// </summary>
    private static string PasswordFile()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("premagentic-password-").FullName, "password.txt");
        File.WriteAllText(path, Password + "\n");
        return path;
    }

    private static string DocumentFile()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("premagentic-document-").FullName, "client.json");
        File.WriteAllText(path, OAuthClientDocumentReadTests.Document(DocumentTool));
        return path;
    }

    /// <summary>The kind of row each verb leaves, the portal's kind where the portal has the same change.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        ["users add"] = "user.add", ["users disable"] = "user.disable", ["users enable"] = "user.enable",
        ["users set-password"] = "user.password", ["groups add"] = "group.add", ["groups rename"] = "group.rename",
        ["groups remove"] = "group.remove", ["groups members"] = "group.member.add",
        ["agents add"] = "agent.add", ["agents set"] = "agent.model",
        ["agents disable"] = "agent.disable", ["agents enable"] = "agent.enable", ["agents grant"] = "agent.grant",
        ["agents ungrant"] = "agent.ungrant", ["tokens issue"] = "token.issue", ["tokens revoke"] = "token.revoke",
        ["agents remove"] = AgentLifecycle.RemoveKind, ["tokens reissue"] = AgentLifecycle.ReissueKind,
        ["rules set"] = "rule.set", ["rules remove"] = "rule.remove",
        ["oauth clients add"] = "oauth.client.add", ["oauth clients replace"] = "oauth.client.replace",
        ["oauth clients disable"] = "oauth.client.disable",
        ["oauth clients enable"] = "oauth.client.enable", ["oauth clients remove"] = "oauth.client.remove",
        ["oauth grants revoke"] = "oauth.grant.revoke",
        ["greeting set"] = GreetingSample.ChangeKind,
    };

    /// <summary>The greeting sample, loaded through the host from its own folder, as a deployment loads it.</summary>
    private static readonly ExtensionHost Extensions = GreetingSample.LoadedOnce();

    public static TheoryData<string> WriteVerbs()
    {
        var data = new TheoryData<string>();
        foreach (var verb in AllWriteVerbs()) data.Add(verb);
        return data;
    }

    /// <summary>
    /// The verbs that write, of the administrator verbs, of prem oauth, and of
    /// the commands an extension adds, as the command line spells them.
    /// </summary>
    private static IEnumerable<string> AllWriteVerbs() =>
        AdminCommands.WriteVerbs.Select(v => $"{v.Noun} {v.Verb}")
            .Concat(OAuthCommands.WriteVerbs.Select(v => $"{OAuthCommands.Verb} {v.Noun} {v.Verb}"))
            .Concat(ExtensionCommands.WriteVerbs(Extensions).Select(v => $"{v.Noun} {v.Verb}"));

    private static async Task<int> RunAsync(string[] args, World w) =>
        args[0] == OAuthCommands.Verb ? await OAuthCommands.RunAsync(args, w.Db, w.Tenant)
        : AdminCommands.Verbs.Contains(args[0]) ? await AdminCommands.RunAsync(args, w.Db, w.Tenant)
        : await ExtensionCommands.RunAsync(Extensions, args, w.Db, w.Tenant, Console.Out, Console.Error) ?? 1;

    /// <summary>
    /// The CLI's own build output on this world's database, with
    /// <paramref name="input"/> as its standard input, and a hard limit, so a
    /// verb that waits on anything else fails the test by name instead of
    /// holding the run.
    /// </summary>
    private static async Task<(int Exit, string Out, string Err)> CliAsync(string[] args, World w, string input)
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
        foreach (var name in new[] { "PREM_CREDENTIALS_FILE", "PREM_SEARCH_CONNECTION_STRING" })
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = w.ConnectionString;
        start.Environment["PREM_TENANT_KEY"] = "t";
        start.Environment["PREM_TENANT_NAME"] = "T";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(input);
        process.StandardInput.Close();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"'prem {string.Join(' ', args)}' did not end within 2 minutes with its input given.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    // The CLI's own build output, as the test project records it (see its project file).
    private static string CliPath()
    {
        var path = typeof(AdminVerbRecordTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }

    private static async Task<List<(string Kind, string Surface)>> RecordAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT kind, actor_surface FROM prem_config.admin_event ORDER BY id");
        var rows = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    [Theory]
    [MemberData(nameof(WriteVerbs))]
    public async Task Every_verb_that_changes_something_leaves_its_row_in_the_change_record(string verb)
    {
        Assert.True(Cases.TryGetValue(verb, out var call),
            $"'prem {verb}' changes something and has no case in this test. Add one, so its row in the change record is proven.");
        await using var w = await NewAsync();
        Assert.Empty(await RecordAsync(w.Db));

        var args = call(w);
        var run = await ConsoleCapture.RunAsync(() => RunAsync(args, w));

        Assert.True(run.Exit == 0, $"'prem {verb}' exited {run.Exit}: {run.Err}");
        var rows = await RecordAsync(w.Db);
        Assert.Contains((Kinds[verb], "cli"), rows);
        Assert.All(rows, r => Assert.Equal("cli", r.Surface));
    }

    [Fact]
    public void The_verbs_marked_as_writing_are_exactly_the_ones_this_test_proves()
    {
        // The other direction from the theory: a verb that writes but is marked
        // as a read would drop out of the theory and pass unproven.
        Assert.Equal(Cases.Keys.Order(StringComparer.Ordinal), AllWriteVerbs().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_password_piped_on_standard_input_is_set_and_leaves_its_row()
    {
        // Through the CLI's own process, so standard input is a pipe whatever
        // the test host's own input is.
        await using var w = await NewAsync();

        var run = await CliAsync(["users", "set-password", "alice"], w, Password);

        Assert.True(run.Exit == 0, $"'prem users set-password' exited {run.Exit}: {run.Err}");
        Assert.Contains((Kinds["users set-password"], "cli"), await RecordAsync(w.Db));
        await using var cmd = w.Db.DataSource.CreateCommand("SELECT password_hash FROM prem_config.app_user WHERE sign_in_name = 'alice'");
        Assert.True(new PasswordHasher().Verify(Password, await cmd.ExecuteScalarAsync() as string));
    }

    [Fact]
    public async Task A_verb_that_changes_nothing_records_nothing()
    {
        await using var w = await NewAsync();

        foreach (var args in new[]
                 {
                     new[] { "users", "enable", "alice" },
                     ["agents", "disable", "off"],
                     ["agents", "ungrant", "svc", "Staff"],
                     ["groups", "members", "Staff", "--add", "alice"],
                     ["users", "list"],
                     ["agents", "list"],
                     ["rules", "list"],
                     ["oauth", "clients", "list"],
                     ["oauth", "grants", "list"],
                     ["oauth", "clients", "enable", DeskTool],
                     ["oauth", "clients", "disable", OldTool],
                     ["oauth", "grants", "revoke", "--user", "bob"],
                 })
        {
            var run = await ConsoleCapture.RunAsync(() => RunAsync(args, w));
            Assert.True(run.Exit == 0, $"{string.Join(' ', args)}: {run.Err}");
        }

        Assert.Empty(await RecordAsync(w.Db));
    }

    [Fact]
    public async Task A_refused_change_records_nothing_and_changes_nothing()
    {
        await using var w = await NewAsync();

        var refused = await ConsoleCapture.RunAsync(() => AdminCommands.RunAsync(["groups", "add", "Staff"], w.Db, w.Tenant));

        Assert.Equal(1, refused.Exit);
        Assert.Empty(await RecordAsync(w.Db));
    }
}
