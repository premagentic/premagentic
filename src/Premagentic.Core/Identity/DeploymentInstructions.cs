using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Identity;

/// <summary>The settings of the MCP surface: what an assistant is told when it connects, and the largest request it may send.</summary>
public static class McpSettings
{
    /// <summary>
    /// The deployment's instructions for assistants: text of at most 8,000
    /// characters, written by an administrator, carried by a profile and in
    /// the change record. Unset means an assistant is told nothing beyond the
    /// tools' own descriptions.
    /// </summary>
    public const string Instructions = "mcp.instructions";

    /// <summary>
    /// The longest instructions a deployment may give. Room for a page of
    /// house rules; short enough that connecting cannot push a manual into
    /// every assistant's context.
    /// </summary>
    public const int InstructionsMaxLength = 8_000;

    /// <summary>What <see cref="Instructions"/> accepts, as a phrase that completes "takes ...".</summary>
    public static string InstructionsAccepts =>
        $"text of 1 to {InstructionsMaxLength:N0} characters, with line breaks and no other control characters";

    /// <summary>The problem with a stored value, or null when it can be used.</summary>
    public static string? InstructionsProblem(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            return $"{Instructions} takes {InstructionsAccepts}. Unset it to tell assistants nothing.";
        var text = value.GetString()!;
        if (text.Length > InstructionsMaxLength)
            return $"{Instructions} takes at most {InstructionsMaxLength:N0} characters, not {text.Length:N0}.";
        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
            return $"{Instructions} takes line breaks and no other control characters.";
        return null;
    }

    /// <summary>
    /// The largest request body <c>/mcp</c> takes, in bytes. A body over it is
    /// refused before anything reads past the bound, with the HTTP status for
    /// a body too large and a JSON-RPC error, so a client reads one reason.
    /// The stdio bridge reaches the server through <c>/mcp</c>, so the bound
    /// covers it too. Read when the server starts.
    /// </summary>
    public const string MaxRequestBytes = "mcp.max_request_bytes";

    /// <summary>
    /// 256 KB. The largest request an assistant has reason to send is a
    /// <c>tools/call</c> carrying a query and its few arguments, a few
    /// kilobytes even for a pasted passage, and <c>initialize</c> carries a
    /// name and a list of capabilities; the web server's own bound, which a
    /// deployment did not choose, is 30,000,000 bytes.
    /// </summary>
    public const int MaxRequestBytesDefault = 256 * 1024;

    /// <summary>16 KB: below this an assistant's <c>initialize</c> could be refused.</summary>
    public const int MaxRequestBytesMin = 16 * 1024;

    /// <summary>The web server's own bound, which no setting raises.</summary>
    public const int MaxRequestBytesMax = 30_000_000;

    /// <summary>What <see cref="MaxRequestBytes"/> accepts, as a phrase that completes "takes ...".</summary>
    public static string MaxRequestBytesAccepts => $"a whole number of bytes from {MaxRequestBytesMin} to {MaxRequestBytesMax}";

    /// <summary>The problem with a stored value, or null when it can be used.</summary>
    public static string? MaxRequestBytesProblem(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var bytes) && bytes is >= MaxRequestBytesMin and <= MaxRequestBytesMax
            ? null
            : $"{MaxRequestBytes} takes {MaxRequestBytesAccepts}.";
}

/// <summary>
/// What an administrator wrote for every assistant that connects, served when
/// it connects (in the <c>server/discover</c> result under MCP 2026-07-28, and
/// in the <c>initialize</c> result to a client of an earlier revision) and as
/// the resource <c>premagentic://instructions</c>.
/// Instructions are obeyed, not cited, so they are only ever an administrator's
/// words: never machine-written, never taken from a document.
/// </summary>
/// <param name="UpdatedBy">Who last set it, as the change record names the actor.</param>
public sealed record DeploymentInstructions(string Text, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>Reads a deployment's instructions for assistants.</summary>
public interface IInstructionsReader
{
    /// <summary>The instructions in force, or null when none are set.</summary>
    Task<DeploymentInstructions?> ReadAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Reads the instructions from the deployment's settings, with the time they
/// were set and who set them from the change record. A stored value that
/// cannot be used reads as none: an assistant told nothing is safer than one
/// told something nobody meant.
/// </summary>
public sealed class SettingsInstructionsReader(PremagenticDatabase db) : IInstructionsReader
{
    public async Task<DeploymentInstructions?> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT s.value::text, s.updated_at, e.actor_surface, e.actor_account, e.actor_user_id
            FROM prem_config.setting s
            LEFT JOIN LATERAL (
                SELECT actor_surface, actor_account, actor_user_id FROM prem_config.admin_event
                WHERE tenant_id = s.tenant_id AND target = s.key ORDER BY id DESC LIMIT 1) e ON true
            WHERE s.tenant_id = @tenant AND s.key = @key
            """);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("key", McpSettings.Instructions);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        using var stored = JsonDocument.Parse(reader.GetString(0));
        if (McpSettings.InstructionsProblem(stored.RootElement) is not null) return null;
        var by = reader.IsDBNull(2)
            ? "unknown"
            : new AdminActor(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4)).Describe();
        return new DeploymentInstructions(stored.RootElement.GetString()!, reader.GetFieldValue<DateTimeOffset>(1), by);
    }
}
