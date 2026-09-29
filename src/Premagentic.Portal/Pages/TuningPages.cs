using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The retrieval tuning: each value in force with where it came from and what
/// it accepts, an edit form checked the way <c>prem settings</c> checks it, a
/// way back to the default, the golden set the evaluation runs, and the
/// history of these keys from the change record. "Run the golden set" runs
/// the evaluation in process under the settings saved now, one run at a time
/// and bounded in time, and the result sits beside the run before it, so a
/// change is judged rather than guessed.
/// <para>
/// The golden set path is shown and never changed here: the server reads
/// whatever file it names, so it is set on the command line only.
/// </para>
/// </summary>
internal static class TuningPages
{
    // The longest submitted value a refusal quotes back.
    private const int QuotedLength = 40;

    private const string AuthorityExample = """{"default": 1.0, "by_class": {"runbook": 1.2}}""";

    public static async Task<IResult> Show(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var store = new TuningSettingsStore(r.Db, r.Tenant);
        var readings = await store.ReadAllAsync(r.Aborted);
        var golden = await store.ReadGoldenSetPathAsync(r.Aborted);
        var runs = await GoldenSetRun.LatestAsync(r.Db, r.Tenant, 2, r.Aborted);
        var history = (await new ChangeRecord(r.Db, r.Tenant).ListAsync(200, r.Aborted))
            .Where(e => e.Kind is TuningSettingsStore.SetKind or TuningSettingsStore.UnsetKind && TuningSettingsStore.IsKey(e.Target))
            .Take(50).ToArray();
        var names = await AuditPages.UserNamesAsync(r);

        var rows = readings.Select(s => Layout.Row(
            M.H($"<code>{s.Key}</code>{Note(s.Key)}"),
            M.H($"<code>{s.Value.GetRawText()}</code>"),
            s.Source switch
            {
                TuningSettingSource.Default => M.H($"default"),
                TuningSettingSource.Stored => M.H($"set"),
                _ => M.H($"<span class=\"tag bad\">not used: {s.Problem} The default applies.</span>"),
            },
            s.Allowed,
            M.H($"""
                <div class="actions">
                {Layout.Form(r, "/portal/tuning", M.H($"{Layout.Hidden("key", s.Key)}{Layout.Field("", "value", (s.Stored ?? s.Value).GetRawText(), required: true)}"), "Save")}
                {(s.Stored is null ? Markup.Empty : Layout.Form(r, "/portal/tuning/unset", Layout.Hidden("key", s.Key), "Use the default", secondary: true))}
                </div>
                """)));

        return Layout.Page(r, "Tuning", M.H($"""
            <p class="note">How passages are ordered once the gates have decided what may be returned. Each value applies
            to the next search. A value is JSON: a number, or for the class weights an object such as
            <code>{AuthorityExample}</code>. A stored value that cannot be used
            is shown with why, and its default applies. Every change is in the change record.</p>
            {Layout.Table(["Setting", "In force", "From", "Allowed", ""], rows)}
            <h2>Golden set</h2>
            {GoldenSet(golden)}
            {Layout.Form(r, "/portal/tuning/run", Markup.Empty, "Run the golden set")}
            {Runs(runs, names)}
            <h2>History</h2>
            {Layout.Table(["When", "Setting", "Before", "After", "By"], kind: "wraps",
                rows: history.Select(e => Layout.Row(Layout.WhenExact(e.OccurredAt), e.Target, AuditPages.Json(e.OldValue),
                    e.Kind == TuningSettingsStore.UnsetKind ? "unset, so the default applies" : AuditPages.Json(e.NewValue),
                    AuditPages.Actor(e.Actor, names))))}
            """));
    }

    /// <summary>Stores a retrieval value, checked by the same rules as <c>prem settings set</c>.</summary>
    public static async Task<IResult> Set(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var key = form["key"].ToString();
        var text = form["value"].ToString();

        if (Refusal(key) is { } refused) return Layout.After("/portal/tuning", error: refused);
        if (!TryJson(text, out var value))
            return Layout.After("/portal/tuning", error: $"{key} takes {RetrievalSettings.Allowed(key)}, not '{Clip(text)}'.");

        try
        {
            var change = await new TuningSettingsStore(r.Db, r.Tenant).SetAsync(key, value, r.Actor, r.Aborted);
            return Layout.After("/portal/tuning", done: change.Changed
                ? $"{key} is now {value.GetRawText()}. {Applies(key)}"
                : $"{key} is already {value.GetRawText()}. Nothing changed.");
        }
        catch (ArgumentException ex)
        {
            return Layout.After("/portal/tuning", error: ex.Message);
        }
    }

    /// <summary>Removes a stored retrieval value, so its default applies.</summary>
    public static async Task<IResult> Unset(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var key = (await r.FormAsync())["key"].ToString();

        if (Refusal(key) is { } refused) return Layout.After("/portal/tuning", error: refused);
        var change = await new TuningSettingsStore(r.Db, r.Tenant).UnsetAsync(key, r.Actor, r.Aborted);
        return Layout.After("/portal/tuning", done: change.Changed
            ? $"{key} is back to its default, {RetrievalSettings.DefaultValue(key).GetRawText()}. {Applies(key)}"
            : $"{key} is not set, so it already has its default. Nothing changed.");
    }

    /// <summary>
    /// Runs the golden set under the settings saved now, in this process, and
    /// comes back to the page, where the result sits beside the run before it.
    /// </summary>
    public static async Task<IResult> Run(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var result = await new GoldenSetRun(
            r.Db, r.Tenant, http.RequestServices.GetRequiredService<HybridSearch>(), new PrincipalNames(r.Identity()), r.Clock)
            .RunAsync(r.Actor);
        return result.Outcome switch
        {
            GoldenRunOutcome.Completed => Layout.After("/portal/tuning", done:
                $"The golden set ran: {result.Summary!.Passed} of {result.Summary.Cases.Count} passed. The result is below, " +
                "beside the run before it."),
            GoldenRunOutcome.TimedOut => Layout.After("/portal/tuning", error:
                $"The golden-set run passed its limit of {GoldenSetRun.Limit.TotalMinutes:0} minutes and was stopped. " +
                "The stop is in the change record."),
            GoldenRunOutcome.Busy => Layout.After("/portal/tuning", error:
                "A golden-set run is already in progress. Its result will show here when it finishes."),
            _ => Layout.After("/portal/tuning", error: result.Problem),
        };
    }

    // The portal changes the retrieval settings only: never the golden set
    // path, and never a key it does not know.
    private static string? Refusal(string key) =>
        key == TuningSettingsStore.GoldenSetPath
            ? $"{key} is set on the command line only, with 'prem settings set {key} <absolute path>': the server reads " +
              "whatever file it names."
            : RetrievalSettings.Keys.Contains(key)
                ? null
                : $"There is no retrieval setting '{Clip(key)}'. The retrieval settings are {string.Join(", ", RetrievalSettings.Keys)}.";

    private static Markup Note(string key) => key == RetrievalSettings.NoAnswerDistanceFloor
        ? M.H($"<br><small>Read by the golden-set evaluation; a search is not filtered by it.</small>")
        : Markup.Empty;

    private static Markup GoldenSet(GoldenSetPathReading golden)
    {
        var state = (golden.Path, golden.Problem) switch
        {
            ({ } path, _) => M.H($"The golden set is <code>{path}</code>."),
            (null, { } problem) => M.H($"<span class=\"tag bad\">The stored golden set path is not used: {problem}</span>"),
            _ => M.H($"No golden set is set."),
        };
        return M.H($"""
            <p>{state}</p>
            <p class="note">It is set on the machine that runs Premagentic, with
            <code>prem settings set {TuningSettingsStore.GoldenSetPath} &lt;absolute path&gt;</code>, and cannot be changed
            here, because the server reads whatever file it names.</p>
            """);
    }

    /// <summary>The latest recorded run beside the one before it.</summary>
    private static Markup Runs(IReadOnlyList<GoldenRunRecord> runs, IReadOnlyDictionary<Guid, string> names)
    {
        if (runs.Count == 0) return M.H($"<p class=\"empty\">No golden-set run is recorded yet.</p>");
        var latest = runs[0];
        var before = runs.Count > 1 ? runs[1] : null;

        Markup Line(string label, Func<GoldenRunRecord, object?> cell) =>
            Layout.Row(label, cell(latest), before is null ? "" : cell(before));

        return M.H($"""
            <h3>Runs</h3>
            {Layout.Table(["", "Latest run", before is null ? "The run before (none yet)" : "The run before"],
            [
                Line("When", run => Layout.WhenExact(run.At)),
                Line("By", run => AuditPages.Actor(run.Actor, names)),
                Line("Golden set", run => run.GoldenSetPath),
                Line("Outcome", run => Text(run, "outcome") == "timed_out"
                    ? $"stopped at the limit of {Text(run, "limit_seconds")} seconds"
                    : Text(run, "outcome")),
                Line("Passed", run => Of(run, "passed", "cases")),
                Line("No-answer questions answered correctly", run => Of(run, "no_answer_correct", "no_answer_cases")),
                Line("Failed", run => run.Value.TryGetProperty("failed", out var failed)
                    ? failed.GetArrayLength() == 0 ? "none" : string.Join(", ", failed.EnumerateArray().Select(f => f.GetString()))
                    : ""),
                Line("No-answer floor", run => Setting(run, RetrievalSettings.NoAnswerDistanceFloor)),
                Line("RRF k", run => Setting(run, RetrievalSettings.RrfK)),
                Line("Fallback weight", run => Setting(run, RetrievalSettings.FallbackRrfWeight)),
                Line("Class weights", run => Setting(run, RetrievalSettings.Authority)),
                Line("Settings changed during the run", run => run.Value.TryGetProperty("settings_changed", out var changed)
                    ? changed.GetBoolean() ? "yes: each question was judged by the settings it ran under" : "no"
                    : ""),
            ])}
            """);
    }

    private static string Text(GoldenRunRecord run, string name) =>
        run.Value.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText() : "";

    private static string Of(GoldenRunRecord run, string part, string whole) =>
        run.Value.TryGetProperty(part, out var p) && run.Value.TryGetProperty(whole, out var w) ? $"{p.GetRawText()} of {w.GetRawText()}" : "";

    private static string Setting(GoldenRunRecord run, string key) =>
        run.Value.TryGetProperty("settings", out var settings) && settings.TryGetProperty(key, out var v) ? v.GetRawText() : "";

    private static string Applies(string key) => key == RetrievalSettings.NoAnswerDistanceFloor
        ? "It applies to the next golden-set evaluation; a search is not filtered by it."
        : "It applies to the next search.";

    private static bool TryJson(string text, out JsonElement value)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            value = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static string Clip(string text) =>
        text.Length <= QuotedLength ? text : string.Concat(text.AsSpan(0, QuotedLength), "...");
}
