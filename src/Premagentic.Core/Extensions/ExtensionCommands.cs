using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;
using Npgsql;

namespace Premagentic.Core.Extensions;

/// <summary>One subcommand of a command an extension adds.</summary>
/// <param name="Name">The word it is called by, such as <c>prune</c>.</param>
/// <param name="Writes">
/// True when it changes anything. Such a verb makes its change through
/// <see cref="CommandContext.Changes"/>, so the change and its row in the
/// change record commit together; a test runs every verb marked as writing and
/// fails on one that leaves no row.
/// </param>
/// <param name="RunAsync">Runs one call and returns its exit code: 0 done, 1 refused.</param>
public sealed record ExtensionVerb(string Name, bool Writes, Func<CommandContext, CancellationToken, Task<int>> RunAsync);

/// <summary>
/// A <c>prem</c> command an extension adds: a noun of its own, such as
/// <c>audit</c>, or subcommands under a built-in noun that takes them, such as
/// <c>groups</c> (<see cref="BuiltInCommands.Open"/>).
/// </summary>
/// <param name="Summary">One line, for a list of commands.</param>
/// <param name="Usage">
/// The usage text, written as a built-in command's is: a line
/// <c>prem noun verb ...</c> for every verb, then what they do and the flags
/// they take. It is printed wherever the command is described, and a call
/// with a flag it does not name is refused with it.
/// </param>
public sealed record ExtensionCommand(string Noun, string Summary, string Usage, IReadOnlyList<ExtensionVerb> Verbs);

/// <summary>A command a loaded extension added, and that extension.</summary>
public sealed record ContributedCommand(string Extension, string Version, ExtensionCommand Command);

/// <summary>
/// What a command an extension adds is given for one call: the call, the
/// deployment's database and tenant, who is acting, and the loaded
/// extensions. Nobody signs in to the command line, so a change is recorded
/// against the operating-system account that ran it, as a built-in verb's is.
/// </summary>
public sealed class CommandContext
{
    internal CommandContext(IReadOnlyList<string> args, PremagenticDatabase db, Guid tenantId, AdminActor actor,
        ExtensionHost host, TextWriter output, TextWriter error)
    {
        Args = args;
        Db = db;
        TenantId = tenantId;
        Actor = actor;
        Host = host;
        Out = output;
        Error = error;
    }

    /// <summary>The whole call: the noun, the verb, then everything after them.</summary>
    public IReadOnlyList<string> Args { get; }

    public PremagenticDatabase Db { get; }

    public Guid TenantId { get; }

    /// <summary>Who is acting, as the change record names them.</summary>
    public AdminActor Actor { get; }

    /// <summary>The extensions this process loaded, with what each added.</summary>
    public ExtensionHost Host { get; }

    /// <summary>Every setting this deployment defines, an extension's own included.</summary>
    public DeploymentSettings Settings => Host.Settings;

    public TextWriter Out { get; }

    public TextWriter Error { get; }

    /// <summary>The value after <paramref name="flag"/>, or null when the flag is absent or has no value.</summary>
    public string? Value(string flag) => CommandLine.Value(Args, flag);

    /// <summary>The value after every occurrence of <paramref name="flag"/>, in order.</summary>
    public IReadOnlyList<string> Values(string flag) => CommandLine.Values(Args, flag);

    public bool Has(string flag) => CommandLine.Has(Args, flag);

    /// <summary>
    /// The words after the noun and the verb that are not flags and not the
    /// value of a flag named in <paramref name="flagsWithValues"/>.
    /// </summary>
    public IReadOnlyList<string> Positionals(params string[] flagsWithValues) =>
        CommandLine.Positionals(Args, 2, flagsWithValues);

    /// <summary>
    /// The settings store, which knows this deployment's keys and the loaded
    /// extensions' own; inside <paramref name="transaction"/> when one is
    /// given. It refuses a <c>trust.</c> or <c>extensions.</c> key, and writes a
    /// value only when the key's definition takes it.
    /// </summary>
    public SettingsStore SettingsStore(NpgsqlTransaction? transaction = null) =>
        new(Db, TenantId, transaction, Settings) { ForExtensionCommand = true };

    /// <summary>A change and its rows in the change record, in one transaction, as a built-in verb makes one.</summary>
    public AdminChanges Changes(TimeProvider? time = null) => new(Db, TenantId, time);
}

/// <summary>
/// Runs a command a loaded extension added. The command line calls this for a
/// call it does not have built in, once the extensions are loaded, and a test
/// can call it the same way.
/// </summary>
public static class ExtensionCommands
{
    /// <summary>
    /// Runs the call when a loaded extension added its command, and returns its
    /// exit code; null when none did, so the caller gives its own refusal.
    /// <c>--help</c> or <c>-h</c>, a noun of its own called alone, and
    /// <c>prem help noun</c> print the usage. A flag the usage does not name is
    /// refused with it before anything runs. An <see cref="ArgumentException"/>
    /// or <see cref="InvalidOperationException"/> from the verb is printed as
    /// one line with exit code 1, as a built-in verb's is.
    /// </summary>
    /// <param name="actor">Who the change record names; null is the command line's actor.</param>
    public static async Task<int?> RunAsync(
        ExtensionHost host, IReadOnlyList<string> args, PremagenticDatabase db, Guid tenantId,
        TextWriter output, TextWriter error, AdminActor? actor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0) return null;

        if (args[0] == "help")
        {
            if (args.Count != 2 || UsageFor(host, args[1]) is not { } asked) return null;
            output.WriteLine(asked);
            return 0;
        }

        var noun = args[0];
        var added = host.Commands.Where(c => c.Command.Noun == noun).ToArray();
        if (added.Length == 0) return null;
        var word = args.Count > 1 && !args[1].StartsWith('-') ? args[1] : null;
        var found = added
            .SelectMany(c => c.Command.Verbs.Select(v => (Added: c, Verb: v)))
            .FirstOrDefault(x => x.Verb.Name == word);
        var helpAsked = args.Contains("--help") || args.Contains("-h");
        var ownNoun = !BuiltInCommands.Open.ContainsKey(noun);

        if (found.Verb is null)
        {
            // A built-in noun's refusal is the command line's to give.
            if (!ownNoun) return null;
            var usage = UsageFor(host, noun)!;
            if (args.Count == 1 || helpAsked)
            {
                output.WriteLine(usage);
                return 0;
            }
            error.WriteLine($"prem {noun} has no subcommand '{args[1]}'.\n\n{usage}");
            return 1;
        }

        var text = Attributed(found.Added);
        if (helpAsked)
        {
            output.WriteLine(text);
            return 0;
        }
        var accepted = CommandLine.FlagsIn(found.Added.Command.Usage);
        if (args.Skip(2).FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal) && !accepted.Contains(a)) is { } unknown)
        {
            error.WriteLine($"prem {noun} does not take {unknown}.\n\n{text}");
            return 1;
        }

        var context = new CommandContext(args, db, tenantId, actor ?? AdminActor.Cli(), host, output, error);
        try
        {
            return await found.Verb.RunAsync(context, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The usage of what the loaded extensions added under <paramref name="noun"/>,
    /// each with the extension that added it, or null when none added anything.
    /// </summary>
    public static string? UsageFor(ExtensionHost host, string noun)
    {
        ArgumentNullException.ThrowIfNull(host);
        var added = host.Commands.Where(c => c.Command.Noun == noun).Select(Attributed).ToArray();
        return added.Length == 0 ? null : string.Join("\n\n", added);
    }

    /// <summary>Every verb the loaded extensions added that changes something, as noun and verb.</summary>
    public static IReadOnlyList<(string Noun, string Verb)> WriteVerbs(ExtensionHost host) =>
        [.. host.Commands.SelectMany(c => c.Command.Verbs.Where(v => v.Writes).Select(v => (c.Command.Noun, v.Name)))];

    private static string Attributed(ContributedCommand added) =>
        $"{added.Command.Usage.TrimEnd()}\n\n  Added by the extension {added.Extension} {added.Version}.";
}
