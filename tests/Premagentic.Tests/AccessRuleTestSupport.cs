using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Premagentic.Cli.Setup;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Access rules as another account could leave them, on real folders and
/// files: the test process makes no second account, so it grants one of the
/// built-in groups what a planted folder or file would give it, or judges a
/// folder as another account would.
/// </summary>
internal static class AccessRules
{
    /// <summary>BUILTIN\Users, every local account.</summary>
    public const string UsersSid = "S-1-5-32-545";

    /// <summary>The name Windows gives BUILTIN\Users here, as the refusals print it.</summary>
    [SupportedOSPlatform("windows")]
    public static string UsersName => new SecurityIdentifier(UsersSid).Translate(typeof(NTAccount)).Value;

    /// <summary>The name of the account this process runs as, the way the refusals print it.</summary>
    [SupportedOSPlatform("windows")]
    public static string ThisAccountName => WindowsIdentity.GetCurrent().User!.Translate(typeof(NTAccount)).Value;

    /// <summary>Lets every user make files and folders in a folder, or write a file: the rule C:\ProgramData gives every folder made in it the ordinary way.</summary>
    [SupportedOSPlatform("windows")]
    public static void LetUsersWrite(string path) => Allow(path, UsersSid, FileSystemRights.Write);

    /// <summary>Lets other accounts read a file: a rule for every user on Windows, mode 644 on Unix.</summary>
    public static void LetOthersRead(string path)
    {
        if (OperatingSystem.IsWindows()) Allow(path, UsersSid, FileSystemRights.Read);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    [SupportedOSPlatform("windows")]
    public static void Allow(string path, string sid, FileSystemRights rights)
    {
        var account = new SecurityIdentifier(sid);
        if (Directory.Exists(path))
        {
            var folder = new DirectoryInfo(path);
            var rules = folder.GetAccessControl();
            rules.AddAccessRule(new FileSystemAccessRule(account, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(rules);
            return;
        }
        var file = new FileInfo(path);
        var fileRules = file.GetAccessControl();
        fileRules.AddAccessRule(new FileSystemAccessRule(account, rights, AccessControlType.Allow));
        file.SetAccessControl(fileRules);
    }

    /// <summary>Makes this account the owner, which an elevated process might otherwise have made Administrators.</summary>
    [SupportedOSPlatform("windows")]
    public static void OwnedByThisAccount(string path)
    {
        FileSystemSecurity rules = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        rules.SetOwner(WindowsIdentity.GetCurrent().User!);
        if (rules is DirectorySecurity folder) new DirectoryInfo(path).SetAccessControl(folder);
        else new FileInfo(path).SetAccessControl((FileSecurity)rules);
    }

    /// <summary>Replaces a file's rules with one full-control rule for <paramref name="sid"/>, inheritance off.</summary>
    [SupportedOSPlatform("windows")]
    public static void OnlyFor(string path, string sid)
    {
        var rules = new FileSecurity();
        rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        rules.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(rules);
    }

    /// <summary>
    /// Makes <paramref name="link"/> a junction to the folder <paramref name="target"/>,
    /// as mklink /J does, which needs no elevation.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && InstallAccess.IsLink(link), $"mklink /J failed ({process.ExitCode}): {output}");
    }

    /// <summary>A security identifier Windows derives for a service that is not installed: an account that is not this one.</summary>
    public static string AnotherAccount => WindowsServiceRegistration.ServiceSid("PremagenticTestOther");
}

/// <summary>
/// A Windows test that needs an elevated process, skipped with its reason
/// anywhere else. The elevated Windows job in CI runs it.
/// </summary>
public sealed class ElevatedWindowsFactAttribute : FactAttribute
{
    public ElevatedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows access rules exist only on Windows.";
        else if (!WindowsServiceRegistration.IsElevated()) Skip = "This needs an elevated process, as setup's Windows service step does; the elevated Windows CI job runs it.";
    }
}
