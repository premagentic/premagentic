using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Premagentic.Api.Callers;

internal static class CallerServices
{
    /// <summary>Where MCP is served: the resource every access token of the authorization flow is bound to.</summary>
    public const string McpPath = "/mcp";

    /// <summary>Sign-in, sessions, throttles and per-request identity for the deployment's one tenant.</summary>
    public static IServiceCollection AddPremagenticCallers(this IServiceCollection services)
    {
        services.AddSingleton<Deployment>();
        services.AddSingleton(SessionPolicy.Default);
        services.AddSingleton(LockoutPolicy.Default);
        services.AddSingleton(SignInThrottleOptions.Default);
        services.AddSingleton(PortalFormDrain.Default);
        services.AddSingleton(_ => new PasswordHasher());
        services.AddSingleton(sp => new SignInService(sp.GetRequiredService<PasswordHasher>(), sp.GetRequiredService<LockoutPolicy>()));
        services.AddSingleton<SignInThrottle>();
        services.AddSingleton<AgentRateLimiter>();
        services.AddSingleton<CallerQueries>();
        services.AddSingleton<UnmappedPrincipalLog>();

        services.AddScoped(sp => new IdentityStore(
            sp.GetRequiredService<PremagenticDatabase>(), sp.GetRequiredService<Deployment>().TenantId,
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new SessionStore(
            sp.GetRequiredService<PremagenticDatabase>(), sp.GetRequiredService<Deployment>().TenantId,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<SessionPolicy>()));

        // What a sign-in adapter's groups mean here, read through the principal
        // mapper a loaded extension brought, or through none. The portal's
        // ingest runs read the same registration, so both callers agree.
        services.AddScoped(sp => new PrincipalMapping(
            sp.GetRequiredService<PremagenticDatabase>(), sp.GetRequiredService<Deployment>().TenantId,
            sp.GetRequiredService<ExtensionHost>().PrincipalMapper));

        // Scoped, because the session adapter reads the deployment's sessions
        // through the store for this request. The adapters the loaded
        // extensions brought come last, after both built-in ways.
        services.AddScoped(sp => new SignInAdapters(
            sp.GetRequiredService<ApiSettings>(), sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<ExtensionHost>().SignInAdapters));
        return services;
    }

    /// <summary>
    /// MCP over HTTP with the two read-only tools and the deployment's
    /// instructions for assistants (see <see cref="McpInstructions"/>). Stateless, as MCP revision
    /// 2026-07-28 requires of Streamable HTTP: every request stands alone, is
    /// authenticated by its own token, and there is no session to hijack.
    /// </summary>
    public static IServiceCollection AddPremagenticMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<McpInstructionsState>();
        services.AddSingleton<IInstructionsReader>(sp => new SettingsInstructionsReader(sp.GetRequiredService<PremagenticDatabase>()));
        services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "premagentic", Version = Premagentic.Core.BuildVersion.Informational })
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
                // Stateless, so every request builds its server from the
                // options; the instructions this process read at start go on each.
                options.ConfigureSessionOptions = (http, server, _) =>
                {
                    server.ServerInstructions = http.RequestServices.GetRequiredService<McpInstructionsState>().Current?.Text;
                    return Task.CompletedTask;
                };
            })
            .WithTools<PremagenticMcpTools>()
            .WithListResourcesHandler(McpInstructions.ListAsync)
            .WithReadResourceHandler(McpInstructions.ReadAsync);
        return services;
    }
}
