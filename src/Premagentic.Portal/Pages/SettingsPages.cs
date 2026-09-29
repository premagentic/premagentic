using Premagentic.Core.Admin;
using Premagentic.Core.Okf;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// The three trust settings, with the safe starting value marked, a warning
/// that asks again before a looser value is saved, and their history from the
/// change record. A change applies to the very next search.
/// </summary>
internal static class SettingsPages
{
    public static async Task<IResult> Show(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var readings = await new TrustSettingsStore(r.Db, r.Tenant).ReadAllAsync(r.Aborted);
        var history = (await new ChangeRecord(r.Db, r.Tenant).ListAsync(200, r.Aborted))
            .Where(e => e.Kind == TrustSettingsStore.ChangeKind && TrustSettingsStore.Keys.Contains(e.Target)).Take(50).ToArray();
        var names = await AuditPages.UserNamesAsync(r);

        var rows = readings.Select(s => Layout.Row(
            M.H($"<code>{s.Key}</code>"), s.Value,
            s.Source switch
            {
                TrustSettingSource.Default => M.H($"default"),
                TrustSettingSource.Stored => M.H($"set"),
                _ => M.H($"<span class=\"tag bad\">unreadable: {s.StoredJson}, so the strictest applies</span>"),
            },
            Layout.Form(r, "/portal/settings", M.H($"{Layout.Hidden("key", s.Key)}{Layout.Select("", "value", s.AllowedValues.Select(v => (v, Label(s.Key, v))), s.Value)}"), "Save")));

        return Layout.Page(r, "Settings", M.H($"""
            <p class="note">What agents and people are served of machine-written and stale content. Every result still says
            what it is, whatever the setting. Each change applies to the next search, and is in the change record.</p>
            {Layout.Table(["Setting", "Value", "From", ""], rows)}
            <h2>History</h2>
            {Layout.Table(["When", "Setting", "Before", "After", "By"], kind: "wraps",
                rows: history.Select(e => Layout.Row(Layout.WhenExact(e.OccurredAt), e.Target, AuditPages.Json(e.OldValue), AuditPages.Json(e.NewValue), AuditPages.Actor(e.Actor, names))))}
            """));
    }

    /// <summary>
    /// Saves a value. A looser one is not saved on the first request: the
    /// warning comes back with a second form that says yes.
    /// </summary>
    public static async Task<IResult> Set(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var form = await r.FormAsync();
        var key = form["key"].ToString();
        var value = form["value"].ToString();
        var confirmed = form["confirm"].ToString() == "yes";
        var store = new TrustSettingsStore(r.Db, r.Tenant);

        try
        {
            if (!TrustSettingsStore.TryNormalize(key, value, out var normalized))
                return Layout.After("/portal/settings", error: $"'{value}' is not a value of {key}. Allowed: {string.Join(", ", TrustSettingsStore.AllowedValues(key))}.");

            var current = await store.ReadAsync(key, r.Aborted);
            if (TrustSettingsStore.IsLooser(key, current.Value, normalized) && !confirmed)
                return Layout.Page(r, "Loosen this setting?", M.H($"""
                    <p class="warning">{normalized} is looser than {current.Value}. {Consequence(key, normalized)}</p>
                    {Layout.Form(r, "/portal/settings", M.H($"{Layout.Hidden("key", key)}{Layout.Hidden("value", normalized)}{Layout.Hidden("confirm", "yes")}"), $"Set {key} to {normalized}", danger: true)}
                    <p>{Layout.Link("/portal/settings", "Keep it as it is")}</p>
                    """));

            var change = await store.SetAsync(key, normalized, r.Actor, r.Aborted);
            return Layout.After("/portal/settings", done: change.Changed
                ? $"{key} is now {normalized}. It applies to the next search."
                : $"{key} is already {normalized}. Nothing changed.");
        }
        catch (ArgumentException ex)
        {
            return Layout.After("/portal/settings", error: ex.Message);
        }
    }

    private static string Label(string key, string value) =>
        value == TrustSettingsStore.DefaultValue(key)
            ? value + " (default, the safe start)"
            : value == TrustSettingsStore.StrictestValue(key) ? value + " (strictest)" : value;

    private static string Consequence(string key, string value) => (key, value) switch
    {
        (TrustSettingsStore.AgentsMinimumTier, "unverified") =>
            "Agents will be served machine-written content that nobody has confirmed, so one agent's unreviewed output can become another agent's input.",
        (TrustSettingsStore.AgentsMinimumTier, _) => "Agents will be served machine-written content that only a machine has confirmed.",
        (TrustSettingsStore.PeopleMinimumTier, _) => $"People will be served machine-written content at {value}, flagged with its tier.",
        (_, "shown-to-everyone") => "Agents will be served content past its stale date, flagged as stale.",
        _ => "People will be served content past its stale date, flagged as stale.",
    };
}
