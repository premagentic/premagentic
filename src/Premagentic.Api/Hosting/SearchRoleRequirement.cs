using Premagentic.Core.Security;
using Premagentic.Core.Storage;

namespace Premagentic.Api.Hosting;

/// <summary>
/// Whether this process may serve search with no search role, which leaves
/// only the SQL gate between a caller and the whole index.
/// <list type="bullet">
/// <item>An installed deployment (<c>PREM_CREDENTIALS_FILE</c>) never gets
/// here without one: it refuses to start when <c>search.credentials</c> is
/// missing, and no switch changes that.</item>
/// <item>The development database (<c>PREM_DEV_DATABASE=1</c> with no
/// connection string) runs without one and says so, so the quick start is
/// unchanged.</item>
/// <item>A deployment configured by <c>PREM_CONNECTION_STRING</c> must name
/// its search role in <c>PREM_SEARCH_CONNECTION_STRING</c>, or say with
/// <c>PREM_ALLOW_NO_SEARCH_ROLE=1</c> that it accepts one line of defense
/// instead of two. Without either it refuses to start.</item>
/// </list>
/// </summary>
public static class SearchRoleRequirement
{
    /// <summary>The switch a deployment sets to run by connection string with no search role, on purpose.</summary>
    public const string AllowKey = "PREM_ALLOW_NO_SEARCH_ROLE";

    /// <summary>
    /// The sentence to refuse to start with, or null when this process may run
    /// without a search role. Called only when it has none.
    /// </summary>
    public static string? Refusal(Func<string, string?> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!string.IsNullOrEmpty(setting("PREM_CREDENTIALS_FILE"))) return null;
        var connectionString = setting("PREM_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString) && setting(PremagenticDatabase.DevelopmentSwitch) == "1") return null;
        if (setting(AllowKey) == "1") return null;

        return "No search role is configured, so search reads would connect as the application role, which reads the " +
               "whole index, and only the SQL gate would filter them. Set " + SearchRole.ConnectionStringVariable +
               " to the search role that 'prem setup' creates, or set " + AllowKey + "=1 to run with the SQL gate alone.";
    }
}
