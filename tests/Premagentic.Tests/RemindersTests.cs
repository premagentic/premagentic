using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Npgsql;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Reminders;
using Premagentic.Core.Security;
using Premagentic.Core.Sources.Registry;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A reminders run over an invented deployment: two owners, a source nobody
/// owns and a folder given by hand. Requires a running Docker daemon.
/// </summary>
public sealed class RemindersTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task Each_owner_gets_their_own_documents_and_the_unowned_go_to_the_administrators_once()
    {
        var (db, tenant, connection) = await World.CreateAsync(server);
        await using var _ = db;

        var job = new RemindersJob(db, tenant, []);
        var run = await job.RunAsync(plan: true, CancellationToken.None);

        Assert.Equal(["user:alice", "user:bob", ReminderSummary.Administrators], job.Summaries.Select(s => s.OwnerPrincipal));
        var alice = job.Summaries[0];
        // Stale and in review at once: on each list, once.
        Assert.Equal(["greenhouse/watering.md"], alice.Stale.Select(i => i.Path));
        Assert.Equal(["greenhouse/humidity.md", "greenhouse/watering.md"], alice.InReview.Select(i => i.Path));
        Assert.Empty(alice.Unowned);
        // Each source's documents go to its own owner.
        var bob = job.Summaries[1];
        Assert.Equal(["nursery/seeds.md"], bob.Stale.Select(i => i.Path));
        Assert.Empty(bob.InReview);
        // An unowned source and a folder given by hand; a document on both lists is here once.
        var administrators = job.Summaries[2];
        Assert.Empty(administrators.Stale);
        Assert.Empty(administrators.InReview);
        Assert.Equal(["loose/notes.md", "office/both.md", "office/hours.md"], administrators.Unowned.Select(i => i.Path));

        Assert.Equal((3, 2, 2, 3), (run.Owners, run.Stale, run.InReview, run.Unowned));
        // A stale date still ahead is not stale, and a document on no list is on none.
        Assert.DoesNotContain(job.Summaries.SelectMany(s => s.Stale.Concat(s.InReview).Concat(s.Unowned)),
            i => i.Path is "greenhouse/fresh.md" or "greenhouse/later.md");
    }

    [Fact]
    public async Task A_planned_run_writes_nothing_and_a_delivered_one_is_what_the_portal_reads()
    {
        var (db, tenant, connection) = await World.CreateAsync(server);
        await using var _ = db;

        var planned = new RemindersJob(db, tenant, []);
        Assert.True((await planned.RunAsync(plan: true, CancellationToken.None)).Planned);
        Assert.Equal((0, 0), await RowsAsync(db));
        Assert.Null(await new ReminderSummaries(db, tenant).LatestAsync(CancellationToken.None));

        var delivered = new RemindersJob(db, tenant, []);
        var run = await delivered.RunAsync(plan: false, CancellationToken.None);
        Assert.False(run.Planned);
        Assert.Equal((1, 7), await RowsAsync(db));

        var latest = await new ReminderSummaries(db, tenant).LatestAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(run.ComputedAt.ToUnixTimeMilliseconds(), latest.Value.ComputedAt.ToUnixTimeMilliseconds());
        Assert.Equal(Describe(delivered.Summaries), Describe(latest.Value.Summaries));

        // Another plan, after a run was kept, leaves that run as it was.
        await new RemindersJob(db, tenant, []).RunAsync(plan: true, CancellationToken.None);
        Assert.Equal((1, 7), await RowsAsync(db));
        Assert.Equal(latest.Value.ComputedAt, (await new ReminderSummaries(db, tenant).LatestAsync(CancellationToken.None))!.Value.ComputedAt);
    }

    [Fact]
    public async Task The_built_in_sink_goes_first_and_a_sink_that_fails_does_not_stop_the_next()
    {
        var (db, tenant, connection) = await World.CreateAsync(server);
        await using var _ = db;
        var summaries = new ReminderSummaries(db, tenant);
        var after = new RecordingSink("after", () => summaries.LatestAsync(CancellationToken.None));

        var job = new RemindersJob(db, tenant, [new FailingSink(), after]);
        await job.RunAsync(plan: false, CancellationToken.None);

        // The later sink saw the run already kept by the built-in one.
        Assert.True(after.SawKeptRun);
        Assert.Equal(3, after.Delivered!.Count);
        var failure = Assert.Single(job.Failures);
        Assert.Equal("failing", failure.Sink);
    }

    [Fact]
    public async Task The_cli_runs_the_job_as_a_scheduler_would_and_its_plan_names_every_owner()
    {
        var (db, tenant, connection) = await World.CreateAsync(server);
        await using var _ = db;

        var (code, output, errors) = await CliAsync(connection, db, "reminders", "run", "--plan");
        Assert.True(code == 0, $"exit {code}{Environment.NewLine}{output}{errors}");
        Assert.Contains("Planned, not delivered: 3 owner(s), 2 stale, 2 in review, 3 unowned", output);
        Assert.Contains("user:alice", output);
        Assert.Contains("  unowned    office/both.md", output);
        Assert.Equal((0, 0), await RowsAsync(db));

        (code, output, errors) = await CliAsync(connection, db, "reminders", "run");
        Assert.True(code == 0, $"exit {code}{Environment.NewLine}{output}{errors}");
        Assert.Contains("reminders: 3 owner(s), 2 stale, 2 in review, 3 unowned", output);
        Assert.Contains("Delivered to table.", output);
        Assert.Equal((1, 7), await RowsAsync(db));
    }

    private static async Task<(long Runs, long Items)> RowsAsync(PremagenticDatabase db)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT (SELECT count(*) FROM prem_config.reminder_run), (SELECT count(*) FROM prem_config.reminder_item)");
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    private static string[] Describe(IReadOnlyList<ReminderSummary> summaries) =>
        [.. summaries.SelectMany(s =>
            s.Stale.Select(i => $"{s.OwnerPrincipal} stale {i.Path} {i.Since?.ToUnixTimeMilliseconds()}")
                .Concat(s.InReview.Select(i => $"{s.OwnerPrincipal} review {i.Path} {i.Since?.ToUnixTimeMilliseconds()}"))
                .Concat(s.Unowned.Select(i => $"{s.OwnerPrincipal} unowned {i.Path} {i.Since?.ToUnixTimeMilliseconds()}")))];

    private static async Task<(int Code, string Output, string Errors)> CliAsync(string connection, PremagenticDatabase db, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PREM_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PREM_CONNECTION_STRING"] = connection;
        start.Environment["PREM_TENANT_KEY"] = World.TenantKey;
        start.Environment["PREM_TENANT_NAME"] = "T";
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "hash";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string CliPath()
    {
        var path = typeof(RemindersTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }

    private sealed class FailingSink : IReminderSink
    {
        public string Name => "failing";

        public Task DeliverAsync(IReadOnlyList<ReminderSummary> summaries, DateTimeOffset computedAt, CancellationToken ct) =>
            throw new IOException("the mail relay is down");
    }

    private sealed class RecordingSink(string name, Func<Task<(DateTimeOffset, IReadOnlyList<ReminderSummary>)?>> kept) : IReminderSink
    {
        public string Name => name;

        public IReadOnlyList<ReminderSummary>? Delivered { get; private set; }

        public bool SawKeptRun { get; private set; }

        public async Task DeliverAsync(IReadOnlyList<ReminderSummary> summaries, DateTimeOffset computedAt, CancellationToken ct)
        {
            Delivered = summaries;
            SawKeptRun = await kept() is { } run && run.Item1.ToUnixTimeMilliseconds() == computedAt.ToUnixTimeMilliseconds();
        }
    }
}

/// <summary>
/// The built-in sink calls nothing but the database, proven the way the
/// runtime telemetry was: every connection this process opens while the sink
/// delivers is watched, the database connection is the control that proves the
/// watch sees connections, and a sink that opens one more is caught. Runs on
/// its own, since the watch is process wide. Requires a running Docker daemon.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ReminderSinkConnectionTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public async Task The_built_in_sink_opens_no_connection_but_the_database()
    {
        var (db, tenant, connection) = await World.CreateAsync(server);
        await using var _ = db;
        var summaries = await PlannedAsync(db, tenant);

        // No pool, so the sink's own connection is a new one and is seen.
        var unpooled = new NpgsqlConnectionStringBuilder(connection) { Pooling = false };
        await using var fresh = new PremagenticDatabase(unpooled.ConnectionString);
        var database = (Host: unpooled.Host!, Port: unpooled.Port);

        var seen = await WatchAsync(() => new TableReminderSink(fresh, tenant).DeliverAsync(summaries, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains(database.Port, seen.Ports);
        Assert.All(seen.Ports, port => Assert.Equal(database.Port, port));
        Assert.All(seen.Names, name => Assert.Equal(database.Host, name));

        // The control that the watch can fail: a sink that opens one more
        // connection, to a listener on this computer, is caught.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var elsewhere = ((IPEndPoint)listener.LocalEndpoint).Port;
        var caught = await WatchAsync(async () =>
        {
            await new TableReminderSink(fresh, tenant).DeliverAsync(summaries, DateTimeOffset.UtcNow, CancellationToken.None);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, elsewhere);
        });
        Assert.Contains(elsewhere, caught.Ports);
    }

    private static async Task<IReadOnlyList<ReminderSummary>> PlannedAsync(PremagenticDatabase db, Guid tenant)
    {
        var job = new RemindersJob(db, tenant, []);
        await job.RunAsync(plan: true, CancellationToken.None);
        Assert.NotEmpty(job.Summaries);
        return job.Summaries;
    }

    private static async Task<(IReadOnlyList<int> Ports, IReadOnlyList<string> Names)> WatchAsync(Func<Task> act)
    {
        using var watch = new ConnectionWatch();
        watch.On = true;
        await act();
        watch.On = false;
        return ([.. watch.Ports], [.. watch.Names]);
    }

    /// <summary>
    /// Every socket connect and every name lookup the runtime reports while
    /// <see cref="On"/>. A connect is reported with its address as the runtime
    /// writes a socket address, "InterNetwork:16:{249,208,127,0,0,1,...}": the
    /// family, the size, then the bytes after the family, of which the first
    /// two are the port. The database connection is the control that the port
    /// is read right: the test fails when it is not found.
    /// </summary>
    private sealed class ConnectionWatch : EventListener
    {
        public volatile bool On;
        public readonly ConcurrentQueue<int> Ports = new();
        public readonly ConcurrentQueue<string> Names = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name is "System.Net.Sockets" or "System.Net.NameResolution")
                EnableEvents(source, EventLevel.Informational, EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!On || e.Payload is not { Count: > 0 } payload) return;
            if (e.EventName == "ConnectStart" && payload[0] is string address) Ports.Enqueue(PortOf(address));
            if (e.EventName == "ResolutionStart" && payload[0] is string host) Names.Enqueue(host);
        }

        private static int PortOf(string address)
        {
            var open = address.IndexOf('{');
            var bytes = address[(open + 1)..address.IndexOf('}')].Split(',').Select(b => int.Parse(b.Trim())).ToArray();
            return bytes[0] * 256 + bytes[1];
        }
    }
}

/// <summary>The invented deployment both classes run against.</summary>
internal static class World
{
    public const string TenantKey = "t";

    public static async Task<(PremagenticDatabase Db, Guid Tenant, string Connection)> CreateAsync(DatastoreTestDatabase server)
    {
        var connection = await server.CreateDatabaseAsync();
        var db = new PremagenticDatabase(connection);
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync(TenantKey, "T");

        var identity = new IdentityStore(db, tenant);
        var alice = await identity.CreateUserAsync("alice", "Alice", Role.Member);
        var bob = await identity.CreateUserAsync("bob", "Bob", Role.Member);

        var registry = new SourceRegistry(db, tenant);
        var folder = Path.GetTempPath();
        await registry.AddAsync("greenhouse", folder, "greenhouse", false, false, AdminActor.Cli());
        await registry.AddAsync("nursery", folder, "nursery", false, false, AdminActor.Cli());
        await registry.AddAsync("office", folder, "office", false, false, AdminActor.Cli());
        await registry.SetOwnerAsync("greenhouse", alice.Id, AdminActor.Cli());
        await registry.SetOwnerAsync("nursery", bob.Id, AdminActor.Cli());

        await new IngestPipeline(db, new HashEmbeddingProvider()).RunAsync(tenant, new DatastoreSource("", [
            DatastoreSource.Doc("greenhouse/watering.md", "## Watering\nwater the benches before nine", DocumentAccess.Everyone),
            DatastoreSource.Doc("greenhouse/humidity.md", "## Humidity\nkeep it between seventy and eighty", DocumentAccess.Everyone),
            DatastoreSource.Doc("greenhouse/fresh.md", "## Fresh\nnothing to remind anyone of", DocumentAccess.Everyone),
            DatastoreSource.Doc("greenhouse/later.md", "## Later\nstale only next year", DocumentAccess.Everyone),
            DatastoreSource.Doc("nursery/seeds.md", "## Seeds\ntwo seeds per cell", DocumentAccess.Everyone),
            DatastoreSource.Doc("office/hours.md", "## Hours\nopen at eight", DocumentAccess.Everyone),
            DatastoreSource.Doc("office/both.md", "## Both\nstale and unreviewed", DocumentAccess.Everyone),
            DatastoreSource.Doc("loose/notes.md", "## Notes\na folder given by hand", DocumentAccess.Everyone),
        ]));

        // Stale: past its stale date. In review: machine-written and below
        // human-reviewed, the review queue's own condition.
        await using var cmd = db.DataSource.CreateCommand("""
            UPDATE prem_index.document SET stale_after = now() - interval '1 day'
             WHERE path IN ('greenhouse/watering.md', 'nursery/seeds.md', 'office/hours.md', 'office/both.md');
            UPDATE prem_index.document SET stale_after = now() + interval '365 days' WHERE path = 'greenhouse/later.md';
            UPDATE prem_index.document SET trust_tier = 0, authorship = 2
             WHERE path IN ('greenhouse/watering.md', 'greenhouse/humidity.md', 'office/both.md', 'loose/notes.md');
            """);
        await cmd.ExecuteNonQueryAsync();
        return (db, tenant, connection);
    }
}
