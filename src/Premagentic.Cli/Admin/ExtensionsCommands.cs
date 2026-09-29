using Premagentic.Core.Admin;
using Premagentic.Core.Extensions;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// <c>prem extensions</c>: what this process loaded, what it refused and why,
/// and the allow list that decides. Nobody signs in to the CLI, so a change
/// made here is recorded against the operating-system account that ran it.
/// <para>
/// Allowing measures the assembly on disk. It does not load it, and it does not
/// change a running service: an extension starts being used when a Premagentic
/// process next starts, which is also the only safe moment to replace one.
/// </para>
/// </summary>
internal static class ExtensionsCommands
{
    public const string Verb = "extensions";

    public const string Usage = """
        prem extensions list
        prem extensions allow <folder>
        prem extensions disallow <name>

          list says what this process loaded from the extensions folder, the commands and settings each
          loaded one adds, what it refused and why, and what is allowed. allow measures the extension in
          that folder, the hash of its assembly and of every file its manifest lists, checks them, and
          allows it by those hashes. disallow stops allowing every hash under that name. An extension is
          used, or stops being used, when a PremAgentic process next starts.
        """;

    public static async Task<int> RunAsync(string[] args, PremagenticDatabase db, Guid tenantId, ExtensionHost host)
    {
        var allowList = new ExtensionAllowList(db, tenantId);
        try
        {
            return args.ElementAtOrDefault(1) switch
            {
                "list" => await ListAsync(host, allowList),
                "allow" => await AllowAsync(args, allowList),
                "disallow" => await DisallowAsync(args, allowList),
                _ => Fail("Usage:\n" + Usage),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> ListAsync(ExtensionHost host, ExtensionAllowList allowList)
    {
        Console.WriteLine(host.Folder is null
            ? $"No extensions folder is set, so no extension can load. Set {ExtensionSettings.Folder} or {ExtensionSettings.FolderVariable}."
            : $"Extensions folder: {host.Folder}");
        Console.WriteLine();

        var reading = await allowList.ReadAsync();
        if (reading.Problem is not null)
            Console.Error.WriteLine($"WARNING: {ExtensionSettings.Allowed} cannot be read, so nothing is allowed. {reading.Problem}");

        if (host.Loaded.Count == 0)
        {
            Console.WriteLine("Loaded: none.");
        }
        else
        {
            Console.WriteLine("Loaded:");
            foreach (var loaded in host.Loaded)
            {
                Console.WriteLine($"  {loaded.Name} {loaded.Version}  {loaded.Sha256}\n    {loaded.Folder}");
                // What it adds to the command line, so a command or a setting
                // that is not built in can be traced to the extension it came from.
                var verbs = host.Commands.Where(c => c.Extension == loaded.Name)
                    .SelectMany(c => c.Command.Verbs.Select(v => $"prem {c.Command.Noun} {v.Name}{(v.Writes ? " (changes)" : "")}"))
                    .ToArray();
                if (verbs.Length > 0) Console.WriteLine($"    adds {string.Join(", ", verbs)}");
                var keys = host.Settings.Contributed.Where(s => s.Extension == loaded.Name).Select(s => s.Definition.Key).ToArray();
                if (keys.Length > 0) Console.WriteLine($"    adds the setting(s) {string.Join(", ", keys)}");
                // The one registration that adds to what a caller holds, named
                // where an administrator looks for what an extension brought.
                if (host.PrincipalMapper is { } mapper && host.PrincipalMapperExtension == loaded.Name)
                    Console.WriteLine($"    brings the principal mapper {mapper.Name}, which decides what groups and principals from outside mean here");
            }
        }
        Console.WriteLine();

        if (host.Refused.Count == 0)
        {
            Console.WriteLine("Refused: none.");
        }
        else
        {
            Console.WriteLine("Refused:");
            foreach (var refused in host.Refused)
                Console.WriteLine($"  {refused.Name}  ({ExtensionHosting.Reason(refused.Reason)})\n    {refused.Folder}\n    {refused.Detail}");
        }
        Console.WriteLine();

        if (reading.Allowed.Count == 0)
        {
            Console.WriteLine($"Allowed ({ExtensionSettings.Allowed}): nothing. An extension loads only once it is allowed.");
        }
        else
        {
            Console.WriteLine($"Allowed ({ExtensionSettings.Allowed}):");
            foreach (var (name, sha256) in reading.Allowed)
            {
                var installed = host.Loaded.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && l.Sha256 == sha256)
                    ? "loaded"
                    : "not in the extensions folder";
                Console.WriteLine($"  {name}  {sha256}  ({installed})");
            }
        }
        return 0;
    }

    private static async Task<int> AllowAsync(string[] args, ExtensionAllowList allowList)
    {
        var positionals = CliArgs.Positionals(args, 2);
        if (positionals.Count != 1) return Fail("Give one folder: prem extensions allow <folder>.");

        var change = await allowList.AllowAsync(Path.GetFullPath(positionals[0]), AdminActor.Cli());
        // Every hash that was measured, so what was allowed can be compared
        // with what the extension's author published.
        if (change.Files.Count > 0)
        {
            Console.WriteLine($"Measured the assembly and the {change.Files.Count} file(s) its manifest lists; each matches it:");
            Console.WriteLine($"  assembly  {change.AssemblySha256}");
            foreach (var file in change.Files) Console.WriteLine($"  {file.File}  {file.Sha256}");
        }
        Console.WriteLine(change.Changed
            ? $"Allowed {change.Name} with hash {change.Sha256}. It loads the next time a Premagentic process starts."
            : $"{change.Name} with hash {change.Sha256} was already allowed. Nothing changed.");
        return 0;
    }

    private static async Task<int> DisallowAsync(string[] args, ExtensionAllowList allowList)
    {
        var positionals = CliArgs.Positionals(args, 2);
        if (positionals.Count != 1) return Fail("Give one name: prem extensions disallow <name>.");

        var change = await allowList.DisallowAsync(positionals[0], AdminActor.Cli());
        if (!change.Changed)
        {
            Console.WriteLine($"Nothing named '{change.Name}' is allowed. Nothing changed.");
            return 0;
        }
        foreach (var (name, sha256) in change.Removed)
            Console.WriteLine($"No longer allowed: {name} with hash {sha256}.");
        Console.WriteLine("A process already running keeps what it loaded until it is restarted.");
        Console.WriteLine(
            "Documents it read stay indexed, and are still found by search: an ingest keeps them and says so. " +
            "To remove them, run the ingest of their folder once with --remove-unread.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
