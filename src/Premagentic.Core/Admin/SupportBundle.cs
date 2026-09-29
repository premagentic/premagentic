using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Premagentic.Core.Okf;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Admin;

/// <summary>
/// What someone helping with a deployment needs, in one JSON file: versions,
/// the hash of every Premagentic assembly, the schema, the settings the gates
/// depend on, the sources, sizes, each source's last run, and the recent
/// administrator changes with their values left out.
/// <para>
/// It never holds a credential, a connection string, a token, a hash of a
/// secret, a question anyone asked, or any text from a document. Paths the
/// last runs could not read are counted, not listed. Premagentic writes its
/// process log to standard output, where the service manager keeps it, and
/// stores none, so the bundle carries the stored records instead.
/// </para>
/// </summary>
public static class SupportBundle
{
    /// <param name="extensions">
    /// The extension host this process composed, or null when it composed none.
    /// The two are written differently, because "no extensions are loaded" and
    /// "this process never looked" are different facts to whoever reads the
    /// bundle to find out why a format is not being read.
    /// </param>
    public static async Task WriteAsync(
        PremagenticDatabase db, Guid tenantId, DateTimeOffset generatedAt, Stream output,
        Extensions.ExtensionHost? extensions = null, CancellationToken ct = default)
    {
        var health = await HealthReport.ReadAsync(db, tenantId, ct);
        var settings = await new TrustSettingsStore(db, tenantId).ReadAllAsync(ct);
        var sources = await new SourceRegistry(db, tenantId).ListAsync(ct);
        var runs = new IngestRuns(db, tenantId);
        var changes = await new ChangeRecord(db, tenantId).ListAsync(50, ct);

        await using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("generated_at", generatedAt.ToUniversalTime());
        json.WriteString("version", health.Version);
        json.WriteString("runtime", health.Runtime);
        json.WriteString("operating_system", health.OperatingSystem);
        json.WriteString("database", health.DatabaseVersion);

        json.WriteStartArray("assemblies");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.GetName().Name?.StartsWith("Premagentic", StringComparison.Ordinal) == true
                                 && a.GetName().Name != "Premagentic.Tests")
                     .OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("name", assembly.GetName().Name);
            json.WriteString("version", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            json.WriteString("sha256", FileHash(assembly));
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("migrations_applied");
        foreach (var m in health.Applied)
        {
            json.WriteStartObject();
            json.WriteString("schema", m.Schema);
            json.WriteNumber("version", m.Version);
            json.WriteString("name", m.Name);
            json.WriteString("checksum", m.Checksum);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("migrations_pending");
        foreach (var m in health.Pending) json.WriteStringValue($"{m.Version:D4}_{m.Name} ({m.Schema})");
        json.WriteEndArray();

        json.WriteStartObject("settings");
        foreach (var s in settings)
        {
            json.WriteStartObject(s.Key);
            json.WriteString("value", s.Value);
            json.WriteString("from", s.Source.ToString().ToLowerInvariant());
            json.WriteEndObject();
        }
        json.WriteEndObject();

        json.WriteStartObject("configured_from");
        if (await Profiles.ProfileApply.LatestAsync(db, tenantId, ct) is { } applied)
        {
            json.WriteString("profile", applied.Name);
            json.WriteString("version", applied.Version);
            json.WriteString("applied_at", applied.AppliedAt.ToUniversalTime());
            json.WriteString("applied_by", applied.AppliedBy);
            json.WriteString("applied_from", applied.AppliedFrom);
        }
        json.WriteEndObject();

        json.WriteStartObject("extensions");
        if (extensions is null)
        {
            json.WriteString("host", "none composed by this process");
        }
        else
        {
            json.WriteString("folder", extensions.Folder);
            json.WriteStartArray("loaded");
            foreach (var e in extensions.Loaded)
            {
                json.WriteStartObject();
                json.WriteString("name", e.Name);
                json.WriteString("version", e.Version);
                json.WriteString("sha256", e.Sha256);
                json.WriteString("folder", e.Folder);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("refused");
            foreach (var e in extensions.Refused)
            {
                json.WriteStartObject();
                json.WriteString("name", e.Name);
                json.WriteString("folder", e.Folder);
                json.WriteString("reason", Extensions.ExtensionHosting.Reason(e.Reason));
                json.WriteString("detail", e.Detail);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        json.WriteEndObject();

        // The MCP authorization flow, only while its stored flag turns it on:
        // an install that never turned it on shows nothing of it here, and its
        // tables are not read.
        if (await Identity.OAuthStatus.SummaryAsync(db, tenantId, generatedAt, ct) is { } oauth)
        {
            json.WriteStartObject("oauth");
            json.WriteString("public_url", oauth.PublicUrl);
            json.WriteNumber("clients", oauth.Clients);
            json.WriteNumber("pending_clients", oauth.PendingClients);
            json.WriteNumber("max_pending_clients", oauth.PendingCap);
            json.WriteNumber("live_grants", oauth.LiveGrants);
            json.WriteEndObject();
        }

        json.WriteStartObject("sizes");
        json.WriteNumber("database_bytes", health.DatabaseBytes);
        json.WriteNumber("documents", health.Documents);
        json.WriteNumber("chunks", health.Chunks);
        json.WriteNumber("vector_bytes", health.VectorBytes);
        json.WriteNumber("process_bytes", health.ProcessBytes);
        json.WriteEndObject();

        json.WriteStartArray("sources");
        foreach (var source in sources)
        {
            var last = await runs.LastAsync(source.Id, ct);
            json.WriteStartObject();
            json.WriteString("name", source.Name);
            json.WriteString("folder", source.Folder);
            json.WriteString("path_prefix", source.PathPrefix);
            json.WriteBoolean("okf_bundle", source.OkfBundle);
            json.WriteBoolean("undeclared_is_machine", source.UndeclaredIsMachine);
            json.WriteString("chunker", source.Chunker);
            if (last is null)
            {
                json.WriteNull("last_run");
            }
            else
            {
                json.WriteStartObject("last_run");
                json.WriteString("outcome", last.Outcome.ToString());
                json.WriteString("started_at", last.StartedAt);
                if (last.FinishedAt is { } finished) json.WriteString("finished_at", finished);
                json.WriteString("error", last.Error);
                if (last.Summary is { } s)
                {
                    json.WriteNumber("scanned", s.Scanned);
                    json.WriteNumber("ingested", s.Ingested);
                    json.WriteNumber("unchanged", s.Unchanged);
                    json.WriteNumber("unreadable", s.Unreadable);
                    json.WriteNumber("readable_by_nobody", s.DeniedToEveryone);
                    json.WriteStartObject("skipped");
                    foreach (var (key, count) in s.SkippedFormats.OrderBy(k => k.Key, StringComparer.Ordinal)) json.WriteNumber(key, count);
                    json.WriteEndObject();
                }
                json.WriteNumber("conformance_issues", last.ConformanceIssues.Count);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("recent_changes");
        foreach (var change in changes)
        {
            json.WriteStartObject();
            json.WriteString("at", change.OccurredAt);
            json.WriteString("kind", change.Kind);
            json.WriteString("target", change.Target);
            json.WriteString("surface", change.Actor.Surface);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteString("log",
            "Premagentic writes its process log to standard output, where the service manager keeps it, and stores none. " +
            "Attach the service manager's log separately if it is wanted, after reading it.");
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    private static string? FileHash(Assembly assembly)
    {
        try
        {
            return assembly.Location is { Length: > 0 } path && File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
