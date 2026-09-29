using System.Text.Json;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Admin;

/// <summary>
/// The deployment's configuration as plain JSON: every row of the tables that
/// say who may read what and how the deployment reads its sources. It is what
/// cannot be rebuilt from the customer's files.
/// <para>
/// Only the tables named here are exported, so a table added later is left out
/// until someone decides it belongs. Password and token hashes are left out
/// unless asked for. Sessions are never exported. The audit trail and the
/// change record are exported separately, as JSON lines.
/// </para>
/// </summary>
public static class ConfigExport
{
    private static readonly (string Table, string[] Secrets)[] Tables =
    [
        ("tenant", []),
        ("app_user", ["password_hash"]),
        ("app_group", []),
        ("group_membership", []),
        ("agent", []),
        ("agent_group_grant", []),
        ("agent_token", ["secret_sha256"]),
        ("acl_set", []),
        ("folder_rule", []),
        ("hosted_hold", []),
        ("setting", []),
        ("source", []),
    ];

    /// <summary>The tables exported, in order.</summary>
    public static IReadOnlyList<string> TableNames { get; } = Tables.Select(t => t.Table).ToArray();

    public static async Task WriteAsync(
        PremagenticDatabase db, Guid tenantId, bool includeSecrets, DateTimeOffset exportedAt, Stream output, CancellationToken ct = default)
    {
        await using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("exported_at", exportedAt.ToUniversalTime());
        json.WriteBoolean("includes_secrets", includeSecrets);
        json.WriteStartObject("tables");

        foreach (var (table, secrets) in Tables)
        {
            var strip = includeSecrets || secrets.Length == 0 ? "" : $" - ARRAY['{string.Join("','", secrets)}']";
            var tenantColumn = table == "tenant" ? "id" : "tenant_id";
            await using var cmd = db.DataSource.CreateCommand(
                $"SELECT (to_jsonb(t){strip})::text FROM prem_config.{table} t WHERE t.{tenantColumn} = @tenant ORDER BY 1");
            cmd.Parameters.AddWithValue("tenant", tenantId);

            json.WriteStartArray(table);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) json.WriteRawValue(reader.GetString(0));
            json.WriteEndArray();
        }

        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    /// <summary>The change record, oldest first, one JSON object per line.</summary>
    public static async IAsyncEnumerable<string> ChangeRecordLinesAsync(
        PremagenticDatabase db, Guid tenantId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT row_to_json(e)::text FROM prem_config.admin_event e WHERE e.tenant_id = @tenant ORDER BY e.id");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return reader.GetString(0);
    }
}
