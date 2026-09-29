using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Premagentic.Cli.Setup;
using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Premagentic.Tests;

/// <summary>
/// What <c>prem setup</c> adds on top of the database: the first
/// administrator, the HTTPS certificate and settings, the driver defaults it
/// writes, the PostgreSQL floor and the Windows service step. Same arrangement as
/// <see cref="SetupEngineTests"/>: a stock PostgreSQL as its superuser, and a
/// database, two roles and a folder of its own for every test. The server is
/// <see cref="SetupTestServer"/>'s: a container, or the one
/// <c>PREM_TEST_ADMIN_CONNECTION</c> names, which is how the Windows tests run
/// on a machine that cannot run a Linux container.
/// </summary>
public sealed class SetupInstallTests(SetupTestServer server) : IClassFixture<SetupTestServer>, IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private SetupOptions NewOptions(string? folder = null, string? adminConnection = null)
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        folder ??= Path.Combine(Path.GetTempPath(), "premagentic-install-tests", suffix);
        _folders.Add(folder);
        return new SetupOptions(
            AdminConnectionString: adminConnection ?? server.AdminConnectionString,
            CredentialsDirectory: folder,
            EmbedderFactory: _ => new SeededEmbeddingProvider(),
            RequiredModelDirectory: null,
            DatabaseName: "prem_" + suffix,
            OwnerRole: "owner_" + suffix,
            AppRole: "app_" + suffix,
            // A search role of its own too, so no test depends on another's
            // password for a role they would otherwise share on one server.
            SearchRole: "search_" + suffix,
            HostName: "search.example.test");
    }

    private static async Task<(SetupReport Report, string Output)> RunAsync(SetupOptions options)
    {
        var output = new StringWriter();
        var report = await new SetupEngine(options, output).RunAsync();
        return (report, output.ToString());
    }

    private static async Task<IdentityStore> StoreAsync(SetupOptions options)
    {
        var db = new PremagenticDatabase(CredentialsFile.ReadConnectionString(options.AppCredentialsPath));
        var tenantId = await db.EnsureTenantAsync(options.TenantKey, options.TenantName);
        return new IdentityStore(db, tenantId);
    }

    private static Func<string?> Once(string password, List<int> calls) => () =>
    {
        calls.Add(1);
        return password;
    };

    private static Func<string?> Never => () => throw new InvalidOperationException("the password was asked for");

    // --------------------------------------------------------------- versions

    [Theory]
    [InlineData(130022, "Failed")]
    [InlineData(139999, "Failed")]
    [InlineData(140000, "Warning")]
    [InlineData(160004, "Warning")]
    [InlineData(170011, "Checked")]
    [InlineData(180000, "Warning")]
    public void PostgreSQL_14_is_the_floor_and_17_the_tested_version(int serverVersionNum, string outcome)
    {
        Assert.Equal(Enum.Parse<StepOutcome>(outcome), SetupEngine.VersionOutcome(serverVersionNum));
    }

    // ------------------------------------------------------------ gss default

    [Fact]
    public async Task Both_credentials_files_turn_off_gss_encryption_unless_the_admin_connection_chose()
    {
        var quiet = NewOptions();
        var (report, output) = await RunAsync(quiet);
        Assert.False(report.Failed, output);
        foreach (var path in new[] { quiet.AppCredentialsPath, quiet.OwnerCredentialsPath })
            Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(CredentialsFile.ReadConnectionString(path)).GssEncryptionMode);

        var chosen = NewOptions(adminConnection: new NpgsqlConnectionStringBuilder(server.AdminConnectionString)
            { GssEncryptionMode = GssEncryptionMode.Prefer }.ConnectionString);
        (report, output) = await RunAsync(chosen);
        Assert.False(report.Failed, output);
        foreach (var path in new[] { chosen.AppCredentialsPath, chosen.OwnerCredentialsPath })
        {
            var connection = CredentialsFile.ReadConnectionString(path);
            Assert.Equal(GssEncryptionMode.Prefer, new NpgsqlConnectionStringBuilder(connection).GssEncryptionMode);
            Assert.Contains("GSS Encryption Mode=Prefer", connection);
        }
    }

    // ---------------------------------------------------------- administrator

    [Fact]
    public async Task The_first_administrator_is_made_once_and_its_password_is_never_printed()
    {
        const string password = "cafe\u0301 horse battery staple";
        var calls = new List<int>();
        var options = NewOptions() with { AdministratorName = "first-admin", AdministratorPassword = Once(password, calls) };

        var (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Applied, report.Find("administrator")!.Outcome);
        Assert.Single(calls);
        Assert.DoesNotContain(password, output);
        Assert.DoesNotContain("horse", output);
        var store = await StoreAsync(options);
        var admin = Assert.Single(await store.ListUsersAsync());
        Assert.Equal(("first-admin", Role.Administrator, false, true), (admin.SignInName, admin.Role, admin.Disabled, admin.HasPassword));

        // The password works, typed with the accent composed.
        await using (var db = new PremagenticDatabase(CredentialsFile.ReadConnectionString(options.AppCredentialsPath)))
        await using (var cmd = db.DataSource.CreateCommand("SELECT password_hash FROM prem_config.app_user WHERE sign_in_name = 'first-admin'"))
            Assert.True(new PasswordHasher().Verify("caf\u00E9 horse battery staple", (string)(await cmd.ExecuteScalarAsync())!));

        // Again, even naming someone else: an administrator exists, so nothing is
        // made and the password is not even asked for.
        (report, output) = await RunAsync(options with { AdministratorName = "second-admin", AdministratorPassword = Never });
        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Done, report.Find("administrator")!.Outcome);
        Assert.Contains("'second-admin' was not", report.Find("administrator")!.Detail);
        Assert.Single(await store.ListUsersAsync());
    }

    [Fact]
    public async Task Without_a_sign_in_name_no_account_is_made_and_setup_says_how_to_make_one()
    {
        var options = NewOptions() with { AdministratorPassword = Never };

        var (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        var step = report.Find("administrator")!;
        Assert.Equal(StepOutcome.Warning, step.Outcome);
        Assert.Contains("--admin-user", step.Detail);
        Assert.Empty(await (await StoreAsync(options)).ListUsersAsync());
    }

    [Fact]
    public async Task A_sign_in_name_that_belongs_to_a_member_is_refused_and_nothing_is_changed()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var store = await StoreAsync(options);
        await store.CreateUserAsync("taken", "Taken", Role.Member);

        (report, output) = await RunAsync(options with { AdministratorName = "TAKEN", AdministratorPassword = Never });

        Assert.True(report.Failed);
        Assert.Contains("already a user, as member", report.Find("administrator")!.Detail);
        var user = Assert.Single(await store.ListUsersAsync());
        Assert.Equal((Role.Member, false), (user.Role, user.HasPassword));
    }

    [Fact]
    public async Task An_administrator_left_without_a_password_by_an_earlier_run_gets_one()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var store = await StoreAsync(options);
        await store.CreateUserAsync("first-admin", "first-admin", Role.Administrator);

        var calls = new List<int>();
        (report, output) = await RunAsync(options with { AdministratorName = "first-admin", AdministratorPassword = Once("a long passphrase", calls) });

        Assert.False(report.Failed, output);
        Assert.Contains("which had none", report.Find("administrator")!.Detail);
        Assert.True(Assert.Single(await store.ListUsersAsync()).HasPassword);
    }

    [Fact]
    public async Task An_empty_password_stops_setup_and_makes_no_account()
    {
        var options = NewOptions() with { AdministratorName = "first-admin", AdministratorPassword = () => "" };

        var (report, _) = await RunAsync(options);

        Assert.True(report.Failed);
        Assert.Equal(StepOutcome.Failed, report.Find("administrator")!.Outcome);
        Assert.Empty(await (await StoreAsync(options)).ListUsersAsync());
    }

    // ------------------------------------------------------------------ https

    private static JsonNode Https(SetupOptions options) =>
        JsonNode.Parse(File.ReadAllText(options.KestrelSettingsPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!
            ["Kestrel"]!["Endpoints"]!["Https"]!;

    [Fact]
    public async Task Https_settings_and_certificate_are_private_name_the_host_and_keep_their_password_out_of_the_output()
    {
        var options = NewOptions() with { HttpsPort = 9443 };

        var (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Applied, report.Find("https")!.Outcome);
        Assert.True(CredentialsFile.IsPrivate(options.KestrelSettingsPath), "kestrel.json is readable by other accounts");
        Assert.True(CredentialsFile.IsPrivate(options.CertificatePath), "https.pfx is readable by other accounts");

        var https = Https(options);
        Assert.Equal("https://*:9443", https["Url"]!.GetValue<string>());
        Assert.Equal(InstallFiles.Certificate, https["Certificate"]!["Path"]!.GetValue<string>());
        var password = https["Certificate"]!["Password"]!.GetValue<string>();
        Assert.Equal(43, password.Length);
        Assert.DoesNotContain(password, output);

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, password);
        Assert.True(certificate.HasPrivateKey);
        Assert.True(HttpsCertificate.Covers(certificate, "search.example.test"));
        Assert.True(HttpsCertificate.Covers(certificate, "localhost"));
        Assert.False(HttpsCertificate.Covers(certificate, "other.example.test"));
        Assert.InRange((certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays, 729, 731);
        var usage = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal(["1.3.6.1.5.5.7.3.1"], usage.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
        Assert.False(Assert.Single(certificate.Extensions.OfType<X509BasicConstraintsExtension>()).CertificateAuthority);

        // The public half, for clients, is the same certificate and holds no key.
        using var published = X509Certificate2.CreateFromPem(File.ReadAllText(options.PublicCertificatePath));
        Assert.Equal(certificate.Thumbprint, published.Thumbprint);
        Assert.False(published.HasPrivateKey);

        // The control: a wrong password does not open the certificate, so the file is protected.
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, "wrong"));
    }

    [Fact]
    public async Task Running_again_keeps_the_certificate_and_a_new_host_name_gets_a_new_one()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var settings = File.ReadAllBytes(options.KestrelSettingsPath);
        var certificate = File.ReadAllBytes(options.CertificatePath);

        (report, output) = await RunAsync(options);
        Assert.False(report.Changed, output);
        Assert.Equal(StepOutcome.Done, report.Find("https")!.Outcome);
        Assert.Equal(settings, File.ReadAllBytes(options.KestrelSettingsPath));
        Assert.Equal(certificate, File.ReadAllBytes(options.CertificatePath));

        (report, output) = await RunAsync(options with { HostName = "renamed.example.test" });
        Assert.Equal(StepOutcome.Applied, report.Find("https")!.Outcome);
        Assert.Contains("does not name renamed.example.test", report.Find("https")!.Detail);
        using var renamed = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, Https(options)["Certificate"]!["Password"]!.GetValue<string>());
        Assert.True(HttpsCertificate.Covers(renamed, "renamed.example.test"));
    }

    [Fact]
    public async Task A_certificate_close_to_expiry_is_replaced()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);

        // Put in a certificate of setup's own kind that expires in ten days.
        var password = Https(options)["Certificate"]!["Password"]!.GetValue<string>();
        using (var old = HttpsCertificate.Create(options.HostName, DateTimeOffset.UtcNow - HttpsCertificate.Validity + TimeSpan.FromDays(10)))
            CredentialsFile.WritePrivate(options.CertificatePath, old.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password));

        (report, output) = await RunAsync(options);

        Assert.Equal(StepOutcome.Applied, report.Find("https")!.Outcome);
        Assert.Contains("expires", report.Find("https")!.Detail);
        using var renewed = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, Https(options)["Certificate"]!["Password"]!.GetValue<string>());
        Assert.True(renewed.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(700));
    }

    [Fact]
    public async Task A_certificate_setup_did_not_make_is_left_alone()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var own = """{ "Kestrel": { "Endpoints": { "Https": { "Url": "https://*:443", "Certificate": { "Path": "/etc/ssl/search.crt", "KeyPath": "/etc/ssl/search.key" } } } } }""";
        File.WriteAllText(options.KestrelSettingsPath, own);

        (report, output) = await RunAsync(options with { HttpsPort = 9443, HostName = "renamed.example.test" });

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Done, report.Find("https")!.Outcome);
        Assert.Contains("did not make", report.Find("https")!.Detail);
        Assert.Equal(own, File.ReadAllText(options.KestrelSettingsPath));
    }

    [Fact]
    public async Task A_plan_makes_no_certificate_and_never_asks_for_a_password()
    {
        var options = NewOptions() with { Plan = true, AdministratorName = "first-admin", AdministratorPassword = Never };

        var (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.WouldApply, report.Find("administrator")!.Outcome);
        Assert.Equal(StepOutcome.WouldApply, report.Find("https")!.Outcome);
        Assert.False(Directory.Exists(options.CredentialsDirectory));

        // On a finished install with no administrator yet, a plan still makes none.
        (report, output) = await RunAsync(options with { Plan = false, AdministratorName = null });
        Assert.False(report.Failed, output);
        File.Delete(options.KestrelSettingsPath);
        (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.WouldApply, report.Find("administrator")!.Outcome);
        Assert.Equal(StepOutcome.WouldApply, report.Find("https")!.Outcome);
        Assert.Empty(await (await StoreAsync(options)).ListUsersAsync());
        Assert.False(File.Exists(options.KestrelSettingsPath));
    }

    [Fact]
    public async Task A_bad_host_name_stops_setup_at_the_https_step()
    {
        var (report, _) = await RunAsync(NewOptions() with { HostName = "not a host" });

        Assert.True(report.Failed);
        Assert.Equal(StepOutcome.Failed, report.Find("https")!.Outcome);
    }

    // ------------------------------------------- the folder and what is in it

    [Fact]
    public async Task Setup_makes_its_folder_private_even_where_every_user_may_add_files()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows access rules; on Unix the folder is mode 700.

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "premagentic-install-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var options = NewOptions(folder);

        var (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Applied, report.Find("credentials.folder")!.Outcome);
        Assert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected);
        Assert.Null(InstallAccess.Untrusted(folder));
        // Found as it was left the next time.
        (report, output) = await RunAsync(options);
        Assert.Equal(StepOutcome.Done, report.Find("credentials.folder")!.Outcome);
    }

    [Fact]
    public async Task A_folder_other_accounts_may_add_files_to_stops_setup_before_anything_is_made()
    {
        if (!OperatingSystem.IsWindows()) return;

        var options = NewOptions();
        InstallAccess.CreatePrivateFolder(options.CredentialsDirectory);
        AccessRules.LetUsersWrite(options.CredentialsDirectory);

        var (report, output) = await RunAsync(options);

        Assert.True(report.Failed, output);
        var step = report.Find("credentials.folder")!;
        Assert.Equal(StepOutcome.Failed, step.Outcome);
        Assert.StartsWith($"{Path.GetFullPath(options.CredentialsDirectory)} can be changed by {AccessRules.UsersName}, ", step.Detail);
        // Never "move the folder aside": it can hold the bundled server's data.
        Assert.EndsWith($"; take that access away with the command on {InstallAccess.RecoveryPage}, from an elevated prompt, and run setup again.", step.Detail);
        Assert.DoesNotContain("aside", step.Detail);
        Assert.Null(report.Find("role.owner"));
        Assert.Empty(Directory.GetFiles(options.CredentialsDirectory));
    }

    [Fact]
    public async Task A_credentials_file_another_account_could_have_written_stops_setup_and_its_password_is_never_set()
    {
        if (!OperatingSystem.IsWindows()) return;

        var options = NewOptions();
        InstallAccess.CreatePrivateFolder(options.CredentialsDirectory);
        // Waiting for setup, with a password its writer knows, and changeable by every user.
        var planted = new NpgsqlConnectionStringBuilder(server.AdminConnectionString)
            { Database = options.DatabaseName, Username = options.AppRole, Password = "chosen by someone else" }.ConnectionString;
        await File.WriteAllTextAsync(options.AppCredentialsPath, $"role=application\nconnection={planted}\n");
        AccessRules.LetUsersWrite(options.AppCredentialsPath);

        var (report, output) = await RunAsync(options);

        Assert.True(report.Failed, output);
        var step = report.Find("credentials.app")!;
        Assert.Equal(StepOutcome.Failed, step.Outcome);
        Assert.StartsWith($"{options.AppCredentialsPath} can be changed by {AccessRules.UsersName}, ", step.Detail);
        Assert.EndsWith("; move it aside and run setup again.", step.Detail);
        Assert.Null(report.Find("role.app"));
    }

    [Fact]
    public async Task A_password_other_accounts_could_read_is_never_kept()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var old = CredentialsFile.ReadConnectionString(options.AppCredentialsPath);
        AccessRules.LetOthersRead(options.AppCredentialsPath);
        Assert.False(CredentialsFile.IsPrivate(options.AppCredentialsPath));

        (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        var step = report.Find("credentials.app")!;
        Assert.Equal(StepOutcome.Checked, step.Outcome);
        Assert.Equal($"other accounts could read the password in {options.AppCredentialsPath}, so {options.AppRole} gets a new one, written there once the role has it", step.Detail);
        var login = report.Find("login.app")!;
        Assert.Equal(StepOutcome.Applied, login.Outcome);
        Assert.Equal(
            $"set a new password on {options.AppRole}, since other accounts could read the old one, and wrote it to {options.AppCredentialsPath}, " +
            $"readable only by this account. Restart the API, and anything else that connects as {options.AppRole}, so it connects with the new password",
            login.Detail);
        var fresh = CredentialsFile.ReadConnectionString(options.AppCredentialsPath);
        var (oldPassword, freshPassword) = (new NpgsqlConnectionStringBuilder(old).Password!, new NpgsqlConnectionStringBuilder(fresh).Password!);
        Assert.NotEqual(oldPassword, freshPassword);
        Assert.DoesNotContain(freshPassword, output);
        Assert.True(CredentialsFile.IsPrivate(options.AppCredentialsPath));

        // The old password no longer logs in; the new one does, on the same
        // server a moment later. A server may refuse a password by ending the
        // connection rather than with 28P01, so the refusal is any failure to
        // connect, and the new password connecting is what tells it from an
        // unreachable server.
        await using (var refused = NpgsqlDataSource.Create(old))
        {
            var refusal = await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await refused.OpenConnectionAsync());
            if (refusal is PostgresException postgres) Assert.Equal("28P01", postgres.SqlState);
        }
        await using var accepted = NpgsqlDataSource.Create(fresh);
        await using (await accepted.OpenConnectionAsync()) { }
    }

    [Fact]
    public async Task A_refusal_the_server_ends_the_connection_on_is_a_refused_password_and_the_new_one_is_set()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        AccessRules.LetOthersRead(options.AppCredentialsPath);
        // A server that answers a refused password by ending the connection,
        // with no 28P01, as a PostgreSQL on Windows was seen to.
        await using var proxy = RefusalDroppingProxy.Start(server.AdminConnectionString);

        (report, output) = await RunAsync(options with { AdminConnectionString = proxy.ConnectionString });

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Applied, report.Find("login.app")!.Outcome);
        // The control: a refusal really reached setup as an ended connection,
        // whether the proxy dropped the server's error or the server ended the
        // connection first, so the test cannot pass on a plain 28P01.
        Assert.True(proxy.Dropped > 0, "no login through the proxy ended before authentication without an error");
    }

    [Fact]
    public async Task A_new_password_goes_to_the_file_only_once_the_role_has_it()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var before = await File.ReadAllBytesAsync(options.AppCredentialsPath);
        var old = CredentialsFile.ReadConnectionString(options.AppCredentialsPath);
        AccessRules.LetOthersRead(options.AppCredentialsPath);

        // An admin connection that may create databases and not roles, so the
        // ALTER ROLE that sets the new password is refused.
        var name = "nocreaterole_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        const string Password = "an invented password for a role that cannot alter roles";
        await using (var admin = NpgsqlDataSource.Create(server.AdminConnectionString))
        await using (var create = admin.CreateCommand($"CREATE ROLE {name} LOGIN CREATEDB PASSWORD '{Password}'"))
            await create.ExecuteNonQueryAsync();
        var limited = new NpgsqlConnectionStringBuilder(server.AdminConnectionString) { Username = name, Password = Password }.ConnectionString;

        (report, output) = await RunAsync(options with { AdminConnectionString = limited });

        Assert.True(report.Failed, output);
        Assert.Equal(StepOutcome.Checked, report.Find("credentials.app")!.Outcome);
        Assert.Null(report.Find("login.app"));
        // The file still holds the old password, which the role still has.
        Assert.Equal(before, await File.ReadAllBytesAsync(options.AppCredentialsPath));
        await using (var still = NpgsqlDataSource.Create(old))
        await using (await still.OpenConnectionAsync()) { }

        // With an admin that may, the next run sets the new one, then writes it.
        (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Applied, report.Find("login.app")!.Outcome);
        Assert.NotEqual(before, await File.ReadAllBytesAsync(options.AppCredentialsPath));
    }

    [Fact]
    public async Task Https_settings_that_set_more_than_the_kestrel_section_stop_setup_before_it_says_it_is_complete()
    {
        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        // Setup's own settings, with the header sign-in added: the file keeps its private rules.
        var settings = Https(options).Root.AsObject();
        settings["PREM_SIGN_IN_HEADER"] = "X-Remote-User";
        await File.WriteAllTextAsync(options.KestrelSettingsPath, settings.ToJsonString());

        (report, output) = await RunAsync(options);

        Assert.True(report.Failed, output);
        var step = report.Find("https")!;
        Assert.Equal(StepOutcome.Failed, step.Outcome);
        Assert.Equal(
            $"{options.KestrelSettingsPath} may hold only the Kestrel section, and it also sets PREM_SIGN_IN_HEADER; set that in the environment or in appsettings.json instead.",
            step.Detail);
        Assert.Null(report.Find("health"));
    }

    [Fact]
    public async Task A_credentials_folder_that_is_a_junction_stops_setup_whatever_it_points_to()
    {
        if (!OperatingSystem.IsWindows()) return;

        var options = NewOptions();
        var real = options.CredentialsDirectory + "-real";
        _folders.Add(real);
        InstallAccess.CreatePrivateFolder(real);
        Assert.Null(InstallAccess.Untrusted(real));
        AccessRules.Junction(options.CredentialsDirectory, real);

        var (report, output) = await RunAsync(options);

        Assert.True(report.Failed, output);
        var step = report.Find("credentials.folder")!;
        Assert.Equal(StepOutcome.Failed, step.Outcome);
        Assert.Equal(
            $"{Path.GetFullPath(options.CredentialsDirectory)} {InstallAccess.Link}; setup does not follow it: pass --credentials-dir with the folder itself, " +
            "or put the folder in place of the link, and run setup again.",
            step.Detail);
        Assert.Empty(Directory.GetFiles(real));
    }

    [Fact]
    public async Task Https_settings_another_account_could_have_written_stop_setup_rather_than_being_left_as_they_are()
    {
        if (!OperatingSystem.IsWindows()) return;

        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        // Settings naming a certificate setup did not make, which setup leaves
        // alone when they are the operator's, and which every user may change.
        await File.WriteAllTextAsync(options.KestrelSettingsPath,
            """{ "Kestrel": { "Endpoints": { "Https": { "Url": "https://*:443", "Certificate": { "Path": "C:/certs/other.pfx", "Password": "x" } } } } }""");
        AccessRules.LetUsersWrite(options.KestrelSettingsPath);

        (report, output) = await RunAsync(options);

        Assert.True(report.Failed, output);
        var step = report.Find("https")!;
        Assert.Equal(StepOutcome.Failed, step.Outcome);
        Assert.StartsWith($"{options.KestrelSettingsPath} can be changed by {AccessRules.UsersName}, ", step.Detail);
    }

    // -------------------------------------------------------- windows service

    // Windows only: the attribute skips it anywhere else, with the reason.
    [ServiceRegistrationFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Registering_the_windows_service_is_planned_and_without_elevation_refused_with_what_to_do()
    {
        // Outside this account's profile, where a service could reach it.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "premagentic-install-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var api = Path.Combine(Path.GetTempPath(), "premagentic install tests", "Premagentic.Api.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(api)!);
        File.WriteAllText(api, "not a program");
        _folders.Add(Path.GetDirectoryName(api)!);
        var options = NewOptions(folder) with { WindowsService = true, ApiExecutable = api };

        var (plan, planOutput) = await RunAsync(options with { Plan = true });
        Assert.False(plan.Failed, planOutput);
        var planned = plan.Find("service.windows")!;
        Assert.Equal(StepOutcome.WouldApply, planned.Outcome);
        Assert.Contains(@"NT SERVICE\Premagentic", planned.Detail);
        Assert.Contains("elevated", planned.Detail);

        var (report, output) = await RunAsync(options);
        Assert.True(report.Failed);
        Assert.Contains("Run as administrator", report.Find("service.windows")!.Detail);
        Assert.False(WindowsServiceRegistration.Exists());
        // Everything before it was done, and a later elevated run would skip it.
        Assert.Equal(StepOutcome.Applied, report.Find("https")!.Outcome);
        Assert.Null(report.Find("health"));
    }

    [Fact]
    public async Task The_service_may_keep_read_access_to_the_application_file_and_never_to_the_owner_file()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows access rules; a systemd unit uses LoadCredential instead.

        var options = NewOptions();
        var (report, output) = await RunAsync(options);
        Assert.False(report.Failed, output);
        var service = WindowsServiceRegistration.ServiceSid(WindowsServiceRegistration.ServiceName);
        CredentialsFile.GrantRead(options.AppCredentialsPath, service);
        CredentialsFile.GrantRead(options.OwnerCredentialsPath, service);

        (report, output) = await RunAsync(options);

        Assert.False(report.Failed, output);
        Assert.Equal(StepOutcome.Done, report.Find("credentials.app")!.Outcome);
        Assert.True(CredentialsFile.CanRead(options.AppCredentialsPath, service));
        // The service may read it, so its password is replaced: found here, set and written by the login step.
        Assert.Equal(StepOutcome.Checked, report.Find("credentials.owner")!.Outcome);
        Assert.Equal(StepOutcome.Applied, report.Find("login.owner")!.Outcome);
        Assert.False(CredentialsFile.CanRead(options.OwnerCredentialsPath, service));
    }

    [Fact]
    public async Task A_service_cannot_be_given_files_inside_a_user_profile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "premagentic-install-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var (report, _) = await RunAsync(NewOptions(profile) with { WindowsService = true, Plan = true, ApiExecutable = Environment.ProcessPath });

        Assert.True(report.Failed);
        Assert.Contains("profile", report.Find("service.windows")!.Detail);
    }
}

/// <summary>
/// The PostgreSQL that <see cref="SetupInstallTests"/> installs into as its
/// superuser. When <c>PREM_TEST_ADMIN_CONNECTION</c> holds a connection string,
/// that server is used and no container is started: a Windows runner cannot run
/// the Linux container, and its image carries a PostgreSQL 17 of its own, which
/// the Windows CI job starts and names here. Point it only at a throwaway server
/// whose superuser the connection names: every test makes a database and roles
/// on it and leaves them there. When the variable is unset, a stock container
/// is started as for every other datastore test, so a local run is unchanged.
/// </summary>
public sealed class SetupTestServer : IAsyncLifetime
{
    public const string Variable = "PREM_TEST_ADMIN_CONNECTION";

    private readonly string? _external;
    private PostgreSqlContainer? _container;

    public SetupTestServer() : this(Environment.GetEnvironmentVariable(Variable)) { }

    internal SetupTestServer(string? variable) =>
        _external = string.IsNullOrWhiteSpace(variable) ? null : variable.Trim();

    /// <summary>True when the server is the one the variable names, not a container.</summary>
    public bool IsExternal => _external is not null;

    public Task InitializeAsync()
    {
        if (_external is not null) return Task.CompletedTask;
        _container = new PostgreSqlBuilder(DatastoreTestDatabase.Image).Build();
        return _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>The server's superuser, for tests that create roles and databases themselves.</summary>
    public string AdminConnectionString =>
        _external ?? _container?.GetConnectionString() ?? throw new InvalidOperationException("the server has not been started");
}

/// <summary>
/// <see cref="SetupTestServer"/>'s choice: the server the variable names when it
/// names one, and a container only when it does not.
/// </summary>
public sealed class SetupTestServerTests
{
    [Fact]
    public async Task A_connection_in_the_variable_is_used_as_given_and_no_container_is_started()
    {
        // No such server exists; a fixture that started a container instead would
        // hand out the container's connection, and one that tried to reach this
        // name would not be what InitializeAsync does for an external server.
        const string external = "Host=db.example.test;Port=5432;Username=postgres;Password=unused;Database=postgres";
        var server = new SetupTestServer("  " + external + "\n");

        await server.InitializeAsync();
        try
        {
            Assert.True(server.IsExternal);
            Assert.Equal(external, server.AdminConnectionString);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_connection_in_the_variable_a_container_is_used(string? variable) =>
        Assert.False(new SetupTestServer(variable).IsExternal);
}

/// <summary>
/// A test of registering the Windows service, skipped with its reason where it
/// must not or cannot run: off Windows, in an elevated process (a test never
/// registers a real service, and setup would from there), or where a real
/// PremAgentic service already exists. The results then say why it did not
/// run, where an early return would count as a pass.
/// </summary>
public sealed class ServiceRegistrationFactAttribute : FactAttribute
{
    public const string NotWindows = "Registering a Windows service exists only on Windows.";
    public const string Elevated = "This process is elevated, and a test never registers a real service, which setup would do from here.";
    public const string ServiceExists = "A real PremAgentic service exists on this machine, and the test leaves it alone.";

    public ServiceRegistrationFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = NotWindows;
        else if (WindowsServiceRegistration.IsElevated()) Skip = Elevated;
        else if (WindowsServiceRegistration.Exists()) Skip = ServiceExists;
    }
}
