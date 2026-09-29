using System.Reflection;
using System.Runtime.InteropServices;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Admin;

/// <summary>One migration section the database records.</summary>
public sealed record RecordedMigration(string Schema, int Version, string Name, string Checksum, DateTimeOffset AppliedAt);

/// <summary>What the health page shows: versions, the schema, sizes and counts.</summary>
/// <param name="VectorBytes">
/// The bytes of every stored embedding: what the in-memory vector index holds
/// once every vector is loaded. Measured from the database, not from the
/// process, so it reads the same from any host.
/// </param>
/// <param name="ProcessBytes">The working set of the process serving this page.</param>
public sealed record HealthSnapshot(
    string Version,
    string Runtime,
    string OperatingSystem,
    string DatabaseVersion,
    long DatabaseBytes,
    long Documents,
    long Chunks,
    long VectorBytes,
    long ProcessBytes,
    IReadOnlyList<RecordedMigration> Applied,
    IReadOnlyList<AppliedMigration> Pending);

/// <summary>The embedding model this process loaded.</summary>
/// <param name="Folder">Where a local model was loaded from; null for a provider with no folder.</param>
/// <param name="Revision">The revision <c>prem-model.json</c> names, or null.</param>
public sealed record LoadedModel(string Name, string? Folder, string? Revision);

public static class HealthReport
{
    /// <summary>
    /// The model this process embeds with, and for a local one the folder it
    /// came from. The walk up from the executable can find a models/minilm
    /// that is not the one installed, so the page that says what is running
    /// says where it came from.
    /// </summary>
    public static LoadedModel Model(Embeddings.IEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider is Embeddings.LocalOnnxEmbeddingProvider local
            ? new LoadedModel(local.Name, local.Folder, local.Revision)
            : new LoadedModel(provider.Name, null, null);
    }

    /// <summary>The product version this build carries.</summary>
    public static string Version =>
        typeof(HealthReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HealthReport).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static async Task<HealthSnapshot> ReadAsync(PremagenticDatabase db, Guid tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT version(), pg_database_size(current_database()),
                   (SELECT count(*) FROM prem_index.document WHERE tenant_id = @tenant),
                   (SELECT count(*) FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id WHERE d.tenant_id = @tenant),
                   (SELECT coalesce(sum(octet_length(c.embedding)), 0) FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id WHERE d.tenant_id = @tenant)
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);

        string dbVersion;
        long dbBytes, documents, chunks, vectorBytes;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            dbVersion = r.GetString(0);
            dbBytes = r.GetInt64(1);
            documents = r.GetInt64(2);
            chunks = r.GetInt64(3);
            vectorBytes = r.GetInt64(4);
        }

        var applied = new List<RecordedMigration>();
        foreach (var schema in Migration.Schemas)
        {
            await using var read = db.DataSource.CreateCommand(
                $"SELECT version, name, checksum, applied_at FROM {schema}.schema_migration ORDER BY version");
            await using var r = await read.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                applied.Add(new RecordedMigration(schema, r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        }

        return new HealthSnapshot(
            Version,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            dbVersion,
            dbBytes,
            documents,
            chunks,
            vectorBytes,
            Environment.WorkingSet,
            applied.OrderBy(m => m.Version).ThenBy(m => m.Schema, StringComparer.Ordinal).ToArray(),
            await new MigrationRunner(db.DataSource).PendingAsync(ct));
    }
}
