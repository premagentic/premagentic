using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Premagentic.Portal;

/// <summary>Who may open a portal endpoint. An endpoint that declares none is for administrators only.</summary>
public enum PortalNeed
{
    /// <summary>Any signed-in person: the search page.</summary>
    Person = 1,

    /// <summary>An auditor or an administrator: every page that shows, but View as, and changes nothing.</summary>
    Reader = 2,

    /// <summary>An administrator: every request that changes something, and View as, which shows passages of documents' text.</summary>
    Administrator = 3,
}

/// <summary>Endpoint metadata carrying a <see cref="PortalNeed"/>.</summary>
internal sealed record PortalNeedMetadata(PortalNeed Need);

internal static class PortalNeedExtensions
{
    public static TBuilder Needs<TBuilder>(this TBuilder builder, PortalNeed need) where TBuilder : Microsoft.AspNetCore.Builder.IEndpointConventionBuilder =>
        Microsoft.AspNetCore.Builder.RoutingEndpointConventionBuilderExtensions.WithMetadata(builder, new PortalNeedMetadata(need));
}

/// <summary>One portal request: the person, the tenant, and the services a page reads through.</summary>
internal sealed class PortalRequest
{
    private static readonly object Key = new();

    private PortalRequest(HttpContext http, PortalPerson person, Guid tenant, IPortalHost host)
    {
        Http = http;
        Person = person;
        Tenant = tenant;
        Host = host;
    }

    public HttpContext Http { get; }
    public PortalPerson Person { get; }
    public Guid Tenant { get; }
    public IPortalHost Host { get; }

    public PremagenticDatabase Db => Http.RequestServices.GetRequiredService<PremagenticDatabase>();
    public TimeProvider Clock => Http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>The chunkers the host registered, or the built-in ones when it registered none.</summary>
    public ChunkerRegistry Chunkers => Http.RequestServices.GetService<ChunkerRegistry>() ?? ChunkerRegistry.BuiltIn;

    /// <summary>
    /// The readers the host registered, or the built-in ones when it registered
    /// none. A run started from the portal reads what the rest of the process
    /// reads: without this, an extension's reader would load, appear on the
    /// health page as loaded, and index nothing.
    /// </summary>
    public Core.Ingestion.Readers.ReaderRegistry Readers =>
        Http.RequestServices.GetService<Core.Ingestion.Readers.ReaderRegistry>()
        ?? Core.Ingestion.Readers.ReaderRegistry.BuiltIn;

    /// <summary>
    /// The principal mapper the host reads outside principals through, or null
    /// when it has none. A run started from the portal reads what a sign-in
    /// reads, so the two never disagree about what an outside group means. The
    /// registration is required: a host without it would read no mapper where
    /// it has one, which is a fault to see, not a default to take.
    /// </summary>
    public IPrincipalMapper? PrincipalMapper =>
        Http.RequestServices.GetRequiredService<PrincipalMapping>().Mapper;

    /// <summary>
    /// The extension host this process composed, or null when nothing
    /// registered one. Null is not "no extensions": it is a host that never
    /// looked, and the health page tells the two apart.
    /// </summary>
    public Core.Extensions.ExtensionHost? Extensions =>
        Http.RequestServices.GetService<Core.Extensions.ExtensionHost>();

    public Core.Sources.Registry.SourceRegistry Sources() => new(Db, Tenant, Chunkers);
    public CancellationToken Aborted => Http.RequestAborted;

    public bool IsAdministrator => Person.User.Role == Role.Administrator;
    public bool IsReader => Person.User.Role is Role.Administrator or Role.Auditor;

    /// <summary>The actor every change made through the portal is recorded under: the signed-in user.</summary>
    public AdminActor Actor => new("portal", null, Person.User.Id);

    public IdentityStore Identity() => new(Db, Tenant, Clock);

    public AdminChanges Changes() => new(Db, Tenant, Clock);

    /// <summary>
    /// Runs one administrator change and its record in one transaction, then
    /// redirects to <paramref name="back"/> with what happened. A change the
    /// stores refuse is shown as the refusal, with nothing changed.
    /// </summary>
    /// <param name="change">Makes the change and returns the sentence to show.</param>
    public async Task<IResult> ChangeAsync(string back, Func<AdminChange, Task<string>> change)
    {
        try
        {
            return Html.Layout.After(back, done: await Changes().RunAsync(Actor, change, Aborted));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Html.Layout.After(back, error: ex.Message);
        }
    }

    public string? Query(string name) => Http.Request.Query[name].FirstOrDefault() is { Length: > 0 } value ? value : null;

    public async Task<IFormCollection> FormAsync() => await Http.Request.ReadFormAsync(Aborted);

    public static bool Allows(Role role, PortalNeed need) => need switch
    {
        PortalNeed.Person => true,
        PortalNeed.Reader => role is Role.Administrator or Role.Auditor,
        _ => role == Role.Administrator,
    };

    internal static PortalRequest Attach(HttpContext http, PortalPerson person, Guid tenant, IPortalHost host)
    {
        var request = new PortalRequest(http, person, tenant, host);
        http.Items[Key] = request;
        return request;
    }

    /// <summary>The request the access filter attached. There is no fallback: a page without one throws.</summary>
    public static PortalRequest Of(HttpContext http) =>
        http.Items[Key] as PortalRequest
        ?? throw new InvalidOperationException("No signed-in person was attached to this portal request.");
}
