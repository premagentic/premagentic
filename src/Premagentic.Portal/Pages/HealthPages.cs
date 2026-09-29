using System.Globalization;
using Premagentic.Core.Admin;
using Premagentic.Core.Okf;
using Premagentic.Core.Sources.Registry;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>Versions, the schema, sizes, the last run of each source, and the support bundle.</summary>
internal static class HealthPages
{
    public static async Task<IResult> Show(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var health = await HealthReport.ReadAsync(r.Db, r.Tenant, r.Aborted);
        var awaitingReview = await new ReviewQueue(r.Db, r.Tenant).CountAsync(ct: r.Aborted);
        var runs = new IngestRuns(r.Db, r.Tenant);
        var now = r.Clock.GetUtcNow();
        var sourceRows = new List<Markup>();
        foreach (var s in await new SourceRegistry(r.Db, r.Tenant).ListAsync(r.Aborted))
        {
            var last = await runs.LastAsync(s.Id, r.Aborted);
            sourceRows.Add(Layout.Row(Layout.Link("/portal/sources/" + M.Segment(s.Name), s.Name),
                last is null ? M.H($"never") : Layout.Link("/portal/runs/" + last.Id, SourcePages.Outcome(last, now)),
                Layout.When(last?.StartedAt), Layout.When(last?.FinishedAt)));
        }

        // Awaited before the markup, because an interpolated markup handler
        // cannot be held across an await.
        var configuredFrom = await ProfileAsync(r);
        var flow = await FlowAsync(r, now);
        var embedder = r.Http.RequestServices.GetService(typeof(Core.Embeddings.IEmbeddingProvider)) as Core.Embeddings.IEmbeddingProvider;
        var model = embedder is null ? null : Core.Admin.HealthReport.Model(embedder);

        return Layout.Page(r, "Health", M.H($"""
            <dl class="facts">
            <dt>Version</dt><dd>{health.Version}</dd>
            <dt>Runtime</dt><dd>{health.Runtime} on {health.OperatingSystem}</dd>
            <dt>Model</dt><dd>{Model(model)}</dd>
            <dt>Database</dt><dd>{health.DatabaseVersion}</dd>
            <dt>Schema</dt><dd>{(health.Pending.Count == 0 ? $"current: {health.Applied.Count} migration part(s) applied" : $"{health.Pending.Count} migration part(s) pending")}</dd>
            <dt>Database size</dt><dd>{Bytes(health.DatabaseBytes)}</dd>
            <dt>Documents</dt><dd>{health.Documents}</dd>
            <dt>Chunks</dt><dd>{health.Chunks}</dd>
            <dt>Review queue</dt><dd>{Layout.Link("/portal/review", $"{awaitingReview} document(s) below human-reviewed")}</dd>
            <dt>Vector index</dt><dd>{Bytes(health.VectorBytes)} when every vector is loaded (from the stored embeddings)</dd>
            <dt>This process</dt><dd>{Bytes(health.ProcessBytes)} working set</dd>
            {flow}
            </dl>
            <h2>Last run of each source</h2>
            {Layout.Table(["Source", "Outcome", "Started", "Finished"], sourceRows)}
            <h2>Configured from</h2>
            {configuredFrom}
            <h2>Extensions</h2>
            {Extensions(r)}
            <h2>Support</h2>
            <p>{Layout.Link("/portal/health/support-bundle.json", "Download the support bundle")}: versions, assembly hashes, the schema,
            the settings the gates depend on, sizes, each source's last run, the profile this deployment was configured from, and the
            extensions loaded and refused. Never a credential, a question anyone asked, or document text.</p>
            <h2>Migrations</h2>
            {Layout.Table(["Version", "Name", "Schema", "Applied"], health.Applied.Select(m => Layout.Row(m.Version.ToString("D4", CultureInfo.InvariantCulture), m.Name, m.Schema, Layout.When(m.AppliedAt))))}
            """));
    }

    public static async Task<IResult> SupportBundle(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var buffer = new MemoryStream();
        await Core.Admin.SupportBundle.WriteAsync(r.Db, r.Tenant, r.Clock.GetUtcNow(), buffer, r.Extensions, r.Aborted);
        return Results.File(buffer.ToArray(), "application/json", $"premagentic-support-{r.Clock.GetUtcNow():yyyyMMdd-HHmmss}.json");
    }

    /// <summary>
    /// The MCP authorization flow at a glance, while the stored flag or this
    /// process has it on, and nothing while both have it off. The process reads
    /// the flag at start, so the two can differ until a restart, and the line
    /// says which way. While the flag is off only the settings are read, never
    /// a table of the flow.
    /// </summary>
    private static async Task<Markup> FlowAsync(PortalRequest r, DateTimeOffset now)
    {
        var running = Layout.FlowOn(r.Http);
        var summary = await Core.Identity.OAuthStatus.SummaryAsync(r.Db, r.Tenant, now, r.Aborted);
        if (summary is null && !running) return Markup.Empty;

        var state = summary is null
            ? M.H($"off in the settings, and still on in this process until it restarts.")
            : running
                ? M.H($"on, for the public address <code>{summary.PublicUrl ?? "not set"}</code>.")
                : M.H($"""
                    on in the settings, and off in this process, which read them at start. It runs after a restart once
                    <code>{Core.Identity.OAuthSettings.PublicUrl}</code> holds this server's https address; the log at start
                    names what stopped it, if anything did.
                    """);
        var counts = summary is null ? Markup.Empty : M.H($"""
            <br>{summary.Clients} client(s) registered, {summary.PendingClients} of at most {summary.PendingCap} registered
            themselves and wait for a first approval; {summary.LiveGrants} live grant(s).
            """);
        var pages = running
            ? M.H($"<br>{Layout.Link(Core.Identity.OAuthPaths.Clients, "Clients")} · {Layout.Link(Core.Identity.OAuthPaths.Grants, "Grants")}")
            : Markup.Empty;
        var refused = r.Http.RequestServices.GetService(typeof(Core.Identity.IOAuthHealth)) is Core.Identity.IOAuthHealth health
            ? health.LastRegistrationRefusal is { } at
                ? M.H($"<br>A self-registration was last refused at one of the limits at {Layout.WhenText(at)}.")
                : M.H($"<br>No self-registration refused at a limit since this process started.")
            : Markup.Empty;

        return M.H($"<dt>MCP authorization</dt><dd>{state}{counts}{refused}{pages}</dd>");
    }

    /// <summary>
    /// What this deployment was configured from, and whether it still matches
    /// the profile it came from.
    /// </summary>
    /// <summary>
    /// The embedding model this process loaded: its name, the folder a local
    /// one came from and the revision recorded beside it. The folder is on this
    /// page and not on the public /health, which anyone on the port can read.
    /// </summary>
    internal static Markup Model(Core.Admin.LoadedModel? model) => model is null
        ? M.H($"not known: this process registered no embedding model")
        : M.H($"""{model.Name}<br>{(model.Folder is { } folder ? M.H($"from <code>{folder}</code>") : M.H($"no folder: this provider runs without one"))}<br>revision {model.Revision ?? "not recorded"}""");

    private static async Task<Markup> ProfileAsync(PortalRequest r)
    {
        var applied = await Core.Profiles.ProfileApply.LatestAsync(r.Db, r.Tenant, r.Aborted);
        if (applied is null)
            return M.H($"""
                <p>No profile has been applied. Every setting, source and rule here was set by hand, which is
                ordinary; a profile is how that is written down and carried to another deployment.</p>
                """);

        var drift = await DriftAsync(r, applied);

        return M.H($"""
            <dl class="facts">
            <dt>Profile</dt><dd>{applied.Name} {applied.Version}</dd>
            <dt>Applied</dt><dd>{Layout.When(applied.AppliedAt)} by {applied.AppliedBy}</dd>
            <dt>From</dt><dd>{applied.AppliedFrom ?? "not recorded"}</dd>
            <dt>Drift</dt><dd>{drift}</dd>
            </dl>
            """);
    }

    /// <summary>
    /// How far the deployment has moved from its profile, in settings, sources
    /// and rules. Every answer that is not a count says WHY there is no count,
    /// because "no drift" and "not compared" are different sentences and only
    /// one of them is reassuring.
    /// </summary>
    private static async Task<string> DriftAsync(PortalRequest r, Core.Profiles.AppliedProfile applied)
    {
        if (applied.AppliedFrom is not { } folder)
            return "not compared: the folder it was applied from was not recorded";
        if (!Directory.Exists(folder))
            return $"not compared: {folder} is not on this machine";
        if (!Core.Profiles.ProfileReader.TryRead(folder, out var profile, out var unreadable))
            return $"not compared: the profile there cannot be read ({unreadable.Count} problem(s))";

        // The golden set's target folder is the CLI's to choose, so it is taken
        // from where the running one already sits. A count here must not move
        // because a page guessed a path differently from the person who applied
        // the profile, which is also why the count below leaves it out.
        var running = await new Core.Evaluation.TuningSettingsStore(r.Db, r.Tenant).ReadGoldenSetPathAsync(r.Aborted);
        var goldenFolder = (running.Path is { } path ? Path.GetDirectoryName(path) : null) ?? folder;

        var (plan, problems) = await Core.Profiles.ProfilePlanner.BuildAsync(
            profile!, r.Db, r.Tenant, goldenFolder, r.Chunkers, r.Extensions?.Settings, r.Aborted);
        if (plan is null)
            return $"not compared: the profile there no longer applies to this deployment ({problems.Count} problem(s))";

        var drifted = plan.Changes.Count(c => c is not Core.Profiles.GoldenSetChange);
        return drifted == 0
            ? "none: the settings, sources and rules match the profile"
            : $"{drifted} item(s) differ from the profile, in settings, sources or rules";
    }

    /// <summary>
    /// What this process loaded, and what it refused. The states are told apart
    /// on purpose: a host that never looked, a folder nobody configured, a
    /// configured folder holding nothing, and a folder whose extensions were
    /// refused all read as "nothing loaded" if they are collapsed, and only one
    /// of them means the deployment is as its administrator intended.
    /// </summary>
    private static Markup Extensions(PortalRequest r)
    {
        if (r.Extensions is not { } host)
            return M.H($"""
                <p>This process composed no extension host, so this page cannot say what is loaded. That is this
                process's own condition and says nothing about how the deployment is configured.</p>
                """);

        if (host.Folder is null)
            return M.H($"""
                <p>No extensions folder is configured, so none is loaded. Point the setting
                <code>extensions.folder</code>, or PREM_EXTENSIONS_DIR, at one to load any.</p>
                """);

        if (host.Loaded.Count == 0 && host.Refused.Count == 0)
            return M.H($"<p>Nothing found in <code>{host.Folder}</code>.</p>");

        var loaded = Layout.Table(["Name", "Version", "SHA-256 of the bytes loaded", "Folder"],
            host.Loaded.Select(e => Layout.Row(e.Name, e.Version, e.Sha256, e.Folder)));
        // The one registration that adds to what a caller holds, named with
        // the extension it came from, and its absence said as plainly.
        var mapper = host.PrincipalMapper is { } m
            ? M.H($"<p>The principal mapper <code>{m.Name}</code>, from the extension <code>{host.PrincipalMapperExtension}</code>, decides what groups and principals from outside mean here.</p>")
            : M.H($"<p>No principal mapper is loaded, so groups and principals from outside mean nothing here.</p>");
        var refused = host.Refused.Count == 0
            ? M.H($"<p>Nothing was refused.</p>")
            : M.H($"""
                <h3>Refused</h3>
                <p>A refusal is not an error: the process started without them, and they registered nothing at all.</p>
                {Layout.Table(["Name", "Folder", "Reason", "What happened"],
                    host.Refused.Select(e => Layout.Row(e.Name, e.Folder, Core.Extensions.ExtensionHosting.Reason(e.Reason), e.Detail)))}
                """);

        return M.H($"""
            <p>Loaded from <code>{host.Folder}</code>. Compare a hash with what you published: it is taken from the
            bytes this process actually loaded, not from the manifest that describes them.</p>
            {loaded}
            {mapper}
            {refused}
            """);
    }

    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} bytes",
    };
}
