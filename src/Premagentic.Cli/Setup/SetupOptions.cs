using Premagentic.Core.Embeddings;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Setup;

/// <summary>
/// Everything <see cref="SetupEngine"/> needs, so one run can be driven entirely
/// by flags and tested.
/// </summary>
/// <param name="AdminConnectionString">
/// A PostgreSQL role that can create roles and databases. Used for the steps
/// that need it and never written anywhere.
/// </param>
/// <param name="CredentialsDirectory">Where the credentials files, the certificate and the HTTPS settings are written.</param>
/// <param name="EmbedderFactory">
/// The embedding provider the health check searches with, given the providers
/// the deployment's own extensions registered. It is given them because by the
/// time the health check runs the database exists and the allow list can be
/// read, so a deployment that embeds with a provider an extension brought is
/// checked with that provider rather than refused for naming it.
/// </param>
/// <param name="RequiredModelDirectory">
/// The folder that must hold <c>model.onnx</c> and <c>vocab.txt</c>, or null when
/// the configured provider needs no model files.
/// </param>
/// <param name="Plan">Report every step and change nothing.</param>
/// <param name="AdministratorName">
/// The sign-in name of the first administrator, created when no administrator
/// exists. Null creates none.
/// </param>
/// <param name="AdministratorPassword">
/// Asked for the first administrator's password only when that account is about
/// to be created or finished, never in plan mode and never on a run that finds
/// an administrator. Null or empty stops the step.
/// </param>
/// <param name="HostName">The name clients use for the API; the self-signed certificate is made for it.</param>
/// <param name="HttpsPort">The port the API listens on for HTTPS.</param>
/// <param name="WindowsService">
/// Register the API as a Windows service under its own virtual account, and give
/// that account read access to the files it needs. Needs an elevated prompt.
/// </param>
/// <param name="ApiExecutable">The API program the Windows service runs.</param>
/// <param name="BundledPostgres">
/// The <c>pgsql</c> folder of the bundled PostgreSQL. Setup then makes and starts
/// that server first and connects as its superuser instead of taking an admin
/// connection. Null for a PostgreSQL that already exists.
/// </param>
/// <param name="DataDirectory">Where the bundled server keeps its data; required with <paramref name="BundledPostgres"/>.</param>
/// <param name="PostgresPort">The bundled server's port, on localhost only.</param>
internal sealed record SetupOptions(
    string AdminConnectionString,
    string CredentialsDirectory,
    Func<IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>>?, IEmbeddingProvider> EmbedderFactory,
    string? RequiredModelDirectory,
    string DatabaseName = "premagentic",
    string OwnerRole = "premagentic_owner",
    string AppRole = "premagentic_app",
    string SearchRole = "premagentic_search",
    bool Plan = false,
    string TenantKey = "default",
    string TenantName = "Premagentic deployment",
    string? AdministratorName = null,
    Func<string?>? AdministratorPassword = null,
    string HostName = "localhost",
    int HttpsPort = 8443,
    bool WindowsService = false,
    string? ApiExecutable = null,
    string? BundledPostgres = null,
    string? DataDirectory = null,
    int PostgresPort = 5432)
{
    public const long MinimumFreeDiskBytes = 100L * 1024 * 1024;
    public const long RecommendedFreeDiskBytes = 2L * 1024 * 1024 * 1024;
    public const long RecommendedMemoryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The application role's file: what the service, the API and every read and write use.</summary>
    public string AppCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.AppCredentials);

    /// <summary>The owner role's file: for migrations and rebuild-index only.</summary>
    public string OwnerCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.OwnerCredentials);

    /// <summary>The search role's file: what the search and section reads connect with.</summary>
    public string SearchCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.SearchCredentials);

    /// <summary>The bundled server's superuser: for setup only, readable by administrators and nobody else.</summary>
    public string SuperuserCredentialsPath => Path.Combine(CredentialsDirectory, InstallFiles.SuperuserCredentials);

    public string KestrelSettingsPath => Path.Combine(CredentialsDirectory, InstallFiles.KestrelSettings);

    public string CertificatePath => Path.Combine(CredentialsDirectory, InstallFiles.Certificate);

    public string PublicCertificatePath => Path.Combine(CredentialsDirectory, InstallFiles.PublicCertificate);
}
