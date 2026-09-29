using System.Reflection;

namespace Premagentic.Core;

/// <summary>
/// The version this build carries, read from the assembly rather than typed
/// in, so the MCP server, the command line and a bug report all name the same
/// build. It is the informational version: the version number, and the
/// commit it was built from when the build knew it.
/// </summary>
public static class BuildVersion
{
    /// <summary>The informational version of Premagentic.Core, such as <c>0.1.0+abc1234</c>.</summary>
    public static string Informational { get; } =
        typeof(BuildVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// Where this program's source is published. The portal links it on every
    /// page, signed in or not, so everyone who uses PremAgentic over a network
    /// is offered its source, as section 13 of the GNU Affero General Public
    /// License asks.
    /// </summary>
    public const string SourceUrl = "https://github.com/premagentic/premagentic";
}
