using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Ingestion;

/// <param name="DeniedToEveryone">
/// Documents indexed with an access value that reaches nobody, which is what a
/// connector returns when it could not read the source's permissions. Counted
/// and surfaced rather than swallowed: a run with a non-zero count here means
/// part of the corpus is invisible, and silence would look identical to
/// success.
/// </param>
/// <param name="Unreadable">
/// Paths that exist in the source and could not be read: encrypted without a key
/// this account holds, denied by an access list, locked, or corrupt. Their index
/// entries are PRESERVED, because the document was not deleted.
/// </param>
/// <param name="ReconciliationSkipped">
/// True when the run saw nothing at all while the index holds documents under
/// this source's prefix, and deletion was therefore refused. The usual cause is
/// the source being unavailable rather than empty: an unmounted encrypted
/// volume, a disconnected share, or revoked access all present as an empty
/// directory. Deleting on that signal would wipe the index and report success.
/// </param>
/// <param name="SkippedByExtension">
/// Files the source saw and did not index, counted by why: a format this
/// version does not read, by lower-case extension (<c>.pdf</c>, or
/// <c>(none)</c>), and <see cref="EmptyKey"/> for a file that was read and has
/// no text to index. They are not failures. A skipped format is not in
/// <see cref="Scanned"/>; an empty file was read, so it is. They are counted
/// so that a folder of PDFs does not read as a clean run over nothing.
/// </param>
/// <param name="UnmappedPrincipals">
/// Principals a connector named that mean nothing in this deployment, counted
/// once each however many documents carried them. Each one reached nobody, which
/// is the safe reading and also an invisible one: a share whose groups were
/// never mapped indexes cleanly and answers nothing, and without this count that
/// looks exactly like a folder with nothing in it.
/// </param>
/// <param name="KeptWithoutReader">
/// Documents indexed earlier at a path whose format no reader in this process
/// reads now, because the reader was refused at start, disallowed, or never
/// installed here. They are KEPT, and still found by search: removing indexed
/// documents is an explicit act (<see cref="IngestPipeline.RemoveUnread"/>),
/// never the side effect of a reader going missing.
/// </param>
/// <param name="RemovedNowSkipped">
/// Documents indexed earlier and removed by this run because the file is now
/// skipped: a reader read it and found nothing to index (such as a PDF saved
/// again without its text layer), or, with
/// <see cref="IngestPipeline.RemoveUnread"/>, no reader here reads its format.
/// These are part of <see cref="OrphansRemoved"/>, which counts every removal.
/// </param>
/// <param name="UndeclaredAuthorship">
/// Concepts of a source read as an OKF bundle that do not say who wrote them:
/// no frontmatter, or frontmatter without <c>generated</c>. What the source's
/// undeclared-authorship setting would hold back, counted whether it is on or
/// off, so the exposure is visible before anyone switches it on.
/// </param>
public sealed record IngestSummary(
    int Scanned,
    int Ingested,
    int Unchanged,
    int ChunksEmbedded,
    int OrphansRemoved = 0,
    int DeniedToEveryone = 0,
    int Unreadable = 0,
    bool ReconciliationSkipped = false,
    IReadOnlyList<SourceFailure>? Failures = null,
    IReadOnlyDictionary<string, int>? SkippedByExtension = null,
    int UndeclaredAuthorship = 0,
    int UnmappedPrincipals = 0,
    int KeptWithoutReader = 0,
    int RemovedNowSkipped = 0)
{
    /// <summary>The <see cref="SkippedByExtension"/> key for a file with no text to index.</summary>
    public const string EmptyKey = "(empty)";

    public IReadOnlyList<SourceFailure> FailureList => Failures ?? [];

    public IReadOnlyDictionary<string, int> SkippedFormats => SkippedByExtension ?? new Dictionary<string, int>();

    /// <summary>Every file skipped for its format.</summary>
    public int Skipped => SkippedFormats.Values.Sum();
}

/// <summary>
/// One-directional ingest: source system to derived store. The source stays
/// authoritative and this store must always be rebuildable from it, so nothing
/// here writes back to the source.
/// <para>
/// Unchanged documents (same content hash) are skipped; changed ones have their
/// chunks fully replaced. After the pass, documents under the source's declared
/// path prefix that it no longer yields are reconciled away, scoped to that
/// prefix so one connector never deletes another's documents.
/// </para>
/// <para>
/// Each run cuts documents with the chunker it names, from
/// <paramref name="chunkers"/> (the built-in ones when null). The name is
/// stored with each document, so a run under another chunker than the one that
/// cut a document stores it again.
/// </para>
/// <para>
/// A connector that hands over paths rather than finished documents has them
/// read here, through <paramref name="readers"/> (the built-in ones when
/// null). Which formats an installation can read is a property of the process,
/// so every connector reads the same set and a file no reader claims is
/// counted the same way whichever connector found it.
/// </para>
/// </summary>
public sealed partial class IngestPipeline(
    PremagenticDatabase db, IEmbeddingProvider embedder, bool headingPrefix = false,
    ChunkerRegistry? chunkers = null, ReaderRegistry? readers = null)
{
    /// <summary>
    /// Suffix appended to embedding_model when chunks are embedded with a
    /// "title > heading" context prefix. Prefixed passages live in a distinct
    /// vector space, so the name must differ or mixed spaces would fuse.
    /// The stored chunk content is NEVER prefixed, only the embedding input.
    /// </summary>
    public const string HeadingPrefixSuffix = "+hctx";

    private string EmbeddingModelName => headingPrefix ? embedder.Name + HeadingPrefixSuffix : embedder.Name;

    /// <summary>
    /// Whether a document indexed earlier at a path whose format no reader in
    /// this process reads may be removed by this run. Off by default, so a
    /// reader that was refused at start, or disallowed, costs nothing that was
    /// indexed: the document is kept, counted and named. An administrator who
    /// means to remove them runs one ingest with this on.
    /// </summary>
    public bool RemoveUnread { get; init; }

    /// <summary>
    /// What the principals a connector names mean here: the loaded extension's
    /// principal mapper, or null for none, which is the default. With none, a
    /// document whose permissions a connector gave only as outside principals
    /// is indexed readable by nobody, and the run counts the principals.
    /// </summary>
    public Identity.IPrincipalMapper? PrincipalMapper { get; init; }

    private string EmbedInput(SourceDocument doc, DocumentChunk chunk)
    {
        if (!headingPrefix) return chunk.Content;
        var context = string.IsNullOrEmpty(chunk.HeadingPath)
            ? doc.Title ?? ""
            : $"{doc.Title} > {chunk.HeadingPath}";
        return context.Length == 0 ? chunk.Content : $"{context}\n\n{chunk.Content}";
    }

    /// <param name="allowEmptySource">
    /// Permits reconciliation to delete every indexed document under this
    /// source's prefix when the run saw nothing at all. Off by default: an
    /// unavailable source and a genuinely emptied one are indistinguishable from
    /// the outside, and only one of them should destroy an index. An operator who
    /// really did empty the folder passes this once.
    /// </param>
    public Task<IngestSummary> RunAsync(
        Guid tenantId, IDocumentSource source,
        Action<string>? log = null, bool allowEmptySource = false, CancellationToken ct = default) =>
        RunAsync(tenantId, source, ChunkerRegistry.DefaultName, log, allowEmptySource, ct);

    /// <param name="chunker">The name of the chunker to cut this run's documents with.</param>
    /// <exception cref="UnknownChunkerException">
    /// This process has no chunker by that name. Thrown before the source is
    /// read, so the index is untouched.
    /// </exception>
    public async Task<IngestSummary> RunAsync(
        Guid tenantId, IDocumentSource source, string chunker,
        Action<string>? log = null, bool allowEmptySource = false, CancellationToken ct = default)
    {
        // Fails closed: a name this process does not have stops the run here,
        // never falling back to another chunker's boundaries.
        var cutter = (chunkers ?? ChunkerRegistry.BuiltIn).Resolve(chunker);

        int scanned = 0, ingested = 0, unchanged = 0, chunksEmbedded = 0, denied = 0, undeclared = 0;
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        // Skipped paths, by who decided: no reader here, or a reader's reason.
        var unreadPaths = new List<string>();
        var declined = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<SourceFailure>();
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var prefix = source.PathPrefix.Length == 0 ? "" : source.PathPrefix + "/";
        var access = await AclDecider.LoadAsync(db, tenantId, PrincipalMapper, ct);

        await foreach (var read in source.ReadThroughAsync(readers ?? ReaderRegistry.BuiltIn, ct))
        {
            if (read.Failure is { } failure)
            {
                scanned++;
                failures.Add(failure);
                // Seen, so reconciliation leaves the existing entry alone. The
                // document still exists; only this run could not read it.
                seenPaths.Add(failure.Path);
                log?.Invoke($"  {failure.Path}: unreadable, keeping the existing index entry ({failure.Reason})");
                continue;
            }

            // Not read and not indexed. What that means for a document indexed
            // earlier at the path depends on who decided. No reader here says
            // nothing about the file, so the path counts as seen and an earlier
            // entry is kept, unless removal was asked for. A reader that read
            // the file and found nothing to index says the text is gone, so
            // the path is not seen and an earlier entry is removed, and said.
            if (read.Skip is { } skip)
            {
                skipped[skip.Extension] = skipped.GetValueOrDefault(skip.Extension) + 1;
                if (skip.Reason is { } reason)
                {
                    declined[skip.Path] = reason;
                }
                else
                {
                    unreadPaths.Add(skip.Path);
                    if (!RemoveUnread) seenPaths.Add(skip.Path);
                }
                continue;
            }

            var doc = read.Document
                ?? throw new InvalidOperationException(
                    $"Connector '{source.Name}' yielded a SourceRead with neither a document nor a failure.");

            scanned++;

            if (!doc.Path.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Connector '{source.Name}' yielded path '{doc.Path}' outside its declared prefix '{prefix}'. " +
                    "Reconciliation is prefix-scoped, so this would leave orphans behind on every run.");

            var chunks = cutter.Chunk(doc);
            if (chunks.Count == 0)
            {
                // Read, and nothing in it to index: an empty file, or one that
                // is only frontmatter. Counted, so the run can say it was seen.
                skipped[IngestSummary.EmptyKey] = skipped.GetValueOrDefault(IngestSummary.EmptyKey) + 1;
                continue;
            }
            if (doc.Okf is { DeclaresNoAuthor: true }) undeclared++;

            var acl = await access.DecideAsync(source.Name, doc, ct);
            if (acl.DeniesEveryone)
            {
                denied++;
                log?.Invoke($"  {doc.Path}: indexed but readable by nobody (no connector list or folder rule lets anyone read it)");
            }

            seenPaths.Add(doc.Path);
            if (await IsUnchangedAsync(tenantId, doc, acl, cutter.Name, ct))
            {
                unchanged++;
                continue;
            }

            log?.Invoke($"  {doc.Path} ({chunks.Count} chunks, {doc.LifecycleStatus})");
            var embeddings = await embedder.EmbedAsync(
                chunks.Select(c => EmbedInput(doc, c)).ToArray(), ct);
            try
            {
                await UpsertAsync(tenantId, source.Name, doc, acl, cutter.Name, chunks, embeddings, ct);
            }
            catch (PostgresException ex) when (IsThisDocuments(ex))
            {
                // What this one document holds is more than the index can
                // store, such as a chunk whose search vector is over
                // PostgreSQL's 1 MB. Its transaction is gone, so nothing of it
                // was written; it is recorded like a file that could not be
                // read, its earlier entry kept, and the rest of the source is
                // still ingested and reconciled.
                var stored = new SourceFailure(doc.Path, $"the index could not store it ({ex.SqlState}: {Brief(ex.MessageText)})");
                failures.Add(stored);
                log?.Invoke($"  {doc.Path}: not stored, keeping the existing index entry ({stored.Reason})");
                continue;
            }
            ingested++;
            chunksEmbedded += chunks.Count;
        }

        // The guard. A run that saw nothing at all is far more likely to mean the
        // source is gone than that every document was deleted, so deletion is
        // refused unless an operator says otherwise. A run that saw even one
        // path, document, failure or skipped file, reconciles normally: a file
        // skipped for its format proves the folder is there and readable.
        if (scanned == 0 && skipped.Count == 0 && !allowEmptySource)
        {
            var indexed = await CountUnderPrefixAsync(tenantId, prefix, ct);
            if (indexed > 0)
            {
                log?.Invoke(
                    $"  REFUSING to reconcile: the source yielded nothing while {indexed} document(s) are " +
                    "indexed under this prefix. An unavailable source (unmounted volume, disconnected " +
                    "share, revoked access) looks exactly like an emptied one. The index is unchanged. " +
                    "Pass allowEmptySource only if the source really is empty.");
                return new IngestSummary(
                    scanned, ingested, unchanged, chunksEmbedded,
                    OrphansRemoved: 0, DeniedToEveryone: denied,
                    Unreadable: failures.Count, ReconciliationSkipped: true, Failures: failures,
                    SkippedByExtension: skipped, UndeclaredAuthorship: undeclared,
                    UnmappedPrincipals: access.UnmappedPrincipals.Count);
            }
        }

        // Said once, at the end, naming them: an operator who sees this knows a
        // mapping is missing rather than guessing at an empty folder.
        if (access.UnmappedPrincipals.Count > 0)
            log?.Invoke(
                $"  {access.UnmappedPrincipals.Count} principal(s) named by the source mean nothing here " +
                (access.Mapper is { } mapper
                    ? $"(the principal mapper {mapper.Name} maps them to no group)"
                    : "(no principal mapper is loaded)") +
                $", so what they alone allowed reaches nobody: {string.Join(", ", access.UnmappedPrincipals.Order(StringComparer.Ordinal))}.");

        var kept = RemoveUnread ? Array.Empty<string>() : await IndexedAsync(tenantId, unreadPaths, ct);
        foreach (var path in kept)
            log?.Invoke($"  {path}: no reader here reads its format now; keeping the existing index entry");

        var removed = await ReconcileAsync(tenantId, prefix, seenPaths, ct);
        var removedNowSkipped = 0;
        var unread = RemoveUnread ? unreadPaths.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in removed)
        {
            if (declined.TryGetValue(path, out var reason))
            {
                removedNowSkipped++;
                log?.Invoke($"  removed, now skipped: {path} ({reason})");
            }
            else if (unread.Contains(path))
            {
                removedNowSkipped++;
                log?.Invoke($"  removed, now skipped: {path} (no reader here reads its format; --remove-unread)");
            }
            else
            {
                log?.Invoke($"  orphan removed: {path}");
            }
        }

        return new IngestSummary(
            scanned, ingested, unchanged, chunksEmbedded, removed.Count, denied,
            Unreadable: failures.Count, ReconciliationSkipped: false, Failures: failures,
            SkippedByExtension: skipped, UndeclaredAuthorship: undeclared,
            UnmappedPrincipals: access.UnmappedPrincipals.Count,
            KeptWithoutReader: kept.Count, RemovedNowSkipped: removedNowSkipped);
    }

    /// <summary>
    /// A write refused for what one document holds: a data exception (class
    /// 22), such as a value the column cannot take, or a program limit (class
    /// 54), such as a search vector over 1 MB. Any other failure, a lost
    /// connection or a lock, is the run's and still ends it.
    /// </summary>
    private static bool IsThisDocuments(PostgresException ex) =>
        ex.SqlState.StartsWith("22", StringComparison.Ordinal) || ex.SqlState.StartsWith("54", StringComparison.Ordinal);

    /// <summary>One line and at most 200 characters, so a server's message cannot fill a report.</summary>
    private static string Brief(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "...";
    }

    private async Task<int> CountUnderPrefixAsync(Guid tenantId, string prefix, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM prem_index.document WHERE tenant_id = @tenant AND starts_with(path, @prefix)");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("prefix", prefix);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Which of these paths are indexed now, in path order.</summary>
    private async Task<IReadOnlyList<string>> IndexedAsync(Guid tenantId, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return [];
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT path FROM prem_index.document WHERE tenant_id = @tenant AND path = ANY(@paths) ORDER BY path");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("paths", paths.ToArray());
        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) found.Add(reader.GetString(0));
        return found;
    }

    /// <summary>
    /// Deletes indexed documents under the source's prefix whose path this run
    /// did not count as seen: deleted, moved, or newly out of scope at the
    /// source, or skipped by a reader that found nothing to index. Chunks go
    /// with them via ON DELETE CASCADE. Returns the paths removed.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReconcileAsync(
        Guid tenantId, string prefix, IReadOnlyCollection<string> seenPaths, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            DELETE FROM prem_index.document
            WHERE tenant_id = @tenant
              AND starts_with(path, @prefix)
              AND NOT (path = ANY(@seen))
            RETURNING path
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("seen", seenPaths.ToArray());

        var removed = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) removed.Add(reader.GetString(0));
        return removed;
    }

    private async Task<bool> IsUnchangedAsync(
        Guid tenantId, SourceDocument doc, StoredAcl acl, string chunker, CancellationToken ct)
    {
        // Access is part of the comparison: a permission change at the source
        // with no content edit must still land, or the gate would serve a stale
        // audience. So are the OKF values: switching a source into or out of
        // bundle mode changes them without changing a byte of content. So is
        // the chunker: another chunker cuts other chunks from the same text.
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT 1 FROM prem_index.document d
            WHERE d.tenant_id = @tenant AND d.path = @path AND d.content_hash = @hash
              AND d.acl_set_id = @aclSet
              AND d.acl_from_rule = @aclFromRule
              AND d.okf_concept_id IS NOT DISTINCT FROM @okfConceptId
              AND d.trust_tier = @okfTrustTier
              AND d.authorship = @okfAuthorship
              AND d.stale_after IS NOT DISTINCT FROM @okfStaleAfter
              AND d.generated_at IS NOT DISTINCT FROM @okfGeneratedAt
              AND d.last_verified_at IS NOT DISTINCT FROM @okfLastVerifiedAt
              AND d.frontmatter_state = @okfFrontmatterState
              AND d.chunker = @chunker
              AND EXISTS (SELECT 1 FROM prem_index.chunk c WHERE c.document_id = d.id AND c.embedding_model = @model)
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("path", doc.Path);
        cmd.Parameters.AddWithValue("hash", doc.ContentHash);
        cmd.Parameters.AddWithValue("aclSet", acl.SetId);
        cmd.Parameters.AddWithValue("aclFromRule", acl.FromFolderRule);
        cmd.Parameters.AddWithValue("model", EmbeddingModelName);
        cmd.Parameters.AddWithValue("chunker", chunker);
        Okf.OkfColumns.For(doc.Okf).AddParameters(cmd);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Writes the document and replaces its chunks, in one transaction.
    /// <para>
    /// Chunks are deleted and inserted again rather than updated, and every
    /// insert takes a fresh id. The in-memory vector index relies on that: a
    /// chunk id's vector never changes, so a search only has to learn which ids
    /// exist, never whether one it already holds has moved.
    /// </para>
    /// </summary>
    private async Task UpsertAsync(
        Guid tenantId, string sourceName, SourceDocument doc, StoredAcl acl, string chunker,
        IReadOnlyList<DocumentChunk> chunks, float[][] embeddings, CancellationToken ct)
    {
        if (embeddings.Length != chunks.Count)
            throw new InvalidOperationException(
                $"Embedding provider '{embedder.Name}' returned {embeddings.Length} vectors for {chunks.Count} chunks.");

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid docId;
        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO prem_index.document(
                tenant_id, path, title, doc_class, lifecycle_status,
                source_name, source_modified_at,
                content_hash, updated_at,
                okf_concept_id, trust_tier, authorship, stale_after,
                generated_at, last_verified_at, frontmatter_state, chunker)
            VALUES(@tenant, @path, @title, @docClass, @lifecycle,
                   @source, @sourceModified, @hash, now(),
                   @okfConceptId, @okfTrustTier, @okfAuthorship, @okfStaleAfter,
                   @okfGeneratedAt, @okfLastVerifiedAt, @okfFrontmatterState, @chunker)
            ON CONFLICT (tenant_id, path) DO UPDATE SET
                title = EXCLUDED.title,
                doc_class = EXCLUDED.doc_class,
                lifecycle_status = EXCLUDED.lifecycle_status,
                source_name = EXCLUDED.source_name,
                source_modified_at = EXCLUDED.source_modified_at,
                content_hash = EXCLUDED.content_hash,
                updated_at = now(),
                okf_concept_id = EXCLUDED.okf_concept_id,
                trust_tier = EXCLUDED.trust_tier,
                authorship = EXCLUDED.authorship,
                stale_after = EXCLUDED.stale_after,
                generated_at = EXCLUDED.generated_at,
                last_verified_at = EXCLUDED.last_verified_at,
                frontmatter_state = EXCLUDED.frontmatter_state,
                chunker = EXCLUDED.chunker
            RETURNING id
            """, conn, tx))
        {
            upsert.Parameters.AddWithValue("tenant", tenantId);
            upsert.Parameters.AddWithValue("path", doc.Path);
            upsert.Parameters.AddWithValue("title", (object?)doc.Title ?? DBNull.Value);
            upsert.Parameters.AddWithValue("docClass", (object?)doc.DocClass ?? DBNull.Value);
            upsert.Parameters.AddWithValue("lifecycle", doc.LifecycleStatus);
            upsert.Parameters.AddWithValue("source", sourceName);
            upsert.Parameters.AddWithValue("sourceModified", (object?)doc.SourceModifiedAt ?? DBNull.Value);
            upsert.Parameters.AddWithValue("hash", doc.ContentHash);
            upsert.Parameters.AddWithValue("chunker", chunker);
            Okf.OkfColumns.For(doc.Okf).AddParameters(upsert);
            docId = (Guid)(await upsert.ExecuteScalarAsync(ct))!;
        }
        await AssignAclAsync(conn, tx, docId, acl, ct);

        await using (var wipe = new NpgsqlCommand("DELETE FROM prem_index.chunk WHERE document_id = @doc", conn, tx))
        {
            wipe.Parameters.AddWithValue("doc", docId);
            await wipe.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (embeddings[i].Length != embedder.Dimensions)
                throw new InvalidOperationException(
                    $"Embedding provider '{embedder.Name}' declares {embedder.Dimensions} dimensions " +
                    $"and returned a vector of {embeddings[i].Length}.");

            await using var insert = new NpgsqlCommand("""
                INSERT INTO prem_index.chunk(
                    document_id, seq, heading_path, doc_title, content,
                    embedding, embedding_model, embedding_dims, embedded_at)
                VALUES(@doc, @seq, @heading, @title, @content, @embedding, @model, @dims, now())
                """, conn, tx);
            insert.Parameters.AddWithValue("doc", docId);
            insert.Parameters.AddWithValue("seq", chunk.Seq);
            insert.Parameters.AddWithValue("heading", chunk.HeadingPath);
            insert.Parameters.AddWithValue("title", doc.Title ?? "");
            insert.Parameters.AddWithValue("content", chunk.Content);
            // Normalized here, once, so every search can use a plain dot product.
            insert.Parameters.AddWithValue("embedding", VectorCodec.Encode(embeddings[i]));
            insert.Parameters.AddWithValue("model", EmbeddingModelName);
            insert.Parameters.AddWithValue("dims", embedder.Dimensions);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
