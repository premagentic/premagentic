using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Premagentic.Cli.Setup;

/// <summary>
/// The PostgreSQL Premagentic brings with it on Windows: the minimal set of
/// stock binaries <c>installer/windows/fetch-postgresql.ps1</c> lays out (six
/// tools and the libraries they load), a cluster <c>initdb</c> makes in a data
/// folder the operator chooses, listening on localhost only, and a superuser
/// whose generated password is kept where only administrators read it.
/// <para>
/// Run by hand, the server is started as the account running setup. With the
/// Windows service option it is registered as the service <c>PremagenticDb</c>
/// under its own virtual account, which may change the data folder and nothing
/// else; PostgreSQL itself refuses to run with administrator rights.
/// </para>
/// </summary>
internal sealed class BundledPostgres(string root)
{
    public const string ServiceName = "PremagenticDb";

    /// <summary>The superuser <c>initdb</c> makes. Setup connects as it only to create the three roles and the database.</summary>
    public const string Superuser = "premagentic_admin";

    /// <summary>BUILTIN\Administrators, which may read the superuser's credentials file beside the account that ran setup.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    public static readonly string[] Tools = ["initdb", "pg_ctl", "postgres", "pg_dump", "pg_restore", "psql"];

    // A process that cannot load a DLL it imports, or a function in one, ends
    // with one of these before running a line of its own.
    private const int DllNotFound = unchecked((int)0xC0000135);
    private const int EntryPointNotFound = unchecked((int)0xC0000139);

    public string Root { get; } = Path.GetFullPath(root);

    public string Tool(string name) => Path.Combine(Root, "bin", OperatingSystem.IsWindows() ? name + ".exe" : name);

    public static string ServiceAccount => $@"NT SERVICE\{ServiceName}";

    public IReadOnlyList<string> MissingTools() => Tools.Where(t => !File.Exists(Tool(t))).ToList();

    /// <summary>The Visual C++ runtime libraries the binaries import from the system.</summary>
    public static readonly string[] RuntimeLibraries = ["vcruntime140.dll", "msvcp140.dll"];

    public const string RuntimeMissing =
        "PostgreSQL needs the Microsoft Visual C++ 2015 to 2022 x64 runtime, which is not installed on this computer. " +
        "Install vc_redist.x64.exe from Microsoft (the Visual C++ Redistributable for Visual Studio 2015 to 2022, x64), " +
        "then run setup again.";

    /// <summary>
    /// Null when every tool loads; otherwise what to do. On Windows the binaries
    /// need the Microsoft Visual C++ 2015 to 2022 x64 runtime, which the archive
    /// does not carry, and which Windows Update services once it is installed;
    /// its libraries are looked for in <paramref name="systemFolder"/> first. A
    /// tool that still cannot load a library means the bundle itself is missing
    /// one, or the runtime is older than the binaries need.
    /// </summary>
    public string? RuntimeProblem(string? systemFolder = null)
    {
        if (OperatingSystem.IsWindows())
        {
            systemFolder ??= Environment.SystemDirectory;
            if (RuntimeLibraries.Any(library => !File.Exists(Path.Combine(systemFolder, library))))
                return RuntimeMissing;
        }
        foreach (var tool in new[] { "postgres", "initdb" })
        {
            var (code, output) = Run(Tool(tool), ["--version"], TimeSpan.FromSeconds(30));
            if (code is DllNotFound or EntryPointNotFound)
                return $"{Path.GetFileName(Tool(tool))} cannot load a library it needs (exit code 0x{code:X8}). The bundle in " +
                       $"{Root} is incomplete, or the Visual C++ runtime is older than it needs: lay the bundle out again with " +
                       "installer/windows/fetch-postgresql.ps1, and install the latest vc_redist.x64.exe from Microsoft.";
            if (code != 0)
                return $"{Path.GetFileName(Tool(tool))} --version failed with exit code {code}: {Last(output)}";
        }
        return null;
    }

    /// <summary>The <c>initdb</c> arguments: UTF-8, the ICU locale, SCRAM for every connection, and localhost only.</summary>
    public static IReadOnlyList<string> InitDbArguments(string dataDirectory, string passwordFile, int port) =>
    [
        "-D", dataDirectory, "-U", Superuser, "--pwfile", passwordFile, "--auth=scram-sha-256", "-E", "UTF8",
        "--locale-provider=icu", "--icu-locale=en-US", "--no-instructions",
        "-c", "listen_addresses=localhost", "-c", $"port={port}",
    ];

    /// <summary>
    /// The <c>pg_ctl register</c> arguments: the service under its own virtual
    /// account, with no password (Windows manages it), started automatically.
    /// </summary>
    public static IReadOnlyList<string> RegisterArguments(string dataDirectory) =>
        ["register", "-N", ServiceName, "-U", ServiceAccount, "-D", dataDirectory, "-S", "auto"];

    public bool IsCluster(string dataDirectory) => File.Exists(Path.Combine(dataDirectory, "PG_VERSION"));

    /// <summary>True when a server is running on the cluster (<c>pg_ctl status</c> answers 0).</summary>
    public bool IsRunning(string dataDirectory) => Run(Tool("pg_ctl"), ["status", "-D", dataDirectory], TimeSpan.FromSeconds(30)).Code == 0;

    /// <summary>True when nothing on this computer listens on the port at the loopback address.</summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public (int Code, string Output) InitDb(string dataDirectory, string passwordFile, int port) =>
        Run(Tool("initdb"), InitDbArguments(dataDirectory, passwordFile, port), TimeSpan.FromMinutes(5));

    /// <summary>
    /// Starts the server as this account and waits for it. The server's own
    /// output goes to <c>server.log</c> in the data folder. The server outlives
    /// this process, so its output handles are never waited on.
    /// </summary>
    public (int Code, string Output) Start(string dataDirectory) =>
        Run(Tool("pg_ctl"), ["start", "-D", dataDirectory, "-l", Path.Combine(dataDirectory, "server.log"), "-w", "-t", "120", "-s"],
            TimeSpan.FromMinutes(3), outputGrace: TimeSpan.FromSeconds(2));

    public (int Code, string Output) Stop(string dataDirectory) =>
        Run(Tool("pg_ctl"), ["stop", "-D", dataDirectory, "-m", "fast", "-w", "-s"], TimeSpan.FromMinutes(2));

    [SupportedOSPlatform("windows")]
    public (int Code, string Output) Register(string dataDirectory) =>
        Run(Tool("pg_ctl"), RegisterArguments(dataDirectory), TimeSpan.FromMinutes(1));

    /// <summary>
    /// Makes the data folder before <c>initdb</c> does, so it is private from the
    /// start: on Windows no inherited rules, full control for this account,
    /// SYSTEM and administrators; on Unix mode 700.
    /// </summary>
    public static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
        CreatePrivateDirectoryOnWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void CreatePrivateDirectoryOnWindows(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(AdministratorsSid) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    /// <summary>Lets the database service's virtual account change the data folder, and nothing else.</summary>
    [SupportedOSPlatform("windows")]
    public static void GrantServiceModify(string dataDirectory)
    {
        var folder = new DirectoryInfo(dataDirectory);
        var security = folder.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WindowsServiceRegistration.ServiceSid(ServiceName)), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
    }

    /// <summary>The last line of a tool's output, for an error message.</summary>
    public static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "(no output)";

    /// <summary>
    /// Runs a tool and collects what it printed. After the tool exits, what it
    /// printed can still be on its way, so the end of both streams is waited for,
    /// but only for <paramref name="outputGrace"/>: <c>pg_ctl start</c> leaves the
    /// server holding the same handles, and waiting for them to close would wait
    /// for the server to stop.
    /// </summary>
    internal static (int Code, string Output) Run(string file, IEnumerable<string> arguments, TimeSpan timeout, TimeSpan? outputGrace = null)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var output = new System.Text.StringBuilder();
        // Not disposed here: after pg_ctl start the streams end only when the
        // server stops, long after this returns, and their end still signals it.
        var outputEnded = new CountdownEvent(2);
        using var process = new Process { StartInfo = start };
        void Collect(DataReceivedEventArgs e)
        {
            if (e.Data is null) outputEnded.Signal();
            else lock (output) output.AppendLine(e.Data);
        }
        process.OutputDataReceived += (_, e) => Collect(e);
        process.ErrorDataReceived += (_, e) => Collect(e);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
            return (-1, $"{Path.GetFileName(file)} did not finish within {timeout.TotalSeconds:F0} seconds.");
        }
        outputEnded.Wait(outputGrace ?? TimeSpan.FromSeconds(30));
        lock (output) return (process.ExitCode, output.ToString());
    }
}
