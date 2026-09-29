using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity.SignIn;
using Premagentic.Core.Ingestion;

namespace Premagentic.Conformance;

/// <summary>
/// Inherit this and give it your sign-in adapter to prove it keeps the rules
/// the sign-in seam rests on. An adapter says which existing account a request
/// is, and nothing more: PremAgentic looks the name up among the accounts an
/// administrator made, so a name nobody made reaches nothing; the groups an
/// adapter reports count only through the deployment's principal mapper, and
/// what it does not map, or everything when there is none, is ignored, so an
/// unmapped one widens nothing; and the seam gives an adapter no way to make an
/// account or grant a role. Those are the host's to keep, and it keeps them for
/// every adapter. What only an adapter can break, this checks: that a request
/// with nothing on it, or with credentials nobody issued, signs in nobody, and
/// that the adapter holds nothing that could make an account, grant a role or
/// change a rule behind the host's back.
/// </summary>
public abstract class SignInAdapterConformance
{
    /// <summary>
    /// This kit proves the version of this seam that the PremAgentic your
    /// extension builds against offers. A kit from another release fails here,
    /// naming the version to match, rather than passing in silence.
    /// </summary>
    [Fact]
    public void The_kit_proves_the_seams_this_extension_builds_against() =>
        KitSeams.AssertProves(SeamVersions.SignInName, nameof(SeamVersions.SignIn));

    /// <summary>Your adapter, set up as a deployment would set it up.</summary>
    protected abstract ISignInAdapter Adapter { get; }

    [Fact]
    public void It_names_itself_as_settings_and_the_log_name_it()
    {
        Assert.True(ChunkerRegistry.IsName(Adapter.Name) && Adapter.Name == Adapter.Name.ToLowerInvariant(),
            $"'{Adapter.Name}' cannot name a sign-in adapter: a name is up to 64 lower case letters, digits, dots, hyphens " +
            "and underscores, starting with a letter or digit.");
    }

    [Fact]
    public async Task A_request_with_nothing_on_it_signs_in_nobody()
    {
        var resolution = await Adapter.ResolveAsync(new KitSignInRequest(forged: false), CancellationToken.None);

        Assert.True(resolution?.SignInName is null,
            $"The adapter '{Adapter.Name}' signed a request with no headers and no cookies in as '{resolution?.SignInName}'. " +
            "With nothing on it, a request is not this adapter's (null), and every request without credentials would " +
            "otherwise be that person.");
    }

    [Fact]
    public async Task A_request_whose_every_header_and_cookie_is_forged_signs_in_nobody()
    {
        var request = new KitSignInRequest(forged: true);

        var resolution = await Adapter.ResolveAsync(request, CancellationToken.None);

        Assert.True(resolution?.SignInName is null,
            $"The adapter '{Adapter.Name}' signed a request in as '{resolution?.SignInName}' when every header and cookie " +
            $"it read ({request.Asked}) held a value nobody issued. An adapter says who a credential proves, so it " +
            "verifies what it reads; a credential it cannot verify proves nobody (SignInResolution.Nobody). Taking a " +
            "header's word is what the built-in trusted-header mode is for, behind a proxy that alone can reach the port.");
    }

    [Fact]
    public void It_holds_nothing_that_could_make_an_account_grant_a_role_or_change_a_rule()
    {
        var assembly = Adapter.GetType().Assembly;
        var found = StoreReferences(assembly);

        Assert.True(found.Count == 0,
            $"The adapter '{Adapter.Name}' is in {assembly.GetName().Name}, which uses {string.Join(", ", found)}. An adapter " +
            "says which existing account a request is. It never makes an account, grants a role or changes what anyone " +
            "may read, so it has no use for PremAgentic's store of accounts, groups and rules, or for its database: an " +
            "administrator makes the accounts, and maps a directory's groups to PremAgentic groups.");
    }

    /// <summary>
    /// The types and assemblies <paramref name="assembly"/> uses that could
    /// make an account, grant a role, open a session or change a rule:
    /// PremAgentic's identity store (the sign-in seam itself excepted), its
    /// database, its access rules and its administration, and the PostgreSQL
    /// driver. Read from the assembly's metadata, so it names what the
    /// assembly's code refers to whether or not a test ran that code.
    /// </summary>
    public static IReadOnlyList<string> StoreReferences(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrEmpty(assembly.Location))
            throw new InvalidOperationException($"{assembly.GetName().Name} was not loaded from a file, so its references cannot be read.");

        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var metadata = pe.GetMetadataReader();
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (name.Equals("Npgsql", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Npgsql.", StringComparison.OrdinalIgnoreCase))
                found.Add(name);
        }

        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            // A nested type names no namespace of its own; its outermost type does.
            var outer = type;
            while (outer.ResolutionScope.Kind == HandleKind.TypeReference)
                outer = metadata.GetTypeReference((TypeReferenceHandle)outer.ResolutionScope);
            var ns = metadata.GetString(outer.Namespace);
            var full = ns.Length == 0 ? metadata.GetString(outer.Name) : $"{ns}.{metadata.GetString(outer.Name)}";
            if (StoreNamespaces.Contains(ns) || StoreTypes.Contains(full) || ns.StartsWith("Npgsql", StringComparison.Ordinal))
                found.Add(full);
        }
        return [.. found];
    }

    private static readonly HashSet<string> StoreNamespaces = new(StringComparer.Ordinal)
    {
        "Premagentic.Core.Identity",
        "Premagentic.Core.Storage",
        "Premagentic.Core.Security.Acl",
        "Premagentic.Core.Admin",
    };

    private static readonly HashSet<string> StoreTypes = new(StringComparer.Ordinal)
    {
        "Premagentic.Core.Security.AclStore",
    };
}

/// <summary>
/// A request for the fixture to hand an adapter: one with nothing on it, or
/// one where every header and cookie the adapter asks for holds a value
/// nobody issued, the same value each time it asks.
/// </summary>
internal sealed class KitSignInRequest(bool forged) : ISignInRequest
{
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    /// <summary>The headers and cookies the adapter asked for, for the failure message.</summary>
    public string Asked =>
        _headers.Count + _cookies.Count == 0
            ? "none"
            : string.Join(", ", _headers.Keys.Select(h => $"header {h}").Concat(_cookies.Keys.Select(c => $"cookie {c}")));

    public string Header(string name)
    {
        if (!forged) return "";
        if (!_headers.TryGetValue(name, out var value)) _headers[name] = value = Forged();
        return value;
    }

    public string? Cookie(string name)
    {
        if (!forged) return null;
        if (!_cookies.TryGetValue(name, out var value)) _cookies[name] = value = Forged();
        return value;
    }

    private static string Forged() => "forged." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
}
