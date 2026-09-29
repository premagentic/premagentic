using System.Reflection;

namespace Premagentic.McpServer;

/// <summary>
/// The version this build of the bridge carries, read from its assembly. The
/// bridge does not reference the core, and every project takes its version
/// from the same build properties, so it names the same build the API does.
/// </summary>
internal static class BridgeVersion
{
    public static string Informational { get; } =
        typeof(BridgeVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BridgeVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
