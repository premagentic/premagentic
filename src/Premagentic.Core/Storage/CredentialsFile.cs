using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Premagentic.Core.Storage;

/// <summary>
/// The file that holds one database role's connection settings, password
/// included, so the password never has to travel through a command line, an
/// environment variable, the console or a log. A process is pointed at it with
/// <c>PREM_CREDENTIALS_FILE</c>.
/// <para>
/// Format: UTF-8 text, one <c>key=value</c> per line, and a line starting with
/// <c>#</c> is a comment. The one required key is <c>connection</c>, an Npgsql
/// connection string. <c>role</c> names which role the file is for, for the
/// operator's benefit. Unknown keys are ignored, so later versions can add to
/// the file without breaking earlier readers.
/// </para>
/// <para>
/// Access: the account that wrote it and nobody else. On Unix the file is mode
/// 600 and its folder 700; on Windows the file has inheritance removed and one
/// full-control rule for the current user. Both are set as the file is created,
/// so there is no moment when anyone else can read it. Granting a service
/// account read access belongs to whoever installs that service.
/// </para>
/// </summary>
public static class CredentialsFile
{
    public const string ConnectionKey = "connection";
    public const string RoleKey = "role";

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file has no <c>connection</c> line.</exception>
    public static string ReadConnectionString(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Credentials file '{path}' does not exist.", path);

        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            if (line[..separator].Trim() == ConnectionKey)
                return line[(separator + 1)..].Trim();
        }
        // The file's content is never quoted in an error: it holds a password.
        throw new InvalidDataException($"Credentials file '{path}' has no '{ConnectionKey}=' line.");
    }

    /// <summary>
    /// Writes the file, replacing any file already at <paramref name="path"/>. The
    /// content goes to a new private file beside it first and is then moved into
    /// place, so a reader never sees half a file.
    /// </summary>
    public static void Write(string path, string connectionString, string role)
    {
        var content = Encoding.UTF8.GetBytes(
            "# Premagentic database credentials, written by prem setup.\n" +
            "# Readable only by the account that wrote it. Never copy it into an environment variable or a log.\n" +
            $"{RoleKey}={role}\n" +
            $"{ConnectionKey}={connectionString}\n");
        try
        {
            WritePrivate(path, content);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>
    /// Writes any secret file the same way as a credentials file: private from
    /// the moment it exists, through a temporary file and a move, replacing any
    /// file already at <paramref name="path"/>. The folder is created private
    /// when it does not exist (<see cref="InstallAccess.CreatePrivateFolder"/>),
    /// so it never takes the rules of the folder above it, which on Windows
    /// may let other accounts put files in it. On Windows the file is read back
    /// once it is in place, as the folder is, and anything but the private file
    /// just written is refused.
    /// </summary>
    /// <exception cref="IOException">What is at <paramref name="path"/> once written is not the private file.</exception>
    public static void WritePrivate(string path, ReadOnlySpan<byte> content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(directory)) InstallAccess.CreatePrivateFolder(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}.tmp");
        try
        {
            using (var stream = CreatePrivate(temporary))
                stream.Write(content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        AfterMoveForTests.Value?.Invoke(path);
        if (OperatingSystem.IsWindows()) InstallAccess.RequireMadePrivate(path, "file");
    }

    /// <summary>
    /// Tests only, and only for the calling flow: runs between the move and
    /// the read back, where another account's process could act.
    /// </summary>
    internal static readonly AsyncLocal<Action<string>?> AfterMoveForTests = new();

    /// <summary>
    /// True when only the current account can read the file: mode 600 or stricter
    /// on Unix; on Windows, owned by this account, administrators or SYSTEM, with
    /// no inherited rules and no allow rule for anyone else.
    /// <paramref name="readerSid"/> names one more account that may hold a
    /// read-only rule on Windows, such as the service a setup run registered.
    /// </summary>
    public static bool IsPrivate(string path, string? readerSid = null) => IsPrivate(path, readerSid, account: null);

    /// <summary>
    /// <see cref="IsPrivate(string, string?)"/> as <paramref name="account"/>
    /// would judge it, by security identifier, rather than the account this
    /// process runs as. Windows only; ignored on Unix.
    /// </summary>
    internal static bool IsPrivate(string path, string? readerSid, string? account)
    {
        if (!OperatingSystem.IsWindows())
            return (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) == 0;

        return IsPrivateOnWindows(path, readerSid, account);
    }

    /// <summary>
    /// Gives one Windows account read access to a private file, and nothing more.
    /// Used for the service account that runs the application. On Unix this does
    /// nothing: a systemd unit hands the service its files with LoadCredential.
    /// </summary>
    public static void GrantRead(string path, string readerSid)
    {
        if (OperatingSystem.IsWindows()) GrantReadOnWindows(path, readerSid);
    }

    /// <summary>True when <paramref name="readerSid"/> holds an allow rule on the file. Always false on Unix.</summary>
    public static bool CanRead(string path, string readerSid) =>
        OperatingSystem.IsWindows() && HasReadRuleOnWindows(path, readerSid);

    private static FileStream CreatePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });

        return CreatePrivateOnWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreatePrivateOnWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows account has no security identifier.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(
            FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemRights ReadOnly => FileSystemRights.Read | FileSystemRights.Synchronize;

    [SupportedOSPlatform("windows")]
    private static bool IsPrivateOnWindows(string path, string? readerSid, string? account)
    {
        var user = account is null ? WindowsIdentity.GetCurrent().User! : new SecurityIdentifier(account);
        var security = new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        // A file another account owns is one it may have put here, and its
        // owner may change its rules at any time, whatever they say now.
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !InstallAccess.IsTrusted(owner, user)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited) return false;
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(user)) continue;
            // The one other account allowed: read, and nothing that could change the file or its rules.
            if (readerSid is null || rule.IdentityReference.Value != readerSid || (rule.FileSystemRights & ~ReadOnly) != 0) return false;
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void GrantReadOnWindows(string path, string readerSid)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(readerSid), ReadOnly, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasReadRuleOnWindows(string path, string readerSid)
    {
        foreach (FileSystemAccessRule rule in new FileInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value == readerSid
                && (rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                return true;
        return false;
    }
}
