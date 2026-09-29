using Premagentic.Core.Identity;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Security;

/// <summary>A folder held back from agents whose model runs outside the network.</summary>
/// <param name="Source">The connector a folder rule names, <c>filesystem</c> for a folder.</param>
/// <param name="PathPrefix">The folder, as a folder rule writes it; empty is the whole source.</param>
public sealed record HostedHold(string Source, string PathPrefix);

/// <summary>
/// The "may be served to hosted models" switch, turned off: a hold on a
/// folder, kept beside the folder rules where no rule write reaches it.
/// <para>
/// A hold covers every document of its source at or beneath its prefix, in
/// whole segments, whatever rule or connector decided the document's list. It
/// is enforced twice, and each alone would keep the documents from an agent
/// whose model runs outside the network:
/// <list type="number">
/// <item>Stamped. The two places that write a document's list, ingest and a
/// rule change, store a held document's list with the denial of the
/// hosted-model agents group first (<see cref="Stamp"/>), and holding or
/// releasing a folder stamps or restores the documents under it. The list
/// itself then denies, so the SQL gate, row-level security and the why tool
/// all say so with no change to any of them.</item>
/// <item>Excluded. For an agent PremAgentic keeps in that group, the lists of
/// every held document are taken out of what it may read
/// (<see cref="HeldSetIdsAsync"/>), before the read is bound. A writer that
/// ever failed to stamp still leaks nothing.</item>
/// </list>
/// </para>
/// </summary>
public static class HostedHolds
{
    /// <summary>
    /// True when a hold covers this document: its source is the hold's, and
    /// its path is the hold's prefix or lies beneath it at a segment boundary,
    /// exactly as a folder rule covers a path.
    /// </summary>
    public static bool Covers(IReadOnlyCollection<HostedHold> holds, string source, string path)
    {
        ArgumentNullException.ThrowIfNull(holds);
        if (holds.Count == 0 || source is null || path is null) return false;
        foreach (var hold in holds)
        {
            if (!string.Equals(hold.Source, source, StringComparison.Ordinal)) continue;
            var prefix = hold.PathPrefix;
            if (prefix.Length == 0 || string.Equals(path, prefix, StringComparison.Ordinal)
                || (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length] == '/'))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The hold that decides for <paramref name="prefix"/>: the one at it, or
    /// else the nearest one above it, or null when none covers it.
    /// </summary>
    public static HostedHold? Covering(IReadOnlyCollection<HostedHold> holds, string source, string prefix)
    {
        ArgumentNullException.ThrowIfNull(holds);
        return holds.Where(h => Covers([h], source, prefix)).MaxBy(h => h.PathPrefix.Length);
    }

    /// <summary>A hold as the rules list and the sources list name it: <c>source:folder</c>.</summary>
    public static string Name(HostedHold hold) =>
        $"{hold.Source}:{(hold.PathPrefix.Length == 0 ? "(whole source)" : hold.PathPrefix)}";

    /// <summary>
    /// The holds that reach documents a rule decides: one at the rule's folder
    /// or above it, and one beneath it. A list of rules shows these beside the
    /// rule, so the entries are not read as the whole story.
    /// </summary>
    public static IReadOnlyList<HostedHold> Touching(IReadOnlyList<HostedHold> holds, FolderRule rule)
    {
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(rule);
        return [.. holds.Where(h => h.Source == rule.Source
            && (Covers([h], rule.Source, rule.PathPrefix) || rule.Covers(h.PathPrefix)))];
    }

    /// <summary>What a list of rules says beside a rule that <paramref name="touching"/> reach.</summary>
    public static string Describe(IReadOnlyList<HostedHold> touching) =>
        "held back from hosted-model agents by the source's switch: " + string.Join(", ", touching.Select(Name));

    /// <summary>
    /// The list a held document stores: the denial of the hosted-model agents
    /// group first, once, and every other entry after it in its own order.
    /// </summary>
    public static AclSet Stamp(AclSet set, Guid hostedModelAgentsGroupId)
    {
        ArgumentNullException.ThrowIfNull(set);
        var denial = Denial(hostedModelAgentsGroupId);
        return AclSet.Create([denial, .. set.Entries.Where(e => e != denial)]);
    }

    /// <summary>The one entry a stamp puts first.</summary>
    public static AclEntry Denial(Guid hostedModelAgentsGroupId) =>
        AclEntry.Deny(Principal.Group(CallerResolver.IdText(hostedModelAgentsGroupId)));

    /// <summary>Every hold of this tenant.</summary>
    public static async Task<IReadOnlyList<HostedHold>> ListAsync(PremagenticDatabase db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await ReadAsync(conn, null, tenantId, ct);
    }

    /// <summary>
    /// The holds migration 0141 made from a switch turned off before it, whose
    /// old entry still opens the folder's rule until the hold is released.
    /// </summary>
    public static async Task<IReadOnlySet<HostedHold>> LegacyAsync(PremagenticDatabase db, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT source, path_prefix FROM prem_config.hosted_hold WHERE tenant_id = @tenant AND legacy");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        var holds = new HashSet<HostedHold>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) holds.Add(new HostedHold(reader.GetString(0), reader.GetString(1)));
        return holds;
    }

    internal static async Task<IReadOnlyList<HostedHold>> ReadAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT source, path_prefix FROM prem_config.hosted_hold WHERE tenant_id = @tenant ORDER BY source, path_prefix",
            conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        var holds = new List<HostedHold>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) holds.Add(new HostedHold(reader.GetString(0), reader.GetString(1)));
        return holds;
    }

    /// <summary>
    /// The hosted-model agents group's id. It is created with the tenant and
    /// cannot be removed, so not finding it while a hold has to be applied is a
    /// broken deployment, and saying so stops the write rather than storing a
    /// held document's list without its denial.
    /// </summary>
    internal static async Task<Guid> RequireGroupAsync(PremagenticDatabase db, Guid tenantId, CancellationToken ct)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await RequireGroupAsync(conn, null, tenantId, ct);
    }

    /// <inheritdoc cref="RequireGroupAsync(PremagenticDatabase, Guid, CancellationToken)"/>
    internal static async Task<Guid> RequireGroupAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM prem_config.app_group WHERE tenant_id = @tenant AND system_key = @key AND deleted_at IS NULL",
            conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("key", SystemGroups.HostedModelAgents);
        return await cmd.ExecuteScalarAsync(ct) is Guid id
            ? id
            : throw new InvalidOperationException(
                "This deployment has no hosted-model agents group. It is created with the tenant and cannot be removed, " +
                "so something has changed the configuration database by hand. A folder held back from hosted models " +
                "cannot be written until that is put right.");
    }

    /// <summary>
    /// The exclusion's query, as <see cref="HeldSetIdsAsync"/> runs it. The
    /// range is the folder in byte order: its own path, and every path that
    /// goes on with '/', sort at or after the prefix and before the prefix
    /// followed by '0', the next byte after '/'. The segment test after it
    /// drops a sibling such as "held.md" that the range lets in. A hold on the
    /// whole source ('') has no range: the source is enough.
    /// </summary>
    internal const string ProbeSql = """
        SELECT p.id
        FROM unnest(@permitted) AS p(id)
        WHERE EXISTS (SELECT 1 FROM prem_config.app_group g
                      WHERE g.tenant_id = @tenant AND g.system_key = @key AND g.id = ANY(@groups))
          AND EXISTS (
            SELECT 1 FROM prem_config.hosted_hold h
            WHERE h.tenant_id = @tenant
              AND ((h.path_prefix = '' AND EXISTS (
                      SELECT 1 FROM prem_index.document d
                      WHERE d.tenant_id = @tenant AND d.source_name = h.source AND d.acl_set_id = p.id))
                OR (h.path_prefix <> '' AND EXISTS (
                      SELECT 1 FROM prem_index.document d
                      WHERE d.tenant_id = @tenant AND d.source_name = h.source AND d.acl_set_id = p.id
                        AND (d.path COLLATE "C") >= h.path_prefix
                        AND (d.path COLLATE "C") < h.path_prefix || '0'
                        AND (d.path = h.path_prefix OR starts_with(d.path, h.path_prefix || '/'))))))
        """;

    /// <summary>
    /// Of the lists <paramref name="permitted"/> names, the ones some document
    /// under a hold stores, for a scope PremAgentic keeps in the hosted-model
    /// agents group; empty for any other scope, and when nothing is held.
    /// <para>
    /// One query, asked per list and per hold rather than per document: does
    /// any document of the hold's source, under this list, lie at or beneath
    /// the held folder. Each question is one seek on
    /// <c>document_hold_probe_idx</c> (migration 0141), which orders a
    /// source's documents under one list by path in byte order, where a
    /// folder's documents are one range. So the cost grows with lists times
    /// holds, not with the documents; a list only held documents store (a
    /// stamped one) is never permitted to such a scope, so none is asked about.
    /// </para>
    /// </summary>
    /// <param name="systemGroups">The scope's system groups (<see cref="AccessScope.SystemGroups"/>).</param>
    /// <param name="permitted">The lists the scope may read before the holds are weighed.</param>
    internal static async Task<HashSet<long>> HeldSetIdsAsync(
        PremagenticDatabase db, Guid tenantId, IReadOnlyList<Principal> systemGroups, IReadOnlyList<long> permitted,
        CancellationToken ct)
    {
        var held = new HashSet<long>();
        var ids = systemGroups
            .Where(g => g.Kind == PrincipalKind.Group && Guid.TryParse(g.Value, out _))
            .Select(g => Guid.Parse(g.Value))
            .ToArray();
        if (ids.Length == 0 || permitted.Count == 0) return held;

        // As the application role, which setup lists in index_writer: the row
        // policy on the documents lets it read every row with no caller
        // session open, which is when this runs.
        await using var cmd = db.DataSource.CreateCommand(ProbeSql);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("key", SystemGroups.HostedModelAgents);
        cmd.Parameters.AddWithValue("groups", ids);
        cmd.Parameters.AddWithValue("permitted", permitted.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) held.Add(reader.GetInt64(0));
        return held;
    }
}
