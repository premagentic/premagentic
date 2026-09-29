using System.Net;
using Premagentic.Core.Storage;
using Microsoft.AspNetCore.HttpOverrides;

namespace Premagentic.Api.Hosting;

/// <summary>
/// The reverse proxies the API may believe about the client: their
/// <c>X-Forwarded-For</c> gives the client's address and their
/// <c>X-Forwarded-Proto</c> the scheme the client used. Off by default, so every
/// forwarding header is ignored and the address and scheme are the
/// connection's own.
/// <para>
/// <c>PREM_TRUSTED_PROXIES</c> lists the proxies by address or range, for
/// example <c>10.0.0.5, 10.1.0.0/16, fd00::/8</c>. Headers are believed only
/// from a listed address, and walking back through the chain stops at the first
/// address that is not listed, so a client cannot forge its way past the proxy
/// by sending the headers itself. The sign-in throttle then counts real clients,
/// and password sign-in over HTTPS that ends at the proxy is allowed.
/// </para>
/// <para>
/// Never the framework's own switch (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>),
/// which believes forwarding headers from anyone who sends them: the API refuses
/// to start with it set. A range that covers every address is refused for the
/// same reason.
/// </para>
/// </summary>
internal static class TrustedProxies
{
    public const string Key = "PREM_TRUSTED_PROXIES";

    // The framework reads its switch as ASPNETCORE_FORWARDEDHEADERS_ENABLED from
    // the environment, which reaches configuration under both names.
    private static readonly string[] FrameworkSwitch = ["FORWARDEDHEADERS_ENABLED", "ASPNETCORE_FORWARDEDHEADERS_ENABLED"];

    /// <exception cref="StartupRefusedException">The framework's switch is on, or the list does not parse.</exception>
    public static void AddTo(IServiceCollection services, IConfiguration config)
    {
        foreach (var key in FrameworkSwitch)
            if (string.Equals(config[key], "true", StringComparison.OrdinalIgnoreCase))
                throw new StartupRefusedException(
                    $"ASPNETCORE_FORWARDEDHEADERS_ENABLED believes forwarding headers from any client, which lets a client " +
                    $"choose its own address. Unset it, and list your reverse proxies in {Key}.");

        var list = Parse(config[Key]);
        if (list.Count == 0) return;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // No fixed number of hops: every listed proxy in the chain is
            // unwrapped, and the first address that is not listed is the client.
            options.ForwardLimit = null;
            // The defaults trust the loopback addresses; only the list is trusted.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var network in list) options.KnownIPNetworks.Add(network);
        });
        services.AddSingleton<IStartupFilter, TrustedProxyStartupFilter>();
    }

    /// <summary>The listed proxies as networks; a single address is a network of one.</summary>
    /// <exception cref="StartupRefusedException">An entry is not an address or a range, or covers every address.</exception>
    internal static IReadOnlyList<System.Net.IPNetwork> Parse(string? value)
    {
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            System.Net.IPNetwork network;
            if (IPAddress.TryParse(entry, out var address) && !entry.Contains('/'))
                network = new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            else if (!System.Net.IPNetwork.TryParse(entry, out network))
                throw new StartupRefusedException(
                    $"{Key} lists '{entry}', which is not an address or a range such as 10.1.0.0/16. List each reverse proxy, separated by commas.");
            if (network.PrefixLength == 0)
                throw new StartupRefusedException(
                    $"{Key} lists '{entry}', which covers every address and would believe any client about itself. List only your reverse proxies.");
            networks.Add(network);
        }
        return networks;
    }

    /// <summary>Puts the forwarding headers first in the pipeline, before anything reads the address or the scheme.</summary>
    private sealed class TrustedProxyStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseForwardedHeaders();
            next(app);
        };
    }
}
