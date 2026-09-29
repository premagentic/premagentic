using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;
using Premagentic.Core.Sources.Registry;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The sources registry, "run now", and the history of every run with what it
/// read, skipped, could not read and found nonconforming.
/// </summary>
internal static class SourcePages
{
    /// <summary>
    /// A run with no end this long after it started is shown as "did not
    /// finish": its process most likely stopped. A running row younger than
    /// this is treated as in progress, and "run now" refuses beside it.
    /// </summary>
    public static readonly TimeSpan DidNotFinishAfter = TimeSpan.FromHours(12);

    public static async Task<IResult> List(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var runs = new IngestRuns(r.Db, r.Tenant);
        var exposure = new Core.Sources.SourceExposureStore(r.Db, r.Tenant);
        var people = (await r.Identity().ListUsersAsync(r.Aborted)).ToDictionary(u => u.Id, u => u.SignInName);
        var rows = new List<Markup>();
        foreach (var s in await r.Sources().ListAsync(r.Aborted))
        {
            var last = await runs.LastAsync(s.Id, r.Aborted);
            rows.Add(Layout.Row(Layout.Link(Path(s.Name), s.Name), s.Folder, Prefix(s.PathPrefix),
                s.OkfBundle ? "OKF bundle" : "plain folder", s.UndeclaredIsMachine ? "machine" : "unknown", Chunker(r, s.Chunker),
                Owner(people, s.OwnerUserId),
                (await exposure.ExplainAsync(s.PathPrefix, r.Aborted)).Words,
                last is null ? M.H($"never") : M.H($"{Layout.Link("/portal/runs/" + last.Id, Outcome(last, r.Clock.GetUtcNow()))} {Layout.When(last.StartedAt)}")));
        }

        var add = Layout.Form(r, "/portal/sources", M.H($"""
            {Layout.Field("Name", "name", required: true)}
            {Layout.Field("Folder on this server", "folder", required: true)}
            {Layout.Field("Prefix in the index", "prefix")}
            {Layout.Check("Read as an OKF bundle", "okfBundle")}
            {Layout.Check("In a bundle, undeclared authorship is machine-written", "undeclared")}
            {ChunkerSelect(r, ChunkerRegistry.DefaultName)}
            """), "Register source");

        return Layout.Page(r, "Sources", M.H($"""
            {Layout.Table(["Source", "Folder", "Prefix", "Read as", "Undeclared authorship", "Chunker", "Owner", "Hosted models", "Last run"], rows, kind: "paths")}
            {(r.IsAdministrator ? M.H($"<h2>Register a source</h2><p class=\"note\">Two sources may not share or nest a prefix: each run removes the documents under its own prefix that it did not see.</p>{add}") : Markup.Empty)}
            """));
    }

    public static async Task<IResult> Show(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var registry = r.Sources();
        var source = await registry.FindAsync(name, r.Aborted);
        if (source is null) return Layout.Refused(r, StatusCodes.Status404NotFound, $"There is no source named '{name}'.");

        var history = await new IngestRuns(r.Db, r.Tenant).ListAsync(source.Id, 30, r.Aborted);
        var now = r.Clock.GetUtcNow();
        var path = Path(source.Name);
        var indexed = await registry.IndexedDocumentCountAsync(source, r.Aborted);
        var exposure = await new Core.Sources.SourceExposureStore(r.Db, r.Tenant).ExplainAsync(source.PathPrefix, r.Aborted);
        var owner = source.OwnerUserId is { } ownerId ? await r.Identity().FindUserAsync(ownerId, r.Aborted) : null;
        var people = await r.Identity().ListUsersAsync(r.Aborted);

        var forms = !r.IsAdministrator ? Markup.Empty : M.H($"""
            <h2>Change</h2>
            {Layout.Form(r, path + "/run", Markup.Empty, "Run now")}
            {Layout.Form(r, path + "/set", M.H($"{Layout.Check("Read as an OKF bundle", "okfBundle", source.OkfBundle)}{Layout.Check("In a bundle, undeclared authorship is machine-written", "undeclared", source.UndeclaredIsMachine)}{ChunkerSelect(r, source.Chunker)}{OwnerSelect(people, source.OwnerUserId)}"), "Save")}
            <p class="note">A change of chunker takes effect at the next run, which cuts, embeds and stores every document of the source again.
            An owner decides nothing: who may read this source is its folder rule.</p>
            {Layout.Form(r, path + "/hosted", M.H($"{Layout.Check("May be served to hosted models", "hosted", exposure.State == Core.Sources.SourceExposureState.MayBeServed)}"), "Save")}
            <p class="note">Off, the switch holds this source's folder back from agents on a hosted model: every document at or beneath it, whatever rule decides it.
            The hold is kept beside the rules, so no rule written here or beneath undoes it. It takes effect on the next search; nothing has to be ingested again.</p>
            {Layout.Form(r, path + "/remove", Markup.Empty, "Remove from the registry", danger: true)}
            <p class="note">Removing a source keeps its documents indexed and its run history, and keeps its folder held if the switch is off. To remove the documents, ingest an empty folder over the same prefix, or rebuild the index.</p>
            """);

        return Layout.Page(r, source.Name, M.H($"""
            <dl class="facts">
            <dt>Folder</dt><dd>{source.Folder}</dd>
            <dt>Prefix</dt><dd>{Prefix(source.PathPrefix)}</dd>
            <dt>Read as</dt><dd>{(source.OkfBundle ? "an OKF bundle" : "a plain folder")}</dd>
            <dt>Undeclared authorship</dt><dd>{(source.UndeclaredIsMachine ? "machine-written and unverified" : "unknown, served as an ordinary file")}</dd>
            <dt>Owner</dt><dd>{(owner is null ? "nobody" : owner.Name)}{(source.OwnerUserId is not null && owner is null ? " (a user who no longer signs in)" : "")}</dd>
            <dt>Hosted models</dt><dd>{exposure.Words}</dd>
            <dt>Chunker</dt><dd>{Chunker(r, source.Chunker)}</dd>
            <dt>Indexed now</dt><dd>{indexed} document(s) under its prefix</dd>
            <dt>Registered</dt><dd>{Layout.When(source.CreatedAt)}</dd>
            </dl>
            {forms}
            <h2>Runs</h2>
            {Layout.Table(["Started", "Outcome", "Scanned", "Ingested", "Unchanged", "Skipped", "Unreadable", "Readable by nobody"], kind: "numbers",
                rows:
                history.Select(run => Layout.Row(
                    Layout.Link("/portal/runs/" + run.Id, Layout.WhenExact(run.StartedAt)), Outcome(run, now),
                    run.Summary?.Scanned, run.Summary?.Ingested, run.Summary?.Unchanged, run.Summary?.Skipped,
                    run.Summary?.Unreadable, run.Summary?.DeniedToEveryone)))}
            """));
    }

    public static async Task<IResult> Run(HttpContext http, Guid id)
    {
        var r = PortalRequest.Of(http);
        var run = await new IngestRuns(r.Db, r.Tenant).GetAsync(id, r.Aborted);
        if (run is null) return Layout.Refused(r, StatusCodes.Status404NotFound, "There is no such run.");
        var s = run.Summary;

        var counts = s is null ? M.H($"<p class=\"note\">No counts: the run did not complete.</p>") : M.H($"""
            <dl class="facts">
            <dt>Scanned</dt><dd>{s.Scanned}</dd>
            <dt>Ingested</dt><dd>{s.Ingested}</dd>
            <dt>Unchanged</dt><dd>{s.Unchanged}</dd>
            <dt>Chunks embedded</dt><dd>{s.ChunksEmbedded}</dd>
            <dt>Orphans removed</dt><dd>{s.OrphansRemoved}</dd>
            <dt>Of those, removed because now skipped</dt><dd>{s.RemovedNowSkipped}</dd>
            <dt>Readable by nobody</dt><dd>{s.DeniedToEveryone}</dd>
            <dt>Unreadable</dt><dd>{s.Unreadable}</dd>
            <dt>Skipped</dt><dd>{s.Skipped}</dd>
            <dt>Kept without a reader</dt><dd>{s.KeptWithoutReader}</dd>
            <dt>Concepts that declare no author</dt><dd>{s.UndeclaredAuthorship}</dd>
            </dl>
            <h2>Not read, by why</h2>
            <p class="note">A format this version does not read is listed by extension; {IngestSummary.EmptyKey} is a file with no text to index.</p>
            {Layout.Table(["Why", "Files"], s.SkippedFormats.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => Layout.Row(k.Key, k.Value)))}
            <h2>Could not be read</h2>
            {Layout.Table(["Path", "Reason"], s.FailureList.Select(f => Layout.Row(f.Path, f.Reason)))}
            """);

        return Layout.Page(r, "Run " + run.Id, M.H($"""
            <dl class="facts">
            <dt>Outcome</dt><dd>{Outcome(run, r.Clock.GetUtcNow())}</dd>
            <dt>Started</dt><dd>{Layout.WhenExact(run.StartedAt)}</dd>
            <dt>Finished</dt><dd>{Layout.WhenExact(run.FinishedAt)}</dd>
            <dt>Folder</dt><dd>{run.Folder}</dd>
            <dt>Prefix</dt><dd>{Prefix(run.PathPrefix)}</dd>
            <dt>Read as</dt><dd>{(run.OkfBundle ? "an OKF bundle" : "a plain folder")}</dd>
            <dt>Chunker</dt><dd>{run.Chunker}</dd>
            <dt>Error</dt><dd>{run.Error ?? ""}</dd>
            </dl>
            {counts}
            {(run.OkfBundle ? M.H($"""
                <h2>OKF</h2>
                <p>okf_version {run.OkfVersion ?? "(not declared)"}{(run.OkfVersion is not null && run.OkfVersionKnown == false ? " (unknown version, read best-effort)" : "")}</p>
                {Layout.Table(["Path", "Problem", "Detail"], run.ConformanceIssues.Select(i => Layout.Row(i.Path, i.Problem.ToString(), i.Detail)))}
                """) : Markup.Empty)}
            """));
    }

    public static async Task<IResult> Add(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var name = form["name"].ToString().Trim();
        try
        {
            var source = await r.Sources().AddAsync(
                name, form["folder"].ToString().Trim(), form["prefix"].ToString().Trim(),
                form["okfBundle"] == "on", form["undeclared"] == "on", r.Actor,
                form["chunker"].ToString() is { Length: > 0 } chunker ? chunker : ChunkerRegistry.DefaultName, r.Aborted);
            return Layout.After(Path(source.Name), done: $"Source '{source.Name}' registered. A folder rule decides who may read its documents.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After("/portal/sources", error: ex.Message);
        }
    }

    public static async Task<IResult> Set(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        try
        {
            var registry = r.Sources();
            // A chunker the form sends back unchanged is not a change, even one
            // this process does not have, so the other two values can still be saved.
            var current = (await registry.FindAsync(name, r.Aborted))?.Chunker;
            var chunker = form["chunker"].ToString() is { Length: > 0 } c && !c.Equals(current, StringComparison.OrdinalIgnoreCase) ? c : null;
            var (before, after, changed) = await registry.UpdateAsync(
                name, form["okfBundle"] == "on", form["undeclared"] == "on", r.Actor, chunker, r.Aborted);

            // The owner is a separate write because it is a separate record
            // entry; an empty value clears it, which is what "nobody" sends.
            var owner = Guid.TryParse(form["owner"].ToString(), out var id) ? id : (Guid?)null;
            var (_, _, ownerChanged) = await registry.SetOwnerAsync(name, owner, r.Actor, r.Aborted);
            changed = changed || ownerChanged;

            return Layout.After(Path(after.Name), done: !changed
                ? "Nothing changed."
                : before.Chunker != after.Chunker
                    ? "Saved. It takes effect at the next run, which cuts, embeds and stores every document of this source again."
                    : "Saved. It takes effect at the next run, which re-stores every document whose trust values change.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After("/portal/sources", error: ex.Message);
        }
    }

    public static async Task<IResult> Remove(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var registry = r.Sources();
        var removed = await registry.RemoveAsync(name, r.Actor, r.Aborted);
        return removed is null
            ? Layout.After("/portal/sources", error: $"There is no source named '{name}'.")
            : Layout.After("/portal/sources",
                done: $"Source '{removed.Name}' removed from the registry. Its {await registry.IndexedDocumentCountAsync(removed, r.Aborted)} indexed document(s) stay searchable under their folder rules.");
    }

    /// <summary>
    /// Starts a run of the source in the background and returns at once, since
    /// a large folder takes minutes. Refused while a run of the same source is
    /// in progress, and for a folder no rule covers, as the command line
    /// refuses it. The run records itself; the change record says who asked.
    /// </summary>
    public static async Task<IResult> RunNow(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var source = await r.Sources().FindAsync(name, r.Aborted);
        if (source is null) return Layout.After("/portal/sources", error: $"There is no source named '{name}'.");
        var path = Path(source.Name);

        var runs = new IngestRuns(r.Db, r.Tenant);
        if (await runs.LastAsync(source.Id, r.Aborted) is { Outcome: IngestRunOutcome.Running } running
            && r.Clock.GetUtcNow() - running.StartedAt < DidNotFinishAfter)
            return Layout.After(path, error: $"A run of this source started at {Layout.WhenText(running.StartedAt)} is still in progress.");

        var connector = source.ToFileSystemSource();
        var rules = await new AclStore(r.Db, r.Tenant).ListRulesAsync(r.Aborted);
        if (!rules.Any(x => x.Rule.Source == connector.Name && CanDecideUnder(x.Rule.PathPrefix, connector.PathPrefix)))
            return Layout.After(path, error: "No folder rule covers this source, so every document in it would be readable by nobody. Set a rule on the permissions page first.");

        await r.Changes().RunAsync(r.Actor, change =>
        {
            change.Record("source.run", source.Name, null, new { path_prefix = source.PathPrefix });
            return Task.FromResult(true);
        }, r.Aborted);

        var services = http.RequestServices;
        var pipeline = new IngestPipeline(
            r.Db, services.GetRequiredService<IEmbeddingProvider>(), r.Host.HeadingPrefix(http), r.Chunkers, r.Readers)
        {
            PrincipalMapper = r.PrincipalMapper,
        };
        var stopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        var log = services.GetService<ILoggerFactory>()?.CreateLogger("Premagentic.Portal.RunNow");
        _ = Task.Run(async () =>
        {
            try
            {
                await runs.RunAsync(pipeline, connector, source.Id, allowEmptySource: false, chunker: source.Chunker, ct: stopping);
            }
            catch (Exception ex)
            {
                // The run records its own failure. This is for a refusal before
                // it started, such as another run of the same source holding it.
                log?.LogWarning("A run of source {Source} started from the portal did not complete: {Reason}", source.Name, ex.Message);
            }
        }, CancellationToken.None);

        return Layout.After(path, done: "Run started. Reload this page to follow it.");
    }

    /// <summary>True when a rule at <paramref name="rulePrefix"/> can decide a document under <paramref name="folder"/>.</summary>
    internal static bool CanDecideUnder(string rulePrefix, string folder) =>
        rulePrefix.Length == 0 || folder.Length == 0 || rulePrefix == folder
        || rulePrefix.StartsWith(folder + "/", StringComparison.Ordinal)
        || folder.StartsWith(rulePrefix + "/", StringComparison.Ordinal);

    internal static string Outcome(IngestRunRecord run, DateTimeOffset now) => run.Outcome switch
    {
        IngestRunOutcome.Completed => "completed",
        IngestRunOutcome.ReconciliationRefused => "completed, deletion refused",
        IngestRunOutcome.Failed => "failed",
        _ when now - run.StartedAt >= DidNotFinishAfter => "did not finish",
        _ => "running",
    };

    private static string Prefix(string prefix) => prefix.Length == 0 ? "(whole index)" : prefix;

    /// <summary>
    /// A source's owner by sign-in name. A user who was removed still has an id
    /// on the source, and saying so is better than showing nothing or an id.
    /// </summary>
    private static string Owner(IReadOnlyDictionary<Guid, string> people, Guid? id) =>
        id is not { } owner ? "nobody"
        : people.TryGetValue(owner, out var name) ? name
        : "a user who no longer signs in";

    /// <summary>
    /// The people who could be named as answerable for a source, with nobody
    /// first, since that is what a source starts as and what clearing it means.
    /// </summary>
    private static Markup OwnerSelect(IReadOnlyList<Core.Identity.UserAccount> people, Guid? selected) =>
        Layout.Select("Owner", "owner",
            [("", "nobody"), .. people.Where(u => !u.Disabled).Select(u => (u.Id.ToString(), u.SignInName))],
            selected?.ToString() ?? "");

    /// <summary>
    /// The "may be served to hosted models" switch: off holds this source's
    /// folder back from hosted-model agents, whatever rule decides the
    /// documents in it. Its own form, because it changes who may read the
    /// folder rather than how the source is read, and the two should not look
    /// like one save.
    /// </summary>
    public static async Task<IResult> Hosted(HttpContext http, string name)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        try
        {
            var source = await r.Sources().FindAsync(name, r.Aborted)
                ?? throw new ArgumentException($"There is no source named '{name}'.");
            var store = new Core.Sources.SourceExposureStore(r.Db, r.Tenant);
            var turn = await store.SetAsync(source.PathPrefix, form["hosted"] == "on", r.Actor, r.Aborted);
            var now = (await store.ExplainAsync(source.PathPrefix, r.Aborted)).Words;
            return Layout.After(Path(source.Name), done:
                turn.Changed
                    ? $"Source {source.Name} ({Core.Sources.SourceExposureStore.RuleSource}:{Prefix(source.PathPrefix)}) changed: "
                      + $"{Core.Sources.SourceExposure.Describe(turn.Before)} is now {now}. "
                      + "It takes effect on the next search, for every folder beneath it too."
                : turn.Moved > 0
                    ? $"This source already {now}. {turn.Moved} indexed document(s) under it stored without the denial were stamped again, and that is recorded."
                    : $"Nothing changed: this source already {now}.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Layout.After(Path(name), error: ex.Message);
        }
    }

    /// <summary>A chunker's name, marked when this process does not have it, since a run then fails before it reads anything.</summary>
    private static string Chunker(PortalRequest r, string name) =>
        r.Chunkers.Find(name) is null ? $"{name} (not in this process: a run fails before it reads anything)" : name;

    /// <summary>The chunkers this process has, with <paramref name="selected"/> chosen, and listed even when this process does not have it.</summary>
    private static Markup ChunkerSelect(PortalRequest r, string selected)
    {
        var names = r.Chunkers.Names.ToList();
        if (!names.Contains(selected, StringComparer.OrdinalIgnoreCase)) names.Add(selected);
        var chosen = names.First(n => n.Equals(selected, StringComparison.OrdinalIgnoreCase));
        return Layout.Select("Chunker", "chunker", names.Select(n => (n, Chunker(r, n))), chosen);
    }

    private static string Path(string name) => "/portal/sources/" + M.Segment(name);
}
