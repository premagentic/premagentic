using Premagentic.Api;
using Premagentic.Api.Callers;
using Premagentic.Api.Hosting;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Storage;

// Premagentic signs people in and checks agent tokens itself, and every search
// runs as the caller who asked, with that caller's rights and trust policy as
// they are at that moment. A request with no caller is refused. See
// CallerMiddleware for the three ways a caller is known.
//
// The trusted-header mode (PREM_SIGN_IN_HEADER) is for a customer whose
// proxy already signs people in: the header carries a Premagentic sign-in name.
// With it on, do not expose this port directly, because a client that can set
// the header itself can name any user it likes.
StartupRefusedException.ExitCleanlyWhenUnhandled("Premagentic API");
var builder = WebApplication.CreateBuilder(args);
Premagentic.Api.Hosting.PremagenticHosting.AddPremagenticHosting(builder);

builder.Services.AddSingleton(sp => ApiSettings.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => ApiResponseHeaderOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new PremagenticDatabase(PremagenticDatabase.ConnectionStringFromEnvironment()));
// The extensions an administrator allowed, loaded once and shared: the one
// place this process decides what it can read, cut and embed. Resolved at
// startup, below, so no request waits for a folder read.
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExtensionHost>();
    return ExtensionHosting.LoadAsync(
        new SettingsStore(sp.GetRequiredService<PremagenticDatabase>(), sp.GetRequiredService<Deployment>().TenantId),
        note: line => log.LogInformation("{Extensions}", line),
        warn: line => log.LogWarning("{Extensions}", line)).GetAwaiter().GetResult();
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<ExtensionHost>().Chunkers);
builder.Services.AddSingleton(sp => sp.GetRequiredService<ExtensionHost>().Readers);
builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
    EmbeddingProviderFactory.FromEnvironment(sp.GetRequiredService<ExtensionHost>().EmbeddingProviders, sp));
// Search and section reads connect as the search role, which row-level security
// binds to each caller, when the deployment has one.
if (SearchRole.FromEnvironment() is { } searchRole)
    builder.Services.AddSingleton(searchRole);
// Retrieval tuning is read from the deployment's settings for every search. A
// stored value that cannot be used keeps its default and is logged once.
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<HybridSearch>();
    return new HybridSearch(
        sp.GetRequiredService<PremagenticDatabase>(),
        sp.GetRequiredService<IEmbeddingProvider>(),
        headingPrefixSpace: sp.GetRequiredService<ApiSettings>().HeadingPrefix,
        searchRole: sp.GetService<SearchRole>(),
        storedTuning: new RetrievalSettingsLoader(sp.GetRequiredService<PremagenticDatabase>(), problem => log.LogWarning(
            "The stored setting {Key} is not used: {Problem} Its default applies until it is changed.", problem.Key, problem.Problem)));
});
builder.Services.AddSingleton(sp => new SectionFetcher(sp.GetRequiredService<PremagenticDatabase>(), sp.GetService<SearchRole>()));
builder.Services.AddPremagenticCallers();
builder.Services.AddPremagenticMcp();
builder.Services.AddPremagenticOAuth();

var app = builder.Build();

// Read once the configuration is complete. A retired or malformed setting stops the start here.
var settings = app.Services.GetRequiredService<ApiSettings>();
var db = app.Services.GetRequiredService<PremagenticDatabase>();
await db.InitializeAsync();
app.Services.GetRequiredService<Deployment>().Start(await db.EnsureTenantAsync(settings.TenantKey, settings.TenantName));

// The second line is in force only when the search role is one the policy
// binds; a search role that reads the whole index would silently turn it off.
if (app.Services.GetService<SearchRole>() is { } reads)
{
    if (await reads.VerifyAsync() is { } unbound)
        throw new StartupRefusedException(unbound + " Fix the role before starting Premagentic.");
    app.Logger.LogInformation("Row-level security binds search reads to each caller.");
}
else
{
    // A deployment by connection string names its search role or says on
    // purpose that it runs without one; only the development database is
    // let through with the warning alone.
    if (SearchRoleRequirement.Refusal(key => app.Configuration[key]) is { } refusal)
        throw new StartupRefusedException(refusal);
    app.Logger.LogWarning(
        "No search role is configured, so search reads connect as the application role and only the SQL gate " +
        "filters them. An installed deployment reads through the search role that prem setup creates.");
}

// Made now rather than at the first sign-in, so the first attempt costs what
// every other attempt costs.
app.Services.GetRequiredService<SignInService>();

// Extensions load here, before anything is served. A refusal is logged with
// its reason and the deployment starts with the built-ins.
app.Services.GetRequiredService<ExtensionHost>();

// What this deployment calls its two MCP tools, read once, before anything is
// served. An assistant reads a tool's description when it lists the tools, so
// changing it under a running server would leave two assistants disagreeing
// about what the same tool does.
await McpToolText.ApplyAsync(app.Services, app.Logger);

// What this deployment tells every assistant at connect, read once for the
// same reason: an assistant takes it in when it connects.
await McpInstructions.ApplyAsync(app.Services, app.Logger);

// The largest request /mcp takes, read once for the same reason: a bound that
// moved under a running server would refuse what an assistant sent a minute ago.
var mcpRequestLimit = await McpRequestSizeLimit.ReadAsync(app.Services, app.Logger);

// Whether this process runs the MCP authorization flow, read once, before
// anything is served. It is off unless an administrator turned it on. A flag
// that is on over settings the flow cannot run with leaves it off, with one
// warning, and the server starts either way. While it is off none of its
// paths exists.
var oauth = app.Services.GetRequiredService<OAuthStart>().Loaded;
if (oauth is not null)
{
    app.Logger.LogInformation(
        "The MCP authorization flow is on: issuer {Issuer}, resource {Resource}, self-registration {Registration}.",
        oauth.Issuer, oauth.Resource, oauth.DynamicRegistration ? "on" : "off");
    // Only when this process holds the certificate setup made, with no proxy
    // in front: then the name an assistant checks is this certificate's.
    if (app.Services.GetRequiredService<ApiResponseHeaderOptions>().Hsts
        && InstallFiles.KestrelSettingsBeside(app.Configuration["PREM_CREDENTIALS_FILE"]) is { } kestrel
        && OAuthEndpoints.CertificateWarning(Path.Combine(Path.GetDirectoryName(kestrel)!, InstallFiles.PublicCertificate), oauth) is { } mismatch)
        app.Logger.LogWarning("{Certificate}", mismatch);
}

// Load every stored vector before the first request, so no caller pays for it.
var warmUp = System.Diagnostics.Stopwatch.StartNew();
var vectorsHeld = await app.Services.GetRequiredService<HybridSearch>().WarmUpAsync();
app.Logger.LogInformation("Vector index loaded: {Count} chunk vectors in {Elapsed} ms.", vectorsHeld, warmUp.ElapsedMilliseconds);

if (settings.SignInHeader is not null)
    app.Logger.LogWarning(
        "Trusted-header sign-in is on: a request whose {Header} header names a Premagentic user runs as that user. " +
        "Only the authenticating proxy may reach this port.", settings.SignInHeader);
if (settings.AllowHttpSignIn)
    app.Logger.LogWarning(
        "{Switch}=1: password sign-in is allowed over plain HTTP, so passwords cross the network UNENCRYPTED. " +
        "This is for development on one machine only. Unset it for any other use.", ApiSettings.AllowHttpSignInKey);

app.UseMiddleware<ApiResponseHeaders>();
app.UseRouting();
app.UseMiddleware<CallerMiddleware>();
app.UseMiddleware<McpRequestLimitMiddleware>();
app.UseMiddleware<ReadRequestLimitMiddleware>();
app.MapPremagenticApi();
if (oauth is not null) app.MapPremagenticOAuth(oauth);
app.MapPremagenticPortal(new ApiPortalHost());
app.MapMcp(CallerServices.McpPath).WithMetadata(CallerRequirement.AgentOnly, mcpRequestLimit);

await app.RunAsync();
