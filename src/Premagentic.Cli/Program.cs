using Premagentic.Cli.Admin;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;

// A refusal that reaches the top of a command, such as an embedding provider
// that cannot be made or cannot be reached, ends it with one line and exit
// code 2 instead of a stack trace.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is not StartupRefusedException refused) return;
    Console.Error.WriteLine(args is ["search", ..]
        ? $"prem search stopped before it searched: {refused.Message}"
        : $"prem{(args.Length > 0 ? " " + args[0] : "")} stopped: {refused.Message}");
    Environment.Exit(StartupRefusedException.ExitCode);
};

var tenantKey = Environment.GetEnvironmentVariable("PREM_TENANT_KEY") ?? "default";
var tenantName = Environment.GetEnvironmentVariable("PREM_TENANT_NAME") ?? "Premagentic deployment";

// Help, the version, and a call a command's usage does not allow are answered
// here, before anything connects, so the command line can be learned where no
// database is configured.
if (Premagentic.Cli.Help.Answer(args, Console.Out, Console.Error, out var notBuiltIn) is { } answered) return answered;

if (args[0] == "setup") return await Premagentic.Cli.Setup.SetupCommand.RunAsync(args[1..]);
if (args[0] == "remove") return await Premagentic.Cli.Setup.RemoveCommand.RunAsync(args[1..]);

// With no database configured, or a credentials file that cannot be read, stop
// here with the sentence that says what to set and exit code 2, as the API does.
string connectionString;
try
{
    connectionString = PremagenticDatabase.ConnectionStringFromEnvironment();
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
{
    // With nothing configured no extension can load, so a call that names no
    // built-in command is answered from the built-ins alone, or with the
    // sentence that names the add-on a command of it comes with. A
    // configuration that is set and cannot be used is refused as it always
    // was, whatever the call.
    if (notBuiltIn is not null && NothingConfigured())
    {
        Console.Error.WriteLine(BusinessAddOn.Refusal(args, host: null) ?? notBuiltIn);
        return 1;
    }
    Console.Error.WriteLine($"prem cannot start: {ex.Message}");
    return 2;
}
await using var db = new PremagenticDatabase(connectionString);
// Every command migrates first. The runner holds a lock and applies only what
// is missing, so this is a no-op on a current schema and safe beside a running
// service.
IReadOnlyList<AppliedMigration> applied;
Guid tenantId;
try
{
    applied = await db.MigrateAsync();
    tenantId = await db.EnsureTenantAsync(tenantKey, tenantName);
}
catch (Exception ex) when (notBuiltIn is not null && ex is Npgsql.NpgsqlException or InvalidOperationException)
{
    // A call no built-in command takes waits for the extensions, which are
    // read from the database. One that cannot be reached, that this role may
    // not migrate, or whose schema this build does not know, stops it with one
    // line and exit code 2, the shape of every startup refusal.
    throw new StartupRefusedException($"The database cannot be used: {ex.Message}", ex);
}
var headingPrefix = Environment.GetEnvironmentVariable("PREM_HEADING_PREFIX") != "0";
// The same extensions the API loads, from the same two settings, so a chunker
// an extension adds is usable from the console as well.
var extensions = await ExtensionHosting.LoadAsync(new SettingsStore(db, tenantId),
    warn: line => Console.Error.WriteLine($"WARNING: {line}"));

// A call no built-in command takes: a command an extension added, or else the
// refusal the built-ins alone give, with what the extensions added under the
// same built-in command.
if (notBuiltIn is not null)
{
    if (await ExtensionCommands.RunAsync(extensions, args, db, tenantId, Console.Out, Console.Error) is { } ran) return ran;
    Console.Error.WriteLine(BusinessAddOn.Refusal(args, extensions)
        ?? (BuiltInCommands.Open.ContainsKey(args[0]) && ExtensionCommands.UsageFor(extensions, args[0]) is { } added
            ? $"{notBuiltIn}\n\n{added}"
            : notBuiltIn));
    return 1;
}

if (AdminCommands.Verbs.Contains(args[0]))
    return await AdminCommands.RunAsync(args, db, tenantId);
if (args[0] == SettingsCommands.Verb) return await SettingsCommands.RunAsync(args, db, tenantId, extensions);
if (args[0] == OAuthCommands.Verb) return await OAuthCommands.RunAsync(args, db, tenantId);
if (args[0] == SourcesCommands.Verb) return await SourcesCommands.RunAsync(args, db, tenantId, extensions.Chunkers);
if (args[0] == ExtensionsCommands.Verb) return await ExtensionsCommands.RunAsync(args, db, tenantId, extensions);
if (args[0] == ProfileCommands.Verb) return await ProfileCommands.RunAsync(args, db, tenantId, extensions.Chunkers, extensions.Settings);
if (args[0] == RemindersCommands.Verb) return await RemindersCommands.RunAsync(args, db, tenantId, extensions);

switch (args[0])
{
    case "migrate":
    case "init-db":
        if (applied.Count == 0)
            Console.WriteLine("Schema is current; nothing to apply.");
        foreach (var m in applied)
            Console.WriteLine($"Applied {m.Version:D4}_{m.Name} ({m.Schema}).");
        Console.WriteLine($"Tenant '{tenantKey}' ensured.");
        return 0;

    case "rebuild-index":
    {
        if (!args.Contains("--confirm"))
        {
            Console.Error.WriteLine(
                "Refusing to empty the index without --confirm. rebuild-index deletes every indexed document " +
                "and chunk (not the audit trail or any configuration); search returns nothing until ingest is " +
                "run again for each source.");
            return 1;
        }
        var (documents, chunks) = await db.ClearIndexAsync();
        Console.WriteLine(
            $"Index emptied: {documents} document(s) and {chunks} chunk(s) removed. Configuration and the audit " +
            "trail are unchanged. Run 'prem ingest' for each source to rebuild it.");
        return 0;
    }

    case "ingest":
        return await SourcesCommands.IngestAsync(args, db, tenantId, headingPrefix,
            chunkers: extensions.Chunkers, embeddingProviders: extensions.EmbeddingProviders,
            readers: extensions.Readers, principalMapper: extensions.PrincipalMapper);

    case "search":
    {
        if (args.Length < 2) { Console.Error.WriteLine(Premagentic.Cli.Help.Search); return 1; }
        var caller = await CallerOrExitAsync(args);
        if (caller is null) return 1;
        var scope = caller.Scope;
        var embedder = EmbeddingProviderFactory.FromEnvironment(extensions.EmbeddingProviders);
        using var _ = embedder as IDisposable;

        var historical = args.Contains("--historical");
        var topK = ParseInt(args, "--top") ?? 5;

        await using var searchRole = SearchRoleOrExit();
        // The tuning the deployment has stored; one that cannot be used keeps its default, with a warning.
        var storedTuning = new RetrievalSettingsLoader(db, problem => Console.Error.WriteLine(
            $"WARNING: the stored setting {problem.Key} is not used. {problem.Problem} Its default applies."));
        var search = new HybridSearch(db, embedder, headingPrefixSpace: headingPrefix, searchRole: searchRole, storedTuning: storedTuning);
        SearchResult result;
        try
        {
            result = await search.SearchAsync(tenantId, args[1],
                new SearchOptions(scope, topK, historical, Trust: await TrustAsync(caller)));
        }
        catch (QueryTooLongException tooLong)
        {
            Console.Error.WriteLine(tooLong.Message);
            return 1;
        }

        Console.WriteLine(
            $"{result.Hits.Count} hits in {result.ElapsedMs} ms " +
            $"(access={scope.AuditLabel}, historical={historical}):\n");
        foreach (var hit in result.Hits)
        {
            var lex = hit.LexicalRank is null ? "-"
                : hit.LexicalFallback ? $"~{hit.LexicalRank}" : hit.LexicalRank.ToString();
            Console.WriteLine(
                $"[{hit.FusedScore:F4}] {hit.Citation}  (lex={lex}, " +
                $"vec={hit.VectorRank?.ToString() ?? "-"}, " +
                $"dist={hit.CosineDistance?.ToString("F3") ?? "-"}, {hit.LifecycleStatus})");
            Console.WriteLine($"   {Fields(hit.TrustTier, hit.Authorship, hit.Stale, hit.ConceptId)}");
            Console.WriteLine($"   {Snippet(hit.Content)}\n");
        }
        return 0;
    }

    case "section":
    {
        if (args.Length < 2) { Console.Error.WriteLine(Premagentic.Cli.Help.Section); return 1; }
        var heading = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
        var caller = await CallerOrExitAsync(args);
        if (caller is null) return 1;
        var scope = caller.Scope;
        await using var searchRole = SearchRoleOrExit();
        DocumentSectionResult? section;
        try
        {
            section = await new SectionFetcher(db, searchRole).GetAsync(
                tenantId, new SearchOptions(scope, IncludeHistorical: args.Contains("--historical"), Trust: await TrustAsync(caller)),
                args[1], heading);
        }
        catch (QueryTooLongException tooLong)
        {
            Console.Error.WriteLine(tooLong.Message);
            return 1;
        }

        if (section is null)
        {
            Console.Error.WriteLine($"'{args[1]}' is not in the index, or is not readable as {scope.AuditLabel}.");
            return 1;
        }
        if (section.LifecycleGated)
        {
            Console.Error.WriteLine($"'{args[1]}' is {section.LifecycleStatus}; pass --historical for historical access.");
            return 1;
        }
        if (section.Chunks.Count == 0) { Console.Error.WriteLine($"No section matches '{heading}'."); return 1; }

        Console.WriteLine($"{section.Path}  ({section.LifecycleStatus}, {Fields(section.TrustTier, section.Authorship, section.Stale, section.ConceptId)})");
        Console.WriteLine();
        foreach (var chunk in section.Chunks)
        {
            Console.WriteLine($"## {chunk.HeadingPath}");
            Console.WriteLine(chunk.Content);
            Console.WriteLine();
        }
        return 0;
    }

    case "eval":
    {
        if (args.Length < 2) { Console.Error.WriteLine(Premagentic.Cli.Help.Eval); return 1; }
        // With no path given, the report goes to the current folder, not beside
        // the golden set: a golden set can live in a profile folder, and a file
        // the profile does not name would make its next apply refuse. For the
        // same reason no report is written into a folder that holds a profile.
        var reportPath = Path.GetFullPath(args.Length > 2 && !args[2].StartsWith("--") ? args[2]
            : $"report-{DateTime.Now:yyyy-MM-dd-HHmm}.md");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(reportPath)!, Premagentic.Core.Profiles.ProfileReader.ManifestFile)))
        {
            Console.Error.WriteLine(
                $"prem eval writes no report into {Path.GetDirectoryName(reportPath)}, which holds a profile: a file the " +
                "profile does not name would make its next apply refuse. Give a report path in another folder.");
            return 1;
        }
        var embedder = EmbeddingProviderFactory.FromEnvironment(extensions.EmbeddingProviders);
        using var _ = embedder as IDisposable;
        // A question the run could not ask is refused in one line, before
        // anything runs or any report is written. After the embedder, so a
        // model that cannot be loaded is still what a run meets first.
        if (EvalRunner.OverLongQuestion(args[1]) is { } tooLong)
        {
            Console.Error.WriteLine(tooLong);
            return 1;
        }
        // Ranked and judged under the tuning the deployment has stored, as a search is.
        var storedTuning = new RetrievalSettingsLoader(db, problem => Console.Error.WriteLine(
            $"WARNING: the stored setting {problem.Key} is not used. {problem.Problem} Its default applies."));
        var runner = new EvalRunner(
            new HybridSearch(db, embedder, headingPrefixSpace: headingPrefix, storedTuning: storedTuning), tenantId,
            new PrincipalNames(new IdentityStore(db, tenantId)));
        var exitCode = await runner.RunAsync(args[1], reportPath);
        Console.WriteLine($"Report written to {reportPath}");
        return exitCode;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        return 1;
}

async Task<CliCaller?> CallerOrExitAsync(string[] args)
{
    try
    {
        var (caller, error) = await CallerFlags.ResolveAsync(args, db, tenantId);
        if (error is not null) Console.Error.WriteLine(error);
        return caller;
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return null;
    }
}

// Reads go through the search role when the deployment has one, as the API's
// do. A configuration that names none is read as the application role.
static SearchRole? SearchRoleOrExit()
{
    try
    {
        return SearchRole.FromEnvironment();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"prem cannot start: {ex.Message}");
        Environment.Exit(2);
        return null;
    }
}

// A person at the console gets a person's trust policy, and an agent token its
// agent's, from the same place every other host asks.
Task<Premagentic.Core.Okf.TrustPolicy> TrustAsync(CliCaller caller) =>
    CallerPolicy.TrustAsync(db, tenantId, caller.Kind, caller.AgentMinimumTrustTier);

// What each passage is, whatever the policy that let it through.
static string Fields(Premagentic.Core.Okf.OkfTrustTier tier, Premagentic.Core.Okf.OkfAuthorship authorship, bool stale, string? conceptId) =>
    $"trust={(Enum.IsDefined(tier) ? Premagentic.Core.Okf.TrustPolicy.TierKey(tier) : "unverified")} " +
    $"authorship={authorship.ToString().ToLowerInvariant()} stale={(stale ? "yes" : "no")} concept={conceptId ?? "-"}";

static string? ParseValue(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--") ? args[idx + 1] : null;
}

static int? ParseInt(string[] args, string flag) =>
    int.TryParse(ParseValue(args, flag), out var n) ? n : null;

static string Snippet(string content)
{
    var oneLine = content.ReplaceLineEndings(" ").Trim();
    return oneLine.Length <= 220 ? oneLine : oneLine[..220] + "...";
}

// None of the three ways a database is named is set: the one case in which no
// extension could load whatever the database held.
static bool NothingConfigured() =>
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PREM_CONNECTION_STRING"))
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PREM_CREDENTIALS_FILE"))
    && Environment.GetEnvironmentVariable(PremagenticDatabase.DevelopmentSwitch) != "1";
