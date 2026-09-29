// The PremAgentic benchmark. Synthetic data only, in a throwaway database;
// every number it prints depends on the machine. See README.md.
//
//   synth --chunks N [--seed S]                           fills an empty index with N synthetic chunks
//   measure [--search-role] [--rounds R] [--seconds T]     the permitted reads, the vector leg, a whole search, five searchers
//   spin --on|--off [--search-role] [--rounds R]           a whole search with ONNX Runtime's spin-waiting on or off
//   ingest [--documents D] [--rounds R] [--spin off|on|both] documents and chunks a second through the ingest path
//
// Without --search-role the reads connect as the database user given and no
// caller session is opened, so only the SQL gate filters. With it, they run as
// an installed deployment's do: as the search role prem setup made, with a
// caller session for every search and the row policies judging every row.
// ingest writes as whoever it connects as, and its title names that role.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Premagentic.Benchmark;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Retrieval.Gates;
using Premagentic.Core.Security;
using Premagentic.Core.Security.Acl;
using Premagentic.Core.Sources;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

const string Tenant = "benchmark";
const string SyntheticPrefix = "synthetic/";
const string IngestPrefix = "synthetic/ingest";

if (args.Length == 0 || args[0] is not ("synth" or "measure" or "spin" or "ingest"))
{
    Console.Error.WriteLine("usage: synth --chunks N [--seed S] | measure [--search-role] [--rounds R] [--seconds T] | " +
                            "spin --on|--off [--search-role] [--rounds R] | ingest [--documents D] [--rounds R] [--spin off|on|both]");
    return 64;
}

await using var db = new PremagenticDatabase(PremagenticDatabase.ConnectionStringFromEnvironment());
await db.InitializeAsync();
var tenantId = await db.EnsureTenantAsync(Tenant, "Benchmark");

// The role the reads run as: none (the SQL gate alone), or the search role
// prem setup made, which the benchmark refuses unless the policy binds it.
SearchRole? searchRole = null;
if (args.Contains("--search-role"))
{
    if (args[0] is "synth" or "ingest")
        throw new ArgumentException($"{args[0]} writes the index as the role it connects as; --search-role is for measure and spin.");
    searchRole = SearchRole.FromEnvironment()
                 ?? throw new InvalidOperationException("--search-role reads as the search role prem setup made: set PREM_CREDENTIALS_FILE to the app.credentials it wrote.");
    if (await searchRole.VerifyAsync() is { } unbound)
        throw new InvalidOperationException($"The search role is not bound by the row policies, so this would not measure a deployment: {unbound}");
}
var mode = searchRole is null
    ? "the SQL gate alone: no search role, no caller session"
    : "as a deployment reads: the search role, a caller session for every search";

switch (args[0])
{
    case "synth":
        await Synth(int.Parse(Option("--chunks") ?? throw new ArgumentException("synth needs --chunks N"), CultureInfo.InvariantCulture),
            int.Parse(Option("--seed") ?? Synthetic.DefaultSeed.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        break;
    case "measure":
        await Measure(int.Parse(Option("--rounds") ?? "5", CultureInfo.InvariantCulture), int.Parse(Option("--seconds") ?? "20", CultureInfo.InvariantCulture));
        break;
    case "spin":
        var on = args.Contains("--on");
        if (on == args.Contains("--off")) throw new ArgumentException("spin needs exactly one of --on and --off");
        await Spin(on, int.Parse(Option("--rounds") ?? "5", CultureInfo.InvariantCulture));
        break;
    case "ingest":
        var spinning = Option("--spin") ?? "both";
        if (spinning is not ("off" or "on" or "both")) throw new ArgumentException("--spin is off, on or both");
        await Ingest(int.Parse(Option("--documents") ?? "200", CultureInfo.InvariantCulture),
            int.Parse(Option("--rounds") ?? "5", CultureInfo.InvariantCulture), spinning);
        break;
}
return 0;

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

// ---- synth ----

async Task Synth(int chunks, int seed)
{
    if (chunks <= 0) throw new ArgumentException("--chunks must be above zero");
    await using (var any = db.DataSource.CreateCommand("SELECT count(*) FROM prem_index.document"))
        if ((long)(await any.ExecuteScalarAsync())! > 0)
            throw new InvalidOperationException(
                "synth fills an empty index, and this database's index holds documents. Point PREM_CONNECTION_STRING at a throwaway database.");

    // Labeled with the name a search embeds its queries under, so a real query
    // embedding searches these vectors.
    var model = NewSearch(EmbeddingProviderFactory.FromEnvironment(null)).EmbeddingModelName;
    var everyone = await new AclStore(db, tenantId).EnsureSetAsync(AclSet.Of(AclEntry.Allow(Principal.Everyone)));

    var clock = Stopwatch.StartNew();
    var documents = await Synthetic.LoadAsync(db, tenantId, everyone, model, chunks, seed);
    Console.WriteLine($"synthetic: {chunks} chunks in {documents} documents, seed {seed}, model {model}, written in {clock.Elapsed.TotalSeconds:F0} s");
}

// ---- measure ----

HybridSearch NewSearch(IEmbeddingProvider embedder) => new(db, embedder, headingPrefixSpace: true, searchRole: searchRole);

async Task<(int Chunks, int Documents, string Model)> Index()
{
    await using var cmd = db.DataSource.CreateCommand("""
        SELECT count(DISTINCT d.id), count(c.id), min(c.embedding_model), count(*) FILTER (WHERE d.path NOT LIKE 'synthetic/%')
        FROM prem_index.document d LEFT JOIN prem_index.chunk c ON c.document_id = d.id
        """);
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    if (r.GetInt64(3) > 0)
        throw new InvalidOperationException($"This index holds documents synth did not make (a path outside {SyntheticPrefix}). The benchmark reads only its own synthetic index.");
    if (r.GetInt64(1) == 0) throw new InvalidOperationException("The index is empty. Run synth first.");
    return ((int)r.GetInt64(1), (int)r.GetInt64(0), r.GetString(2));
}

async Task Measure(int rounds, int seconds)
{
    var (chunks, documents, stored) = await Index();
    var embedder = EmbeddingProviderFactory.FromEnvironment(null);
    var search = NewSearch(embedder);
    var model = search.EmbeddingModelName;
    if (model != stored) throw new InvalidOperationException($"The synthetic chunks are labeled {stored} and this model searches {model}. Run synth with the same model.");

    var questions = Synthetic.Questions(Synthetic.DefaultSeed, 20);
    await Warm(search, questions);

    // The scope and gates a public-only search reads with, resolved as a search resolves them.
    var scope = await PermittedSetReader.ResolveAsync(db, tenantId, AccessScope.PublicOnly);
    var gate = new GateContext(new SearchOptions(scope));
    var vectors = new Dictionary<string, float[]>();
    foreach (var q in questions) vectors[q] = (await embedder.EmbedAsync([q]))[0];

    var rows = new List<(string, List<double>, string)>();
    long permittedDocuments = 0, permittedChunks = 0;

    if (searchRole is not null)
    {
        // What every search pays before its first read in a deployment: a
        // caller session, and the search role's connection bound to it.
        var opening = new List<double>();
        for (var i = 0; i < rounds * questions.Length + 3; i++)
        {
            var sw = Stopwatch.StartNew();
            await using (await BoundRead.OpenAsync(db, searchRole, tenantId, scope, CancellationToken.None))
            {
            }
            if (i >= 3) opening.Add(sw.Elapsed.TotalMilliseconds);
        }
        rows.Add(("Opening a caller session and binding the read to it", opening, "once per search"));
    }

    await using (var read = await BoundRead.OpenAsync(db, searchRole, tenantId, scope, CancellationToken.None))
    {
        // The read a search makes today: every permitted document with its version.
        var shipped = new List<double>();
        for (var i = 0; i < rounds * questions.Length + 3; i++)
        {
            var sw = Stopwatch.StartNew();
            permittedDocuments = await Count(read, $"SELECT d.id, d.updated_at FROM prem_index.document d WHERE d.tenant_id = @tenant AND {GateSet.Default.Sql}", gate, null);
            if (i >= 3) shipped.Add(sw.Elapsed.TotalMilliseconds);
        }
        rows.Add(("Permitted read: document ids, as a search reads them", shipped, $"{permittedDocuments} documents"));

        // The read the vector leg made before it read documents: every
        // permitted chunk id, through the same gates.
        var before = new List<double>();
        for (var i = 0; i < rounds * questions.Length + 3; i++)
        {
            var sw = Stopwatch.StartNew();
            permittedChunks = await Count(read,
                $"SELECT c.id FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id WHERE d.tenant_id = @tenant AND {GateSet.Default.Sql} AND c.embedding_model = @model",
                gate, model);
            if (i >= 3) before.Add(sw.Elapsed.TotalMilliseconds);
        }
        rows.Add(("Permitted read: chunk ids, the read before the fix", before, $"{permittedChunks} chunk ids"));
    }

    var leg = new List<double>();
    var embed = new List<double>();
    var whole = new List<double>();
    for (var r = 0; r < rounds; r++)
        foreach (var q in questions)
        {
            var sw = Stopwatch.StartNew();
            await search.Vectors.SearchAsync(tenantId, model, vectors[q], gate, 20);
            leg.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            await embedder.EmbedAsync([q]);
            embed.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            await search.SearchAsync(tenantId, q, Options());
            whole.Add(sw.Elapsed.TotalMilliseconds);
        }
    rows.Add(("Vector leg alone (its permitted read included)", leg, "top 20"));
    rows.Add(("Query embedding alone", embed, ""));
    rows.Add(("Whole search", whole, "top 5 of a pool of 20"));

    var (together, perSecond) = await Searchers(search, questions, 5, seconds);
    rows.Add(("Five searchers at once, each search", together, $"{perSecond:F1} searches a second over {seconds} s"));

    Print($"measure, {mode}: {chunks} chunks in {documents} documents, {rounds} rounds of {questions.Length} questions", rows);
}

async Task<(List<double>, double)> Searchers(HybridSearch search, string[] questions, int workers, int seconds)
{
    var all = new List<double>();
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    var wall = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
    {
        var mine = new List<double>();
        for (var i = w; DateTime.UtcNow < deadline; i++)
        {
            var sw = Stopwatch.StartNew();
            await search.SearchAsync(tenantId, questions[i % questions.Length], Options());
            mine.Add(sw.Elapsed.TotalMilliseconds);
        }
        lock (all) all.AddRange(mine);
    })));
    return (all, all.Count / wall.Elapsed.TotalSeconds);
}

async Task<long> Count(BoundRead read, string sql, GateContext gate, string? model)
{
    await using var cmd = read.Command(sql);
    cmd.Parameters.AddWithValue("tenant", tenantId);
    if (model is not null) cmd.Parameters.AddWithValue("model", model);
    GateSet.Default.AddParameters(cmd, gate);
    long rows = 0;
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) rows++;
    return rows;
}

static SearchOptions Options() => new(AccessScope.PublicOnly);

// Every vector loaded, then every question asked five times and a pause, so
// the runtime has compiled the search's hot paths fully before anything is
// timed. A process timed after one pass measures code that is still being
// recompiled, and two verbs that warm differently disagree about the same
// search.
async Task Warm(HybridSearch search, string[] questions)
{
    await search.WarmUpAsync();
    for (var round = 0; round < 5; round++)
        foreach (var q in questions) await search.SearchAsync(tenantId, q, Options());
    await Task.Delay(TimeSpan.FromSeconds(2));
}

// ---- spin ----

async Task Spin(bool spinning, int rounds)
{
    var (chunks, documents, _) = await Index();
    var embedder = EmbeddingProviderFactory.FromEnvironment(null);
    var how = OnnxSpinning.Set(embedder, spinning);
    var search = NewSearch(embedder);
    var questions = Synthetic.Questions(Synthetic.DefaultSeed, 20);
    await Warm(search, questions);

    var whole = new List<double>();
    var embed = new List<double>();
    for (var r = 0; r < rounds; r++)
        foreach (var q in questions)
        {
            var sw = Stopwatch.StartNew();
            await search.SearchAsync(tenantId, q, Options());
            whole.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            await embedder.EmbedAsync([q]);
            embed.Add(sw.Elapsed.TotalMilliseconds);
        }

    Print($"spin, {mode}: ONNX Runtime spin-waiting {(spinning ? "on" : "off")} ({how}), {chunks} chunks in {documents} documents, {rounds} rounds of {questions.Length} questions",
        [("Whole search", whole, ""), ("Query embedding alone", embed, "")]);
}

// ---- ingest ----

// The ingest path a deployment runs, timed: a folder of synthetic Markdown
// files through FileSystemSource and the pipeline's own steps (the read with
// its size cap, the storable-text guard, the Markdown chunker, the embedding,
// the insert), taking the folder rule's access and recorded as a run, as prem
// ingest does. The batch goes into an index synth filled to the size under
// test, under a prefix of its own, and the pipeline removes it again after
// every round, untimed, by reconciling an empty folder under that prefix.
async Task Ingest(int batch, int rounds, string spin)
{
    if (batch <= 0 || rounds <= 0) throw new ArgumentException("--documents and --rounds must be above zero");
    var (chunks, documents, _) = await Index();
    await using (var left = db.DataSource.CreateCommand($"SELECT count(*) FROM prem_index.document WHERE path LIKE '{IngestPrefix}/%'"))
        if ((long)(await left.ExecuteScalarAsync())! > 0)
            throw new InvalidOperationException($"This index holds documents under {IngestPrefix}/ from an ingest that did not finish. Start from a fresh synth.");

    var (role, superuser) = await ConnectedRole();
    var how = superuser
        ? $"as {role}, a superuser, which a deployment's ingest never is"
        : Environment.GetEnvironmentVariable("PREM_CREDENTIALS_FILE") is { Length: > 0 }
            ? $"as {role}, from the credentials file prem setup wrote, as a deployment ingests"
            : $"as {role}";

    var embedder = EmbeddingProviderFactory.FromEnvironment(null);
    // As prem ingest reads it: passages embedded under their headings unless
    // PREM_HEADING_PREFIX is 0.
    var pipeline = new IngestPipeline(db, embedder, Environment.GetEnvironmentVariable("PREM_HEADING_PREFIX") != "0");
    var runs = new IngestRuns(db, tenantId);
    var rules = new AclStore(db, tenantId);
    var folder = Directory.CreateTempSubdirectory("prem-bench-ingest-");
    var empty = Directory.CreateTempSubdirectory("prem-bench-empty-");
    var source = new FileSystemSource(folder.FullName, DocumentAccess.FolderRules, IngestPrefix);
    var nothing = new FileSystemSource(empty.FullName, DocumentAccess.FolderRules, IngestPrefix);
    await rules.SetRuleAsync(new FolderRule(source.Name, IngestPrefix, AclSet.Of(AclEntry.Allow(Principal.Everyone))));
    try
    {
        Synthetic.WriteFiles(folder.FullName, batch, Synthetic.DefaultSeed + 2);
        var rows = new List<(string, List<double>, string)>();
        foreach (var on in spin == "both" ? [false, true] : new[] { spin == "on" })
        {
            // Off first: spinning can be turned on in this process, not back off.
            var session = OnnxSpinning.Set(embedder, on);
            // One untimed round, so the model's session and the runtime's
            // compiled code are ready before anything is timed.
            await Round();
            var perDocument = new List<double>();
            var perChunk = new List<double>();
            double seconds = 0, read = 0, embedded = 0;
            for (var r = 0; r < rounds; r++)
            {
                var (elapsed, summary) = await Round();
                perDocument.Add(elapsed.TotalMilliseconds / summary.Ingested);
                perChunk.Add(elapsed.TotalMilliseconds / summary.ChunksEmbedded);
                seconds += elapsed.TotalSeconds;
                read += summary.Ingested;
                embedded += summary.ChunksEmbedded;
            }
            var state = on ? "on" : "off";
            rows.Add(($"Ingest, spinning {state}: a document of ten chunks", perDocument,
                $"{read / seconds:F1} documents a second ({session})"));
            rows.Add(($"Ingest, spinning {state}: one chunk", perChunk, $"{embedded / seconds:F1} chunks a second"));
        }
        Print($"ingest, {how}: {rounds} rounds of {batch} documents of ten chunks into an index of {chunks} chunks in {documents} documents, " +
              "each read, guarded, chunked, embedded and inserted by the pipeline and recorded as a run", rows);
    }
    finally
    {
        await runs.RunAsync(pipeline, nothing, null, allowEmptySource: true);
        await rules.RemoveRuleAsync(source.Name, IngestPrefix);
        folder.Delete(recursive: true);
        empty.Delete(recursive: true);
    }

    // One batch through the pipeline, timed, then removed, untimed. A round
    // that did not read, chunk and embed the whole batch is not a measure.
    async Task<(TimeSpan, IngestSummary)> Round()
    {
        var clock = Stopwatch.StartNew();
        var result = await runs.RunAsync(pipeline, source, null);
        clock.Stop();
        var summary = result.Summary;
        if (summary.Ingested != batch || summary.ChunksEmbedded != batch * 10 || summary.Unreadable != 0 || summary.Skipped != 0)
            throw new InvalidOperationException(
                $"A round read {summary.Ingested} of {batch} documents into {summary.ChunksEmbedded} chunks, with {summary.Unreadable} unreadable and {summary.Skipped} skipped; " +
                $"it should read every document into ten chunks.");
        var removed = await runs.RunAsync(pipeline, nothing, null, allowEmptySource: true);
        if (removed.Summary.OrphansRemoved != batch)
            throw new InvalidOperationException($"Removing the batch removed {removed.Summary.OrphansRemoved} of {batch} documents.");
        return (clock.Elapsed, summary);
    }
}

async Task<(string Role, bool Superuser)> ConnectedRole()
{
    await using var cmd = db.DataSource.CreateCommand("SELECT current_user, rolsuper FROM pg_roles WHERE rolname = current_user");
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    return (r.GetString(0), r.GetBoolean(1));
}

// ---- output ----

static void Print(string title, IEnumerable<(string Measure, List<double> Times, string Note)> rows)
{
    Console.WriteLine($"**{title}**");
    Console.WriteLine();
    Console.WriteLine("| Measure | Median | p90 | Runs | |");
    Console.WriteLine("|---|---|---|---|---|");
    foreach (var (measure, times, note) in rows)
        Console.WriteLine($"| {measure} | {Percentile(times, 0.5):F1} ms | {Percentile(times, 0.9):F1} ms | {times.Count} | {note} |");
    Console.WriteLine();
    Console.WriteLine(Machine.Line());
}

static double Percentile(List<double> times, double p)
{
    var sorted = times.Order().ToArray();
    return sorted.Length == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
}

namespace Premagentic.Benchmark
{
    using NpgsqlTypes;

    /// <summary>
    /// The synthetic index: invented words, random unit vectors, and one seed
    /// for both, so two loads of the same size and seed hold the same chunks.
    /// Ten chunks to a document, every document readable by everyone, as
    /// <c>prem ingest --public</c> makes it.
    /// <para>
    /// The words are 4,096 made of three syllables each, none a word of any
    /// language on purpose, and a chunk holds twelve of them. A question's word
    /// is then in about one chunk in three hundred, as a word of a real
    /// question tends to be, so the text leg ranks a realistic share of the
    /// index; with a few dozen words it would match a tenth of every index and
    /// its cost would hide everything else a search does.
    /// </para>
    /// </summary>
    internal static class Synthetic
    {
        public const int DefaultSeed = 20260925;
        private const int Dimensions = 384;
        private const int PerDocument = 10;

        private static readonly string[] Syllables = ["ba", "ce", "di", "fo", "gu", "ka", "le", "mi", "no", "pu", "ra", "se", "ti", "vo", "wu", "zy"];

        private static readonly string[] Words =
            [.. from a in Syllables from b in Syllables from c in Syllables select a + b + c];

        /// <summary>Questions made from the same words, the same each time for a seed.</summary>
        public static string[] Questions(int seed, int count)
        {
            var rng = new Random(seed + 1);
            return Enumerable.Range(0, count)
                .Select(_ => $"{Word(rng)} {Word(rng)} near the {Word(rng)}")
                .ToArray();
        }

        private static string Word(Random rng) => Words[rng.Next(Words.Length)];

        private static string Passage(Random rng) => string.Join(' ', Enumerable.Range(0, 12).Select(_ => Word(rng))) + ".";

        /// <summary>
        /// Writes <paramref name="documents"/> Markdown files of ten sections
        /// each, a passage of the same invented words under each heading, so
        /// the Markdown chunker cuts every file into ten chunks. The same files
        /// each time for a seed.
        /// </summary>
        public static void WriteFiles(string folder, int documents, int seed)
        {
            var rng = new Random(seed);
            for (var d = 0; d < documents; d++)
            {
                var text = string.Join("\n\n", Enumerable.Range(0, PerDocument).Select(i => $"## Section {i}\n{Passage(rng)}"));
                File.WriteAllText(Path.Combine(folder, $"{d:D6}.md"), text + "\n");
            }
        }

        /// <summary>Writes the documents and their chunks with COPY, then analyzes both tables. Returns the number of documents.</summary>
        public static async Task<int> LoadAsync(PremagenticDatabase db, Guid tenantId, long everyone, string model, int chunks, int seed)
        {
            var rng = new Random(seed);
            var ids = new Guid[(chunks + PerDocument - 1) / PerDocument];
            var bytes = new byte[16];
            for (var d = 0; d < ids.Length; d++)
            {
                rng.NextBytes(bytes);
                ids[d] = new Guid(bytes);
            }

            await using (var conn = await db.DataSource.OpenConnectionAsync())
            {
                await using (var w = await conn.BeginBinaryImportAsync(
                    "COPY prem_index.document(id, tenant_id, path, title, acl_set_id, content_hash) FROM STDIN (FORMAT BINARY)"))
                {
                    for (var d = 0; d < ids.Length; d++)
                    {
                        await w.StartRowAsync();
                        await w.WriteAsync(ids[d], NpgsqlDbType.Uuid);
                        await w.WriteAsync(tenantId, NpgsqlDbType.Uuid);
                        await w.WriteAsync($"synthetic/{d:D7}.md", NpgsqlDbType.Text);
                        await w.WriteAsync($"Synthetic document {d}", NpgsqlDbType.Text);
                        await w.WriteAsync(everyone, NpgsqlDbType.Bigint);
                        await w.WriteAsync($"synthetic-{seed}-{d}", NpgsqlDbType.Text);
                    }
                    await w.CompleteAsync();
                }

                await using (var w = await conn.BeginBinaryImportAsync(
                    "COPY prem_index.chunk(document_id, seq, heading_path, doc_title, content, embedding, embedding_model, embedding_dims) FROM STDIN (FORMAT BINARY)"))
                {
                    var vector = new float[Dimensions];
                    for (var i = 0; i < chunks; i++)
                    {
                        for (var k = 0; k < Dimensions; k++) vector[k] = (float)(rng.NextDouble() * 2 - 1);
                        await w.StartRowAsync();
                        await w.WriteAsync(ids[i / PerDocument], NpgsqlDbType.Uuid);
                        await w.WriteAsync(i % PerDocument, NpgsqlDbType.Integer);
                        await w.WriteAsync($"Section {i % PerDocument}", NpgsqlDbType.Text);
                        await w.WriteAsync($"Synthetic document {i / PerDocument}", NpgsqlDbType.Text);
                        await w.WriteAsync(Passage(rng), NpgsqlDbType.Text);
                        await w.WriteAsync(VectorCodec.Encode(VectorCodec.Normalize(vector)!), NpgsqlDbType.Bytea);
                        await w.WriteAsync(model, NpgsqlDbType.Text);
                        await w.WriteAsync(Dimensions, NpgsqlDbType.Integer);
                    }
                    await w.CompleteAsync();
                }
            }

            foreach (var table in new[] { "prem_index.document", "prem_index.chunk" })
            {
                await using var analyze = db.DataSource.CreateCommand($"VACUUM ANALYZE {table}");
                await analyze.ExecuteNonQueryAsync();
            }
            return ids.Length;
        }
    }

    /// <summary>
    /// Turns ONNX Runtime's spin-waiting on or off in a local embedding
    /// provider. The product builds its inference session with spinning off and
    /// has no setting for it, so this replaces the provider's session with one
    /// built the same way but for that one option.
    /// </summary>
    internal static class OnnxSpinning
    {
        public static string Set(IEmbeddingProvider embedder, bool spinning)
        {
            if (embedder is not LocalOnnxEmbeddingProvider local)
                throw new InvalidOperationException("spin measures the local ONNX model; set PREM_EMBEDDING_PROVIDER=local.");
            var field = typeof(LocalOnnxEmbeddingProvider).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("This build's local provider holds no session this can replace, so spinning cannot be switched.");
            if (!spinning) return "as the product builds its session";

            using var options = new SessionOptions();
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "1");
            var old = (InferenceSession)field.GetValue(local)!;
            field.SetValue(local, new InferenceSession(Path.Combine(local.Folder, "model.onnx"), options));
            old.Dispose();
            return "the session rebuilt with spinning allowed";
        }
    }

    /// <summary>One line naming the machine and the build, for under every table.</summary>
    internal static class Machine
    {
        public static string Line()
        {
            var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
            var core = typeof(PremagenticDatabase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var commit = core.Contains('+') ? core[(core.IndexOf('+') + 1)..] : "unknown";
            return $"Machine: {Cpu()}, {Environment.ProcessorCount} logical cores, {memory:F0} GB, {RuntimeInformation.OSDescription.Trim()}, " +
                   $"{RuntimeInformation.FrameworkDescription}, commit {(commit.Length > 12 ? commit[..12] : commit)}.";
        }

        private static string Cpu()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim()
                           ?? "unknown processor";
                if (OperatingSystem.IsLinux())
                    return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim()
                           ?? "unknown processor";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
            return "unknown processor";
        }
    }
}
