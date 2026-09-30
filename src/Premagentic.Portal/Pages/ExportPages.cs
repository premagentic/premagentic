using System.Text;
using Premagentic.Core.Admin;
using Premagentic.Portal.Html;
using Microsoft.AspNetCore.Http;

namespace Premagentic.Portal.Pages;

/// <summary>
/// Backup: the configuration as plain JSON and the change record as JSON
/// lines. Password and token hashes are left out; an administrator can ask for
/// them with a form, and that request goes to the change record. The audit
/// trail's export comes with PremAgentic for Teams.
/// </summary>
internal static class ExportPages
{
    public static IResult Show(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var withSecrets = Layout.Form(r, "/portal/export/config.json", Layout.Hidden("secrets", "yes"),
            "Download the configuration with password and token hashes", danger: true);

        return Layout.Page(r, "Export", M.H($"""
            <p class="note">What cannot be rebuilt from the documents: users, groups, agents, tokens, folder rules, settings and sources.
            The index can always be rebuilt by ingesting again.</p>
            <ul>
            <li>{Layout.Link("/portal/export/config.json", "The configuration")} (JSON, without password or token hashes)</li>
            <li>{Layout.Link("/portal/export/changes.jsonl", "The change record")} (JSON lines, oldest first)</li>
            </ul>
            {(r.IsAdministrator ? M.H($"""
                <h2>With hashes</h2>
                <p class="warning">Hashes let anyone holding the file test passwords offline. Keep the file as you would the database backup.
                The download is recorded in the change record.</p>
                {withSecrets}
                """) : Markup.Empty)}
            """));
    }

    /// <summary>The configuration without hashes, for any reader; with them, for an administrator's form only.</summary>
    public static async Task<IResult> Config(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        var withSecrets = HttpMethods.IsPost(http.Request.Method) && (await r.FormAsync())["secrets"].ToString() == "yes";
        if (withSecrets)
            await r.Changes().RunAsync(r.Actor, change =>
            {
                change.Record("export.secrets", "configuration", null, new { tables = ConfigExport.TableNames });
                return Task.FromResult(true);
            }, r.Aborted);

        var buffer = new MemoryStream();
        await ConfigExport.WriteAsync(r.Db, r.Tenant, withSecrets, r.Clock.GetUtcNow(), buffer, r.Aborted);
        return Results.File(buffer.ToArray(), "application/json", $"premagentic-config{(withSecrets ? "-with-hashes" : "")}-{r.Clock.GetUtcNow():yyyyMMdd-HHmmss}.json");
    }

    public static async Task<IResult> ChangeRecord(HttpContext http)
    {
        var r = PortalRequest.Of(http);
        return Lines(await Collect(ConfigExport.ChangeRecordLinesAsync(r.Db, r.Tenant, r.Aborted)), $"premagentic-changes-{r.Clock.GetUtcNow():yyyyMMdd-HHmmss}.jsonl");
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> lines)
    {
        var text = new StringBuilder();
        await foreach (var line in lines) text.Append(line).Append('\n');
        return text.ToString();
    }

    private static IResult Lines(string text, string fileName) =>
        Results.File(Encoding.UTF8.GetBytes(text), "application/jsonl", fileName);
}
