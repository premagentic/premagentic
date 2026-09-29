using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Premagentic.Cli.Setup;

/// <summary>
/// The self-signed certificate the API serves until the customer installs
/// their own: a P-256 key, for the host name setup is given plus localhost and
/// the loopback addresses, for server authentication only, valid for two years
/// (inside the 825 days some clients accept for a server certificate).
/// </summary>
internal static class HttpsCertificate
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(730);

    /// <summary>A certificate this close to expiry is replaced when setup runs again.</summary>
    public static readonly TimeSpan RenewWithin = TimeSpan.FromDays(30);

    private static readonly Regex DnsName = new(
        @"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant);

    /// <summary>True for a DNS name or an IP address.</summary>
    public static bool IsValidHostName(string hostName) =>
        IPAddress.TryParse(hostName, out _) || DnsName.IsMatch(hostName);

    public static X509Certificate2 Create(string hostName, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={hostName}"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(hostName, out var address)) names.AddIpAddress(address);
        else names.AddDnsName(hostName);
        if (!hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase)) names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());

        // A few minutes back, so a client whose clock runs slightly behind still accepts it.
        return request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Validity));
    }

    /// <summary>True when the certificate names <paramref name="hostName"/> in its subject alternative names.</summary>
    public static bool Covers(X509Certificate2 certificate, string hostName) =>
        certificate.MatchesHostname(hostName, allowWildcards: false, allowCommonName: false);
}
