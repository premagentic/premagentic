using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Okf;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// <c>prem_index.text_matches</c>, whose body is generated from the gates and
/// installed by migrate. The body is compared with the text below, so a change
/// to any gate's SQL shows in review as a change here, never silently in the
/// database; and its results are compared, across every gate setting, with the
/// gated read written with the match in the query, the form the text leg had
/// before the policies.
/// <para>
/// Adding a gate, or a parameter to one, means a migration for the signature;
/// the equality test is what catches the two drifting apart.
/// </para>
/// Requires a running Docker daemon, except for the first three.
/// </summary>
public sealed class TextMatchFunctionTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    // The body for GateSet.Default, as migrate installs it.
    private const string ExpectedBody = """

        DECLARE
            whole BOOLEAN := prem_config.reads_whole_index(session_user)
                OR EXISTS (SELECT 1 FROM pg_roles r WHERE r.rolname = session_user AND (r.rolsuper OR r.rolbypassrls));
            lists BIGINT[] := prem_config.caller_acl_sets();
            reads_all BOOLEAN := whole AND g_unrestricted;
            effective BIGINT[] := CASE
                WHEN whole THEN g_permitted
                WHEN g_unrestricted THEN lists
                ELSE ARRAY(SELECT l FROM unnest(lists) l WHERE l = ANY (g_permitted))
            END;
        BEGIN
            RETURN QUERY EXECUTE $query$
                SELECT c.id, c.document_id, c.seq, ts_rank_cd(c.tsv, $1) AS rank, d.path
                FROM prem_index.chunk c
                JOIN prem_index.document d ON d.id = c.document_id
                WHERE d.tenant_id = $2
                  AND c.tsv @@ $1
                  AND ($3 OR d.acl_set_id = ANY ($4))
                  AND ($5 OR d.acl_set_id = ANY($6))
                  AND ($7 OR d.lifecycle_status = 'active')
                  AND (d.authorship <> 2 OR d.trust_tier >= $8)
                  AND ($9 OR d.stale_after IS NULL OR d.stale_after > $10)
                  AND NOT (c.id = ANY ($11))
                ORDER BY rank DESC, d.path, c.seq
                LIMIT $12
            $query$
            USING p_query, p_tenant, reads_all, effective, TRUE, g_permitted, g_historical, g_trust_min_tier, g_freshness_include_stale, g_freshness_now, p_exclude, p_limit;
        END

        """;

    private const string Source = "SELECT prosrc FROM pg_proc WHERE oid = 'prem_index.text_matches'::regproc";

    [Fact]
    public void The_generated_body_is_the_checked_in_text()
    {
        Assert.DoesNotContain("\r", TextMatchFunction.Body);
        Assert.Equal(ExpectedBody.ReplaceLineEndings("\n"), TextMatchFunction.Body);
    }

    [Fact]
    public void The_gates_add_exactly_the_parameters_their_SQL_names()
    {
        using var cmd = new NpgsqlCommand();
        TextMatchFunction.Gates.AddParameters(cmd, new GateContext(new SearchOptions(AccessScope.UnrestrictedAudited("test"))));

        Assert.Equal(TextMatchFunction.GateParameters, cmd.Parameters.Select(p => p.ParameterName).ToArray());
        Assert.Equal(
            ["unrestricted", "permitted", "historical", "trust_min_tier", "freshness_include_stale", "freshness_now"],
            TextMatchFunction.GateParameters);
    }

    [Fact]
    public void The_body_needs_the_access_gate_it_folds_into_the_session_condition()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => TextMatchFunction.Render(GateSet.Default.Without(AccessGate.GateName)));
        Assert.Contains("access gate", refused.Message);
    }

    [Fact]
    public async Task Migrate_installs_the_generated_body_under_the_arguments_the_migration_declares()
    {
        await using var r = await RlsDatabase.NewAsync(server);

        Assert.Equal([TextMatchFunction.Body], await RlsDatabase.ReadAsync(r.OwnerConnection, Source));
        Assert.Equal(TextMatchFunction.ArgumentsFor(GateSet.Default), await RlsDatabase.ReadAsync(r.OwnerConnection,
            "SELECT unnest(proargnames[1:pronargs]) FROM pg_proc WHERE oid = 'prem_index.text_matches'::regproc"));
    }

    [Fact]
    public async Task A_changed_body_keeps_the_application_role_from_starting_until_the_owner_migrates()
    {
        await using var r = await RlsDatabase.NewAsync(server);

        // The control: with the body current, the application role starts.
        await using (var app = new PremagenticDatabase(r.AppConnection))
            Assert.Empty(await app.MigrateAsync());

        var arguments = (await RlsDatabase.ReadAsync(r.OwnerConnection, "SELECT pg_get_function_arguments('prem_index.text_matches'::regproc)"))[0];
        var result = (await RlsDatabase.ReadAsync(r.OwnerConnection, "SELECT pg_get_function_result('prem_index.text_matches'::regproc)"))[0];
        await RlsDatabase.ExecAsync(r.OwnerConnection,
            $"CREATE OR REPLACE FUNCTION prem_index.text_matches({arguments}) RETURNS {result} LANGUAGE plpgsql STABLE SECURITY DEFINER " +
            "SET search_path = pg_catalog, pg_temp AS $$ BEGIN RETURN; END $$");

        await using (var app = new PremagenticDatabase(r.AppConnection))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.MigrateAsync());
            Assert.Contains("prem migrate", refused.Message);
        }

        await using (var owner = new PremagenticDatabase(r.OwnerConnection))
            Assert.Empty(await owner.MigrateAsync());
        Assert.Equal([TextMatchFunction.Body], await RlsDatabase.ReadAsync(r.OwnerConnection, Source));

        // Replaced, not recreated: the grants setup made are still there.
        Assert.Equal(["True", "True"], await RlsDatabase.ReadAsync(r.OwnerConnection,
            $"SELECT has_function_privilege(g.name, 'prem_index.text_matches'::regproc, 'EXECUTE') " +
            $"FROM unnest(ARRAY['{r.AppRole}', '{r.SearchRoleName}']) WITH ORDINALITY AS g(name, n) ORDER BY g.n"));
        Assert.Equal(["5"], await RlsDatabase.ReadAsync(r.AppConnection,
            $"SELECT count(*) FROM {TextMatchFunction.Call("to_tsquery('english', 'zeppelin')")} m",
            cmd => RlsSecondLineTests.AddEverything(cmd, r.Tenant)));
    }

    [Fact]
    public async Task Migrate_refuses_arguments_that_do_not_fit_the_gates_and_changes_nothing()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        const string stub = " BEGIN RETURN; END ";
        await RlsDatabase.ExecAsync(r.OwnerConnection, $"""
            DROP FUNCTION prem_index.text_matches;
            CREATE FUNCTION prem_index.text_matches(p_tenant UUID, p_query TSQUERY, g_unrestricted BOOLEAN, g_permitted BIGINT[],
                g_trust_min_tier SMALLINT, g_freshness_include_stale BOOLEAN, g_freshness_now TIMESTAMPTZ,
                p_exclude UUID[], p_limit INT)
            RETURNS TABLE(chunk_id UUID, document_id UUID, seq INT, rank REAL, path TEXT)
            LANGUAGE plpgsql AS $${stub}$$;
            """);

        await using var owner = new PremagenticDatabase(r.OwnerConnection);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.MigrateAsync());
        Assert.Contains("g_historical", refused.Message);
        Assert.Contains("add a migration", refused.Message);
        Assert.Equal([stub], await RlsDatabase.ReadAsync(r.OwnerConnection, Source));
    }

    [Fact]
    public async Task The_function_returns_what_the_gated_read_with_the_match_in_the_query_returns_for_every_gate_setting()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        await SeedAsync(r, now);

        AccessScope[] scopes =
        [
            AccessScope.UnrestrictedAudited("test"),
            await PermittedSetReader.ResolveAsync(r.App, r.Tenant, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id)),
            await PermittedSetReader.ResolveAsync(r.App, r.Tenant, await CallerAccess.ForUserAsync(r.Identity, r.Bob.Id)),
        ];
        string[] queries =
        [
            "to_tsquery('english', 'quartz')", "to_tsquery('english', 'obsidian')",
            "to_tsquery('english', 'obsidian & quartz')", "to_tsquery('english', 'nothingmatchesthis')",
        ];
        TrustPolicy[] trusts =
        [
            TrustPolicy.Strict, new(OkfTrustTier.MachineConfirmed, IncludeStale: false), new(OkfTrustTier.Unverified, IncludeStale: true),
        ];

        // As the application role, which reads the whole index so only the gates
        // filter, and as the search role bound to each scope's own session.
        await using var appSource = NpgsqlDataSource.Create(r.AppConnection);
        await using var app = await appSource.OpenConnectionAsync();
        await using var searchSource = NpgsqlDataSource.Create(r.SearchConnection);
        var compared = 0;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var longest = 0;
        foreach (var scope in scopes)
        {
            var session = await CallerSessions.OpenAsync(r.App, r.Tenant, scope, default);
            try
            {
                await using var search = await searchSource.OpenConnectionAsync();
                await using var tx = await search.BeginTransactionAsync();
                await using (var bind = new NpgsqlCommand($"SELECT prem_config.bind_caller_session('{session}')", search, tx))
                    await bind.ExecuteNonQueryAsync();

                foreach (var query in queries)
                foreach (var historical in new[] { false, true })
                foreach (var trust in trusts)
                {
                    var gate = new GateContext(new SearchOptions(scope, IncludeHistorical: historical, Trust: trust, AsOf: now));
                    var all = await IdsAsync(app, null, InQuery(query), r.Tenant, gate, [], 1000);
                    foreach (var (exclude, limit) in new (Guid[], int)[] { ([], 1000), ([], 3), (all.Take(2).ToArray(), 1000), (all.Take(2).ToArray(), 3) })
                    {
                        var expected = await IdsAsync(app, null, InQuery(query), r.Tenant, gate, exclude, limit);
                        var label = $"{scope.AuditLabel} {query} historical={historical} trust={trust} exclude={exclude.Length} limit={limit}";
                        Assert.True(expected.SequenceEqual(await IdsAsync(app, null, ThroughFunction(query), r.Tenant, gate, exclude, limit)),
                            "as the application role: " + label);
                        Assert.True(expected.SequenceEqual(await IdsAsync(search, tx, ThroughFunction(query), r.Tenant, gate, exclude, limit)),
                            "as the bound search role: " + label);
                        compared++;
                        distinct.Add(string.Join(",", expected));
                        longest = Math.Max(longest, expected.Count);
                    }
                }
            }
            finally
            {
                await CallerSessions.CloseAsync(r.App, session);
            }
        }

        // The grid discriminates: the gates, the lists, the exclusions and the
        // limit each change what the reference returns.
        Assert.Equal(3 * 4 * 2 * 3 * 4, compared);
        Assert.True(distinct.Count >= 20, $"Only {distinct.Count} distinct answers; the fixture does not exercise the gates.");
        Assert.True(longest > 3, "No answer was longer than the smaller limit.");
    }

    [Fact]
    public async Task Bound_to_one_session_and_asking_with_any_lists_the_function_returns_what_both_permit_for_every_gate_setting()
    {
        await using var r = await RlsDatabase.NewAsync(server);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        await SeedAsync(r, now);

        AccessScope[] scopes =
        [
            AccessScope.UnrestrictedAudited("test"),
            await PermittedSetReader.ResolveAsync(r.App, r.Tenant, await CallerAccess.ForUserAsync(r.Identity, r.Alice.Id)),
            await PermittedSetReader.ResolveAsync(r.App, r.Tenant, await CallerAccess.ForUserAsync(r.Identity, r.Bob.Id)),
        ];
        string[] queries =
        [
            "to_tsquery('english', 'quartz')", "to_tsquery('english', 'obsidian')",
            "to_tsquery('english', 'obsidian & quartz')", "to_tsquery('english', 'nothingmatchesthis')",
        ];
        TrustPolicy[] trusts =
        [
            TrustPolicy.Strict, new(OkfTrustTier.MachineConfirmed, IncludeStale: false), new(OkfTrustTier.Unverified, IncludeStale: true),
        ];

        // An unrestricted session holds every list of the tenant.
        var every = (await RlsDatabase.ReadAsync(r.OwnerConnection, $"SELECT id FROM prem_config.acl_set WHERE tenant_id = '{r.Tenant}'"))
            .Select(long.Parse).ToArray();
        long[] ListsOf(AccessScope scope) => scope.Unrestricted ? every : [.. scope.PermittedSetIds!];

        await using var appSource = NpgsqlDataSource.Create(r.AppConnection);
        await using var app = await appSource.OpenConnectionAsync();
        await using var searchSource = NpgsqlDataSource.Create(r.SearchConnection);
        var compared = 0;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bound in scopes)
        {
            var session = await CallerSessions.OpenAsync(r.App, r.Tenant, bound, default);
            try
            {
                await using var search = await searchSource.OpenConnectionAsync();
                await using var tx = await search.BeginTransactionAsync();
                await using (var bind = new NpgsqlCommand($"SELECT prem_config.bind_caller_session('{session}')", search, tx))
                    await bind.ExecuteNonQueryAsync();

                foreach (var asked in scopes)
                {
                    // The reference reads, as the application role, exactly the
                    // lists both permit: the session's cut down to the ones
                    // asked for, or the session's alone when every list is claimed.
                    var both = asked.Unrestricted ? ListsOf(bound) : ListsOf(bound).Intersect(ListsOf(asked)).ToArray();
                    var reference = AccessScope.PublicOnly with { PermittedSetIds = both };

                    foreach (var query in queries)
                    foreach (var historical in new[] { false, true })
                    foreach (var trust in trusts)
                    foreach (var limit in new[] { 1000, 3 })
                    {
                        var gate = new GateContext(new SearchOptions(asked, IncludeHistorical: historical, Trust: trust, AsOf: now));
                        var referenceGate = new GateContext(new SearchOptions(reference, IncludeHistorical: historical, Trust: trust, AsOf: now));
                        var expected = await IdsAsync(app, null, InQuery(query), r.Tenant, referenceGate, [], limit);
                        Assert.True(expected.SequenceEqual(await IdsAsync(search, tx, ThroughFunction(query), r.Tenant, gate, [], limit)),
                            $"bound as {bound.AuditLabel}, asking as {asked.AuditLabel}: {query} historical={historical} trust={trust} limit={limit}");
                        compared++;
                        distinct.Add(string.Join(",", expected));
                    }
                }
            }
            finally
            {
                await CallerSessions.CloseAsync(r.App, session);
            }
        }

        Assert.Equal(3 * 3 * 4 * 2 * 3 * 2, compared);
        Assert.True(distinct.Count >= 20, $"Only {distinct.Count} distinct answers; the fixture does not exercise the lists.");
    }

    /// <summary>The text leg's read as it was before the policies: the match in the query, the gates beside it.</summary>
    private static string InQuery(string tsquery) => $"""
        SELECT c.id
        FROM prem_index.chunk c
        JOIN prem_index.document d ON d.id = c.document_id
        WHERE d.tenant_id = @tenant
          AND {GateSet.Default.Sql}
          AND c.tsv @@ {tsquery}
          AND NOT (c.id = ANY(@exclude))
        ORDER BY ts_rank_cd(c.tsv, {tsquery}) DESC, d.path, c.seq
        LIMIT @limit
        """;

    private static string ThroughFunction(string tsquery) =>
        $"SELECT m.chunk_id FROM {TextMatchFunction.Call(tsquery)} m ORDER BY m.rank DESC, m.path, m.seq";

    private static async Task<List<Guid>> IdsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, Guid tenant, GateContext gate, Guid[] exclude, int limit)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenant);
        TextMatchFunction.Gates.AddParameters(cmd, gate);
        cmd.Parameters.AddWithValue("exclude", exclude);
        cmd.Parameters.AddWithValue("limit", limit);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// Forty-eight documents of three chunks over the tenant's lists, as the
    /// owner: every fifth superseded, authorship and trust tier cycling, a
    /// quarter stale at <paramref name="now"/> and a quarter going stale after
    /// it. Every chunk says quartz one to four times, so ranks tie often and the
    /// path and sequence decide; one in six also says obsidian.
    /// </summary>
    private static async Task SeedAsync(RlsDatabase r, DateTimeOffset now)
    {
        await using var source = NpgsqlDataSource.Create(r.OwnerConnection);
        await using var cmd = source.CreateCommand("""
            WITH lists AS (SELECT array_agg(id ORDER BY id) AS a FROM prem_config.acl_set WHERE tenant_id = @tenant),
            docs AS (
                INSERT INTO prem_index.document(tenant_id, path, title, content_hash, source_name, acl_set_id,
                                              lifecycle_status, authorship, trust_tier, stale_after)
                SELECT @tenant, format('eq/%s.md', lpad(i::text, 3, '0')), 'Stone ' || i, 'eq' || i, 'eq',
                       (SELECT a[1 + i % cardinality(a)] FROM lists),
                       CASE WHEN i % 5 = 0 THEN 'superseded' ELSE 'active' END,
                       i % 3, (i / 3) % 3,
                       CASE i % 4 WHEN 0 THEN @now - interval '1 day' WHEN 1 THEN @now + interval '1 day' END
                FROM generate_series(1, 48) i
                RETURNING id, substring(path FROM 4 FOR 3)::int AS i)
            INSERT INTO prem_index.chunk(document_id, seq, content, embedding, embedding_model, embedding_dims)
            SELECT docs.id, s,
                   repeat('quartz ', 1 + (docs.i + s) % 4) || CASE WHEN (docs.i + s) % 6 = 0 THEN 'obsidian' ELSE 'granite' END,
                   decode('00000000', 'hex'), 'eq', 1
            FROM docs CROSS JOIN generate_series(0, 2) s
            """);
        cmd.Parameters.AddWithValue("tenant", r.Tenant);
        cmd.Parameters.AddWithValue("now", now);
        Assert.Equal(48 * 3, await cmd.ExecuteNonQueryAsync());
    }
}
