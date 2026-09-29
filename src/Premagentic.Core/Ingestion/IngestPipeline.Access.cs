using Premagentic.Core.Identity;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Ingestion;

/// <summary>
/// The access half of ingest. Each document's list is decided once, by
/// <see cref="AccessDecision"/>, stored by id, compared by id to tell whether
/// anything changed, and written in the same transaction as the document.
/// </summary>
public sealed partial class IngestPipeline
{
    /// <summary>A document's decided access list, as stored.</summary>
    private sealed record StoredAcl(long SetId, bool FromFolderRule, bool DeniesEveryone);

    /// <summary>
    /// Decides documents for one run: the folder rules as they stood when the run
    /// began, and the ids of lists already looked up, so each distinct list costs
    /// one round trip per run.
    /// </summary>
    private sealed class AclDecider(
        AclStore store, FolderRuleMatcher rules, PrincipalMapping mappings, IReadOnlyList<HostedHold> holds, Guid? hosted)
    {
        private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);

        // What each outside principal this run has met means here, read once
        // each. A null value is a principal that means nothing, remembered so
        // the run neither asks again nor counts it twice.
        private readonly Dictionary<string, Principal?> _external = new(StringComparer.Ordinal);

        /// <summary>Outside principals this run met that mean nothing here, each once.</summary>
        public IReadOnlyCollection<string> UnmappedPrincipals =>
            _external.Where(e => e.Value is null).Select(e => e.Key).ToArray();

        /// <summary>The principal mapper this run reads through, or null when there is none.</summary>
        public IPrincipalMapper? Mapper => mappings.Mapper;

        /// <summary>
        /// The rules and the holds as they stood when the run began, both read
        /// under the run's shared rules lock, so neither can change halfway.
        /// </summary>
        public static async Task<AclDecider> LoadAsync(PremagenticDatabase db, Guid tenantId, IPrincipalMapper? mapper, CancellationToken ct)
        {
            var store = new AclStore(db, tenantId);
            var holds = await HostedHolds.ListAsync(db, tenantId, ct);
            Guid? hosted = holds.Count == 0 ? null : await HostedHolds.RequireGroupAsync(db, tenantId, ct);
            return new AclDecider(store, await store.LoadMatcherAsync(ct), new PrincipalMapping(db, tenantId, mapper), holds, hosted);
        }

        public async Task<StoredAcl> DecideAsync(string sourceName, SourceDocument doc, CancellationToken ct)
        {
            var access = await ResolveExternalAsync(doc.Access, ct);
            var decided = AccessDecision.For(access, sourceName, doc.Path, rules);
            // A held folder's document stores its list with the denial of the
            // hosted-model agents group first, whatever decided the list.
            var set = hosted is { } group && HostedHolds.Covers(holds, sourceName, doc.Path)
                ? HostedHolds.Stamp(decided.Set, group)
                : decided.Set;
            if (!_ids.TryGetValue(set.Hash, out var id))
                _ids[set.Hash] = id = await store.EnsureSetAsync(set, ct);
            return new StoredAcl(id, decided.FromFolderRule, set.DeniesEveryone);
        }

        /// <summary>
        /// Turns the outside principals a connector named into the groups they
        /// mean here. A principal nothing is mapped from contributes nothing, so
        /// a document whose principals are all unmapped reaches nobody: the
        /// principal mapper is the only thing that says what an outside name
        /// means, and without one, or without an answer from it, the safe
        /// reading is that it means no one.
        /// </summary>
        private async Task<DocumentAccess> ResolveExternalAsync(DocumentAccess access, CancellationToken ct)
        {
            if (access.ExternalPrincipals.Count == 0) return access;

            var unread = access.ExternalPrincipals.Where(p => !_external.ContainsKey(p)).ToArray();
            if (unread.Length > 0)
            {
                var resolved = await mappings.ResolveAsync(unread, ct);
                foreach (var (principal, group) in resolved.Mapped) _external[principal] = group;
                foreach (var p in resolved.Unmapped) _external[p] = null;
            }

            return access.WithResolvedExternals(
                access.ExternalPrincipals.Select(p => _external[p]).OfType<Principal>());
        }
    }

    private static async Task AssignAclAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid docId, StoredAcl acl, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE prem_index.document SET acl_set_id = @set, acl_from_rule = @fromRule WHERE id = @doc", conn, tx);
        cmd.Parameters.AddWithValue("set", acl.SetId);
        cmd.Parameters.AddWithValue("fromRule", acl.FromFolderRule);
        cmd.Parameters.AddWithValue("doc", docId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
