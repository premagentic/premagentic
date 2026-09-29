using System.Security.Cryptography;
using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// One database laid out the way an installed deployment is, with the three
/// roles as setup makes them: the owner owns every object and ran the
/// migrations; the application role reads and writes rows and is the one role
/// in <c>prem_config.index_writer</c>; the search role may only select from the
/// two index tables. Roles belong to the whole server, so every instance has
/// its own names.
/// <para>
/// The world is the gate tests' world. Alice is staff; Bob is staff and a
/// contractor; Carol owns the report bot. Alice's assistant acts for her; the
/// report bot is a service agent holding the auditors group. <c>pub</c> allows
/// everyone; <c>staff</c> denies contractors, then allows staff; <c>audit</c>
/// allows auditors; <c>hr</c> denies the assistant, then allows Alice;
/// <c>none</c> has no rule.
/// </para>
/// </summary>
internal sealed class RlsDatabase : IAsyncDisposable
{
    public const string Handbook = "pub/handbook.md";
    public const string Plan = "staff/plan.md";
    public const string AuditLog = "audit/log.md";
    public const string Pay = "hr/pay.md";
    public const string Draft = "none/draft.md";
    public static readonly string[] AllPaths = [AuditLog, Pay, Draft, Handbook, Plan];

    private RlsDatabase() { }

    public required string AdminConnection { get; init; }
    public required string OwnerConnection { get; init; }
    public required string AppConnection { get; init; }
    public required string SearchConnection { get; init; }
    public required string OwnerRole { get; init; }
    public required string AppRole { get; init; }
    public required string SearchRoleName { get; init; }

    /// <summary>The application role's database: ingest, identity, caller sessions.</summary>
    public required PremagenticDatabase App { get; init; }

    public required SearchRole Search { get; init; }
    public required Guid Tenant { get; init; }
    public required IdentityStore Identity { get; init; }
    public required SeededEmbeddingProvider Embedder { get; init; }
    public required User Alice { get; init; }
    public required User Bob { get; init; }
    public required User Carol { get; init; }
    public required Group Staff { get; init; }
    public required string AssistantToken { get; init; }
    public required string BotToken { get; init; }

    public static async Task<RlsDatabase> NewAsync(DatastoreTestDatabase server)
    {
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var (owner, app, search) = ("rls_owner_" + suffix, "rls_app_" + suffix, "rls_search_" + suffix);
        var database = "rls_" + suffix;
        var passwords = new[] { NewPassword(), NewPassword(), NewPassword() };

        await using (var admin = NpgsqlDataSource.Create(server.AdminConnectionString))
        {
            foreach (var (role, password) in new[] { owner, app, search }.Zip(passwords))
                await ExecAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
            await ExecAsync(admin, $"CREATE DATABASE {database} OWNER {owner}");
        }

        string As(string role, string password) => new NpgsqlConnectionStringBuilder(server.AdminConnectionString)
        {
            Database = database, Username = role, Password = password, Pooling = true,
        }.ConnectionString;
        var ownerConnection = As(owner, passwords[0]);
        var appConnection = As(app, passwords[1]);
        var searchConnection = As(search, passwords[2]);

        // Migrations as the owner, then the grants setup makes.
        await using (var ownerDb = new PremagenticDatabase(ownerConnection))
            await ownerDb.MigrateAsync();
        await using (var ownerSource = NpgsqlDataSource.Create(ownerConnection))
        {
            await ExecAsync(ownerSource, $"""
                REVOKE ALL ON DATABASE {database} FROM PUBLIC;
                GRANT CONNECT ON DATABASE {database} TO {app}, {search};
                GRANT USAGE ON SCHEMA prem_config, prem_index TO {app}, {search};
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA prem_config, prem_index TO {app};
                REVOKE INSERT, UPDATE, DELETE ON prem_config.schema_migration, prem_index.schema_migration FROM {app};
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA prem_config, prem_index TO {app};
                GRANT EXECUTE ON FUNCTION prem_config.open_caller_session(TEXT, UUID, UUID, UUID, BIGINT[], INTERVAL),
                                          prem_config.close_caller_session(TEXT) TO {app};
                REVOKE ALL ON prem_config.caller_session FROM {app};
                GRANT SELECT ON prem_index.document, prem_index.chunk TO {search};
                GRANT EXECUTE ON FUNCTION prem_index.text_matches TO {app}, {search};
                INSERT INTO prem_config.index_writer(role_name) VALUES ('{app}');
                """);
        }

        var db = new PremagenticDatabase(appConnection);
        var tenant = await db.EnsureTenantAsync("t", "T");
        var identity = new IdentityStore(db, tenant);
        var names = new PrincipalNames(identity);
        var rules = new AclStore(db, tenant);

        var alice = await identity.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await identity.CreateUserAsync("bob", "Bob", Role.Member);
        var carol = await identity.CreateUserAsync("carol", "Carol", Role.Administrator);
        var staff = await identity.CreateGroupAsync("Staff");
        var contractors = await identity.CreateGroupAsync("Contractors");
        var auditors = await identity.CreateGroupAsync("Auditors");
        await identity.AddMemberAsync(staff.Id, alice.Id);
        await identity.AddMemberAsync(staff.Id, bob.Id);
        await identity.AddMemberAsync(contractors.Id, bob.Id);
        var assistant = await identity.CreateAgentAsync("assistant", alice.Id, AgentMode.ActsForUser, 60, null, ModelLocation.Local);
        var bot = await identity.CreateAgentAsync("report-bot", carol.Id, AgentMode.Service, 60, null, ModelLocation.Local);
        await identity.GrantGroupAsync(bot.Id, auditors.Id);

        const string source = "test-datastore";
        await rules.SetRuleAsync(new FolderRule(source, "pub", await names.ToAclSetAsync(["allow everyone"])));
        await rules.SetRuleAsync(new FolderRule(source, "staff", await names.ToAclSetAsync(["deny group:Contractors", "allow group:Staff"])));
        await rules.SetRuleAsync(new FolderRule(source, "audit", await names.ToAclSetAsync(["allow group:Auditors"])));
        await rules.SetRuleAsync(new FolderRule(source, "hr", await names.ToAclSetAsync(["deny agent:assistant", "allow user:alice"])));

        var embedder = new SeededEmbeddingProvider();
        var summary = await new IngestPipeline(db, embedder).RunAsync(tenant, new DatastoreSource("", [
            DatastoreSource.Doc(Handbook, "## Handbook\nthe zeppelin handbook for everyone", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Plan, "## Plan\nthe zeppelin staff plan", DocumentAccess.FolderRules),
            DatastoreSource.Doc(AuditLog, "## Log\nthe zeppelin audit log", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Pay, "## Pay\nthe zeppelin pay bands", DocumentAccess.FolderRules),
            DatastoreSource.Doc(Draft, "## Draft\nthe zeppelin draft nobody may read", DocumentAccess.FolderRules),
        ]));
        if (summary.Ingested != 5) throw new InvalidOperationException($"The fixture ingested {summary.Ingested} documents, not 5.");

        return new RlsDatabase
        {
            AdminConnection = new NpgsqlConnectionStringBuilder(server.AdminConnectionString) { Database = database }.ConnectionString,
            OwnerConnection = ownerConnection, AppConnection = appConnection, SearchConnection = searchConnection,
            OwnerRole = owner, AppRole = app, SearchRoleName = search,
            App = db, Search = new SearchRole(searchConnection), Tenant = tenant, Identity = identity, Embedder = embedder,
            Alice = alice, Bob = bob, Carol = carol, Staff = staff,
            AssistantToken = (await identity.IssueTokenAsync(assistant.Id, TimeSpan.FromDays(30))).PlainText,
            BotToken = (await identity.IssueTokenAsync(bot.Id, TimeSpan.FromDays(30))).PlainText,
        };
    }

    public HybridSearch NewSearch() => new(App, Embedder, searchRole: Search);

    /// <summary>The distinct paths a search as <paramref name="scope"/> returns, sorted.</summary>
    public async Task<string[]> PathsAsync(HybridSearch search, AccessScope scope) =>
        (await search.SearchAsync(Tenant, "zeppelin", new SearchOptions(scope, TopK: 20)))
        .Hits.Select(h => h.Path).Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>Values of the first column, as the given connection reads them.</summary>
    public static async Task<List<string>> ReadAsync(string connection, string sql, Action<NpgsqlCommand>? parameters = null)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var cmd = source.CreateCommand(sql);
        parameters?.Invoke(cmd);
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? "<null>" : Convert.ToString(reader.GetValue(0))!);
        return values;
    }

    /// <summary>Runs <paramref name="sql"/> as the given connection, for what it does rather than what it returns.</summary>
    public static async Task ExecAsync(string connection, string sql)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await ExecAsync(source, sql);
    }

    public async ValueTask DisposeAsync()
    {
        await Search.DisposeAsync();
        await App.DisposeAsync();
    }

    private static async Task ExecAsync(NpgsqlDataSource source, string sql)
    {
        await using var cmd = source.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    // Letters and digits only, so it can sit inside a quoted SQL literal.
    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
