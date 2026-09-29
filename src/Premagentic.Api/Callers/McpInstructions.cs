using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Premagentic.Api.Callers;

/// <summary>The instructions this process read when it started, which every connection is given.</summary>
internal sealed class McpInstructionsState
{
    public DeploymentInstructions? Current { get; set; }
}

/// <summary>
/// A deployment's instructions for assistants, on the MCP surface: the
/// server's <c>instructions</c> at connect, which a client puts in the model's
/// context, and the resource <c>premagentic://instructions</c> for a client
/// that reads it in full. Both are read-only for the agent.
/// <para>
/// Read once, at start, for the reason the tool text is: an assistant takes
/// them in when it connects, and text that changed under a running server
/// would leave two assistants working to different rules. A deployment that
/// changes them restarts the server.
/// </para>
/// </summary>
internal static class McpInstructions
{
    public const string ResourceUri = "premagentic://instructions";

    public static async Task<DeploymentInstructions?> ApplyAsync(
        IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        var reader = services.GetRequiredService<IInstructionsReader>();
        var current = await reader.ReadAsync(services.GetRequiredService<Deployment>().TenantId, ct);
        services.GetRequiredService<McpInstructionsState>().Current = current;

        if (current is null)
            logger.LogInformation("No instructions for assistants are set ({Key}); an assistant is told only what the tools say.", McpSettings.Instructions);
        else
            logger.LogInformation(
                "Assistants are given this deployment's instructions at connect, {Length} characters, set by {By} at {At}.",
                current.Text.Length, current.UpdatedBy, current.UpdatedAt);
        return current;
    }

    /// <summary>The resource list: the instructions when some are set, and nothing otherwise.</summary>
    public static ValueTask<ListResourcesResult> ListAsync(RequestContext<ListResourcesRequestParams> request, CancellationToken ct)
    {
        var current = request.Services!.GetRequiredService<McpInstructionsState>().Current;
        return ValueTask.FromResult(new ListResourcesResult
        {
            Resources = current is null
                ? []
                :
                [
                    new Resource
                    {
                        Uri = ResourceUri,
                        Name = "instructions",
                        Title = "This deployment's instructions for assistants",
                        Description = "What the administrators of this deployment ask every assistant to follow.",
                        MimeType = "text/markdown",
                    },
                ],
        });
    }

    /// <summary>The instructions in full; any other address, or none set, is not found.</summary>
    public static ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken ct)
    {
        var current = request.Services!.GetRequiredService<McpInstructionsState>().Current;
        if (current is null || !string.Equals(request.Params?.Uri, ResourceUri, StringComparison.Ordinal))
            throw new McpProtocolException($"There is no resource {request.Params?.Uri}.", McpErrorCode.ResourceNotFound);

        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = ResourceUri, MimeType = "text/markdown", Text = current.Text }],
        });
    }
}
