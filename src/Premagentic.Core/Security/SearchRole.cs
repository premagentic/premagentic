using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Security;

/// <summary>
/// The database role the search and section read paths connect as, which
/// row-level security binds to one caller's permitted access lists at a time.
/// <para>
/// It may only read the index, and with no caller session bound it reads
/// nothing. Ingest and administration stay on the application role, which the
/// policy lets read the whole index because it writes it. A process that has
/// no search role reads through the application role, the SQL gate still
/// filters every read, and the second line is not in force; <see cref="VerifyAsync"/>
/// says which.
/// </para>
/// </summary>
public sealed class SearchRole : IAsyncDisposable
{
    /// <summary>The file <c>prem setup</c> writes beside <c>app.credentials</c>.</summary>
    public const string CredentialsFileName = InstallFiles.SearchCredentials;

    /// <summary>The search role's connection string, for a deployment configured by connection string.</summary>
    public const string ConnectionStringVariable = "PREM_SEARCH_CONNECTION_STRING";

    public SearchRole(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    internal NpgsqlDataSource DataSource { get; }

    /// <summary>
    /// The search role this process is configured with, or null for none. An
    /// installed deployment (<c>PREM_CREDENTIALS_FILE</c>) reads
    /// <see cref="CredentialsFileName"/> beside that file and refuses to go on
    /// without it; a deployment configured by connection string may name one
    /// in <see cref="ConnectionStringVariable"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">An installed deployment has no search credentials, or both sources are set.</exception>
    public static SearchRole? FromEnvironment() => From(Environment.GetEnvironmentVariable);

    internal static SearchRole? From(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var credentialsFile = environment("PREM_CREDENTIALS_FILE");
        var connectionString = environment(ConnectionStringVariable);

        if (string.IsNullOrEmpty(credentialsFile))
            return string.IsNullOrEmpty(connectionString) ? null : new SearchRole(connectionString);

        if (!string.IsNullOrEmpty(connectionString))
            throw new StartupRefusedException(
                $"Both PREM_CREDENTIALS_FILE and {ConnectionStringVariable} are set. An installed deployment reads " +
                $"{CredentialsFileName} beside its credentials file; unset {ConnectionStringVariable}.");

        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(credentialsFile))!, CredentialsFileName);
        if (!File.Exists(path))
            throw new StartupRefusedException(
                $"There is no {CredentialsFileName} beside {credentialsFile}. Search reads through the search role, " +
                "which row-level security binds to each caller; run 'prem setup' again to create it.");
        return new SearchRole(CredentialsFile.ReadConnectionString(path));
    }

    /// <summary>
    /// Whether the policy binds this role: it is not a superuser, cannot bypass
    /// row-level security, and is not a role that reads the whole index. Null
    /// when it is bound; otherwise why not, in a sentence.
    /// </summary>
    public async Task<string?> VerifyAsync(CancellationToken ct = default)
    {
        await using var cmd = DataSource.CreateCommand("""
            SELECT r.rolname, r.rolsuper, r.rolbypassrls, prem_config.reads_whole_index(r.rolname)
            FROM pg_roles r WHERE r.rolname = current_user
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return "The search role could not be found in pg_roles.";
        var name = reader.GetString(0);
        if (reader.GetBoolean(1)) return $"The search role {name} is a superuser, which row-level security never binds.";
        if (reader.GetBoolean(2)) return $"The search role {name} has BYPASSRLS, which turns row-level security off for it.";
        if (reader.GetBoolean(3)) return $"The search role {name} is listed in prem_config.index_writer, so it reads the whole index.";
        return null;
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
