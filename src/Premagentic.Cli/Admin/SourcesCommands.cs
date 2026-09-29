using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Cli.Admin;

/// <summary>
/// <c>prem sources</c>, the sources registry, and <c>prem ingest</c>, which
/// reads a registered source by name or a folder given by hand. Both forms
/// record the run.
/// </summary>
internal static class SourcesCommands
{
    public const string Verb = "sources";

    public const string Usage = """
        prem sources add <name> <folder> [--prefix p] [--okf-bundle] [--undeclared-as-machine] [--chunker name]
        prem sources set <name> [--okf-bundle on|off] [--undeclared-as-machine on|off] [--chunker name]
                                [--owner <sign-in-name>] [--hosted yes|no]
        prem sources remove <name>
        prem sources list
        prem sources status <name>
        prem sources chunkers
        prem sources hosted-release --prefix p

          Registered folders, the settings each is read with, its runs, and the chunkers a source may
          name. Ingest one with prem ingest --source <name>; a change to how a source is read takes effect
          at its next ingest. Who may read its documents is the folder rule, not the source.

          --prefix p             add: the path its documents are indexed under (default: none)
          --okf-bundle           add: read it as an Open Knowledge Format bundle; set: on or off
          --undeclared-as-machine  add: in a bundle, a concept that does not say who wrote it is machine
                                 written and unverified; set: on or off
          --chunker name         add, set: how its documents are cut into chunks (default: markdown)
          --owner name           set: the user who owns it and is reminded of its stale and unreviewed
                                 documents; "" leaves it with no owner
          --hosted yes|no        set: whether its documents may be served to agents on a hosted model
                                 (default: yes)
          --prefix p             hosted-release: the folder of a hold whose source was removed. Removing a
                                 source keeps its documents indexed and its folder held; this lets them go
        """;

    public const string IngestUsage = """
        prem ingest <folder> [--public | --principals a,b | --entry "allow group:Staff" ...] [--prefix p]
                             [--okf-bundle] [--undeclared-as-machine] [--chunker name] [--allow-empty-source]
                             [--remove-unread]
        prem ingest --source <name> [--allow-empty-source] [--remove-unread]

          Reads a folder into the index: its documents are cut into chunks, embedded on this computer and
          stored, and the ones gone from the folder are removed. A folder given by hand is read with the
          flags given and registers nothing; --source reads a registered source (prem sources) with its
          own folder, prefix and settings. The run is recorded either way. Who may read the documents is
          decided by the folder rules, and a folder no rule covers is refused.

          --public               set this folder's rule to allow everyone, then ingest
          --principals a,b       set this folder's rule to allow these principals, then ingest. Principals
                                 are written by name: group:Staff, user:alice, everyone
          --entry "..."          set this folder's rule from these entries, one --entry each, in order,
                                 then ingest
          --prefix p             the path the documents are indexed under (default: none)
          --okf-bundle           read the folder as an Open Knowledge Format bundle
          --undeclared-as-machine  in a bundle, treat a concept that does not say who wrote it as machine
                                 written and unverified, so agents do not see it until a person signs it off
          --chunker name         how documents are cut into chunks (default: markdown; prem sources
                                 chunkers lists the others)
          --source name          read the registered source with this name; only --allow-empty-source
                                 and --remove-unread may be added
          --allow-empty-source   permit deleting every indexed document under the prefix when the folder
                                 yields nothing. Off by default, because an unmounted volume or a revoked
                                 permission looks exactly like an emptied folder
          --remove-unread        remove documents indexed earlier in a format no reader here reads now.
                                 Off by default: they are kept and counted, so a reader that was refused
                                 or disallowed costs nothing that was indexed
        """;

    /// <param name="chunkers">The chunkers a source may name, or null for the built-in ones.</param>
    public static async Task<int> RunAsync(string[] args, PremagenticDatabase db, Guid tenantId, ChunkerRegistry? chunkers = null)
    {
        var registry = new SourceRegistry(db, tenantId, chunkers);
        try
        {
            return args.ElementAtOrDefault(1) switch
            {
                "add" => await AddAsync(args, registry),
                "set" => await SetAsync(args, registry, db, tenantId),
                "remove" => await RemoveAsync(args, registry),
                "list" => await ListAsync(registry, new IngestRuns(db, tenantId), db, tenantId),
                "chunkers" => ListChunkers(chunkers ?? ChunkerRegistry.BuiltIn),
                "status" => await StatusAsync(args, registry, new IngestRuns(db, tenantId)),
                "hosted-release" => await HostedReleaseAsync(args, registry, db, tenantId),
                _ => Fail("Usage:\n" + Usage),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// <c>prem ingest</c>. <c>--source name</c> takes the folder, the prefix
    /// and how to read it from the registry; the long form takes them from its
    /// flags and registers nothing. Either way the documents take their access
    /// from the folder rules, and the run is recorded.
    /// </summary>
    /// <param name="embedder">The embedding provider, or null for the one the environment names.</param>
    /// <param name="chunkers">The chunkers a run may name, or null for the built-in ones.</param>
    /// <param name="embeddingProviders">The providers the loaded extensions registered, so a run can name one.</param>
    /// <param name="readers">The formats this process can read, or null for the built-in ones.</param>
    /// <param name="principalMapper">The principal mapper a loaded extension brought, or null for none.</param>
    public static async Task<int> IngestAsync(
        string[] args, PremagenticDatabase db, Guid tenantId, bool headingPrefix, IEmbeddingProvider? embedder = null,
        ChunkerRegistry? chunkers = null,
        IReadOnlyDictionary<string, Func<IServiceProvider, IEmbeddingProvider>>? embeddingProviders = null,
        ReaderRegistry? readers = null, Core.Identity.IPrincipalMapper? principalMapper = null)
    {
        FileSystemSource source;
        RegisteredSource? registered = null;
        string chunker;

        if (args.Contains("--source"))
        {
            var name = CliArgs.Value(args, "--source");
            if (name is null) return Fail("--source needs a source name.");
            string[] fromRegistry = ["--prefix", "--okf-bundle", "--undeclared-as-machine", "--chunker", "--public", "--principals", "--entry"];
            if (CliArgs.Positionals(args, 1, "--source").Count > 0 || fromRegistry.Any(args.Contains))
                return Fail(
                    "prem ingest --source takes the folder, the prefix and how to read it from the registry " +
                    "('prem sources set' changes them) and access from the folder rules ('prem rules set'). " +
                    "Only --allow-empty-source and --remove-unread may be added.");

            registered = await new SourceRegistry(db, tenantId).FindAsync(name);
            if (registered is null) return Fail($"There is no source named '{name}'. 'prem sources list' shows the registered ones.");
            source = registered.ToFileSystemSource();
            chunker = registered.Chunker;
        }
        else
        {
            if (args.Length < 2 || args[1].StartsWith("--")) return Fail(IngestUsage);
            // The folder's documents take their access from the folder rules. The
            // --public and --principals flags are a shorthand that sets this
            // folder's rule first.
            source = new FileSystemSource(args[1], DocumentAccess.FolderRules, CliArgs.Value(args, "--prefix") ?? "")
            {
                OkfBundle = args.Contains("--okf-bundle"),
                UndeclaredAuthorshipIsMachine = args.Contains("--undeclared-as-machine"),
            };
            if (args.Contains("--chunker") && CliArgs.Value(args, "--chunker") is null) return Fail("--chunker needs a chunker name.");
            chunker = CliArgs.Value(args, "--chunker") ?? ChunkerRegistry.DefaultName;
        }

        var rules = new AclStore(db, tenantId);
        try
        {
            var shorthand = registered is null ? AdminCommands.RuleEntries(args) : null;
            if (shorthand is not null)
            {
                await AdminCommands.SetRuleAsync(db, tenantId, source.Name, source.PathPrefix, shorthand);
            }
            else if (!(await rules.ListRulesAsync()).Any(r =>
                         r.Rule.Source == source.Name && AdminCommands.CanDecideUnder(r.Rule.PathPrefix, source.PathPrefix)))
            {
                Console.Error.WriteLine(
                    "Refusing to ingest a folder no rule covers: every document in it would be readable by nobody. " +
                    (registered is null ? "Pass --public or --principals a,b to set its rule, or set one" : "Set one") +
                    " with 'prem rules set'.");
                return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // The provider is made here, outside the try above, and must stay outside
        // it: a provider that cannot be made throws StartupRefusedException, an
        // InvalidOperationException, which that catch would print bare with exit
        // code 1 instead of the refusal's "prem ingest stopped:" and exit code 2.
        var owned = embedder is null;
        embedder ??= EmbeddingProviderFactory.FromEnvironment(embeddingProviders);
        using var _ = owned ? embedder as IDisposable : null;

        Console.WriteLine(
            $"Ingesting {(registered is null ? source.Root : $"source '{registered.Name}' ({source.Root})")}" +
            $"{(source.OkfBundle ? " as an OKF bundle" : "")} using " +
            $"{embedder.Name}{(headingPrefix ? IngestPipeline.HeadingPrefixSuffix : "")}, cut by the {chunker} chunker...");

        var pipeline = new IngestPipeline(db, embedder, headingPrefix, chunkers, readers)
        {
            RemoveUnread = args.Contains("--remove-unread"),
            PrincipalMapper = principalMapper,
        };
        RecordedIngestResult result;
        try
        {
            result = await new IngestRuns(db, tenantId).RunAsync(
                pipeline, source, registered?.Id, Console.WriteLine, args.Contains("--allow-empty-source"), chunker);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"{ex.Message}. The run is recorded as failed and nothing was changed.");
            return 1;
        }
        catch (UnknownChunkerException ex)
        {
            Console.Error.WriteLine(
                $"{ex.Message} The run is recorded as failed and nothing was changed. 'prem sources chunkers' lists them" +
                (registered is null ? "." : $"; 'prem sources set {registered.Name} --chunker <name>' changes the source's."));
            return 1;
        }

        var summary = result.Summary;
        Console.WriteLine(
            $"Scanned {summary.Scanned}, ingested {summary.Ingested}, unchanged {summary.Unchanged}, " +
            $"chunks embedded {summary.ChunksEmbedded}, orphans removed {summary.OrphansRemoved}, " +
            $"unreadable {summary.Unreadable}, skipped {summary.Skipped}.");
        if (summary.Skipped > 0)
            Console.WriteLine(
                $"Skipped {summary.Skipped} file(s), not read by this run: {Formats(summary.SkippedFormats)}. " +
                $"A format this version does not read is listed by extension; {IngestSummary.EmptyKey} is a file with no text to index.");
        if (summary.KeptWithoutReader > 0)
            Console.WriteLine(
                $"Kept {summary.KeptWithoutReader} document(s) indexed earlier in a format no reader here reads now; they are " +
                "still found by search. 'prem extensions list' says whether a reader was refused; add --remove-unread to remove them.");
        if (summary.RemovedNowSkipped > 0)
            Console.WriteLine(
                $"Removed {summary.RemovedNowSkipped} document(s) indexed earlier because the file is now skipped " +
                "(listed above as 'removed, now skipped'); they are counted in the orphans removed.");
        if (summary.DeniedToEveryone > 0)
            Console.WriteLine(
                $"WARNING: {summary.DeniedToEveryone} document(s) are indexed but readable by nobody. " +
                "They will never appear in an answer until a folder rule lets someone read them ('prem rules list').");
        if (result.BundleReport is { } bundle)
        {
            Console.WriteLine(
                $"OKF bundle: okf_version {bundle.OkfVersion ?? "(not declared)"}" +
                $"{(bundle.OkfVersion is not null && !bundle.VersionKnown ? " (unknown version, read best-effort)" : "")}, " +
                $"{bundle.Issues.Count} file(s) do not conform.");
            foreach (var issue in bundle.Issues.Take(20))
                Console.WriteLine($"  {issue.Path}: {issue.Problem}{(issue.Detail is null ? "" : $" ({issue.Detail})")}");
            Console.WriteLine(Undeclared(summary.UndeclaredAuthorship, source.UndeclaredAuthorshipIsMachine, registered?.Name));
        }
        if (summary.Unreadable > 0)
        {
            Console.WriteLine(
                $"WARNING: {summary.Unreadable} path(s) exist and could not be read. Their existing index " +
                "entries were preserved, so search still reflects the last successful read:");
            foreach (var f in summary.FailureList.Take(20))
                Console.WriteLine($"  {f.Path}: {f.Reason}");
            if (summary.Unreadable > 20)
                Console.WriteLine($"  ... and {summary.Unreadable - 20} more");
        }
        Console.WriteLine($"Run {result.RunId} recorded{(registered is null ? "" : $"; 'prem sources status {registered.Name}' shows it")}.");
        if (summary.ReconciliationSkipped)
        {
            // Non-zero, because a scheduled ingest that silently reports success
            // here is how an index quietly goes stale behind an unmounted volume.
            Console.Error.WriteLine(
                "ERROR: the source yielded nothing while documents are indexed under this prefix, so " +
                "deletion was refused and the index is unchanged. Check that the folder is mounted, " +
                "reachable and readable by this account. Pass --allow-empty-source if it really is empty.");
            return 2;
        }
        return 0;
    }

    private static async Task<int> AddAsync(string[] args, SourceRegistry registry)
    {
        var positionals = CliArgs.Positionals(args, 2, "--prefix", "--chunker");
        if (positionals.Count != 2) throw new ArgumentException("Give the source's name and its folder: prem sources add <name> <folder>.");

        var source = await registry.AddAsync(
            positionals[0], positionals[1], CliArgs.Value(args, "--prefix") ?? "",
            args.Contains("--okf-bundle"), args.Contains("--undeclared-as-machine"), AdminActor.Cli(),
            ChunkerFlag(args) ?? ChunkerRegistry.DefaultName);

        Console.WriteLine($"Source '{source.Name}' registered: {Describe(source)}.");
        if (source.UndeclaredIsMachine && !source.OkfBundle)
            Console.WriteLine("Note: --undeclared-as-machine has no effect unless the source is read as an OKF bundle.");
        Console.WriteLine($"Ingest it with 'prem ingest --source {source.Name}'. A folder rule decides who may read its documents ('prem rules set').");
        return 0;
    }

    private static async Task<int> SetAsync(
        string[] args, SourceRegistry registry, PremagenticDatabase db, Guid tenantId)
    {
        var name = Single(args, "a source name", "--okf-bundle", "--undeclared-as-machine", "--chunker", "--owner", "--hosted");
        var bundle = OnOff(args, "--okf-bundle");
        var undeclared = OnOff(args, "--undeclared-as-machine");
        var chunker = ChunkerFlag(args);
        var owner = CliArgs.Value(args, "--owner");
        var hosted = YesNo(args, "--hosted");
        if (bundle is null && undeclared is null && chunker is null && owner is null && hosted is null)
            throw new ArgumentException(
                "Say what to change: --okf-bundle on|off, --undeclared-as-machine on|off, --chunker <name>, " +
                "--owner <sign-in name>, --hosted yes|no.");

        if (owner is not null) await SetOwnerAsync(name, owner, registry, db, tenantId);
        if (hosted is not null) await SetHostedAsync(name, hosted.Value, registry, db, tenantId);
        if (bundle is null && undeclared is null && chunker is null) return 0;

        var (before, after, changed) = await registry.UpdateAsync(name, bundle, undeclared, AdminActor.Cli(), chunker);
        if (!changed) return Ok($"Source '{after.Name}' is already {Describe(after)}. Nothing changed and nothing was recorded.");

        Console.WriteLine($"Source '{after.Name}': {Describe(before)} is now {Describe(after)}.");
        Console.WriteLine(before.Chunker != after.Chunker
            ? $"It takes effect at the next 'prem ingest --source {after.Name}', which cuts, embeds and stores every " +
              "document again, since another chunker cuts other chunks from the same text."
            : $"It takes effect at the next 'prem ingest --source {after.Name}', which re-stores every document whose " +
              "trust values change, with no change to its content needed.");
        if (after.UndeclaredIsMachine && !after.OkfBundle)
            Console.WriteLine("Note: undeclared authorship has no effect unless the source is read as an OKF bundle.");
        return 0;
    }

    private static async Task<int> RemoveAsync(string[] args, SourceRegistry registry)
    {
        var name = Single(args, "a source name");
        var removed = await registry.RemoveAsync(name, AdminActor.Cli());
        if (removed is null) return Fail($"There is no source named '{name}'.");

        var indexed = await registry.IndexedDocumentCountAsync(removed);
        var prefix = removed.PathPrefix.Length == 0 ? "" : $" --prefix {removed.PathPrefix}";
        Console.WriteLine(
            $"Source '{removed.Name}' removed from the registry. Its run history is kept, and its {indexed} indexed " +
            "document(s) are NOT deleted: they stay searchable under their folder rules.");
        Console.WriteLine(
            $"To remove them, ingest an empty folder over the same prefix ('prem ingest <empty folder>{prefix} " +
            "--allow-empty-source'), or empty the whole index with 'prem rebuild-index --confirm' and ingest the " +
            "other sources again.");
        return 0;
    }

    private static int ListChunkers(ChunkerRegistry chunkers)
    {
        foreach (var name in chunkers.Names)
            Console.WriteLine(name == ChunkerRegistry.DefaultName ? $"{name} (the default)" : name);
        return 0;
    }

    private static async Task<int> ListAsync(
        SourceRegistry registry, IngestRuns runs, PremagenticDatabase db, Guid tenantId)
    {
        var sources = await registry.ListAsync();
        var exposure = new SourceExposureStore(db, tenantId);
        var identity = new IdentityStore(db, tenantId);
        if (sources.Count == 0)
            Console.WriteLine("No sources are registered. Add one with 'prem sources add <name> <folder>'.");
        foreach (var s in sources)
        {
            var last = await runs.LastAsync(s.Id);
            Console.WriteLine(
                $"{s.Name,-20} {Describe(s),-60} last run: " +
                (last is null ? "never" : $"{Outcome(last.Outcome)} {last.StartedAt:u}"));
            Console.WriteLine(
                $"{"",-20} {(await exposure.ExplainAsync(s.PathPrefix)).Words,-60} " +
                $"owner: {await OwnerNameAsync(identity, s)}");
        }

        // A hold outlives its source, because the documents stay indexed when
        // the source is removed. Listed here so it can be found and released.
        foreach (var hold in (await HostedHolds.ListAsync(db, tenantId)).Where(h =>
                     h.Source == SourceExposureStore.RuleSource && sources.All(s => s.PathPrefix != h.PathPrefix)))
            Console.WriteLine(
                $"{HostedHolds.Name(hold)} is held back from hosted-model agents and no source reads it. " +
                $"Release it with 'prem sources hosted-release --prefix \"{hold.PathPrefix}\"'.");
        return 0;
    }

    /// <summary>
    /// Releases a hold whose source was removed. Removing a source keeps its
    /// documents indexed under their folder rules, so its hold stays and keeps
    /// them from hosted models; this is the way to let them go, recorded as a
    /// turn of the switch. A folder a registered source reads is turned with
    /// <c>prem sources set &lt;name&gt; --hosted yes</c> instead.
    /// </summary>
    private static async Task<int> HostedReleaseAsync(string[] args, SourceRegistry registry, PremagenticDatabase db, Guid tenantId)
    {
        var prefix = SourceRegistry.NormalizePrefix(CliArgs.Value(args, "--prefix")
            ?? throw new ArgumentException("Say which hold: --prefix <folder>, or --prefix \"\" for the whole connector."));
        var hold = new HostedHold(SourceExposureStore.RuleSource, prefix);
        if ((await registry.ListAsync()).FirstOrDefault(s => s.PathPrefix == prefix) is { } live)
            return Fail($"The source '{live.Name}' reads {HostedHolds.Name(hold)}. Turn its switch with " +
                        $"'prem sources set {live.Name} --hosted yes'.");
        if (!(await HostedHolds.ListAsync(db, tenantId)).Contains(hold))
            return Fail($"There is no hold on {HostedHolds.Name(hold)}.");

        var turn = await new SourceExposureStore(db, tenantId).SetAsync(prefix, mayBeServed: true, AdminActor.Cli());
        return Ok($"The hold on {HostedHolds.Name(hold)} is released and recorded. {turn.Moved} indexed document(s) under it " +
                  "went back to the lists their rules give; one whose connector decided its list keeps the denial until its next ingest.");
    }

    /// <summary>The owner's sign-in name, or plain words when there is none.</summary>
    private static async Task<string> OwnerNameAsync(IdentityStore identity, RegisteredSource source) =>
        source.OwnerUserId is not { } id ? "nobody"
        : (await identity.FindUserAsync(id))?.Name ?? "a user who no longer signs in";

    private static async Task<int> StatusAsync(string[] args, SourceRegistry registry, IngestRuns runs)
    {
        var name = Single(args, "a source name");
        var source = await registry.FindAsync(name) ?? throw new ArgumentException($"There is no source named '{name}'.");

        Console.WriteLine($"Source '{source.Name}': {Describe(source)}.");
        Console.WriteLine($"Registered {source.CreatedAt:u}, last changed {source.UpdatedAt:u}.");
        Console.WriteLine($"Indexed now: {await registry.IndexedDocumentCountAsync(source)} document(s) under its prefix.");

        var last = await runs.LastAsync(source.Id);
        if (last is null)
            return Ok($"No ingest has run for this source yet ('prem ingest --source {source.Name}').");

        var took = last.FinishedAt is { } finished ? $", finished {finished:u} ({(finished - last.StartedAt).TotalSeconds:F0} s)" : ", not finished";
        Console.WriteLine($"Last run {last.Id}: {Outcome(last.Outcome)}, started {last.StartedAt:u}{took}.");
        if (last.Error is not null) Console.WriteLine($"  Error: {last.Error}");
        if (last.Summary is { } s)
        {
            Console.WriteLine(
                $"  Scanned {s.Scanned}, ingested {s.Ingested}, unchanged {s.Unchanged}, chunks embedded {s.ChunksEmbedded}, " +
                $"orphans removed {s.OrphansRemoved}, unreadable {s.Unreadable}, readable by nobody {s.DeniedToEveryone}, " +
                $"skipped {s.Skipped}{(s.Skipped > 0 ? $" ({Formats(s.SkippedFormats)})" : "")}.");
            if (s.KeptWithoutReader > 0 || s.RemovedNowSkipped > 0)
                Console.WriteLine(
                    $"  Kept without a reader {s.KeptWithoutReader}; removed because now skipped {s.RemovedNowSkipped} " +
                    "(of the orphans removed).");
            foreach (var f in s.FailureList.Take(10))
                Console.WriteLine($"  unreadable: {f.Path}: {f.Reason}");
        }
        if (last.OkfBundle)
        {
            Console.WriteLine(
                $"  OKF: okf_version {last.OkfVersion ?? "(not declared)"}" +
                $"{(last.OkfVersion is not null && last.OkfVersionKnown == false ? " (unknown version, read best-effort)" : "")}, " +
                $"{last.ConformanceIssues.Count} file(s) do not conform.");
            foreach (var issue in last.ConformanceIssues.Take(10))
                Console.WriteLine($"    {issue.Path}: {issue.Problem}{(issue.Detail is null ? "" : $" ({issue.Detail})")}");
            if (last.Summary is { } summary)
                Console.WriteLine("  " + Undeclared(summary.UndeclaredAuthorship, last.UndeclaredIsMachine, source.Name));
        }
        else
        {
            Console.WriteLine("  Not read as an OKF bundle, so no document is held to a declared author.");
        }
        return 0;
    }

    private static string Undeclared(int count, bool heldBack, string? sourceName)
    {
        if (count == 0) return "Every concept says who wrote it.";
        if (heldBack)
            return $"{count} concept(s) do not say who wrote them, and are treated as machine-written and unverified: " +
                   "agents do not see them until a person signs them off.";
        var how = sourceName is null
            ? "Passing --undeclared-as-machine"
            : $"'prem sources set {sourceName} --undeclared-as-machine on'";
        return $"{count} concept(s) do not say who wrote them, and are served as ordinary files. {how} would hold them " +
               "back from agents until a person signs them off.";
    }

    private static string Describe(RegisteredSource s) =>
        $"folder {s.Folder}, prefix {(s.PathPrefix.Length == 0 ? "(whole index)" : s.PathPrefix)}, " +
        $"{(s.OkfBundle ? "OKF bundle" : "plain folder")}, undeclared authorship {(s.UndeclaredIsMachine ? "machine" : "unknown")}, " +
        $"chunker {s.Chunker}";

    private static string Formats(IReadOnlyDictionary<string, int> byExtension) =>
        string.Join(", ", byExtension.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key} {kv.Value}"));

    private static string Outcome(IngestRunOutcome outcome) => outcome switch
    {
        IngestRunOutcome.Completed => "completed",
        IngestRunOutcome.ReconciliationRefused => "completed, deletion refused",
        IngestRunOutcome.Failed => "failed",
        _ => "running or stopped",
    };

    /// <summary>The value of <c>--chunker</c>, or null when it is absent.</summary>
    private static string? ChunkerFlag(string[] args)
    {
        if (!args.Contains("--chunker")) return null;
        return CliArgs.Value(args, "--chunker") ?? throw new ArgumentException("--chunker needs a chunker name ('prem sources chunkers' lists them).");
    }

    private static bool? OnOff(string[] args, string flag)
    {
        if (!args.Contains(flag)) return null;
        return CliArgs.Value(args, flag) switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgumentException($"{flag} takes on or off."),
        };
    }

    private static bool? YesNo(string[] args, string flag)
    {
        if (!args.Contains(flag)) return null;
        return CliArgs.Value(args, flag) switch
        {
            "yes" => true,
            "no" => false,
            _ => throw new ArgumentException($"{flag} takes yes or no."),
        };
    }

    /// <summary>
    /// Names the person answerable for a source, or clears it with an empty
    /// value. The owner is a name to ask, never a permission: what anyone may
    /// read stays with the folder rules.
    /// </summary>
    private static async Task SetOwnerAsync(
        string source, string owner, SourceRegistry registry, PremagenticDatabase db, Guid tenantId)
    {
        Guid? id = null;
        if (owner.Length > 0)
            id = (await new IdentityStore(db, tenantId).FindUserByNameAsync(owner))?.Id
                 ?? throw new ArgumentException($"No user signs in as '{owner}'.");

        var (_, after, changed) = await registry.SetOwnerAsync(source, id, AdminActor.Cli());
        Console.WriteLine(!changed
            ? $"Source '{after.Name}' already had that owner. Nothing changed and nothing was recorded."
            : id is null
                ? $"Source '{after.Name}' has no owner now. It decides nothing either way; it is who the review queue names."
                : $"Source '{after.Name}' is owned by {owner} now. It decides nothing: who may read it is the folder rule.");
    }

    /// <summary>
    /// Turns the "may be served to hosted models" switch: off holds the
    /// source's folder back from hosted-model agents, whatever rule decides
    /// the documents in it, and on releases it.
    /// </summary>
    private static async Task SetHostedAsync(
        string source, bool mayBeServed, SourceRegistry registry, PremagenticDatabase db, Guid tenantId)
    {
        var registered = await registry.FindAsync(source)
            ?? throw new ArgumentException($"There is no source named '{source}'.");

        var store = new SourceExposureStore(db, tenantId);
        var turn = await store.SetAsync(registered.PathPrefix, mayBeServed, AdminActor.Cli());
        var now = (await store.ExplainAsync(registered.PathPrefix)).Words;

        Console.WriteLine(
            turn.Changed ? $"Source '{registered.Name}': {SourceExposure.Describe(turn.Before)} is now {now}."
            : turn.Moved > 0 ? $"Source '{registered.Name}' already {now}. {turn.Moved} indexed document(s) under it " +
                               "stored without the denial were stamped again, and that is recorded."
            : $"Source '{registered.Name}' already {now}. Nothing changed and nothing was recorded.");
        if (turn.Changed && !mayBeServed)
            Console.WriteLine(
                "It takes effect on the next search, for every folder beneath it too, and nothing has to be ingested again.");
    }

    private static string Single(string[] args, string what, params string[] flagsWithValues)
    {
        var positionals = CliArgs.Positionals(args, 2, flagsWithValues);
        return positionals.Count == 1 ? positionals[0] : throw new ArgumentException($"Give {what}.");
    }

    private static int Ok(string message)
    {
        Console.WriteLine(message);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
