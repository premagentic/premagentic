using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Premagentic.Cli.Setup;

/// <summary>
/// Registers the API as a Windows service that runs under its own virtual
/// account, <c>NT SERVICE\Premagentic</c>: an account Windows manages, with no
/// password to keep, that can read only what it is given. It is given read
/// access to the application's credentials file, the HTTPS settings and the
/// certificate, and to nothing else; the owner's credentials stay with
/// administrators. The service finds its files through one setting,
/// <c>PREM_CREDENTIALS_FILE</c>, kept in the service's own environment.
/// </summary>
internal static class WindowsServiceRegistration
{
    /// <summary>The name the API's hosting code registers under; the two must match.</summary>
    public const string ServiceName = "Premagentic";

    public static string Account => $@"NT SERVICE\{ServiceName}";

    /// <summary>
    /// The security identifier of a service's virtual account. Windows derives it
    /// from the service name alone (the SHA-1 of the name in upper case, as
    /// UTF-16), so it is known before the service exists and on any machine.
    /// </summary>
    public static string ServiceSid(string serviceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(hash, i * 4));
        return "S-1-5-80-" + string.Join('-', parts);
    }

    /// <summary>
    /// The <c>sc.exe</c> calls that create the service, one argument list each.
    /// The program path is quoted inside its own argument, so a path with a space
    /// is stored quoted and cannot be read as a shorter program name.
    /// </summary>
    public static IReadOnlyList<string[]> CreateCommands(string executable, string? dependsOn = null) =>
    [
        ["create", ServiceName, "binPath=", $"\"{executable}\"", "start=", "delayed-auto", "obj=", Account, "DisplayName=", "Premagentic"],
        ["sidtype", ServiceName, "unrestricted"],
        ["description", ServiceName, "Premagentic search API: cited passages from this organization's own documents."],
        ["failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000//"],
        // With the bundled database, the API starts after it and stops before it.
        .. dependsOn is null ? Array.Empty<string[]>() : [["config", ServiceName, "depend=", dependsOn]],
    ];

    /// <summary>The service's environment: one setting, and no secret.</summary>
    public static string[] ServiceEnvironment(string appCredentialsPath) => [$"PREM_CREDENTIALS_FILE={appCredentialsPath}"];

    [SupportedOSPlatform("windows")]
    public static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [SupportedOSPlatform("windows")]
    public static bool Exists(string serviceName = ServiceName) => Sc(["query", serviceName]).ExitCode != ServiceDoesNotExist;

    /// <summary>Creates the service and sets its environment. Needs an elevated process.</summary>
    [SupportedOSPlatform("windows")]
    public static void Create(string executable, string appCredentialsPath, string? dependsOn = null)
    {
        foreach (var command in CreateCommands(executable, dependsOn))
        {
            var (code, output) = Sc(command);
            if (code != 0)
                throw new InvalidOperationException($"sc.exe {command[0]} {ServiceName} failed ({code}): {output.Trim()}");
        }
        SetEnvironment(appCredentialsPath);
    }

    [SupportedOSPlatform("windows")]
    public static void SetEnvironment(string appCredentialsPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}", writable: true)
            ?? throw new InvalidOperationException($"The service {ServiceName} has no registry key.");
        key.SetValue("Environment", ServiceEnvironment(appCredentialsPath), RegistryValueKind.MultiString);
    }

    [SupportedOSPlatform("windows")]
    public static bool HasEnvironment(string appCredentialsPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return key?.GetValue("Environment") is string[] values && values.SequenceEqual(ServiceEnvironment(appCredentialsPath));
    }

    /// <summary>
    /// Stops a service, waits until it has stopped, and removes it. A service
    /// that is not running is only removed. Needs an elevated process.
    /// </summary>
    /// <exception cref="InvalidOperationException">It did not stop within two minutes, or sc.exe refused.</exception>
    [SupportedOSPlatform("windows")]
    public static void StopAndDelete(string serviceName)
    {
        var (stop, stopOutput) = Sc(["stop", serviceName]);
        if (stop is not (0 or ServiceNotActive))
            throw new InvalidOperationException($"sc.exe stop {serviceName} failed ({stop}): {stopOutput.Trim()}");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (State(Sc(["query", serviceName]).Output) != Stopped)
        {
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"The service {serviceName} did not stop within two minutes; nothing was removed.");
            Thread.Sleep(500);
        }
        var (delete, deleteOutput) = Sc(["delete", serviceName]);
        if (delete != 0)
            throw new InvalidOperationException($"sc.exe delete {serviceName} failed ({delete}): {deleteOutput.Trim()}");
    }

    /// <summary>
    /// The state number in what <c>sc.exe query</c> printed (1 stopped, 3 stop
    /// pending, 4 running), or null when there is none.
    /// </summary>
    internal static int? State(string queryOutput) =>
        System.Text.RegularExpressions.Regex.Match(queryOutput, @"STATE\s*:\s*(\d+)") is { Success: true } match
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    internal const int Stopped = 1;

    private const int ServiceDoesNotExist = 1060;
    private const int ServiceNotActive = 1062;

    internal static (int ExitCode, string Output) Sc(string[] arguments)
    {
        var start = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
