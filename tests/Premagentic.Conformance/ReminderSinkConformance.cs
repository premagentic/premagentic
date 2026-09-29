using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Reminders;

namespace Premagentic.Conformance;

/// <summary>
/// Inherit this and give it your reminder sink to prove it keeps the rules a
/// reminders run rests on: it delivers only when a run is handed to it, so
/// <c>prem reminders run --plan</c>, which hands a run to no sink, delivers
/// nothing; each owner receives their own reminders and nobody else's, and
/// every reminder in the run reaches its owner; and one owner it cannot deliver
/// to does not keep the run from the others.
/// <para>
/// The fixture makes the runs itself, from invented documents, and reads back
/// what your sink delivered through <see cref="Delivered"/>: point your sink at
/// a stand-in (a mail server on loopback, a folder) and read the stand-in.
/// </para>
/// </summary>
public abstract class ReminderSinkConformance
{
    /// <summary>
    /// This kit proves the version of this seam that the PremAgentic your
    /// extension builds against offers. A kit from another release fails here,
    /// naming the version to match, rather than passing in silence.
    /// </summary>
    [Fact]
    public void The_kit_proves_the_seams_this_extension_builds_against() =>
        KitSeams.AssertProves(SeamVersions.ReminderName, nameof(SeamVersions.Reminder));

    /// <summary>Your sink, set up to deliver to <see cref="Owners"/> and to the administrators, and not to <see cref="Unreachable"/>.</summary>
    protected abstract IReminderSink Sink { get; }

    /// <summary>Two owners your sink can deliver to, as a run names them: <c>user:alice</c>, <c>group:Staff</c>.</summary>
    protected abstract IReadOnlyList<string> Owners { get; }

    /// <summary>An owner your sink cannot deliver to, such as one with no address.</summary>
    protected abstract string Unreachable { get; }

    /// <summary>
    /// Everything your sink has delivered so far, by the owner it was for: all
    /// the text that owner received, run together.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, string> Delivered();

    [Fact]
    public void It_names_itself_and_not_as_the_built_in_sink()
    {
        Assert.True(ChunkerRegistry.IsName(Sink.Name),
            $"'{Sink.Name}' cannot name a reminder sink: a name is up to 64 letters, digits, dots, hyphens and " +
            "underscores, starting with a letter or digit.");
        Assert.False(Sink.Name.Equals(TableReminderSink.SinkName, StringComparison.OrdinalIgnoreCase),
            $"'{Sink.Name}' is the built-in sink's name, and a deployment refuses an extension that registers it.");
    }

    [Fact]
    public async Task Nothing_is_delivered_until_a_run_is_handed_to_it()
    {
        _ = Sink;
        await Task.Delay(200);

        Assert.True(Delivered().Count == 0,
            $"The sink '{Sink.Name}' delivered to {string.Join(", ", Delivered().Keys)} before any run was handed to it. " +
            "A planned run hands nothing to a sink, so a sink that delivers on its own sends on a plan.");
    }

    [Fact]
    public async Task Each_owner_receives_their_own_reminders_and_nobody_elses()
    {
        CheckOwners();
        var run = Run(Owners[0], Owners[1], ReminderSummary.Administrators);

        await Sink.DeliverAsync(run, Now, CancellationToken.None);

        var delivered = Delivered();
        foreach (var summary in run)
        {
            Assert.True(delivered.TryGetValue(summary.OwnerPrincipal, out var text),
                $"The sink '{Sink.Name}' delivered nothing for {summary.OwnerPrincipal}, whose reminders were in the run.");
            foreach (var path in Paths(summary))
                Assert.True(text!.Contains(path, StringComparison.Ordinal),
                    $"The sink '{Sink.Name}' did not deliver {path} to {summary.OwnerPrincipal}, whose reminder it is.");
            foreach (var other in run.Where(s => s != summary).SelectMany(Paths))
                Assert.False(text!.Contains(other, StringComparison.Ordinal),
                    $"The sink '{Sink.Name}' delivered {other} to {summary.OwnerPrincipal}, whose reminder it is not. " +
                    "A reminder names a document to its owner, and to nobody else.");
        }
        Assert.True(delivered.Keys.All(owner => run.Any(s => s.OwnerPrincipal == owner)),
            $"The sink '{Sink.Name}' delivered to {string.Join(", ", delivered.Keys.Where(o => run.All(s => s.OwnerPrincipal != o)))}, " +
            "who had no summary in the run.");
    }

    [Fact]
    public async Task One_owner_it_cannot_deliver_to_does_not_stop_the_rest()
    {
        CheckOwners();
        // The owner it cannot reach comes between the two it can, so a sink
        // that stops at the first failure misses the last.
        var run = Run(Owners[0], Unreachable, Owners[1]);

        // It may say which owners it could not reach by throwing once the
        // others have their reminders; the run reports that and goes on.
        try
        {
            await Sink.DeliverAsync(run, Now, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        var delivered = Delivered();
        foreach (var owner in new[] { Owners[0], Owners[1] })
            Assert.True(delivered.ContainsKey(owner),
                $"The sink '{Sink.Name}' could not deliver to {Unreachable} and then delivered nothing to {owner}. " +
                "One owner it cannot reach does not keep the run from the rest.");
    }

    [Fact]
    public async Task An_empty_run_delivers_nothing_and_does_not_throw()
    {
        await Sink.DeliverAsync([], Now, CancellationToken.None);

        Assert.True(Delivered().Count == 0, $"The sink '{Sink.Name}' delivered something from a run with nobody in it.");
    }

    private static readonly DateTimeOffset Now = new(2026, 3, 2, 7, 0, 0, TimeSpan.Zero);

    private void CheckOwners() =>
        Assert.True(Owners.Count >= 2 && Owners.Distinct().Count() == Owners.Count && !Owners.Contains(Unreachable),
            "Give the fixture two different owners your sink can deliver to, and an unreachable one that is neither.");

    /// <summary>A run with one summary per owner, each naming invented documents no other summary names.</summary>
    private static IReadOnlyList<ReminderSummary> Run(params string[] owners) =>
        owners.Select((owner, i) => owner == ReminderSummary.Administrators
                ? new ReminderSummary(owner, [], [], [new ReminderItem($"loose/unowned-{i}.md", "Nobody owns this", Now.AddDays(-9))])
                : new ReminderSummary(owner,
                    [new ReminderItem($"owned-{i}/stale-rota.md", "An old rota", Now.AddDays(-3))],
                    [new ReminderItem($"owned-{i}/draft-plan.md", null, Now.AddDays(-1))],
                    []))
            .ToList();

    private static IEnumerable<string> Paths(ReminderSummary summary) =>
        summary.Stale.Concat(summary.InReview).Concat(summary.Unowned).Select(item => item.Path);
}
