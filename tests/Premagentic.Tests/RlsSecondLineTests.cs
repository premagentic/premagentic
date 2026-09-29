using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// The second line: row-level security on the index, as the roles setup makes,
/// on stock PostgreSQL. Search and section reads connect as the search role and
/// are bound to one caller session; the database itself refuses every row the
/// caller may not read, whatever the query asks for.
/// <para>
/// Each case asserts both directions, and each refusal has its control: the
/// same read as the application role, or with the right session, returns rows.
/// </para>
/// Requires a running Docker daemon.
/// </summary>
public sealed class RlsSecondLineTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const string InsufficientPrivilege = "42501";

    [Fact]
    public async Task Two_users_and_two_agents_each_see_exactly_what_the_rules_give_them_through_the_search_role()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var search = r.NewSearch();

        Assert.Equal([RlsDatabase.Pay, RlsDatabase.Handbook, RlsDatabase.Plan], await r.PathsAsync(search, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id)));
        Assert.Equal([RlsDatabase.Handbook], await r.PathsAsync(search, await CallerAccess.ForUserAsync(r.Identity, r.Bob.Id)));
        Assert.Equal([RlsDatabase.Handbook, RlsDatabase.Plan], await r.PathsAsync(search, await CallerAccess.ForAgentTokenAsync(r.Identity, r.AssistantToken)));
        Assert.Equal([RlsDatabase.AuditLog, RlsDatabase.Handbook], await r.PathsAsync(search, await CallerAccess.ForAgentTokenAsync(r.Identity, r.BotToken)));
        Assert.Equal([RlsDatabase.Handbook], await r.PathsAsync(search, AccessScope.PublicOnly));
        Assert.Equal(RlsDatabase.AllPaths, await r.PathsAsync(search, AccessScope.UnrestrictedAudited("test")));
    }

    [Fact]
    public async Task A_section_fetch_through_the_search_role_obeys_its_caller()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var sections = new SectionFetcher(r.App, r.Search);

        Assert.Null(await sections.GetAsync(r.Tenant, await CallerAccess.ForUserAsync(r.Identity, r.Bob.Id), RlsDatabase.Pay, null, false));
        var alice = await sections.GetAsync(r.Tenant, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id), RlsDatabase.Pay, null, false);
        Assert.Contains("pay bands", Assert.Single(alice!.Chunks).Content);
    }

    [Fact]
    public async Task A_hand_written_query_as_the_search_role_reads_only_what_its_bound_session_permits()
    {
        await using var r = await RlsDatabase.NewAsync(server);

        // No gate at all: every row the table has, as the database lets through.
        const string documents = "SELECT path FROM prem_index.document ORDER BY path";
        const string chunks = "SELECT d.path FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id ORDER BY 1";
        const string bareChunks = "SELECT count(*) FROM prem_index.chunk";

        Assert.Equal([RlsDatabase.Handbook], await BoundAsync(r, r.Bob, documents));
        Assert.Equal([RlsDatabase.Handbook], await BoundAsync(r, r.Bob, chunks));
        Assert.Equal(["1"], await BoundAsync(r, r.Bob, bareChunks));
        Assert.Equal([RlsDatabase.Pay, RlsDatabase.Handbook, RlsDatabase.Plan], await BoundAsync(r, r.Alice, documents));
        Assert.Equal(["3"], await BoundAsync(r, r.Alice, bareChunks));
    }

    [Fact]
    public async Task With_no_session_the_search_role_reads_nothing_and_the_application_role_and_owner_read_everything()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        const string documents = "SELECT count(*) FROM prem_index.document";
        const string chunks = "SELECT count(*) FROM prem_index.chunk";

        Assert.Equal(["0"], await RlsDatabase.ReadAsync(r.SearchConnection, documents));
        Assert.Equal(["0"], await RlsDatabase.ReadAsync(r.SearchConnection, chunks));

        // The mirror, so the policy is shown able to let rows through: the
        // application role writes the index, is listed in index_writer, and is
        // not behind the policy; the owner bypasses row-level security.
        Assert.Equal(["5"], await RlsDatabase.ReadAsync(r.AppConnection, documents));
        Assert.Equal(["5"], await RlsDatabase.ReadAsync(r.AppConnection, chunks));
        Assert.Equal(["5"], await RlsDatabase.ReadAsync(r.OwnerConnection, documents));
    }

    [Fact]
    public async Task The_binding_cannot_be_set_by_hand()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        await using var source = NpgsqlDataSource.Create(r.SearchConnection);
        await using var conn = await source.OpenConnectionAsync();

        async Task<string> InTransaction(string setup)
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand(setup, conn, tx)) await set.ExecuteNonQueryAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM prem_index.document", conn, tx);
            var result = Convert.ToString(await count.ExecuteScalarAsync())!;
            await tx.RollbackAsync();
            return result;
        }

        var guessed = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Assert.Equal("0", await InTransaction($"SET LOCAL premagentic.caller_session = '{guessed}'"));
        Assert.Equal("0", await InTransaction("SET LOCAL premagentic.caller_session = 'not a session'"));
        Assert.Equal("0", await InTransaction("SET LOCAL premagentic.acl_set_ids = '{1,2,3,4,5,6,7,8,9,10}'"));

        await using (var bind = new NpgsqlCommand($"SELECT prem_config.bind_caller_session('{guessed}')", conn))
            Assert.Contains("no open caller session", (await Assert.ThrowsAsync<PostgresException>(() => bind.ExecuteNonQueryAsync())).MessageText);

        // A session that was really opened binds, and once closed, binds nothing.
        var session = await CallerSessions.OpenAsync(r.App, r.Tenant, await Resolved(r, r.Alice), default);
        Assert.Equal("3", await InTransaction($"SELECT prem_config.bind_caller_session('{session}')"));
        await CallerSessions.CloseAsync(r.App, session);
        Assert.Equal("0", await InTransaction($"SET LOCAL premagentic.caller_session = '{session}'"));
    }

    [Fact]
    public async Task The_search_role_cannot_open_a_session_read_the_sessions_or_call_the_application_roles_functions()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var id = new string('a', 64);

        foreach (var sql in new[]
        {
            $"SELECT prem_config.open_caller_session('{id}', '{r.Tenant}', NULL, NULL, ARRAY[1,2,3]::bigint[], interval '1 minute')",
            $"SELECT prem_config.close_caller_session('{id}')",
            "SELECT count(*) FROM prem_config.caller_session",
            "SELECT count(*) FROM prem_config.index_writer",
            $"INSERT INTO prem_config.index_writer(role_name) VALUES ('{r.SearchRoleName}')",
            "SELECT count(*) FROM prem_config.acl_set",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => RlsDatabase.ExecAsync(r.SearchConnection, sql));
            Assert.True(ex.SqlState == InsufficientPrivilege, $"'{sql}' failed with {ex.SqlState}, not a permission error");
        }

        // The controls: the application role opens and closes sessions, and
        // still cannot read the sessions or add a role to index_writer.
        await RlsDatabase.ExecAsync(r.AppConnection,
            $"SELECT prem_config.open_caller_session('{id}', '{r.Tenant}', NULL, NULL, ARRAY[1]::bigint[], interval '1 minute')");
        await RlsDatabase.ExecAsync(r.AppConnection, $"SELECT prem_config.close_caller_session('{id}')");
        foreach (var sql in new[]
        {
            "SELECT count(*) FROM prem_config.caller_session",
            $"INSERT INTO prem_config.index_writer(role_name) VALUES ('{r.SearchRoleName}')",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => RlsDatabase.ExecAsync(r.AppConnection, sql));
            Assert.True(ex.SqlState == InsufficientPrivilege, $"'{sql}' as the application role failed with {ex.SqlState}");
        }
        Assert.Equal([r.AppRole], await RlsDatabase.ReadAsync(r.OwnerConnection, "SELECT role_name FROM prem_config.index_writer"));
    }

    [Fact]
    public async Task The_text_match_function_applies_the_bound_session_whatever_its_caller_passes()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        // Every call here claims the most a caller can: unrestricted, historical,
        // any trust tier, stale content included. Only the session decides.
        var matches = $"SELECT m.path FROM {TextMatchFunction.Call("to_tsquery('english', 'zeppelin')")} m ORDER BY 1";
        var count = $"SELECT count(*) FROM {TextMatchFunction.Call("to_tsquery('english', 'zeppelin')")} m";
        void AskForEverything(NpgsqlCommand cmd) => AddEverything(cmd, r.Tenant);

        // Unbound, the search role gets nothing from it; bound, what its session permits.
        Assert.Equal(["0"], await RlsDatabase.ReadAsync(r.SearchConnection, count, AskForEverything));
        Assert.Equal([RlsDatabase.Handbook], await BoundAsync(r, r.Bob, matches, AskForEverything));
        Assert.Equal([RlsDatabase.Pay, RlsDatabase.Handbook, RlsDatabase.Plan], await BoundAsync(r, r.Alice, matches, AskForEverything));

        // The control: the application role, which the policies do not bind, gets every match.
        Assert.Equal(["5"], await RlsDatabase.ReadAsync(r.AppConnection, count, AskForEverything));

        // Its arguments are the tenant, the query, the gates' parameters, the
        // exclusions and the limit; it runs as the owner with its search path
        // pinned; and a role setup did not grant it to cannot call it at all.
        Assert.Equal(
            ["p_tenant uuid, p_query tsquery, g_unrestricted boolean, g_permitted bigint[], g_historical boolean, " +
             "g_trust_min_tier smallint, g_freshness_include_stale boolean, g_freshness_now timestamp with time zone, " +
             "p_exclude uuid[], p_limit integer"],
            await RlsDatabase.ReadAsync(r.OwnerConnection, "SELECT pg_get_function_arguments('prem_index.text_matches'::regproc)"));
        Assert.Equal(["True"], await RlsDatabase.ReadAsync(r.OwnerConnection,
            "SELECT p.prosecdef AND p.proowner = (SELECT datdba FROM pg_database WHERE datname = current_database()) " +
            "AND p.proconfig @> ARRAY['search_path=pg_catalog, pg_temp'] FROM pg_proc p WHERE p.oid = 'prem_index.text_matches'::regproc"));
        await RlsDatabase.ExecAsync(r.OwnerConnection, $"REVOKE EXECUTE ON FUNCTION prem_index.text_matches FROM {r.SearchRoleName}");
        var refused = await Assert.ThrowsAsync<PostgresException>(() => RlsDatabase.ReadAsync(r.SearchConnection, count, AskForEverything));
        Assert.Equal(InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task The_callers_own_lists_bind_where_the_session_reads_the_whole_index()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var matches = $"SELECT m.path FROM {TextMatchFunction.Call("to_tsquery('english', 'zeppelin')")} m ORDER BY 1";

        // The application role reads the whole index, so no session limits it:
        // through the function it gets exactly what the lists it passes allow.
        Assert.Equal([RlsDatabase.Handbook],
            await RlsDatabase.ReadAsync(r.AppConnection, matches, AskWith(r, await Resolved(r, r.Bob))));
        Assert.Equal([RlsDatabase.Pay, RlsDatabase.Handbook, RlsDatabase.Plan],
            await RlsDatabase.ReadAsync(r.AppConnection, matches, AskWith(r, await Resolved(r, r.Alice))));

        // The control: claiming every list, it gets every match.
        Assert.Equal(RlsDatabase.AllPaths, await RlsDatabase.ReadAsync(r.AppConnection, matches, cmd => AddEverything(cmd, r.Tenant)));
    }

    [Fact]
    public async Task A_caller_bound_to_one_session_and_asking_with_anothers_lists_gets_only_what_both_permit()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var matches = $"SELECT m.path FROM {TextMatchFunction.Call("to_tsquery('english', 'zeppelin')")} m ORDER BY 1";
        var alice = await Resolved(r, r.Alice);
        var bob = await Resolved(r, r.Bob);

        // Bob's session asking with Alice's lists, and Alice's asking with Bob's.
        Assert.Equal([RlsDatabase.Handbook], await BoundAsync(r, r.Bob, matches, AskWith(r, alice)));
        Assert.Equal([RlsDatabase.Handbook], await BoundAsync(r, r.Alice, matches, AskWith(r, bob)));

        // The control: Alice's session with her own lists.
        Assert.Equal([RlsDatabase.Pay, RlsDatabase.Handbook, RlsDatabase.Plan], await BoundAsync(r, r.Alice, matches, AskWith(r, alice)));
    }

    /// <summary>The text match's parameters with a caller's lists, and every other gate as open as it goes.</summary>
    private static Action<NpgsqlCommand> AskWith(RlsDatabase r, AccessScope scope) => cmd =>
    {
        cmd.Parameters.AddWithValue("tenant", r.Tenant);
        TextMatchFunction.Gates.AddParameters(cmd, new GateContext(new SearchOptions(
            scope, IncludeHistorical: true, Trust: new TrustPolicy(OkfTrustTier.Unverified, IncludeStale: true))));
        cmd.Parameters.AddWithValue("exclude", Array.Empty<Guid>());
        cmd.Parameters.AddWithValue("limit", 100);
    };

    /// <summary>The text match's parameters asking for the most a caller can.</summary>
    internal static void AddEverything(NpgsqlCommand cmd, Guid tenant)
    {
        cmd.Parameters.AddWithValue("tenant", tenant);
        TextMatchFunction.Gates.AddParameters(cmd, new GateContext(new SearchOptions(
            AccessScope.UnrestrictedAudited("test"), IncludeHistorical: true,
            Trust: new TrustPolicy(OkfTrustTier.Unverified, IncludeStale: true))));
        cmd.Parameters.AddWithValue("exclude", Array.Empty<Guid>());
        cmd.Parameters.AddWithValue("limit", 100);
    }

    [Fact]
    public async Task A_search_role_that_gains_a_write_on_the_index_still_reads_only_its_bound_lists()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        await RlsDatabase.ExecAsync(r.OwnerConnection,
            $"GRANT INSERT, UPDATE, DELETE ON prem_index.document, prem_index.chunk TO {r.SearchRoleName}");

        Assert.Equal(["0"], await RlsDatabase.ReadAsync(r.SearchConnection, "SELECT count(*) FROM prem_index.document"));
        Assert.Equal([RlsDatabase.Handbook], await BoundAsync(r, r.Bob, "SELECT path FROM prem_index.document ORDER BY path"));
    }

    [Fact]
    public async Task An_expired_session_binds_nothing()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await RlsDatabase.ExecAsync(r.AppConnection,
            $"SELECT prem_config.open_caller_session('{id}', '{r.Tenant}', NULL, NULL, ARRAY(SELECT id FROM prem_config.acl_set), interval '1 millisecond')");

        // Each read below is its own transaction, begun after the millisecond passed.
        await Assert.ThrowsAsync<PostgresException>(() =>
            RlsDatabase.ExecAsync(r.SearchConnection, $"SELECT prem_config.bind_caller_session('{id}')"));
        Assert.Equal(["0"], await InTransactionAsync(r.SearchConnection,
            $"SET LOCAL premagentic.caller_session = '{id}'", "SELECT count(*) FROM prem_index.document"));

        // The control: the same list of ids in a live session reads every document.
        var live = await CallerSessions.OpenAsync(r.App, r.Tenant, AccessScope.UnrestrictedAudited("test"), default);
        Assert.Equal(["5"], await InTransactionAsync(r.SearchConnection,
            $"SELECT prem_config.bind_caller_session('{live}')", "SELECT count(*) FROM prem_index.document"));
    }

    [Fact]
    public async Task Removing_a_membership_changes_the_next_search_through_the_search_role()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var search = r.NewSearch();
        Assert.Equal([RlsDatabase.Pay, RlsDatabase.Handbook, RlsDatabase.Plan], await r.PathsAsync(search, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id)));

        await r.Identity.RemoveMemberAsync(r.Staff.Id, r.Alice.Id);
        Assert.Equal([RlsDatabase.Pay, RlsDatabase.Handbook], await r.PathsAsync(search, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id)));
    }

    [Fact]
    public async Task A_search_closes_its_session_and_leaves_none_behind()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var search = r.NewSearch();
        await r.PathsAsync(search, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id));
        await new SectionFetcher(r.App, r.Search).GetAsync(r.Tenant, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id), RlsDatabase.Pay, null, false);

        Assert.Equal(["0"], await RlsDatabase.ReadAsync(r.OwnerConnection, "SELECT count(*) FROM prem_config.caller_session"));
    }

    [Fact]
    public async Task The_search_role_is_verified_bound_and_a_role_that_reads_the_whole_index_is_not()
    {
        await using var r = await RlsDatabase.NewAsync(server);

        Assert.Null(await r.Search.VerifyAsync());
        await using (var app = new SearchRole(r.AppConnection))
            Assert.Contains("index_writer", await app.VerifyAsync());
        await using (var admin = new SearchRole(r.AdminConnection))
            Assert.Contains("superuser", await admin.VerifyAsync());

        await RlsDatabase.ExecAsync(r.AdminConnection, $"ALTER ROLE {r.SearchRoleName} BYPASSRLS");
        Assert.Contains("BYPASSRLS", await r.Search.VerifyAsync());
    }

    /// <summary>What <paramref name="sql"/> returns as the search role, bound to a session for <paramref name="user"/>.</summary>
    private static async Task<List<string>> BoundAsync(RlsDatabase r, User user, string sql, Action<NpgsqlCommand>? parameters = null)
    {
        var session = await CallerSessions.OpenAsync(r.App, r.Tenant, await Resolved(r, user), default);
        try
        {
            return await InTransactionAsync(r.SearchConnection, $"SELECT prem_config.bind_caller_session('{session}')", sql, parameters);
        }
        finally
        {
            await CallerSessions.CloseAsync(r.App, session);
        }
    }

    /// <summary>Runs <paramref name="setup"/>, then returns what <paramref name="sql"/> reads, in one transaction on one connection.</summary>
    private static async Task<List<string>> InTransactionAsync(string connection, string setup, string sql, Action<NpgsqlCommand>? parameters = null)
    {
        await using var source = NpgsqlDataSource.Create(connection);
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var first = new NpgsqlCommand(setup, conn, tx)) await first.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        parameters?.Invoke(cmd);
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(Convert.ToString(reader.GetValue(0))!);
        return values;
    }

    private static async Task<AccessScope> Resolved(RlsDatabase r, User user) =>
        await PermittedSetReader.ResolveAsync(r.App, r.Tenant, await CallerAccess.ForUserAsync(r.Identity, user.Id));
}
