using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Security;

/// <summary>A live folder rule as stored.</summary>
public sealed record StoredFolderRule(Guid Id, FolderRule Rule, long AclSetId, DateTimeOffset CreatedAt);

/// <summary>
/// Access lists and folder rules for one tenant.
/// <para>
/// A list is stored once per distinct canonical text and is never updated, so
/// two folders with the same list share one id. A rule change therefore never
/// edits a list: it records the new rule and points the documents that rule
/// decides at the new list, in one transaction, with no re-embedding. The next
/// search computes its permitted lists afresh, so the change applies to it.
/// </para>
/// <para>
/// A rule change takes the tenant's rules lock (<see cref="IndexLocks"/>) inside
/// its transaction, so it waits for running ingests and refuses after
/// <see cref="RuleChangeWait"/>.
/// </para>
/// </summary>
/// <param name="transaction">
/// A transaction the caller owns, for a change that must commit together with
/// something else, such as its entry in the change record. Every command then
/// runs on its connection, inside it, and nothing here begins, commits or rolls
/// back. Null opens a connection per call, as before.
/// </param>
public sealed class AclStore(PremagenticDatabase db, Guid tenantId, NpgsqlTransaction? transaction = null)
{
    /// <summary>How long a rule change waits for running ingests before it refuses.</summary>
    public TimeSpan RuleChangeWait { get; init; } = IndexLocks.DefaultRuleChangeWait;

    /// <summary>The id of <paramref name="set"/>, inserting it if this tenant has never stored it.</summary>
    public async Task<long> EnsureSetAsync(AclSet set, CancellationToken ct = default)
    {
        if (transaction is not null) return await EnsureSetAsync(transaction.Connection!, transaction, tenantId, set, ct);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await EnsureSetAsync(conn, null, tenantId, set, ct);
    }

    internal static async Task<long> EnsureSetAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, AclSet set, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO prem_config.acl_set(tenant_id, sha256, canonical_text)
            VALUES(@tenant, @sha, @text)
            ON CONFLICT (tenant_id, sha256) DO NOTHING
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("sha", set.Hash);
            insert.Parameters.AddWithValue("text", set.CanonicalText);
            await insert.ExecuteNonQueryAsync(ct);
        }

        // A separate statement, so a row another transaction committed while the
        // insert waited on it is visible here.
        await using var select = new NpgsqlCommand(
            "SELECT id, canonical_text FROM prem_config.acl_set WHERE tenant_id = @tenant AND sha256 = @sha", conn, tx);
        select.Parameters.AddWithValue("tenant", tenantId);
        select.Parameters.AddWithValue("sha", set.Hash);
        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("An access list was neither inserted nor found.");
        var id = reader.GetInt64(0);
        if (!string.Equals(reader.GetString(1), set.CanonicalText, StringComparison.Ordinal))
            throw new InvalidOperationException($"Access list {id} has the same hash as a different list. Refusing to share it.");
        return id;
    }

    /// <summary>
    /// Every list this tenant has stored. A stored text this build cannot parse
    /// is read as the empty list, which reaches nobody.
    /// </summary>
    public async Task<IReadOnlyList<StoredAclSet>> LoadSetsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT id, canonical_text FROM prem_config.acl_set WHERE tenant_id = @tenant";
        await using var cmd = transaction is null
            ? db.DataSource.CreateCommand(sql)
            : new NpgsqlCommand(sql, transaction.Connection, transaction);
        cmd.Parameters.AddWithValue("tenant", tenantId);

        var sets = new List<StoredAclSet>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            sets.Add(new StoredAclSet(reader.GetInt64(0), ParseStored(reader.GetString(1))));
        return sets;
    }

    /// <summary>The live rules, by source and prefix.</summary>
    public async Task<IReadOnlyList<StoredFolderRule>> ListRulesAsync(CancellationToken ct = default)
    {
        if (transaction is not null) return await ReadRulesAsync(transaction.Connection!, transaction, tenantId, ct);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await ReadRulesAsync(conn, null, tenantId, ct);
    }

    /// <summary>
    /// The live rules whose list names <paramref name="principal"/>. Removing a
    /// principal that a deny entry names lets its former holders fall through to
    /// the entries after it, so anything that deletes one should ask this first.
    /// </summary>
    public async Task<IReadOnlyList<StoredFolderRule>> RulesNamingAsync(Principal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return (await ListRulesAsync(ct)).Where(r => r.Rule.Acl.Entries.Any(e => e.Principal == principal)).ToList();
    }

    /// <summary>The live rules, ready to decide documents.</summary>
    public async Task<FolderRuleMatcher> LoadMatcherAsync(CancellationToken ct = default) =>
        new((await ListRulesAsync(ct)).Select(r => r.Rule));

    /// <summary>
    /// Sets the rule for one folder, replacing a live rule for the same folder,
    /// and moves every document the rules decide beneath it (and not beneath a
    /// longer rule) to the new list.
    /// </summary>
    /// <returns>The number of documents moved.</returns>
    /// <exception cref="InvalidOperationException">An ingest held the rules lock for the whole wait.</exception>
    public async Task<int> SetRuleAsync(FolderRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return await InTransactionAsync((conn, tx) => SetRuleAsync(conn, tx, rule, ct), ct);
    }

    private async Task<int> SetRuleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, FolderRule rule, CancellationToken ct)
    {
        await IndexLocks.LockRulesAsync(conn, tx, tenantId, RuleChangeWait, ct);
        var setId = await EnsureSetAsync(conn, tx, tenantId, rule.Acl, ct);
        await RetireAsync(conn, tx, rule.Source, rule.PathPrefix, ct);

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO prem_config.folder_rule(tenant_id, source, path_prefix, acl_set_id)
            VALUES(@tenant, @source, @prefix, @set)
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("source", rule.Source);
            insert.Parameters.AddWithValue("prefix", rule.PathPrefix);
            insert.Parameters.AddWithValue("set", setId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return await RepointAsync(conn, tx, rule.Source, rule.PathPrefix, rule.Acl, setId, ct);
    }

    /// <summary>
    /// Retires the rule for one folder. The documents it decided fall to the
    /// longest remaining rule above it, or, with none, to the empty list.
    /// </summary>
    /// <returns>Whether a live rule existed, and the number of documents moved.</returns>
    /// <exception cref="InvalidOperationException">An ingest held the rules lock for the whole wait.</exception>
    public async Task<(bool Removed, int Moved)> RemoveRuleAsync(string source, string pathPrefix, CancellationToken ct = default)
    {
        // Validates the source and prefix exactly as a rule would.
        _ = new FolderRule(source, pathPrefix, AclSet.Empty);
        return await InTransactionAsync((conn, tx) => RemoveRuleAsync(conn, tx, source, pathPrefix, ct), ct);
    }

    private async Task<(bool Removed, int Moved)> RemoveRuleAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string source, string pathPrefix, CancellationToken ct)
    {
        await IndexLocks.LockRulesAsync(conn, tx, tenantId, RuleChangeWait, ct);
        if (!await RetireAsync(conn, tx, source, pathPrefix, ct))
            return (false, 0);

        var remaining = new FolderRuleMatcher((await ReadRulesAsync(conn, tx, tenantId, ct)).Select(r => r.Rule));
        var inherited = pathPrefix.Length == 0 ? AclSet.Empty : remaining.AclFor(source, pathPrefix);
        var setId = await EnsureSetAsync(conn, tx, tenantId, inherited, ct);

        return (true, await RepointAsync(conn, tx, source, pathPrefix, inherited, setId, ct));
    }

    /// <summary>
    /// Holds a folder back from agents whose model runs outside the network,
    /// and stamps every document of the source at or beneath it with the
    /// denial (<see cref="HostedHolds"/>), under the rules lock, so an ingest
    /// cannot undo it halfway. No rule is read or written for the hold itself:
    /// a folder with no rule may be held too.
    /// </summary>
    /// <returns>Whether the folder was not held before, and the number of documents whose list changed.</returns>
    /// <exception cref="InvalidOperationException">An ingest held the rules lock for the whole wait.</exception>
    public async Task<(bool Changed, int Moved)> HoldAsync(string source, string pathPrefix, CancellationToken ct = default)
    {
        _ = new FolderRule(source, pathPrefix, AclSet.Empty);
        return await InTransactionAsync(async (conn, tx) =>
        {
            await IndexLocks.LockRulesAsync(conn, tx, tenantId, RuleChangeWait, ct);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO prem_config.hosted_hold(tenant_id, source, path_prefix) VALUES(@tenant, @source, @prefix)
                ON CONFLICT DO NOTHING
                """, conn, tx);
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("source", source);
            insert.Parameters.AddWithValue("prefix", pathPrefix);
            var changed = await insert.ExecuteNonQueryAsync(ct) > 0;
            return (changed, await RestampAsync(conn, tx, source, pathPrefix, ct));
        }, ct);
    }

    /// <summary>
    /// Releases a hold, and gives every rule-decided document at or beneath
    /// the folder the list its rules decide now, stamped still where another
    /// hold covers it. A document its connector decided keeps the denial until
    /// its next ingest decides it again, which fails closed.
    /// </summary>
    /// <returns>
    /// Whether the folder was held, the number of documents whose list changed,
    /// and whether the hold was a legacy one, made by migration 0141 from a
    /// switch turned off before it.
    /// </returns>
    /// <exception cref="InvalidOperationException">An ingest held the rules lock for the whole wait.</exception>
    public async Task<(bool Changed, int Moved, bool Legacy)> ReleaseAsync(string source, string pathPrefix, CancellationToken ct = default)
    {
        _ = new FolderRule(source, pathPrefix, AclSet.Empty);
        return await InTransactionAsync(async (conn, tx) =>
        {
            await IndexLocks.LockRulesAsync(conn, tx, tenantId, RuleChangeWait, ct);
            bool? legacy;
            await using (var delete = new NpgsqlCommand(
                "DELETE FROM prem_config.hosted_hold WHERE tenant_id = @tenant AND source = @source AND path_prefix = @prefix RETURNING legacy",
                conn, tx))
            {
                delete.Parameters.AddWithValue("tenant", tenantId);
                delete.Parameters.AddWithValue("source", source);
                delete.Parameters.AddWithValue("prefix", pathPrefix);
                legacy = await delete.ExecuteScalarAsync(ct) as bool?;
            }
            return (legacy is not null, await RestampAsync(conn, tx, source, pathPrefix, ct), legacy is true);
        }, ct);
    }

    /// <summary>
    /// Every document of this source at or beneath the prefix, given the list
    /// the rules and the holds decide for it now: a rule-decided document the
    /// list its folder rule gives, stamped when a hold covers it; a document
    /// its connector decided, stamped when a hold covers it and otherwise left
    /// as it is. Grouped by list, one update for each.
    /// </summary>
    /// <returns>The number of documents whose list changed.</returns>
    private async Task<int> RestampAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string source, string pathPrefix, CancellationToken ct)
    {
        var matcher = new FolderRuleMatcher((await ReadRulesAsync(conn, tx, tenantId, ct)).Select(r => r.Rule));
        var holds = await HostedHolds.ReadAsync(conn, tx, tenantId, ct);
        Guid? hosted = null;

        var documents = new List<(Guid Id, string Path, bool FromRule, string? Text)>();
        await using (var read = new NpgsqlCommand("""
            SELECT d.id, d.path, d.acl_from_rule, s.canonical_text
            FROM prem_index.document d
            LEFT JOIN prem_config.acl_set s ON s.tenant_id = d.tenant_id AND s.id = d.acl_set_id
            WHERE d.tenant_id = @tenant AND d.source_name = @source
              AND (@prefix = '' OR d.path = @prefix OR starts_with(d.path, @prefix || '/'))
            """, conn, tx))
        {
            read.Parameters.AddWithValue("tenant", tenantId);
            read.Parameters.AddWithValue("source", source);
            read.Parameters.AddWithValue("prefix", pathPrefix);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                documents.Add((reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        var wanted = new Dictionary<string, (AclSet Set, List<Guid> Documents)>(StringComparer.Ordinal);
        foreach (var d in documents)
        {
            var covered = HostedHolds.Covers(holds, source, d.Path);
            AclSet set;
            if (d.FromRule)
                set = matcher.AclFor(source, d.Path);
            else if (covered && d.Text is not null)
                set = ParseStored(d.Text);
            else
                continue;

            if (covered)
            {
                hosted ??= await HostedHolds.RequireGroupAsync(conn, tx, tenantId, ct);
                set = HostedHolds.Stamp(set, hosted.Value);
            }
            if (string.Equals(set.CanonicalText, d.Text, StringComparison.Ordinal)) continue;

            if (!wanted.TryGetValue(set.Hash, out var group)) wanted[set.Hash] = group = (set, []);
            group.Documents.Add(d.Id);
        }

        var moved = 0;
        foreach (var (set, ids) in wanted.Values)
        {
            var setId = await EnsureSetAsync(conn, tx, tenantId, set, ct);
            await using var update = new NpgsqlCommand(
                "UPDATE prem_index.document SET acl_set_id = @set WHERE tenant_id = @tenant AND id = ANY(@ids)", conn, tx);
            update.Parameters.AddWithValue("set", setId);
            update.Parameters.AddWithValue("tenant", tenantId);
            update.Parameters.AddWithValue("ids", ids.ToArray());
            moved += await update.ExecuteNonQueryAsync(ct);
        }
        return moved;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in the caller's transaction when one was
    /// given, and otherwise in a transaction of its own, committed only when the
    /// work returns. Nothing that retires no rule writes anything, so committing
    /// that case is the same as rolling it back.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        if (transaction is not null) return await work(transaction.Connection!, transaction);

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    internal static AclSet ParseStored(string canonicalText) =>
        AclSet.TryParseCanonical(canonicalText, out var set) ? set : AclSet.Empty;

    private async Task<bool> RetireAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string source, string pathPrefix, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE prem_config.folder_rule SET deleted_at = now()
            WHERE tenant_id = @tenant AND source = @source AND path_prefix = @prefix AND deleted_at IS NULL
            """, conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("prefix", pathPrefix);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// One statement: every rule-decided document of this source at or beneath
    /// the prefix, in whole segments, that no longer live rule covers, now reads
    /// under <paramref name="setId"/>, or, where a hold covers it
    /// (<see cref="HostedHolds"/>), under the same list stamped with the denial
    /// of the hosted-model agents group. Documents a connector decided are never
    /// touched, and nothing is re-embedded.
    /// </summary>
    private async Task<int> RepointAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string source, string pathPrefix, AclSet set, long setId, CancellationToken ct)
    {
        // The stamped list, when this source has any hold at all; the plain
        // one otherwise, so a deployment with nothing held stores nothing new.
        var heldId = setId;
        if ((await HostedHolds.ReadAsync(conn, tx, tenantId, ct)).Any(h => h.Source == source))
            heldId = await EnsureSetAsync(conn, tx, tenantId,
                HostedHolds.Stamp(set, await HostedHolds.RequireGroupAsync(conn, tx, tenantId, ct)), ct);

        await using var cmd = new NpgsqlCommand("""
            UPDATE prem_index.document d SET acl_set_id = CASE WHEN EXISTS (
                    SELECT 1 FROM prem_config.hosted_hold h
                    WHERE h.tenant_id = @tenant AND h.source = d.source_name
                      AND (h.path_prefix = '' OR d.path = h.path_prefix OR starts_with(d.path, h.path_prefix || '/')))
                THEN @held ELSE @set END
            WHERE d.tenant_id = @tenant
              AND d.acl_from_rule
              AND d.source_name = @source
              AND (@prefix = '' OR d.path = @prefix OR starts_with(d.path, @prefix || '/'))
              AND NOT EXISTS (
                  SELECT 1 FROM prem_config.folder_rule r
                  WHERE r.tenant_id = @tenant AND r.source = @source AND r.deleted_at IS NULL
                    AND length(r.path_prefix) > length(@prefix)
                    AND (d.path = r.path_prefix OR starts_with(d.path, r.path_prefix || '/')))
            """, conn, tx);
        cmd.Parameters.AddWithValue("set", setId);
        cmd.Parameters.AddWithValue("held", heldId);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("prefix", pathPrefix);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<StoredFolderRule>> ReadRulesAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT r.id, r.source, r.path_prefix, r.acl_set_id, s.canonical_text, r.created_at
            FROM prem_config.folder_rule r
            JOIN prem_config.acl_set s ON s.id = r.acl_set_id
            WHERE r.tenant_id = @tenant AND r.deleted_at IS NULL
            ORDER BY r.source, r.path_prefix
            """, conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);

        var rules = new List<StoredFolderRule>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rules.Add(new StoredFolderRule(
                reader.GetGuid(0),
                new FolderRule(reader.GetString(1), reader.GetString(2), ParseStored(reader.GetString(4))),
                reader.GetInt64(3),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return rules;
    }
}
