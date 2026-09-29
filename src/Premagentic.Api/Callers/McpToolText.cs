using Premagentic.Core.Identity;
using Premagentic.Core.Mcp;
using Premagentic.Core.Storage;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Premagentic.Api.Callers;

/// <summary>
/// Puts this deployment's own words on the two MCP tools, once, before anything
/// is served.
/// <para>
/// Once, and at start, because a tool's description is read by an assistant when
/// it lists the tools, and a description that changed under it mid-session would
/// leave two assistants disagreeing about what the same tool does. A deployment
/// that changes the setting restarts the server, which is the same rule as every
/// other setting read at start.
/// </para>
/// </summary>
internal static class McpToolText
{
    /// <summary>
    /// Applies the stored text and says what it did. Nothing stored leaves the
    /// text this version ships; something stored that cannot be used leaves it
    /// too, and is logged with the reason: a description is not worth refusing
    /// to serve over, and serving with no description at all would be worse
    /// than serving with the shipped one.
    /// </summary>
    public static async Task<McpToolDescriptionsReading> ApplyAsync(
        IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = new SettingsStore(
            services.GetRequiredService<PremagenticDatabase>(),
            services.GetRequiredService<Deployment>().TenantId);
        var reading = await McpToolDescriptions.ReadAsync(settings, ct);

        if (reading.Problem is { } problem)
            logger.LogWarning(
                "The stored {Key} cannot be used, so the tools keep the descriptions this version ships. {Problem}",
                McpToolDescriptions.Key, problem);

        var tools = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;
        if (tools is null) return reading;

        foreach (var name in McpToolDescriptions.ToolNames)
        {
            if (reading.For(name) is not { } text) continue;
            if (!tools.TryGetPrimitive(name, out var tool)) continue;
            tool.ProtocolTool.Description = text;
            logger.LogInformation("The MCP tool {Tool} is described in this deployment's own words.", name);
        }
        return reading;
    }
}
