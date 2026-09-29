using System.Text.Json;
using System.Text.Json.Serialization;
using Premagentic.Core.Admin;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Sources.Registry;

/// <summary>
/// A folder the deployment reads, as the registry keeps it.
/// </summary>
/// <param name="Folder">The folder, as an absolute path.</param>
/// <param name="PathPrefix">Where its documents sit in the index, or empty for the whole index.</param>
/// <param name="OkfBundle">Read as an Open Knowledge Format bundle.</param>
/// <param name="UndeclaredIsMachine">
/// In bundle mode, a concept that does not say who wrote it is stored as
/// machine-written and unverified, so agents do not see it until a person
/// signs it off. Off by default.
/// </param>
/// <param name="Chunker">
/// The name of the chunker its documents are cut with; <c>markdown</c> unless
/// set. A run fails before it reads anything when the process running it has no
/// chunker by this name.
/// </param>
public sealed record RegisteredSource(
    Guid Id,
    string Name,
    string Folder,
    string PathPrefix,
    bool OkfBundle,
    bool UndeclaredIsMachine,
    string Chunker,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? OwnerUserId = null)
{
    /// <summary>
    /// The connector for this source. Its documents take their access from the
    /// folder rules, the same as a folder given to <c>prem ingest</c> by hand,
    /// so the two forms store the same rows.
    /// </summary>
    public FileSystemSource ToFileSystemSource() => new(Folder, DocumentAccess.FolderRules, PathPrefix)
    {
        OkfBundle = OkfBundle,
        UndeclaredAuthorshipIsMachine = UndeclaredIsMachine,
    };
}

/// <summary>
/// The sources registry, <c>prem_config.source</c>: the folders this deployment
/// reads, by name, each with its path prefix and how it is read. Every change
/// is written to the change record in the same transaction.
/// <para>
/// Removing a source removes it from the registry and nothing else. Its
/// documents stay indexed, searchable under their folder rules, until a run
/// over the same prefix no longer yields them or the index is rebuilt; its run
/// history is kept.
/// </para>
/// </summary>
/// <param name="chunkers">
/// The chunkers a source may name, or null for the built-in ones. A name not
/// in it is refused when a source is added or changed.
/// </param>
public sealed class SourceRegistry(PremagenticDatabase db, Guid tenantId, ChunkerRegistry? chunkers = null)
{
    public const string AddKind = "source.add";
    public const string SetKind = "source.set";
    public const string RemoveKind = "source.remove";

    private const string Columns = "id, name, folder, path_prefix, okf_bundle, undeclared_is_machine, chunker, created_at, updated_at, owner_user_id";

    private ChunkerRegistry Chunkers => chunkers ?? ChunkerRegistry.BuiltIn;

    /// <summary>A prefix as the connector uses it: forward slashes, no slash at either end.</summary>
    public static string NormalizePrefix(string prefix) => prefix.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Two prefixes overlap when one holds the other. Each run removes documents
    /// under its own prefix that it did not see, so two overlapping sources
    /// would delete each other's documents on every run.
    /// </summary>
    public static bool Overlap(string a, string b) =>
        a.Length == 0 || b.Length == 0 || a == b
        || a.StartsWith(b + "/", StringComparison.Ordinal)
        || b.StartsWith(a + "/", StringComparison.Ordinal);

    /// <param name="chunker">The name of the chunker to cut its documents with.</param>
    /// <exception cref="ArgumentException">The name, the folder or the chunker is not usable.</exception>
    /// <exception cref="InvalidOperationException">The name is taken, or the prefix overlaps a registered source.</exception>
    public async Task<RegisteredSource> AddAsync(
        string name, string folder, string pathPrefix, bool okfBundle, bool undeclaredIsMachine,
        AdminActor actor, string chunker = ChunkerRegistry.DefaultName, CancellationToken ct = default)
    {
        RequireName(name);
        var chunkerName = Chunkers.Canonical(chunker);
        var full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) throw new ArgumentException($"There is no folder at {full}.");
        var prefix = NormalizePrefix(pathPrefix);

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ChangeRecord.LockAsync(conn, tx, tenantId, ct);

        var live = await ListAsync(conn, tx, ct);
        if (live.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A source named '{name}' is already registered.");
        if (live.FirstOrDefault(s => Overlap(s.PathPrefix, prefix)) is { } clash)
            throw new InvalidOperationException(
                $"Source '{clash.Name}' already owns prefix {Show(clash.PathPrefix)}, which overlaps {Show(prefix)}. " +
                "Every ingest removes the documents under its own prefix that it did not see, so two overlapping " +
                "sources would delete each other's documents. Give this source a prefix of its own with --prefix.");

        RegisteredSource added;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO prem_config.source(tenant_id, name, folder, path_prefix, okf_bundle, undeclared_is_machine, chunker)
            VALUES(@tenant, @name, @folder, @prefix, @bundle, @undeclared, @chunker)
            RETURNING {Columns}
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("folder", full);
            insert.Parameters.AddWithValue("prefix", prefix);
            insert.Parameters.AddWithValue("bundle", okfBundle);
            insert.Parameters.AddWithValue("undeclared", undeclaredIsMachine);
            insert.Parameters.AddWithValue("chunker", chunkerName);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            added = Read(reader);
        }

        await ChangeRecord.AppendAsync(conn, tx, tenantId, AddKind, added.Name, null, Json(added), actor, ct);
        await tx.CommitAsync(ct);
        return added;
    }

    /// <summary>
    /// Changes how a source is read. A null argument leaves that value as it is.
    /// Takes effect at the source's next ingest, which re-stores every document
    /// whose stored values change, with no change to its content needed. A
    /// change of chunker changes every document's chunks, so that run stores
    /// every document again.
    /// </summary>
    /// <returns>The source before and after; <c>Changed</c> is false when nothing differed and nothing was recorded.</returns>
    /// <exception cref="ArgumentException">There is no such source, or no chunker by that name.</exception>
    public async Task<(RegisteredSource Before, RegisteredSource After, bool Changed)> UpdateAsync(
        string name, bool? okfBundle, bool? undeclaredIsMachine, AdminActor actor,
        string? chunker = null, CancellationToken ct = default)
    {
        var chunkerName = chunker is null ? null : Chunkers.Canonical(chunker);

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ChangeRecord.LockAsync(conn, tx, tenantId, ct);

        var before = (await ListAsync(conn, tx, ct)).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There is no source named '{name}'.");
        var wanted = before with
        {
            OkfBundle = okfBundle ?? before.OkfBundle,
            UndeclaredIsMachine = undeclaredIsMachine ?? before.UndeclaredIsMachine,
            Chunker = chunkerName ?? before.Chunker,
        };
        if (wanted == before)
        {
            await tx.CommitAsync(ct);
            return (before, before, false);
        }

        RegisteredSource after;
        await using (var update = new NpgsqlCommand($"""
            UPDATE prem_config.source SET okf_bundle = @bundle, undeclared_is_machine = @undeclared, chunker = @chunker, updated_at = now()
            WHERE tenant_id = @tenant AND id = @id
            RETURNING {Columns}
            """, conn, tx))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            update.Parameters.AddWithValue("id", before.Id);
            update.Parameters.AddWithValue("bundle", wanted.OkfBundle);
            update.Parameters.AddWithValue("undeclared", wanted.UndeclaredIsMachine);
            update.Parameters.AddWithValue("chunker", wanted.Chunker);
            await using var reader = await update.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            after = Read(reader);
        }

        await ChangeRecord.AppendAsync(conn, tx, tenantId, SetKind, after.Name, Json(before), Json(after), actor, ct);
        await tx.CommitAsync(ct);
        return (before, after, true);
    }

    /// <summary>
    /// Names the person answerable for a source's documents, or clears it with
    /// null. The owner decides nothing: it is who the review queue and the
    /// sources pages name, never a permission. Access stays with the folder
    /// rules, so that there is one place that decides who may read a document
    /// rather than two.
    /// </summary>
    /// <returns>The source before and after; <c>Changed</c> is false when the owner was already that.</returns>
    /// <exception cref="ArgumentException">There is no such source.</exception>
    public async Task<(RegisteredSource Before, RegisteredSource After, bool Changed)> SetOwnerAsync(
        string name, Guid? ownerUserId, AdminActor actor, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ChangeRecord.LockAsync(conn, tx, tenantId, ct);

        var before = (await ListAsync(conn, tx, ct)).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There is no source named '{name}'.");
        if (before.OwnerUserId == ownerUserId)
        {
            await tx.CommitAsync(ct);
            return (before, before, false);
        }

        RegisteredSource after;
        await using (var update = new NpgsqlCommand($"""
            UPDATE prem_config.source SET owner_user_id = @owner, updated_at = now()
            WHERE tenant_id = @tenant AND id = @id
            RETURNING {Columns}
            """, conn, tx))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            update.Parameters.AddWithValue("id", before.Id);
            update.Parameters.AddWithValue("owner", (object?)ownerUserId ?? DBNull.Value);
            await using var reader = await update.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            after = Read(reader);
        }

        await ChangeRecord.AppendAsync(conn, tx, tenantId, SetKind, after.Name, Json(before), Json(after), actor, ct);
        await tx.CommitAsync(ct);
        return (before, after, true);
    }

    /// <summary>
    /// Removes a source from the registry. Its documents and its run history
    /// are not touched.
    /// </summary>
    /// <returns>The source removed, or null when there was none by that name.</returns>
    public async Task<RegisteredSource?> RemoveAsync(string name, AdminActor actor, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ChangeRecord.LockAsync(conn, tx, tenantId, ct);

        var source = (await ListAsync(conn, tx, ct)).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        await using (var remove = new NpgsqlCommand(
            "UPDATE prem_config.source SET deleted_at = now() WHERE tenant_id = @tenant AND id = @id", conn, tx))
        {
            remove.Parameters.AddWithValue("tenant", tenantId);
            remove.Parameters.AddWithValue("id", source.Id);
            await remove.ExecuteNonQueryAsync(ct);
        }

        await ChangeRecord.AppendAsync(conn, tx, tenantId, RemoveKind, source.Name, Json(source), null, actor, ct);
        await tx.CommitAsync(ct);
        return source;
    }

    /// <summary>The live sources, by name.</summary>
    public async Task<IReadOnlyList<RegisteredSource>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        return await ListAsync(conn, null, ct);
    }

    public async Task<RegisteredSource?> FindAsync(string name, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many documents the index holds under a source's prefix now.</summary>
    public async Task<long> IndexedDocumentCountAsync(RegisteredSource source, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM prem_index.document WHERE tenant_id = @tenant AND starts_with(path, @prefix)");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("prefix", source.PathPrefix.Length == 0 ? "" : source.PathPrefix + "/");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<List<RegisteredSource>> ListAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Columns} FROM prem_config.source
            WHERE tenant_id = @tenant AND deleted_at IS NULL
            ORDER BY lower(name)
            """, conn, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);

        var sources = new List<RegisteredSource>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sources.Add(Read(reader));
        return sources;
    }

    private static RegisteredSource Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        reader.GetBoolean(5),
        reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetGuid(9));

    /// <summary>
    /// Whether text can name a source: up to 64 letters, digits, dots, hyphens
    /// and underscores, starting with a letter or digit. Public so that
    /// anything that has to refuse a name BEFORE calling here, such as a check
    /// that must not leave work half done, asks the rule rather than repeating
    /// it.
    /// </summary>
    public static bool IsName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static void RequireName(string name)
    {
        if (!IsName(name))
            throw new ArgumentException(
                $"'{name}' cannot name a source. A name is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter or digit.");
    }

    private static string Show(string prefix) => prefix.Length == 0 ? "(the whole index)" : $"'{prefix}'";

    private static string Json(RegisteredSource source) =>
        JsonSerializer.Serialize(new RecordedSource(
            source.Folder, source.PathPrefix, source.OkfBundle, source.UndeclaredIsMachine, source.Chunker,
            source.OwnerUserId));

    /// <summary>What the change record keeps of a source.</summary>
    private sealed record RecordedSource(
        [property: JsonPropertyName("folder")] string Folder,
        [property: JsonPropertyName("path_prefix")] string PathPrefix,
        [property: JsonPropertyName("okf_bundle")] bool OkfBundle,
        [property: JsonPropertyName("undeclared_is_machine")] bool UndeclaredIsMachine,
        [property: JsonPropertyName("chunker")] string Chunker,
        [property: JsonPropertyName("owner_user_id")] Guid? OwnerUserId);
}
