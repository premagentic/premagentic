using System.Text.Json;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;

namespace Premagentic.Samples.Commands;

/// <summary>
/// A whole extension that adds to the command line: a command of its own,
/// <c>prem greeting</c>, with a verb that reads and a verb that changes
/// something; a subcommand under a built-in command that takes one,
/// <c>prem groups count</c>; and a setting of its own, <c>greeting.text</c>,
/// which <c>prem settings</c> lists, checks and records like a built-in one.
/// <para>
/// Copy it to start your own. A verb that changes something makes the change
/// through <see cref="CommandContext.Changes"/>, so the change and its row in
/// the change record commit together, and says so with <c>Writes: true</c>.
/// </para>
/// </summary>
public sealed class GreetingExtension : IExtension
{
    public string Name => "greeting-command";

    public void Register(ExtensionRegistrations registrations)
    {
        registrations.AddSetting(Greeting.Setting);
        registrations.AddCommand(new ExtensionCommand("greeting",
            "A greeting this deployment keeps, as a sample of a command an extension adds.",
            Greeting.Usage,
            [new ExtensionVerb("show", Writes: false, Greeting.ShowAsync), new ExtensionVerb("set", Writes: true, Greeting.SetAsync)]));
        registrations.AddCommand(new ExtensionCommand("groups",
            "How many groups there are, as a sample of a subcommand added under a built-in command.",
            GroupCount.Usage,
            [new ExtensionVerb("count", Writes: false, GroupCount.RunAsync)]));
    }
}

/// <summary><c>prem greeting</c> and its setting.</summary>
public static class Greeting
{
    public const string Key = "greeting.text";

    /// <summary>The kind the change record gives a new greeting.</summary>
    public const string ChangeKind = "greeting.set";

    public const string Default = "Hello";

    public const int MaxLength = 80;

    public const string Usage = """
        prem greeting show
        prem greeting set <text> [--quiet]

          show prints the greeting this deployment keeps, or the default when none is set. set keeps a
          new one, of 1 to 80 characters, and records the change.

          --quiet                set: print nothing when it succeeds
        """;

    public static SettingDefinition Setting { get; } = new(Key,
        "The greeting prem greeting show prints.",
        $"a text of 1 to {MaxLength} characters",
        JsonSerializer.SerializeToElement(Default), null,
        Problem, TakesText: true);

    /// <summary>Null when the value can be kept, otherwise the sentence that says why not.</summary>
    public static string? Problem(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= MaxLength }
            ? null
            : $"{Key} takes a text of 1 to {MaxLength} characters.";

    public static async Task<int> ShowAsync(CommandContext context, CancellationToken ct)
    {
        var stored = await context.SettingsStore().GetAsync(Key, ct);
        // A stored value that cannot be used reads as the default.
        context.Out.WriteLine(stored is { } value && Problem(value) is null ? value.GetString() : Default);
        return 0;
    }

    public static async Task<int> SetAsync(CommandContext context, CancellationToken ct)
    {
        var words = context.Positionals();
        if (words.Count != 1) throw new ArgumentException("Give one greeting: prem greeting set <text>.");
        var value = JsonSerializer.SerializeToElement(words[0]);
        if (Problem(value) is { } problem) throw new ArgumentException(problem);

        var changed = await context.Changes().RunAsync(context.Actor, async change =>
        {
            var store = context.SettingsStore(change.Transaction);
            var before = await store.GetAsync(Key, ct);
            if (before is { } old && JsonElement.DeepEquals(old, value)) return false;
            await store.SetAsync(Key, value, ct);
            change.Record(ChangeKind, Key, before, value);
            return true;
        }, ct);

        if (!context.Has("--quiet"))
            context.Out.WriteLine(changed ? $"The greeting is now '{words[0]}'." : "The greeting was already that. Nothing changed.");
        return 0;
    }
}

/// <summary><c>prem groups count</c>.</summary>
public static class GroupCount
{
    public const string Usage = """
        prem groups count

          count prints how many groups this deployment has.
        """;

    public static async Task<int> RunAsync(CommandContext context, CancellationToken ct)
    {
        var groups = await new IdentityStore(context.Db, context.TenantId).ListGroupsAsync(ct);
        context.Out.WriteLine($"{groups.Count} group(s).");
        return 0;
    }
}
