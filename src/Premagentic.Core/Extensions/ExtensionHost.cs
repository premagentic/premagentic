using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Identity.SignIn;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Reminders;

namespace Premagentic.Core.Extensions;

/// <summary>An extension this process loaded, as the health page and the CLI list it.</summary>
/// <param name="Sha256">
/// The hash measured from the bytes that were loaded, not the hash the manifest
/// claimed: the assembly's, or with listed files the one that covers them too
/// (<see cref="ExtensionManifest.ExtensionSha256"/>).
/// </param>
public sealed record LoadedExtension(string Name, string Version, string Sha256, string Folder);

/// <summary>Why an extension folder was passed over.</summary>
public enum ExtensionRefusal
{
    /// <summary>There is no manifest, it is not readable, or what it says cannot be used.</summary>
    BadManifest,

    /// <summary>The assembly does not hash to what the manifest claims.</summary>
    HashMismatch,

    /// <summary>No administrator put this name and this hash in the allow list.</summary>
    NotAllowed,

    /// <summary>It was built for a seam version this process does not offer.</summary>
    SeamTooNew,

    /// <summary>It is allowed and it is what it claims to be, but it could not be made to run.</summary>
    DidNotLoad,

    /// <summary>What it registers is named the same as something already loaded.</summary>
    NameTaken,

    /// <summary>It was built against a newer PremAgentic than this one.</summary>
    CoreTooNew,
}

/// <summary>An extension folder this process did not load, and why.</summary>
/// <param name="Name">The manifest's name, or the folder's when there is no usable manifest.</param>
/// <param name="Detail">One sentence a person can act on.</param>
public sealed record RefusedExtension(string Name, string Folder, ExtensionRefusal Reason, string Detail);

/// <summary>
/// The extensions this process loaded, and the one place the API, the CLI and
/// the portal build their registries from. A host is made once at startup and
/// shared: nothing here reloads, so what an installation is running cannot
/// change under a request.
/// <para>
/// An extension loads only when an administrator has put its name and its hash
/// in the allow list, and only when the bytes on disk still hash to that: the
/// assembly's, and every other file its manifest lists, and nothing from its
/// folder that the manifest does not list. Everything else in the folder is
/// refused with a reason and listed. A refusal is not an error: a deployment whose extension folder is
/// full of rubbish still starts and still serves, with exactly the built-in
/// behavior, which is the behavior an installation with no extensions folder
/// has.
/// </para>
/// </summary>
public sealed class ExtensionHost
{
    private ExtensionHost(
        string? folder,
        IReadOnlyList<LoadedExtension> loaded,
        IReadOnlyList<RefusedExtension> refused,
        ReaderRegistry readers,
        ChunkerRegistry chunkers,
        IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>> embeddingProviders,
        IReadOnlyList<ISignInAdapter> signInAdapters,
        IReadOnlyList<IReminderSink> reminderSinks,
        IReadOnlyList<ContributedCommand> commands,
        DeploymentSettings settings,
        IPrincipalMapper? principalMapper,
        string? principalMapperExtension)
    {
        Folder = folder;
        Loaded = loaded;
        Refused = refused;
        Readers = readers;
        Chunkers = chunkers;
        EmbeddingProviders = embeddingProviders;
        SignInAdapters = signInAdapters;
        ReminderSinks = reminderSinks;
        Commands = commands;
        Settings = settings;
        PrincipalMapper = principalMapper;
        PrincipalMapperExtension = principalMapperExtension;
    }

    /// <summary>The built-ins alone, for a host that loads no extensions.</summary>
    public static ExtensionHost BuiltIn { get; } = Load(null, []);

    /// <summary>
    /// Every extension in <paramref name="folder"/> that an administrator has
    /// allowed, in folder order. A folder that is null, blank or not there
    /// means no extensions and no error, and gives the built-ins alone.
    /// </summary>
    /// <param name="allowed">
    /// The name and assembly hash of each extension the administrator allowed,
    /// from the <c>extensions.allowed</c> setting. Empty allows none.
    /// </param>
    public static ExtensionHost Load(string? folder, IReadOnlyList<(string Name, string Sha256)> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        var loaded = new List<LoadedExtension>();
        var refused = new List<RefusedExtension>();
        var readers = new List<IDocumentReader>();
        var chunkers = new List<IChunker>();
        var embeddingProviders = new Dictionary<string, Func<IServiceProvider, IEmbeddingProvider>>(StringComparer.OrdinalIgnoreCase);
        var signInAdapters = new List<ISignInAdapter>();
        var reminderSinks = new List<IReminderSink>();
        var added = new Added();

        var root = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
        if (root is not null && Directory.Exists(root))
            // Folder order, so two runs of the same installation list the same
            // extensions in the same order and a report can be compared.
            foreach (var directory in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
                Consider(directory, allowed, loaded, refused, readers, chunkers, embeddingProviders, signInAdapters, reminderSinks, added);

        return new ExtensionHost(root, loaded, refused,
            new ReaderRegistry([.. readers]), new ChunkerRegistry([.. chunkers]), embeddingProviders, signInAdapters, reminderSinks,
            added.Commands, new DeploymentSettings(added.Settings), added.PrincipalMapper?.Mapper, added.PrincipalMapper?.Extension);
    }

    /// <summary>Where extensions were looked for, or null when none were.</summary>
    public string? Folder { get; }

    public IReadOnlyList<LoadedExtension> Loaded { get; }

    public IReadOnlyList<RefusedExtension> Refused { get; }

    /// <summary>The built-in readers and every reader the loaded extensions added.</summary>
    public ReaderRegistry Readers { get; }

    /// <summary>The built-in chunkers and every chunker the loaded extensions added.</summary>
    public ChunkerRegistry Chunkers { get; }

    /// <summary>
    /// Embedding providers the loaded extensions added, by name. A factory is
    /// called only if the deployment names its provider, so an extension that
    /// carries a model does not load it just by being installed.
    /// </summary>
    public IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>> EmbeddingProviders { get; }

    /// <summary>
    /// Ways of signing in the loaded extensions added, in folder order. A host
    /// asks them after every built-in way, so installing an extension cannot
    /// take over a way of signing in that already worked.
    /// </summary>
    public IReadOnlyList<ISignInAdapter> SignInAdapters { get; }

    /// <summary>
    /// Reminder sinks the loaded extensions added, in folder order. The
    /// built-in sink is not here: <see cref="RemindersJob"/> always delivers
    /// to it first, whichever host runs the job.
    /// </summary>
    public IReadOnlyList<IReminderSink> ReminderSinks { get; }

    /// <summary>
    /// What principals from outside mean here, when a loaded extension brought
    /// a mapper; null when none did, and then every group a sign-in adapter
    /// reports and every principal a connector names means nothing. A
    /// deployment has one: a second extension that brings a mapper is refused.
    /// </summary>
    public IPrincipalMapper? PrincipalMapper { get; }

    /// <summary>
    /// The name of the extension that brought <see cref="PrincipalMapper"/>, or
    /// null when there is none. The mapper is the one seam that adds to what a
    /// caller holds, so <c>prem extensions list</c> and the health page name
    /// it and where it came from.
    /// </summary>
    public string? PrincipalMapperExtension { get; }

    private static void Consider(
        string directory,
        IReadOnlyList<(string Name, string Sha256)> allowed,
        List<LoadedExtension> loaded,
        List<RefusedExtension> refused,
        List<IDocumentReader> readers,
        List<IChunker> chunkers,
        Dictionary<string, Func<IServiceProvider, IEmbeddingProvider>> embeddingProviders,
        List<ISignInAdapter> signInAdapters,
        List<IReminderSink> reminderSinks,
        Added added)
    {
        var label = Path.GetFileName(directory);
        if (!ExtensionManifest.TryRead(directory, out var manifest, out var problem))
        {
            refused.Add(new RefusedExtension(label, directory, ExtensionRefusal.BadManifest, problem!));
            return;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(directory, manifest!.AssemblyFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            refused.Add(new RefusedExtension(manifest!.Name, directory, ExtensionRefusal.BadManifest,
                $"The manifest names \"{manifest.AssemblyFile}\", which could not be read: {ex.Message}"));
            return;
        }

        // The hash is taken from the bytes that are loaded further down, and
        // those same bytes are what runs. Hashing the file and then loading the
        // path would leave a moment in which the file could be swapped for
        // another.
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!sha256.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.HashMismatch,
                $"\"{manifest.AssemblyFile}\" hashes to {sha256} and the manifest says {manifest.Sha256}."));
            return;
        }

        // Every other file the manifest lists, measured the same way and held
        // as the bytes that were measured, so a managed one is loaded from
        // exactly those bytes.
        var files = new List<VerifiedFile>();
        foreach (var listed in manifest.Files)
        {
            var path = Path.Combine([directory, .. listed.File.Split('/')]);
            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.BadManifest,
                    $"The manifest lists \"{listed.File}\", which could not be read: {ex.Message}"));
                return;
            }
            var fileSha256 = Convert.ToHexStringLower(SHA256.HashData(fileBytes));
            if (!fileSha256.Equals(listed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.HashMismatch,
                    $"\"{listed.File}\" hashes to {fileSha256} and the manifest says {listed.Sha256}."));
                return;
            }
            files.Add(new VerifiedFile(listed.File, path, fileSha256, fileBytes));
        }

        // A native library is loaded when a method first calls it, long after
        // startup, so one that is not listed would only be found then. Every
        // file under runtimes/ is therefore looked at now: one the manifest
        // does not list refuses the extension before anything of it runs.
        // So does a managed library built for one platform, listed or not:
        // this version loads managed files from beside the assembly only, so
        // it would never be loaded, and whatever stood beside it would load on
        // every platform instead.
        var runtimes = Path.Combine(directory, "runtimes");
        if (Directory.Exists(runtimes))
            foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (ExtensionManifest.IsPlatformManagedPath(relative))
                {
                    refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.BadManifest,
                        ExtensionManifest.PlatformManagedRefusal(relative)));
                    return;
                }
                if (files.Any(f => f.File.Equals(relative, StringComparison.OrdinalIgnoreCase))) continue;
                refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NotAllowed,
                    ExtensionManifest.UnlistedRuntimeRefusal(relative)));
                return;
            }

        // Against the hash that was computed, never the one the manifest
        // claimed: the manifest is the thing being checked. With listed files
        // this hash covers them too, so an allow list entry is a decision about
        // every byte the extension can load.
        var extensionSha256 = ExtensionManifest.ExtensionSha256(
            manifest.AssemblyFile, sha256, [.. files.Select(f => new ExtensionFile(f.File, f.Sha256))]);
        if (!allowed.Any(a =>
                a.Name.Equals(manifest.Name, StringComparison.OrdinalIgnoreCase)
                && a.Sha256.Equals(extensionSha256, StringComparison.OrdinalIgnoreCase)))
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NotAllowed,
                $"No entry in {ExtensionSettings.Allowed} names \"{manifest.Name}\" with hash {extensionSha256}."));
            return;
        }

        if (manifest.SeamTooNew(SeamVersions.Of) is { } tooNew)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.SeamTooNew, tooNew));
            return;
        }

        var registrations = new ExtensionRegistrations { Folder = directory };
        var context = new ExtensionLoadContext(manifest.Name, Path.Combine(directory, manifest.AssemblyFile), files);
        int found;
        try
        {
            found = Register(context, bytes, registrations);
        }
        catch (Exception ex)
        {
            // An extension that asked for a file it was not given is refused
            // for that, whatever the failure it caused looked like.
            if (context.Refusal is { } asked)
            {
                refused.Add(new RefusedExtension(manifest.Name, directory, asked.Reason, asked.Detail));
                return;
            }
            // An extension that cannot be made to run is passed over like any
            // other refusal. It is already known to be the assembly an
            // administrator allowed, so this is a defect in it, not an attack,
            // and it must not stop a deployment from serving.
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.DidNotLoad,
                $"\"{manifest.AssemblyFile}\" did not load: {ex.Message}"));
            return;
        }
        if (context.Refusal is { } refusal)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, refusal.Reason, refusal.Detail));
            return;
        }

        if (found == 0)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.DidNotLoad,
                $"No type in \"{manifest.AssemblyFile}\" implements {nameof(IExtension)}."));
            return;
        }
        if (registrations.IsEmpty)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.DidNotLoad,
                $"{found} type(s) in \"{manifest.AssemblyFile}\" implement {nameof(IExtension)} and registered nothing."));
            return;
        }

        if (loaded.Any(l => l.Name.Equals(manifest.Name, StringComparison.OrdinalIgnoreCase)))
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NameTaken,
                $"An extension named \"{manifest.Name}\" is already loaded from {loaded.First(l => l.Name.Equals(manifest.Name, StringComparison.OrdinalIgnoreCase)).Folder}."));
            return;
        }

        // One mapper per deployment, asked first: which extension decides what
        // an outside principal means is the one answer an administrator most
        // needs by name, so a second one is refused for that and not for
        // whatever else it happens to share with the first.
        if (registrations.PrincipalMapper is { } mapper && added.PrincipalMapper is { } kept)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NameTaken,
                $"A principal mapper, \"{kept.Mapper.Name}\", is already registered by the extension \"{kept.Extension}\", and a deployment has one. " +
                $"This one brought \"{mapper.Name}\"."));
            return;
        }

        // The registries decide their own names, so this asks them: the
        // registry is built with what is already held plus what this extension
        // brought, and a name they refuse refuses the extension that brought
        // it and nothing else. The result is thrown away and built once at the
        // end, over a handful of extensions.
        try
        {
            _ = new ReaderRegistry([.. readers, .. registrations.Readers]);
            _ = new ChunkerRegistry([.. chunkers, .. registrations.Chunkers]);
        }
        catch (ArgumentException ex)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NameTaken, ex.Message));
            return;
        }
        if (registrations.EmbeddingProviders.Keys.FirstOrDefault(embeddingProviders.ContainsKey) is { } taken)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NameTaken,
                $"An embedding provider named \"{taken}\" is already registered. Names are compared ignoring case."));
            return;
        }
        if (registrations.SignInAdapters.FirstOrDefault(
                a => signInAdapters.Any(held => held.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))) is { } claimed)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NameTaken,
                $"A sign-in adapter named \"{claimed.Name}\" is already registered. Names are compared ignoring case."));
            return;
        }

        if (registrations.ReminderSinks.FirstOrDefault(
                s => reminderSinks.Any(held => held.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))) is { } sink)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, ExtensionRefusal.NameTaken,
                $"A reminder sink named \"{sink.Name}\" is already registered. Names are compared ignoring case."));
            return;
        }

        if (added.Refusal(manifest, registrations) is { } clash)
        {
            refused.Add(new RefusedExtension(manifest.Name, directory, clash.Reason, clash.Detail));
            return;
        }

        readers.AddRange(registrations.Readers);
        chunkers.AddRange(registrations.Chunkers);
        foreach (var (name, factory) in registrations.EmbeddingProviders) embeddingProviders.Add(name, factory);
        signInAdapters.AddRange(registrations.SignInAdapters);
        reminderSinks.AddRange(registrations.ReminderSinks);
        added.Keep(manifest, registrations);
        loaded.Add(new LoadedExtension(manifest.Name, manifest.Version, extensionSha256, directory));
    }

    /// <summary>
    /// Loads the allowed bytes into their own context and lets every extension
    /// type in them register. Returns how many types were found, so an assembly
    /// that carries none is told apart from one that registered nothing.
    /// </summary>
    private static int Register(ExtensionLoadContext context, byte[] bytes, ExtensionRegistrations registrations)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var assembly = context.LoadFromStream(stream);

        // Every assembly the extension references, and theirs, is looked up now
        // rather than the first time a method needs it, so a file it would ask
        // for and is not given refuses it at startup instead of failing a
        // search later. One that is simply missing is left for the extension's
        // own code to fail on, as before.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>([assembly]);
        while (pending.TryDequeue(out var next))
            foreach (var reference in next.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name)) continue;
                try
                {
                    var resolved = context.LoadFromAssemblyName(reference);
                    if (AssemblyLoadContext.GetLoadContext(resolved) == context) pending.Enqueue(resolved);
                }
                catch (FileNotFoundException)
                {
                    // Missing from both places; the extension's own code meets that when it runs.
                }
                catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
                {
                    // Not loadable here; as above, it is the extension's to fail on.
                }
            }
        if (context.Refusal is not null) return 0;

        var found = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (type is not { IsClass: true, IsAbstract: false } || !typeof(IExtension).IsAssignableFrom(type)) continue;
            found++;
            ((IExtension)Activator.CreateInstance(type)!).Register(registrations);
        }
        return found;
    }

    /// <summary>
    /// The <c>prem</c> commands the loaded extensions added, in the order they
    /// loaded. The command line runs one for a call it does not have built in.
    /// </summary>
    public IReadOnlyList<ContributedCommand> Commands { get; }

    /// <summary>Every setting this deployment defines: the built-in ones and the ones the loaded extensions added.</summary>
    public DeploymentSettings Settings { get; }

    /// <summary>
    /// The commands and settings of the extensions accepted so far, and the
    /// checks that need them: a seam declared for what an extension registers,
    /// and no noun, subcommand or key another extension already brought.
    /// </summary>
    private sealed class Added
    {
        public List<ContributedCommand> Commands { get; } = [];

        public List<ContributedSetting> Settings { get; } = [];

        /// <summary>The deployment's principal mapper and the extension that brought it, once one has.</summary>
        public (IPrincipalMapper Mapper, string Extension)? PrincipalMapper { get; private set; }

        public (ExtensionRefusal Reason, string Detail)? Refusal(ExtensionManifest manifest, ExtensionRegistrations registrations)
        {
            // Declared, so an older version refuses the extension by the seam's
            // name rather than failing it on a member it does not have.
            if (registrations.Commands.Count > 0 && !manifest.Seams.ContainsKey(SeamVersions.CommandName))
                return (ExtensionRefusal.DidNotLoad, Undeclared("commands", SeamVersions.CommandName));
            if (registrations.Settings.Count > 0 && !manifest.Seams.ContainsKey(SeamVersions.SettingName))
                return (ExtensionRefusal.DidNotLoad, Undeclared("settings", SeamVersions.SettingName));
            if (registrations.PrincipalMapper is not null && !manifest.Seams.ContainsKey(SeamVersions.PrincipalsName))
                return (ExtensionRefusal.DidNotLoad, Undeclared("a principal mapper", SeamVersions.PrincipalsName));

            foreach (var command in registrations.Commands)
            {
                var own = !BuiltInCommands.Open.ContainsKey(command.Noun);
                foreach (var held in Commands.Where(c => c.Command.Noun == command.Noun))
                {
                    if (own)
                        return (ExtensionRefusal.NameTaken,
                            $"prem {command.Noun} is already a command of the extension \"{held.Extension}\".");
                    if (held.Command.Verbs.FirstOrDefault(v => command.Verbs.Any(n => n.Name == v.Name)) is { } taken)
                        return (ExtensionRefusal.NameTaken,
                            $"prem {command.Noun} {taken.Name} is already a command of the extension \"{held.Extension}\".");
                }
            }

            foreach (var setting in registrations.Settings)
                if (Settings.FirstOrDefault(s => s.Definition.Key == setting.Key) is { } held)
                    return (ExtensionRefusal.NameTaken,
                        $"The setting '{setting.Key}' is already defined by the extension \"{held.Extension}\".");
            return null;
        }

        public void Keep(ExtensionManifest manifest, ExtensionRegistrations registrations)
        {
            Commands.AddRange(registrations.Commands.Select(c => new ContributedCommand(manifest.Name, manifest.Version, c)));
            Settings.AddRange(registrations.Settings.Select(s => new ContributedSetting(manifest.Name, s)));
            if (registrations.PrincipalMapper is { } brought) PrincipalMapper = (brought, manifest.Name);
        }

        private static string Undeclared(string what, string seam) =>
            $"It registers {what} and its manifest does not declare the \"{seam}\" seam, which an older PremAgentic needs to refuse it by name.";
    }
}
