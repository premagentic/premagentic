using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>
/// The one way principals from outside become PremAgentic groups, for both
/// things that meet them: the caller middleware, with the groups a sign-in
/// adapter reports, and ingest, with the principals a connector names.
/// <para>
/// The mapper decides and this class holds it to the bounds, whichever
/// extension brought it:
/// <list type="number">
/// <item>With no mapper, which is the built-in answer, every principal means
/// nothing. Nothing is asked and nothing is guessed: an outside name that
/// happens to read like a PremAgentic group, a <c>group:</c> principal or
/// <c>everyone</c> means nothing like any other.</item>
/// <item>An answer counts only for a principal that was asked about, named as
/// it was asked.</item>
/// <item>An answer counts only as a live group of this tenant that an
/// administrator made, read here in one query. A group PremAgentic maintains,
/// a deleted group, another tenant's group or an id nothing answers to means
/// nothing.</item>
/// <item>The principal is built here, always as a group, so no answer can be
/// <c>everyone</c>, a user or a role.</item>
/// </list>
/// A mapper that throws is not caught: the request or the run fails where it
/// failed, and nothing is read as mapped or as unmapped on its account.
/// </para>
/// </summary>
/// <param name="mapper">The deployment's principal mapper, or null when no loaded extension brought one.</param>
public sealed class PrincipalMapping(PremagenticDatabase db, Guid tenantId, IPrincipalMapper? mapper)
{
    /// <summary>The mapper this reads through, or null when there is none.</summary>
    public IPrincipalMapper? Mapper => mapper;

    /// <summary>
    /// The groups these principals mean here, and the principals that mean
    /// nothing. Blank principals are dropped and a principal named twice counts
    /// once.
    /// </summary>
    public async Task<MappedPrincipals> ResolveAsync(IEnumerable<string> externalPrincipals, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(externalPrincipals);
        var asked = externalPrincipals
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (asked.Length == 0) return new MappedPrincipals(new Dictionary<string, Principal>(StringComparer.Ordinal), []);

        // The built-in answer: nothing outside means anything here.
        if (mapper is null) return new MappedPrincipals(new Dictionary<string, Principal>(StringComparer.Ordinal), asked);

        // What was asked is settled before the mapper runs, and the mapper is
        // handed a copy it cannot write to, so no mapper can change the names
        // its answers are held to, or the names counted as meaning nothing.
        var askedSet = asked.ToHashSet(StringComparer.Ordinal);
        var answered = await mapper.MapAsync(new PrincipalMapRequest(db, tenantId, Array.AsReadOnly(asked)), ct)
            ?? throw new InvalidOperationException($"The principal mapper '{mapper.Name}' answered nothing at all, not even an empty answer.");

        // Only what was asked, by the name it was asked under.
        var claimed = answered.Where(a => askedSet.Contains(a.Key)).ToArray();

        var groups = await AdministratorGroupsAsync(claimed.Select(a => a.Value).Distinct().ToArray(), ct);
        var mapped = new Dictionary<string, Principal>(StringComparer.Ordinal);
        foreach (var (principal, groupId) in claimed)
            if (groups.Contains(groupId))
                mapped[principal] = Principal.Group(CallerResolver.IdText(groupId));

        return new MappedPrincipals(mapped, asked.Where(p => !mapped.ContainsKey(p)).ToArray());
    }

    /// <summary>Which of these ids are live groups of this tenant that an administrator made.</summary>
    private async Task<HashSet<Guid>> AdministratorGroupsAsync(Guid[] ids, CancellationToken ct)
    {
        var found = new HashSet<Guid>();
        if (ids.Length == 0) return found;

        await using var cmd = db.DataSource.CreateCommand("""
            SELECT g.id FROM prem_config.app_group g
            WHERE g.tenant_id = @tenant AND g.id = ANY(@ids) AND g.deleted_at IS NULL AND g.system_key IS NULL
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) found.Add(reader.GetGuid(0));
        return found;
    }
}

/// <summary>
/// What a set of outside principals came to: the group each mapped one means
/// here, and the ones that mean nothing.
/// </summary>
/// <param name="Mapped">Each principal that means a group, and that group, always as <c>group:&lt;id&gt;</c>.</param>
/// <param name="Unmapped">
/// Principals that mean nothing, in the order they were met. Never an error
/// and never a guess: sign-in ignores them and ingest lets them reach nobody.
/// Counted, so that a principal nothing maps does not look like one that
/// matched.
/// </param>
public sealed record MappedPrincipals(IReadOnlyDictionary<string, Principal> Mapped, IReadOnlyList<string> Unmapped);
