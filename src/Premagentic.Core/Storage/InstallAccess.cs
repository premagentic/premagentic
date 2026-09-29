using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Premagentic.Core.Storage;

/// <summary>What is wrong with a folder or file <see cref="InstallAccess"/> examined.</summary>
public enum AccessProblem
{
    /// <summary>A junction or a symbolic link: its rules and what it holds are another folder's or file's.</summary>
    Link,

    /// <summary>This process may not read its rules, so nothing about it can be checked.</summary>
    Unreadable,

    /// <summary>Owned by an account other than administrators, SYSTEM and the one trusted.</summary>
    Owner,

    /// <summary>Another account may change it.</summary>
    Writers,
}

/// <summary>A folder or file <see cref="InstallAccess.Examine"/> refused: the kind of problem, and the clause that says it, worded to follow the path in a sentence.</summary>
public sealed record AccessFinding(AccessProblem Problem, string Clause);

/// <summary>
/// Who may own and change the folder <c>prem setup</c> writes into, and the
/// files in it that setup and the API read. On Windows: administrators,
/// SYSTEM and the account the process runs as, and nobody else. A folder
/// another account can write to may hold a file that account put there before
/// setup ran, such as a credentials file whose password it knows or HTTPS
/// settings of its own, so setup and the API refuse such a folder or file
/// rather than read what is in it. A junction or a symbolic link is refused
/// whatever it points to, before its rules are read: they would be its
/// target's.
/// <para>
/// On Unix nothing is checked here: setup makes the folder mode 700 and each
/// file in it mode 600, and a systemd unit hands the service its own copies
/// with LoadCredential.
/// </para>
/// </summary>
public static class InstallAccess
{
    /// <summary>BUILTIN\Administrators.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>NT AUTHORITY\SYSTEM.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>The manual's page with the commands that put an earlier install's folder right, as a refusal names it.</summary>
    public const string RecoveryPage = "the manual's Upgrading and removing page (docs/upgrading-and-removing.md)";

    // Placeholders rather than accounts: CREATOR OWNER stands for whoever makes
    // a child, and OWNER RIGHTS for the owner, which is checked on its own.
    private const string CreatorOwnerSid = "S-1-3-0";
    private const string OwnerRightsSid = "S-1-3-4";

    // Every right that changes a folder or a file, what is in it, who may reach
    // it or who owns it, with the generic rights a rule for later children
    // carries (GENERIC_ALL and GENERIC_WRITE).
    [SupportedOSPlatform("windows")]
    private const FileSystemRights Changes =
        FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;

    [SupportedOSPlatform("windows")]
    private const InheritanceFlags ToEverythingInside = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>What <see cref="Untrusted"/> says of a folder or file whose rules this process may not read.</summary>
    public const string Unreadable = "has access rules this account cannot read, so who may change it cannot be checked";

    /// <summary>What <see cref="Untrusted"/> says of a junction or a symbolic link.</summary>
    public const string Link = "is a junction or a symbolic link, so its access rules and what it holds are those of whatever it points to";

    /// <summary>
    /// Null when the existing folder or file at <paramref name="path"/> is
    /// neither a junction nor a symbolic link, is owned by administrators,
    /// SYSTEM or <paramref name="account"/>, and no one else may change it;
    /// otherwise what is wrong. Always null on Unix.
    /// </summary>
    /// <param name="account">
    /// The account trusted beside administrators and SYSTEM, by security
    /// identifier; null for the one this process runs as.
    /// </param>
    public static AccessFinding? Examine(string path, string? account = null) =>
        OperatingSystem.IsWindows() ? ExamineOnWindows(path, account) : null;

    /// <summary><see cref="Examine"/>'s clause alone.</summary>
    public static string? Untrusted(string path, string? account = null) => Examine(path, account)?.Clause;

    /// <summary>
    /// For a host pointed at an installed deployment with
    /// <c>PREM_CREDENTIALS_FILE</c>: the refusal, one sentence, when that
    /// file's folder, the file, or a file a host reads beside it
    /// (<see cref="InstallFiles.SearchCredentials"/>, <see cref="InstallFiles.KestrelSettings"/>
    /// and <see cref="InstallFiles.Certificate"/>) is refused by <see cref="Examine"/>.
    /// Null when all are sound, when no file is named, and on Unix. A part
    /// that does not exist is left to whatever reads it, which refuses in its
    /// own words; so is the whole folder when the named file does not exist,
    /// since the host then refuses to start before it reads anything there.
    /// </summary>
    public static string? HostRefusal(string? credentialsFile, string? account = null)
    {
        if (string.IsNullOrEmpty(credentialsFile) || !OperatingSystem.IsWindows()) return null;
        var file = Path.GetFullPath(credentialsFile);
        // A link is refused before anything is asked of what it points to.
        var folder = Path.GetDirectoryName(file)!;
        if (!Directory.Exists(folder) || !(File.Exists(file) || IsLink(file))) return null;
        string[] beside = [InstallFiles.SearchCredentials, InstallFiles.KestrelSettings, InstallFiles.Certificate];
        foreach (var path in new[] { folder, file }.Concat(beside.Select(name => Path.Combine(folder, name))).Distinct(StringComparer.OrdinalIgnoreCase))
            if ((Directory.Exists(path) || File.Exists(path) || IsLink(path)) && Examine(path, account) is { } finding)
                return $"{path} {finding.Clause}; " + finding.Problem switch
                {
                    AccessProblem.Link => "point PREM_CREDENTIALS_FILE at the folder itself, or put the folder or file in place of the link, and run prem setup again.",
                    AccessProblem.Owner => $"once you have checked what is in it, make Administrators its owner with the command on {RecoveryPage}, and run prem setup again.",
                    AccessProblem.Writers => $"take that access away with the command on {RecoveryPage}, from an elevated prompt, and run prem setup again.",
                    _ => "run prem setup again, from an elevated prompt for the Windows service, which puts it right or says how.",
                };
        return null;
    }

    /// <summary>
    /// Makes the folder private from the moment it exists, with every folder
    /// above it that is missing: inheritance off, full control for SYSTEM,
    /// administrators and the account this process runs as, and on Unix mode
    /// 700. With <paramref name="serviceSid"/> that service may also read it,
    /// and from an elevated process the folder is then the administrators' own:
    /// owned by Administrators, with SYSTEM, administrators and the service
    /// only.
    /// <para>
    /// On Windows the folder is read back once it is made. Making a folder
    /// that is already there does nothing, so one another account made first,
    /// even in the moment between a caller's look and this call, is found
    /// here, not taken as made.
    /// </para>
    /// </summary>
    /// <exception cref="IOException">The folder there is not the private one this made.</exception>
    public static void CreatePrivateFolder(string path, string? serviceSid = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
        CreatePrivateFolderOnWindows(path, serviceSid);
        RequireMadePrivate(path, "folder");
    }

    /// <summary>
    /// Throws unless the folder or file just made at <paramref name="path"/> is
    /// what it was made to be: no link, owned by administrators, SYSTEM or this
    /// account, changeable by nobody else, and with inheritance off.
    /// </summary>
    /// <exception cref="IOException">It is not; the message says why and what to do.</exception>
    [SupportedOSPlatform("windows")]
    internal static void RequireMadePrivate(string path, string what)
    {
        var finding = Examine(path);
        if (finding is null && ProtectedOnWindows(path)) return;
        throw new IOException(
            $"{path} is not the private {what} just made there: it " +
            (finding?.Clause ?? "takes the access rules of the folder above it") +
            ", so another account made or changed it first; remove it, check what else that account could have left, and try again.");
    }

    /// <summary>
    /// Makes an existing folder, and the files in it a Windows service reads,
    /// the administrators' own: each owned by Administrators; the folder
    /// changeable by SYSTEM and administrators alone, and readable by the
    /// service; each file readable by the service and by the account this
    /// process runs as, and changeable by no one. Setup does this when it
    /// registers the service, so the service accepts what it reads. Needs an
    /// elevated process. A junction or a symbolic link among them is refused
    /// before any rule is set, since the rules would be set on its target.
    /// </summary>
    /// <exception cref="IOException">The folder or one of the files is a junction or a symbolic link.</exception>
    [SupportedOSPlatform("windows")]
    public static void GiveToAdministrators(string folder, IEnumerable<string> files, string serviceSid)
    {
        var all = files.ToList();
        RefuseLinks([folder, .. all]);
        var administrators = new SecurityIdentifier(AdministratorsSid);
        var service = new SecurityIdentifier(serviceSid);

        var folderRules = new DirectorySecurity();
        folderRules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var full in new[] { new SecurityIdentifier(SystemSid), administrators })
            folderRules.AddAccessRule(new FileSystemAccessRule(full, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        folderRules.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        folderRules.SetOwner(administrators);
        new DirectoryInfo(folder).SetAccessControl(folderRules);

        foreach (var file in all)
            SetFileRules(file, [WindowsIdentity.GetCurrent().User!, service]);
    }

    /// <summary>
    /// Makes a file the administrators' own: owned by Administrators, readable
    /// by administrators through one rule and by nobody else, and changeable by
    /// no one. Every administrator, from an elevated process, reads it the same
    /// way, so a second administrator's setup run finds it as trusted and as
    /// private as the first's did. Setup does this with the bundled server's
    /// superuser file when it runs elevated. Needs an elevated process.
    /// </summary>
    /// <exception cref="IOException">The file is a junction or a symbolic link.</exception>
    [SupportedOSPlatform("windows")]
    public static void GiveFileToAdministrators(string file)
    {
        RefuseLinks([file]);
        SetFileRules(file, [new SecurityIdentifier(AdministratorsSid)]);
    }

    /// <summary>
    /// Whether the file is as <see cref="GiveFileToAdministrators"/> leaves it:
    /// no link, owned by Administrators, inheritance off, and one rule, their
    /// read.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsAdministratorsOwn(string file)
    {
        if (IsLink(file)) return false;
        var rules = new FileInfo(file).GetAccessControl();
        if (!rules.AreAccessRulesProtected || rules.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier { Value: AdministratorsSid }) return false;
        var all = rules.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return all is [{ AccessControlType: AccessControlType.Allow } only]
               && only.IdentityReference.Value == AdministratorsSid
               && (only.FileSystemRights & ~(FileSystemRights.Read | FileSystemRights.Synchronize)) == 0;
    }

    /// <summary>Whether <paramref name="path"/> is a junction, a symbolic link or any other reparse point, judged on the path itself, never its target.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // Nothing there, or nothing this account may look at: the rules
            // are read next, and refuse what cannot be read.
            return false;
        }
    }

    /// <summary>Whether <paramref name="sid"/> is administrators, SYSTEM or <paramref name="account"/>.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsTrusted(SecurityIdentifier sid, SecurityIdentifier account) =>
        sid.Equals(account) || sid.Value is AdministratorsSid or SystemSid;

    /// <summary>Whether this process holds the Administrators group, which only an elevated one does.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [SupportedOSPlatform("windows")]
    private static void RefuseLinks(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (IsLink(path))
                throw new IOException($"{path} {Link}; no access rule is set through it. Put the folder or file itself in its place and run setup again.");
    }

    [SupportedOSPlatform("windows")]
    private static void SetFileRules(string file, IEnumerable<SecurityIdentifier> readers)
    {
        var rules = new FileSecurity();
        rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var reader in readers.Distinct())
            rules.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.Read | FileSystemRights.Synchronize, AccessControlType.Allow));
        rules.SetOwner(new SecurityIdentifier(AdministratorsSid));
        new FileInfo(file).SetAccessControl(rules);
    }

    [SupportedOSPlatform("windows")]
    private static AccessFinding? ExamineOnWindows(string path, string? account)
    {
        // First, and on the path itself: a link's rules would be its target's.
        if (IsLink(path)) return new AccessFinding(AccessProblem.Link, Link);

        var self = account is null ? WindowsIdentity.GetCurrent().User! : new SecurityIdentifier(account);
        FileSystemSecurity security;
        try
        {
            const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;
            security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl(Sections) : new FileInfo(path).GetAccessControl(Sections);
        }
        catch (UnauthorizedAccessException)
        {
            return new AccessFinding(AccessProblem.Unreadable, Unreadable);
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrusted(owner, self))
            return new AccessFinding(AccessProblem.Owner,
                $"is owned by {Name(owner)}, not by administrators, SYSTEM or {Name(self)}, so another account could have written what is in it");

        var others = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Changes) != 0)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .Where(sid => !IsTrusted(sid, self) && sid.Value is not (CreatorOwnerSid or OwnerRightsSid))
            .Distinct()
            .Select(Name)
            .ToList();
        return others.Count == 0
            ? null
            : new AccessFinding(AccessProblem.Writers,
                $"can be changed by {string.Join(", ", others)}, not only by administrators, SYSTEM and {Name(self)}, so another account could have written what is in it");
    }

    [SupportedOSPlatform("windows")]
    private static bool ProtectedOnWindows(string path) =>
        (Directory.Exists(path)
            ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access)).AreAccessRulesProtected;

    [SupportedOSPlatform("windows")]
    private static void CreatePrivateFolderOnWindows(string path, string? serviceSid)
    {
        var administratorsOwn = serviceSid is not null && IsElevated();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var full = new List<SecurityIdentifier> { new(SystemSid), new(AdministratorsSid) };
        if (!administratorsOwn) full.Add(WindowsIdentity.GetCurrent().User!);
        foreach (var sid in full)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        if (serviceSid is not null)
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(serviceSid), FileSystemRights.ReadAndExecute, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        if (administratorsOwn) security.SetOwner(new SecurityIdentifier(AdministratorsSid));
        new DirectoryInfo(path).Create(security);
    }

    /// <summary>An account's name, or its security identifier when it has none here.</summary>
    [SupportedOSPlatform("windows")]
    private static string Name(SecurityIdentifier? sid)
    {
        if (sid is null) return "no account";
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
