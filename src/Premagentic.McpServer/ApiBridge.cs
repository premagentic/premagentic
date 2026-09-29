using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Premagentic.McpServer;

/// <summary>
/// The stdio MCP server, for an agent that only speaks stdio, as a bridge to the
/// Premagentic API's MCP endpoint. It holds no database credentials and reads no
/// index: it presents one agent token to <c>/mcp</c> and passes the API's tools
/// through, so one person's own agent is a registered agent like any other,
/// with its own rights, rate limit and audit trail.
/// <para>
/// Only tools the API marks read-only are passed through. The API offers
/// nothing else, and this keeps a write tool from reaching a local agent even
/// if the server at the other end were not Premagentic.
/// </para>
/// </summary>
public sealed class ApiBridge : IAsyncDisposable
{
    public const string ApiUrlKey = "PREM_API_URL";
    public const string TokenFileKey = "PREM_AGENT_TOKEN_FILE";
    public const string TokenKey = "PREM_AGENT_TOKEN";

    private const string TokenPrefix = "prem_agt_";

    private readonly string _token;
    private readonly HttpClient? _http;
    private readonly ILoggerFactory _logs;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private McpClient? _client;
    private IReadOnlySet<string> _readOnlyTools = new HashSet<string>();

    /// <param name="apiBase">The API's base address, such as <c>https://prem.example.org:8443</c>.</param>
    /// <param name="token">The agent token. Kept in memory only, and sent only to <paramref name="apiBase"/>.</param>
    /// <param name="http">A client to send requests with, for tests; by default the bridge makes its own.</param>
    public ApiBridge(Uri apiBase, string token, HttpClient? http = null, ILoggerFactory? logs = null)
    {
        Endpoint = McpEndpoint(apiBase);
        _token = CheckToken(token);
        _http = http;
        _logs = logs ?? NullLoggerFactory.Instance;
    }

    /// <summary>Where the bridge sends MCP requests: the API's <c>/mcp</c>.</summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// The bridge <see cref="ApiUrlKey"/> and a token describe. The token is read
    /// from the file <see cref="TokenFileKey"/> names, which is the way to give it;
    /// <see cref="TokenKey"/> holds it directly, where anything that can read
    /// this process's environment can read it too. Setting both is refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">What is missing or wrong, in words an operator can act on.</exception>
    public static ApiBridge FromEnvironment(Func<string, string?> environment, ILoggerFactory? logs = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var url = environment(ApiUrlKey);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var apiBase))
            throw new InvalidOperationException(
                $"{ApiUrlKey} must be the Premagentic API's address, such as https://prem.example.org:8443.");
        return new ApiBridge(apiBase, ReadToken(environment), logs: logs);
    }

    /// <summary>The API's read-only tools, passed through as the API describes them.</summary>
    public async Task<ListToolsResult> ListToolsAsync(CancellationToken ct = default)
    {
        var client = await ClientAsync(ct);
        var tools = (await client.ListToolsAsync(cancellationToken: ct))
            .Select(t => t.ProtocolTool)
            .Where(t => t.Annotations?.ReadOnlyHint == true)
            .ToList();
        _readOnlyTools = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return new ListToolsResult { Tools = tools };
    }

    /// <summary>Calls one of the API's read-only tools as this bridge's agent. Any other name is refused here.</summary>
    public async Task<CallToolResult> CallToolAsync(CallToolRequestParams? request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_readOnlyTools.Contains(request.Name)) await ListToolsAsync(ct);
        if (!_readOnlyTools.Contains(request.Name))
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"There is no read-only tool named '{request.Name}'." }],
            };

        var client = await ClientAsync(ct);
        var arguments = request.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        return await client.CallToolAsync(request.Name, arguments, cancellationToken: ct);
    }

    /// <summary>The address of the one resource the bridge passes through: the deployment's instructions.</summary>
    public const string InstructionsUri = "premagentic://instructions";

    /// <summary>
    /// What the API tells an assistant at connect, or null when it tells it
    /// nothing. The bridge's own server gives the same text to its client.
    /// </summary>
    public async Task<string?> InstructionsAsync(CancellationToken ct = default) =>
        (await ClientAsync(ct)).ServerInstructions;

    /// <summary>The API's resources that the bridge passes through: the instructions, when the API offers them.</summary>
    public async Task<ListResourcesResult> ListResourcesAsync(CancellationToken ct = default)
    {
        var client = await ClientAsync(ct);
        var resources = (await client.ListResourcesAsync(cancellationToken: ct))
            .Select(r => r.ProtocolResource)
            .Where(r => r.Uri == InstructionsUri)
            .ToList();
        return new ListResourcesResult { Resources = resources };
    }

    /// <summary>Reads the instructions from the API. Any other address is not found here, whatever the API has.</summary>
    public async Task<ReadResourceResult> ReadResourceAsync(ReadResourceRequestParams? request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Uri != InstructionsUri)
            throw new ModelContextProtocol.McpProtocolException(
                $"There is no resource {request.Uri}.", ModelContextProtocol.McpErrorCode.ResourceNotFound);
        var client = await ClientAsync(ct);
        return await client.ReadResourceAsync(InstructionsUri, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _connect.Dispose();
    }

    /// <summary>Never includes the token, so it is safe in a log line.</summary>
    public override string ToString() => $"bridge to {Endpoint}";

    /// <summary>
    /// The API's MCP endpoint. HTTPS only, because every request carries the
    /// token; plain HTTP is accepted for this machine alone.
    /// </summary>
    internal static Uri McpEndpoint(Uri apiBase)
    {
        ArgumentNullException.ThrowIfNull(apiBase);
        if (!apiBase.IsAbsoluteUri
            || !(apiBase.Scheme == Uri.UriSchemeHttps || (apiBase.Scheme == Uri.UriSchemeHttp && apiBase.IsLoopback)))
            throw new InvalidOperationException(
                $"{ApiUrlKey} must start with https://, because the agent token goes with every request. " +
                "Plain http:// is accepted only for localhost.");
        return new Uri(apiBase.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/mcp");
    }

    internal static string ReadToken(Func<string, string?> environment)
    {
        var file = environment(TokenFileKey);
        var direct = environment(TokenKey);
        if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(direct))
            throw new InvalidOperationException($"Both {TokenFileKey} and {TokenKey} are set. Set one of them; the file is preferred.");

        if (!string.IsNullOrWhiteSpace(file))
        {
            string text;
            try
            {
                text = File.ReadAllText(file.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The agent token file named by {TokenFileKey} could not be read: {ex.Message}", ex);
            }
            return CheckToken(text.Split('\n', 2)[0]);
        }

        if (!string.IsNullOrWhiteSpace(direct)) return CheckToken(direct);

        throw new InvalidOperationException(
            $"The bridge needs the agent token of a registered agent: set {TokenFileKey} to a file holding it " +
            $"(issued with 'prem tokens issue <agent>'), or {TokenKey} to the token itself.");
    }

    private static string CheckToken(string token)
    {
        token = (token ?? "").Trim();
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"That is not an agent token: agent tokens begin with {TokenPrefix}.");
        return token;
    }

    private async Task<McpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is { } connected) return connected;
        await _connect.WaitAsync(ct);
        try
        {
            if (_client is { } raced) return raced;
            var options = new HttpClientTransportOptions
            {
                Endpoint = Endpoint,
                Name = "premagentic-api",
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _token },
            };
            var transport = _http is null
                ? new HttpClientTransport(options, _logs)
                : new HttpClientTransport(options, _http, _logs, ownsHttpClient: false);
            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "premagentic-stdio-bridge", Version = BridgeVersion.Informational } },
                _logs, ct);
            return _client;
        }
        finally
        {
            _connect.Release();
        }
    }
}
