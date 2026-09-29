using Premagentic.Api;
using Premagentic.Api.Hosting;
using Premagentic.Cli.Setup;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Premagentic.Tests;

/// <summary>
/// The credentials file format and how a process finds its connection. No
/// database needed. The resolution is tested through its inputs rather than by
/// setting environment variables, which every test in the run would share.
/// </summary>
public sealed class SetupCredentialsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "premagentic-credentials-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_written_file_reads_back_and_is_private()
    {
        var path = Path.Combine(_folder, "app.credentials");
        const string connection = "Host=db;Port=5432;Database=premagentic;Username=premagentic_app;Password=abc=def;SSL Mode=Require";
        CredentialsFile.Write(path, connection, "application");

        Assert.Equal(connection, CredentialsFile.ReadConnectionString(path));
        Assert.True(CredentialsFile.IsPrivate(path));
        Assert.Contains("role=application", File.ReadAllText(path));

        // Replacing it keeps it private and leaves no temporary file behind.
        CredentialsFile.Write(path, connection + ";Timeout=30", "application");
        Assert.EndsWith("Timeout=30", CredentialsFile.ReadConnectionString(path));
        Assert.True(CredentialsFile.IsPrivate(path));
        Assert.Equal(new[] { "app.credentials" }, Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void Comments_blank_lines_and_unknown_keys_are_ignored()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "hand-written.credentials");
        File.WriteAllText(path, "# written by hand\n\nrole=owner\nfuture_key=1\nconnection = Host=x;Username=y\n");
        Assert.Equal("Host=x;Username=y", CredentialsFile.ReadConnectionString(path));
    }

    [Fact]
    public void A_file_without_a_connection_line_is_refused_without_quoting_it()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "broken.credentials");
        File.WriteAllText(path, "role=application\nPassword=do-not-echo-me\n");
        var ex = Assert.Throws<InvalidDataException>(() => CredentialsFile.ReadConnectionString(path));
        Assert.DoesNotContain("do-not-echo-me", ex.Message);
        Assert.Throws<FileNotFoundException>(() => CredentialsFile.ReadConnectionString(Path.Combine(_folder, "missing")));
    }

    [Fact]
    public void The_credentials_file_wins_over_the_development_default_and_both_sources_are_refused()
    {
        var path = Path.Combine(_folder, "app.credentials");
        CredentialsFile.Write(path, "Host=installed;Username=premagentic_app;Password=p", "application");

        foreach (var development in new[] { false, true })
        {
            Assert.Equal("Host=installed;Username=premagentic_app;Password=p", PremagenticDatabase.ResolveConnectionString(null, path, development));
            Assert.Equal("Host=explicit", PremagenticDatabase.ResolveConnectionString("Host=explicit", null, development));
            Assert.Throws<StartupRefusedException>(() => PremagenticDatabase.ResolveConnectionString("Host=explicit", path, development));
            // A named file that is missing fails closed rather than falling back.
            Assert.IsType<FileNotFoundException>(Assert.Throws<StartupRefusedException>(
                () => PremagenticDatabase.ResolveConnectionString(null, Path.Combine(_folder, "missing"), development)).InnerException);
        }
    }

    [Fact]
    public void With_nothing_configured_a_host_refuses_to_start_unless_the_development_switch_is_set()
    {
        var refused = Assert.Throws<StartupRefusedException>(() => PremagenticDatabase.ResolveConnectionString(null, null, development: false));
        Assert.Contains("PREM_CREDENTIALS_FILE", refused.Message);
        Assert.Contains("PREM_CONNECTION_STRING", refused.Message);
        Assert.Contains($"{PremagenticDatabase.DevelopmentSwitch}=1", refused.Message);
        // Empty is the same as unset.
        Assert.Throws<StartupRefusedException>(() => PremagenticDatabase.ResolveConnectionString("", "", development: false));

        Assert.Equal(PremagenticDatabase.DevelopmentConnectionString, PremagenticDatabase.ResolveConnectionString(null, null, development: true));
        Assert.Contains("Port=5434", PremagenticDatabase.DevelopmentConnectionString);
    }

    [Fact]
    public void Relative_certificate_paths_in_the_kestrel_settings_are_relative_to_the_settings_file()
    {
        var folder = Path.GetFullPath(Path.Combine(_folder, "install"));
        var absolute = Path.GetFullPath(Path.Combine(_folder, "elsewhere", "own.pfx"));
        var overrides = InstallFiles.ResolveRelativePaths(
        [
            new("Kestrel:Endpoints:Https:Certificate:Path", "https.pfx"),
            new("Kestrel:Endpoints:Https:Certificate:KeyPath", "keys/https.key"),
            new("Kestrel:Endpoints:Https:Certificate:Password", "not-a-path"),
            new("Kestrel:Endpoints:Https:Url", "https://*:8443"),
            new("Kestrel:Certificates:Default:Path", absolute),
            new("Logging:File:Path", "log.txt"),
        ], folder).ToDictionary(o => o.Key, o => o.Value);

        Assert.Equal(2, overrides.Count);
        Assert.Equal(Path.Combine(folder, "https.pfx"), overrides["Kestrel:Endpoints:Https:Certificate:Path"]);
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "keys", "https.key")), overrides["Kestrel:Endpoints:Https:Certificate:KeyPath"]);
    }

    [Fact]
    public void The_kestrel_settings_are_found_only_beside_a_named_credentials_file()
    {
        var credentials = Path.Combine(_folder, InstallFiles.AppCredentials);
        Assert.Null(InstallFiles.KestrelSettingsBeside(null));
        Assert.Null(InstallFiles.KestrelSettingsBeside(credentials));

        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, InstallFiles.KestrelSettings), "{}");
        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, InstallFiles.KestrelSettings)), InstallFiles.KestrelSettingsBeside(credentials));
    }

    [Theory]
    [InlineData("search.example.test", true)]
    [InlineData("localhost", true)]
    [InlineData("SEARCH-01", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("::1", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("-leading.example", false)]
    [InlineData("name,with,commas", false)]
    [InlineData("cn=injected", false)]
    public void Only_a_dns_name_or_an_ip_address_is_taken_as_the_host_name(string name, bool valid)
    {
        Assert.Equal(valid, HttpsCertificate.IsValidHostName(name));
    }

    [Fact]
    public void The_service_account_can_be_given_read_access_and_the_file_stays_private_otherwise()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows access rules; a systemd unit uses LoadCredential instead.

        var path = Path.Combine(_folder, "app.credentials");
        CredentialsFile.Write(path, "Host=x;Username=y;Password=z", "application");
        var service = WindowsServiceRegistration.ServiceSid(WindowsServiceRegistration.ServiceName);
        Assert.False(CredentialsFile.CanRead(path, service));

        CredentialsFile.GrantRead(path, service);

        Assert.True(CredentialsFile.CanRead(path, service));
        Assert.True(CredentialsFile.IsPrivate(path, readerSid: service));
        // The control: without naming the service, the same file is not private.
        Assert.False(CredentialsFile.IsPrivate(path));
        // And another account's read rule is not excused by naming the service.
        CredentialsFile.GrantRead(path, WindowsServiceRegistration.ServiceSid("SomeOtherService"));
        Assert.False(CredentialsFile.IsPrivate(path, readerSid: service));

        // Nor is more than reading, for the service itself.
        var writable = Path.Combine(_folder, "writable.credentials");
        CredentialsFile.Write(writable, "Host=x;Username=y;Password=z", "application");
        CredentialsFile.GrantRead(writable, service);
        Assert.True(CredentialsFile.IsPrivate(writable, readerSid: service));
        AllowWrite(writable, service);
        Assert.False(CredentialsFile.IsPrivate(writable, readerSid: service));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AllowWrite(string path, string sid)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(sid), System.Security.AccessControl.FileSystemRights.Write,
            System.Security.AccessControl.AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    [Theory]
    [InlineData("Premagentic", "S-1-5-80-2960621140-3341259486-2101374570-3505031405-2423110247")]
    [InlineData("Some Other", "S-1-5-80-1871638465-1131891415-1870888232-4153362726-75793499")]
    public void The_service_sid_is_the_one_windows_derives_from_the_name(string name, string sid)
    {
        // Pinned from `sc.exe showsid <name>`, which answers for any name, installed or not.
        Assert.Equal(sid, WindowsServiceRegistration.ServiceSid(name));
    }

    [Fact]
    public void The_service_program_path_is_stored_quoted_and_the_account_is_virtual()
    {
        var create = WindowsServiceRegistration.CreateCommands(@"C:\Program Files\Premagentic\Premagentic.Api.exe")[0];

        Assert.Equal("create", create[0]);
        Assert.Equal("\"C:\\Program Files\\Premagentic\\Premagentic.Api.exe\"", create[Array.IndexOf(create, "binPath=") + 1]);
        Assert.Equal(@"NT SERVICE\Premagentic", create[Array.IndexOf(create, "obj=") + 1]);
        Assert.DoesNotContain("password=", create);
        Assert.Equal(["PREM_CREDENTIALS_FILE=C:\\ProgramData\\Premagentic\\app.credentials"],
            WindowsServiceRegistration.ServiceEnvironment(@"C:\ProgramData\Premagentic\app.credentials"));
    }

    // ------------------------------------------------ who may own and change the folder

    [Fact]
    public void A_folder_other_accounts_may_add_files_to_is_untrusted_and_one_made_private_is_not()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows access rules; on Unix the folder is mode 700.

        var folder = Path.Combine(_folder, "made");
        InstallAccess.CreatePrivateFolder(folder);
        Assert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected);
        Assert.Null(InstallAccess.Untrusted(folder));

        AccessRules.LetUsersWrite(folder);

        Assert.Equal(
            $"can be changed by {AccessRules.UsersName}, not only by administrators, SYSTEM and {AccessRules.ThisAccountName}, " +
            "so another account could have written what is in it",
            InstallAccess.Untrusted(folder));
    }

    [Fact]
    public void A_folder_or_file_another_account_owns_is_untrusted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var folder = Path.Combine(_folder, "owned");
        InstallAccess.CreatePrivateFolder(folder);
        var file = Path.Combine(folder, InstallFiles.AppCredentials);
        CredentialsFile.Write(file, "Host=x;Username=y;Password=z", "application");
        AccessRules.OwnedByThisAccount(folder);
        AccessRules.OwnedByThisAccount(file);

        // Judged as another account judges them, they are someone else's. The
        // owner is named first: this account may also change them, which is
        // what the owner check would otherwise leave to say.
        Assert.StartsWith("is owned by ", InstallAccess.Untrusted(folder, AccessRules.AnotherAccount));
        Assert.StartsWith("is owned by ", InstallAccess.Untrusted(file, AccessRules.AnotherAccount));
        // A real folder this account did not make: the system folder is TrustedInstaller's.
        Assert.StartsWith("is owned by ", InstallAccess.Untrusted(Environment.SystemDirectory));
        // The control: judged by this account, which owns them, they are sound.
        Assert.Null(InstallAccess.Untrusted(folder));
        Assert.Null(InstallAccess.Untrusted(file));
    }

    [Fact]
    public void A_credentials_file_another_account_owns_is_not_private_whatever_its_rules_say()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_folder, InstallFiles.AppCredentials);
        CredentialsFile.Write(path, "Host=x;Username=y;Password=z", "application");
        AccessRules.OwnedByThisAccount(path);
        // One rule, for the other account, inheritance off: by its rules alone
        // the file is that account's, private. Its owner is this one, which that
        // account cannot vouch for.
        AccessRules.OnlyFor(path, AccessRules.AnotherAccount);

        Assert.False(CredentialsFile.IsPrivate(path, readerSid: null, account: AccessRules.AnotherAccount));
    }

    [Fact]
    public void A_folder_made_in_program_data_does_not_take_its_rules()
    {
        if (!OperatingSystem.IsWindows()) return;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var ordinary = Path.Combine(programData, "premagentic-credentials-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var made = ordinary + "-made";
        var written = ordinary + "-written";
        try
        {
            // The control: made the ordinary way, it lets every user add files.
            Directory.CreateDirectory(ordinary);
            Assert.Contains($"can be changed by {AccessRules.UsersName}", InstallAccess.Untrusted(ordinary));

            InstallAccess.CreatePrivateFolder(made);
            Assert.Null(InstallAccess.Untrusted(made));

            // A secret written to a folder that does not exist yet makes it the same way.
            CredentialsFile.Write(Path.Combine(written, InstallFiles.AppCredentials), "Host=x;Username=y;Password=z", "application");
            Assert.Null(InstallAccess.Untrusted(written));
        }
        finally
        {
            foreach (var folder in new[] { ordinary, made, written })
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_folder_another_account_made_first_is_found_when_it_is_made_not_taken_as_made()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Made the ordinary way in C:\ProgramData, as another account's
        // process could make it in the moment between setup's look and its
        // make; making a folder that is there already does nothing.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "premagentic-credentials-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);

            var refused = Assert.Throws<IOException>(() => InstallAccess.CreatePrivateFolder(folder));

            Assert.StartsWith($"{folder} is not the private folder just made there: it can be changed by {AccessRules.UsersName}, ", refused.Message);
            Assert.EndsWith("so another account made or changed it first; remove it, check what else that account could have left, and try again.", refused.Message);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_secret_another_account_changes_as_it_is_written_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_folder, InstallFiles.AppCredentials);
        // Between the move into place and the read back, as another account's
        // process could act if it could reach the folder.
        CredentialsFile.AfterMoveForTests.Value = AccessRules.LetUsersWrite;
        try
        {
            var refused = Assert.Throws<IOException>(() => CredentialsFile.Write(path, "Host=x;Username=y;Password=z", "application"));
            Assert.StartsWith($"{path} is not the private file just made there: it can be changed by {AccessRules.UsersName}, ", refused.Message);
        }
        finally
        {
            CredentialsFile.AfterMoveForTests.Value = null;
        }
        // The control: the same write, with nobody acting, is taken.
        CredentialsFile.Write(path, "Host=x;Username=y;Password=z", "application");
        Assert.True(CredentialsFile.IsPrivate(path));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_junction_is_refused_whatever_it_points_to_and_no_rule_is_set_through_it()
    {
        if (!OperatingSystem.IsWindows()) return;

        var real = Path.Combine(_folder, "real");
        InstallAccess.CreatePrivateFolder(real);
        var credentials = Path.Combine(real, InstallFiles.AppCredentials);
        CredentialsFile.Write(credentials, "Host=x;Username=y;Password=z", "application");
        var link = Path.Combine(_folder, "link");
        AccessRules.Junction(link, real);
        try
        {
            RefusedThroughTheJunction(real, link);
        }
        finally
        {
            // The link alone: a recursive delete of the folder above would try
            // to unmount it, which only an elevated process may do.
            Directory.Delete(link);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RefusedThroughTheJunction(string real, string link)
    {
        // The control: what it points to is sound.
        Assert.Null(InstallAccess.Untrusted(real));

        Assert.Equal(new AccessFinding(AccessProblem.Link, InstallAccess.Link), InstallAccess.Examine(link));
        Assert.Equal(
            $"{link} {InstallAccess.Link}; point PREM_CREDENTIALS_FILE at the folder itself, or put the folder or file in place of the link, and run prem setup again.",
            InstallAccess.HostRefusal(Path.Combine(link, InstallFiles.AppCredentials)));

        var before = new DirectoryInfo(real).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner);
        var refused = Assert.Throws<IOException>(() => InstallAccess.GiveToAdministrators(
            link, [], WindowsServiceRegistration.ServiceSid(WindowsServiceRegistration.ServiceName)));
        Assert.StartsWith($"{link} {InstallAccess.Link}; no access rule is set through it.", refused.Message);
        Assert.Equal(before, new DirectoryInfo(real).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner));
    }

    [ElevatedWindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_superuser_file_given_to_administrators_is_trusted_and_private_to_a_second_administrator()
    {
        var path = Path.Combine(_folder, InstallFiles.SuperuserCredentials);
        CredentialsFile.Write(path, "Host=localhost;Username=premagentic_admin;Password=z", "superuser");
        // The control: before, another account finds this account's file untrusted.
        Assert.NotNull(InstallAccess.Untrusted(path, AccessRules.AnotherAccount));

        InstallAccess.GiveFileToAdministrators(path);

        Assert.True(InstallAccess.IsAdministratorsOwn(path));
        Assert.Null(InstallAccess.Untrusted(path, AccessRules.AnotherAccount));
        Assert.True(CredentialsFile.IsPrivate(path, InstallAccess.AdministratorsSid, account: AccessRules.AnotherAccount));
        // And read through the administrators' rule, as a second administrator's setup run reads it.
        Assert.StartsWith("Host=localhost", CredentialsFile.ReadConnectionString(path));
    }

    [ElevatedWindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void The_folder_and_files_a_service_reads_become_the_administrators_own()
    {
        // As a setup run from a prompt that was not elevated leaves them: this account's own.
        var folder = Path.Combine(_folder, "service");
        InstallAccess.CreatePrivateFolder(folder);
        var files = new[] { InstallFiles.AppCredentials, InstallFiles.SearchCredentials }.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var file in files) CredentialsFile.Write(file, "Host=x;Username=y;Password=z", "application");
        var service = WindowsServiceRegistration.ServiceSid(WindowsServiceRegistration.ServiceName);
        // The control: the service would refuse them, since this account may change them.
        Assert.NotNull(InstallAccess.Untrusted(folder, service));

        InstallAccess.GiveToAdministrators(folder, files, service);

        Assert.Null(InstallAccess.Untrusted(folder, service));
        foreach (var file in files)
        {
            Assert.Null(InstallAccess.Untrusted(file, service));
            Assert.True(CredentialsFile.CanRead(file, service));
            // And setup, run again, still finds its password private and keeps it.
            Assert.True(CredentialsFile.IsPrivate(file, service));
        }
    }

    // --------------------------------------------------------- the API's start

    private static WebApplicationFactory<ApiSettings> ApiHost(string credentialsFile) =>
        new WebApplicationFactory<ApiSettings>().WithWebHostBuilder(web =>
        {
            foreach (var key in new[] { "PREM_CONNECTION_STRING", PremagenticDatabase.DevelopmentSwitch, ApiSettings.SignInHeaderKey })
                web.UseSetting(key, "");
            web.UseSetting("PREM_CREDENTIALS_FILE", credentialsFile);
            // Nothing past the checks may reach a real server: a port nothing listens on.
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<PremagenticDatabase>();
                services.AddSingleton(_ => new PremagenticDatabase("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"));
                services.RemoveAll<IEmbeddingProvider>();
                services.AddSingleton<IEmbeddingProvider>(new SeededEmbeddingProvider());
            });
        });

    private static StartupRefusedException Refusal(Action start)
    {
        var thrown = Record.Exception(start);
        for (var e = thrown; e is not null; e = e.InnerException)
            if (e is StartupRefusedException refused) return refused;
        throw new Xunit.Sdk.XunitException($"expected a startup refusal, got {thrown?.GetType().Name ?? "no exception"}: {thrown?.Message}");
    }

    /// <summary>An install folder as setup makes it, with an application credentials file in it.</summary>
    private string Install()
    {
        var folder = Path.Combine(_folder, "install");
        InstallAccess.CreatePrivateFolder(folder);
        var credentials = Path.Combine(folder, InstallFiles.AppCredentials);
        CredentialsFile.Write(credentials, "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none", "application");
        return credentials;
    }

    [Fact]
    public void The_api_refuses_to_start_on_an_install_folder_other_accounts_may_add_files_to()
    {
        if (!OperatingSystem.IsWindows()) return;

        var credentials = Install();
        var folder = Path.GetDirectoryName(credentials)!;
        // The control: as setup made it, there is nothing to refuse.
        Assert.Null(InstallAccess.HostRefusal(credentials));
        AccessRules.LetUsersWrite(folder);

        using var api = ApiHost(credentials);
        var refused = Refusal(() => _ = api.Server);

        Assert.StartsWith($"{folder} can be changed by {AccessRules.UsersName}, ", refused.Message);
        Assert.EndsWith($"; take that access away with the command on {InstallAccess.RecoveryPage}, from an elevated prompt, and run prem setup again.", refused.Message);
    }

    [Fact]
    public void The_api_refuses_https_settings_that_set_anything_but_kestrel()
    {
        var credentials = Install();
        var settings = Path.Combine(Path.GetDirectoryName(credentials)!, InstallFiles.KestrelSettings);
        CredentialsFile.WritePrivate(settings, """{ "Kestrel": { "Limits": { "MaxConcurrentConnections": 100 } }, "PREM_SIGN_IN_HEADER": "X-Remote-User" }"""u8);

        using var api = ApiHost(credentials);
        var refused = Refusal(() => _ = api.Server);

        Assert.Equal(
            $"{settings} may hold only the Kestrel section, and it also sets PREM_SIGN_IN_HEADER; set that in the environment or in appsettings.json instead.",
            refused.Message);
        // The control: the Kestrel section alone is taken.
        Assert.Null(PremagenticHosting.KestrelSettingsRefusal(settings, ["Kestrel"]));
    }

    [Fact]
    public void Generated_passwords_are_long_random_and_safe_in_a_connection_string()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => Secrets.NewPassword()).ToArray();
        Assert.Equal(50, passwords.Distinct().Count());
        Assert.All(passwords, p => Assert.Matches("^[A-Za-z0-9_-]{43}$", p));
    }

    [Fact]
    public void The_scram_verifier_has_the_form_the_server_stores_and_never_contains_the_password()
    {
        var password = Secrets.NewPassword();
        var verifier = Secrets.ScramSha256Verifier(password);
        Assert.Matches(@"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+$", verifier);
        Assert.DoesNotContain(password, verifier);
        // A fresh salt every time, so two verifiers for one password differ.
        Assert.NotEqual(verifier, Secrets.ScramSha256Verifier(password));
    }
}
