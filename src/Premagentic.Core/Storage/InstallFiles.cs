namespace Premagentic.Core.Storage;

/// <summary>
/// The private files <c>prem setup</c> writes into one folder, and how a host
/// finds them. The application is pointed at <see cref="AppCredentials"/> with
/// <c>PREM_CREDENTIALS_FILE</c>; everything else it needs sits beside that file,
/// so a service needs exactly one setting.
/// <para>
/// Paths inside <see cref="KestrelSettings"/> may be relative, and are then
/// relative to that file's own folder. That keeps the folder movable, and lets a
/// systemd unit hand the files to the service with <c>LoadCredential</c>, which
/// places them in a folder whose name is known only at run time.
/// </para>
/// </summary>
public static class InstallFiles
{
    /// <summary>The application role's connection: what the API, the MCP server and every read and write use.</summary>
    public const string AppCredentials = "app.credentials";

    /// <summary>The owner role's connection: for migrations and rebuild-index only. Never given to a service.</summary>
    public const string OwnerCredentials = "owner.credentials";

    /// <summary>
    /// The search role's connection: the reads a caller makes, bound by row-level
    /// security to what that caller may see. Beside the application's file, with
    /// the same access, because the same service reads both.
    /// </summary>
    public const string SearchCredentials = "search.credentials";

    /// <summary>
    /// The bundled server's superuser, which setup uses to create the roles and the
    /// database. Readable by the account that ran setup and by administrators; never
    /// given to a service.
    /// </summary>
    public const string SuperuserCredentials = "postgres.credentials";

    /// <summary>The HTTPS endpoint, in the configuration form the API reads: the <c>Kestrel</c> section, as JSON.</summary>
    public const string KestrelSettings = "kestrel.json";

    /// <summary>The certificate with its private key, PKCS #12, protected by a password kept in <see cref="KestrelSettings"/>.</summary>
    public const string Certificate = "https.pfx";

    /// <summary>The certificate alone, PEM, for clients that must be told to trust it. Not secret.</summary>
    public const string PublicCertificate = "https.crt";

    /// <summary>
    /// The Kestrel settings file beside <paramref name="credentialsFile"/>, when one
    /// is configured and the settings file exists; otherwise null.
    /// </summary>
    public static string? KestrelSettingsBeside(string? credentialsFile)
    {
        if (string.IsNullOrEmpty(credentialsFile)) return null;
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(credentialsFile))!, KestrelSettings);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Null when the Kestrel settings file at <paramref name="path"/> sets the
    /// <c>Kestrel</c> section alone, judged by its top-level keys (a key such
    /// as <c>Kestrel:Limits</c> is the Kestrel section too); otherwise the
    /// refusal, naming what else it sets. The API reads the file into its whole
    /// configuration, where anything else, such as the header sign-in, would
    /// be a setting nobody looks for there; the API and setup both refuse it.
    /// </summary>
    public static string? KestrelSettingsRefusal(string path, IEnumerable<string> topLevelKeys) =>
        topLevelKeys.Where(key => !key.Split(':')[0].Equals("Kestrel", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList() is { Count: > 0 } others
            ? $"{path} may hold only the Kestrel section, and it also sets {string.Join(", ", others)}; set that in the environment or in appsettings.json instead."
            : null;

    /// <summary>
    /// For settings read from a Kestrel settings file (flattened configuration
    /// keys, such as <c>Kestrel:Endpoints:Https:Certificate:Path</c>), the
    /// overrides that turn each relative <c>Path</c> or <c>KeyPath</c> into a full
    /// path under <paramref name="folder"/>. Full paths and every other key are
    /// left alone.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> ResolveRelativePaths(
        IEnumerable<KeyValuePair<string, string?>> settings, string folder) =>
        settings
            .Where(s => s.Key.StartsWith("Kestrel:", StringComparison.OrdinalIgnoreCase)
                        && (s.Key.EndsWith(":Path", StringComparison.OrdinalIgnoreCase)
                            || s.Key.EndsWith(":KeyPath", StringComparison.OrdinalIgnoreCase))
                        && !string.IsNullOrEmpty(s.Value) && !Path.IsPathFullyQualified(s.Value))
            .Select(s => new KeyValuePair<string, string?>(s.Key, Path.GetFullPath(Path.Combine(folder, s.Value!))))
            .ToList();
}
