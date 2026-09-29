using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Identity.SignIn;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Reminders;

namespace Premagentic.Core.Extensions;

/// <summary>
/// What one extension asks to add, collected while it registers. Each extension
/// fills its own, so an extension that throws halfway through registering adds
/// nothing at all: the host keeps what an extension asked for only once the
/// whole of <see cref="IExtension.Register"/> has returned.
/// <para>
/// A name is checked here, as it is entered, so the extension that brought a
/// name this process cannot hold is the one refused, and the rest still load.
/// </para>
/// </summary>
public sealed class ExtensionRegistrations
{
    private readonly List<IDocumentReader> _readers = [];
    private readonly List<IChunker> _chunkers = [];
    private readonly Dictionary<string, Func<IServiceProvider, IEmbeddingProvider>> _embeddingProviders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ISignInAdapter> _signInAdapters = [];

    /// <summary>
    /// The folder this extension was loaded from, for an extension that keeps
    /// its own settings file beside its manifest; null where no folder is
    /// known. The assembly itself is loaded from its bytes, so its own
    /// location is empty and cannot say this.
    /// </summary>
    public string? Folder { get; init; }
    private readonly List<IReminderSink> _reminderSinks = [];
    private IPrincipalMapper? _principalMapper;

    /// <summary>A reader for file extensions the built-in readers do not cover.</summary>
    /// <exception cref="ArgumentException">The name or one of the file extensions is not usable.</exception>
    public void AddReader(IDocumentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        RequireName(reader.Name, "a reader");
        if (reader.Extensions.Count == 0)
            throw new ArgumentException($"The reader '{reader.Name}' claims no extension, so nothing would ever reach it.");
        foreach (var extension in reader.Extensions)
            if (extension.Length < 2 || extension[0] != '.'
                || extension != extension.ToLowerInvariant()
                || extension.Any(c => c is '/' or '\\' || char.IsWhiteSpace(c)))
                throw new ArgumentException(
                    $"The reader '{reader.Name}' claims '{extension}', which is not an extension. An extension is a dot and at least one more character, lower case, with no path separator or space.");
        _readers.Add(reader);
    }

    /// <summary>Another way to cut a document, which a source then chooses by name.</summary>
    /// <exception cref="ArgumentException">The name is not usable.</exception>
    public void AddChunker(IChunker chunker)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        RequireName(chunker.Name, "a chunker");
        _chunkers.Add(chunker);
    }

    /// <summary>
    /// An embedding provider a deployment can name. The factory is called once,
    /// by the host, only if the deployment names this provider, so an extension
    /// that brings a model does not load it just by being installed.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not usable, or this extension used it twice.</exception>
    public void AddEmbeddingProvider(string name, Func<IServiceProvider, IEmbeddingProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        RequireName(name, "an embedding provider");
        // An extension adds a way to embed and never replaces one. Letting it
        // take the name "local" would let an allowed extension stand in for the
        // local, offline model without a deployment naming anything new.
        if (EmbeddingProviderFactory.BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"'{name}' is a built-in embedding provider, so an extension cannot register one under that name. The built-in names are {string.Join(", ", EmbeddingProviderFactory.BuiltInNames)}.");
        if (!_embeddingProviders.TryAdd(name, factory))
            throw new ArgumentException($"This extension registered two embedding providers named '{name}'. Names are compared ignoring case.");
    }

    /// <summary>
    /// Another way a person proves who they are, asked after every built-in
    /// way. An adapter resolves a person to an account that already exists: it
    /// creates no account, grants no role, and widens nothing, because the
    /// groups it reports become Premagentic groups only through the
    /// deployment's principal mapper, and mean nothing when there is none.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not usable.</exception>
    public void AddSignInAdapter(ISignInAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        RequireName(adapter.Name, "a sign-in adapter");
        _signInAdapters.Add(adapter);
    }

    /// <summary>
    /// Another place a reminders run is delivered, after the built-in sink,
    /// which keeps the run for the portal and cannot be replaced.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not usable, or it is the built-in sink's.</exception>
    public void AddReminderSink(IReminderSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        RequireName(sink.Name, "a reminder sink");
        if (sink.Name.Equals(TableReminderSink.SinkName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"'{sink.Name}' is the built-in reminder sink, so an extension cannot register one under that name.");
        _reminderSinks.Add(sink);
    }

    /// <summary>
    /// What principals from outside mean here: the groups a sign-in adapter
    /// reports and the principals a connector names. It is the one kind of
    /// registration that adds to what a caller holds, so the host bounds it:
    /// an answer counts only for a principal the host asked about and only as
    /// a live group an administrator made. A deployment has one mapper, so an
    /// extension registers one at most.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not usable, or this extension already registered a mapper.</exception>
    public void AddPrincipalMapper(IPrincipalMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        RequireName(mapper.Name, "a principal mapper");
        if (_principalMapper is not null)
            throw new ArgumentException(
                $"This extension registered two principal mappers, '{_principalMapper.Name}' and '{mapper.Name}'. A deployment has one.");
        _principalMapper = mapper;
    }

    /// <summary>
    /// A chunker's name rule, for every name an extension brings: they are all
    /// typed at a console and stored in configuration for the same reasons.
    /// Checking here rather than at the registry means the extension that
    /// brought an unusable name is the one refused, with a reason that says
    /// which name and why.
    /// </summary>
    private static void RequireName(string? name, string what)
    {
        if (name is null || !ChunkerRegistry.IsName(name))
            throw new ArgumentException(
                $"'{name}' cannot name {what}. A name is up to 64 letters, digits, dots, hyphens and underscores, starting with a letter or digit.");
    }

    internal IReadOnlyList<IDocumentReader> Readers => _readers;

    internal IReadOnlyList<IChunker> Chunkers => _chunkers;

    internal IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>> EmbeddingProviders => _embeddingProviders;

    internal IReadOnlyList<ISignInAdapter> SignInAdapters => _signInAdapters;

    internal IReadOnlyList<IReminderSink> ReminderSinks => _reminderSinks;

    internal IPrincipalMapper? PrincipalMapper => _principalMapper;

    internal bool IsEmpty =>
        _readers.Count == 0 && _chunkers.Count == 0 && _embeddingProviders.Count == 0 && _signInAdapters.Count == 0
        && _reminderSinks.Count == 0 && _commands.Count == 0 && _settings.Count == 0 && _principalMapper is null;

    private readonly List<ExtensionCommand> _commands = [];
    private readonly List<SettingDefinition> _settings = [];

    /// <summary>The longest usage text a command may have.</summary>
    public const int MaxUsageLength = 4_000;

    /// <summary>
    /// A <c>prem</c> command: a noun of this extension's own, or subcommands
    /// under a built-in noun that takes them (<see cref="BuiltInCommands.Open"/>).
    /// It adds and never replaces: a built-in noun that takes none, and a
    /// built-in subcommand, are refused. Its usage is held to what a built-in
    /// command's is: it names exactly the subcommands registered.
    /// </summary>
    /// <exception cref="ArgumentException">The command cannot be held, with a sentence saying why.</exception>
    public void AddCommand(ExtensionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CommandLine.IsWord(command.Noun))
            throw new ArgumentException(
                $"'{command.Noun}' cannot name a command. A command is lower-case letters, digits and hyphens, starting with a letter, up to {CommandLine.MaxWordLength} characters.");
        var noun = command.Noun;
        if (BuiltInCommands.Names.Contains(noun) && !BuiltInCommands.Open.ContainsKey(noun))
            throw new ArgumentException(
                $"prem {noun} is built in, and an extension adds subcommands only under {string.Join(", ", BuiltInCommands.Open.Keys)} or under a command of its own.");
        if (_commands.Any(c => c.Noun == noun))
            throw new ArgumentException($"This extension registered prem {noun} twice. Give every subcommand of it in one command.");
        if (string.IsNullOrWhiteSpace(command.Summary) || command.Summary.Contains('\n'))
            throw new ArgumentException($"prem {noun} needs a summary of one line.");
        if (string.IsNullOrWhiteSpace(command.Usage) || command.Usage.Length > MaxUsageLength)
            throw new ArgumentException($"prem {noun} needs a usage text of at most {MaxUsageLength:N0} characters.");
        if (command.Verbs is not { Count: > 0 } verbs)
            throw new ArgumentException($"prem {noun} registers no subcommand, so nothing could ever run.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var verb in verbs)
        {
            if (verb is null || verb.RunAsync is null || !CommandLine.IsWord(verb.Name))
                throw new ArgumentException(
                    $"'{verb?.Name}' cannot name a subcommand of prem {noun}. A subcommand is lower-case letters, digits and hyphens, starting with a letter.");
            if (!names.Add(verb.Name))
                throw new ArgumentException($"prem {noun} registers the subcommand '{verb.Name}' twice.");
            if (BuiltInCommands.Open.TryGetValue(noun, out var builtIn) && builtIn.Contains(verb.Name))
                throw new ArgumentException($"prem {noun} {verb.Name} is built in, and an extension adds a subcommand and never replaces one.");
        }

        // The usage is printed wherever the command is described, with the
        // line ends of the program, whatever the extension's source had.
        var usage = command.Usage.ReplaceLineEndings("\n");
        var named = CommandLine.SubcommandsIn(noun, usage);
        if (!named.SetEquals(names))
            throw new ArgumentException(
                $"The usage of prem {noun} names the subcommands {Words(named)} and the extension registers {Words(names)}. " +
                "A usage text names exactly the subcommands its command takes, on lines that start 'prem " + noun + " '.");
        // A copy of the verbs, so the extension cannot change what was checked.
        _commands.Add(command with { Usage = usage, Verbs = [.. verbs] });
    }

    /// <summary>
    /// A setting this extension defines, listed, read and changed with
    /// <c>prem settings</c> like a built-in one, and written to the change
    /// record. What the key means, what it takes and what applies when nothing
    /// is stored are the extension's to say. <c>prem settings</c>, the tuning
    /// settings store and the store a command is handed
    /// (<see cref="CommandContext.SettingsStore"/>) ask its
    /// <see cref="SettingDefinition.Problem"/> before they write a value; a
    /// <see cref="SettingsStore"/> made directly checks only that the key is
    /// defined. It is defined only while this extension is loaded.
    /// </summary>
    /// <exception cref="ArgumentException">The setting cannot be held, with a sentence saying why.</exception>
    public void AddSetting(SettingDefinition setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var key = setting.Key;
        if (!SettingsStore.IsKey(key))
            throw new ArgumentException($"'{key}' cannot name a setting. A setting key is lower-case letters, digits, dots and underscores.");
        // The trust settings decide what agents and people are served, and the
        // extension settings decide what code loads: neither is an extension's.
        if (key.StartsWith("trust.", StringComparison.Ordinal) || key.StartsWith("extensions.", StringComparison.Ordinal) || setting.IsTrust)
            throw new ArgumentException($"'{key}' is under trust. or extensions., which only PremAgentic itself defines.");
        if (SettingsCatalog.Contains(key))
            throw new ArgumentException($"'{key}' is a built-in setting, so an extension cannot register one under that name.");
        if (_settings.Any(s => s.Key == key))
            throw new ArgumentException($"This extension registered the setting '{key}' twice.");
        if (string.IsNullOrWhiteSpace(setting.Meaning) || string.IsNullOrWhiteSpace(setting.Accepts) || setting.Problem is null)
            throw new ArgumentException($"The setting '{key}' needs a meaning, what it accepts, and a check of its value.");
        if (setting.Default is null && string.IsNullOrWhiteSpace(setting.NotSet))
            throw new ArgumentException($"The setting '{key}' needs a default, or a few words saying what not set means.");
        if (setting.Default is { } fallback && setting.Problem(fallback) is { } problem)
            throw new ArgumentException($"The setting '{key}' refuses its own default: {problem}");
        _settings.Add(setting);
    }

    internal IReadOnlyList<ExtensionCommand> Commands => _commands;

    internal IReadOnlyList<SettingDefinition> Settings => _settings;

    private static string Words(IEnumerable<string> words) =>
        words.Any() ? string.Join(", ", words.Order(StringComparer.Ordinal)) : "none";
}
