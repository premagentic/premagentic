using System.Collections.Concurrent;
using System.Text;
using Premagentic.Core.Reminders;

namespace Premagentic.Conformance;

/// <summary>
/// A reminder sink the kit carries to prove its own fixture: it keeps what it
/// delivers in memory, by owner, and cannot deliver to the owners it is told
/// are unreachable. Correct as made, and wrong in one chosen way when asked,
/// so each rule of <see cref="ReminderSinkConformance"/> can be seen to fail.
/// Not for a deployment.
/// </summary>
public sealed class KitFakeReminderSink : IReminderSink
{
    /// <summary>The one thing it does wrong, or nothing.</summary>
    public enum Flaw
    {
        None,

        /// <summary>The built-in sink's name.</summary>
        TakesTheBuiltInName,

        /// <summary>Delivers a greeting to the administrators as soon as it is made.</summary>
        DeliversWithoutARun,

        /// <summary>Delivers the whole run to every owner.</summary>
        DeliversEveryoneEverything,

        /// <summary>Leaves out the last summary of a run.</summary>
        DropsTheLastOwner,

        /// <summary>Throws at the first owner it cannot reach, before the rest.</summary>
        StopsAtTheFirstUnreachableOwner,

        /// <summary>Throws when a run has nobody in it.</summary>
        ThrowsOnAnEmptyRun,
    }

    private readonly ConcurrentDictionary<string, StringBuilder> _outbox = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreachable;
    private readonly Flaw _flaw;

    public KitFakeReminderSink(IEnumerable<string> unreachable, Flaw flaw = Flaw.None)
    {
        _unreachable = new HashSet<string>(unreachable, StringComparer.Ordinal);
        _flaw = flaw;
        if (flaw == Flaw.DeliversWithoutARun) Write(ReminderSummary.Administrators, "The kit's sink is ready.");
    }

    public string Name => _flaw == Flaw.TakesTheBuiltInName ? TableReminderSink.SinkName : "kit-fake";

    /// <summary>What it has delivered, by owner.</summary>
    public IReadOnlyDictionary<string, string> Delivered => _outbox.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);

    public Task DeliverAsync(IReadOnlyList<ReminderSummary> summaries, DateTimeOffset computedAt, CancellationToken ct)
    {
        if (summaries.Count == 0 && _flaw == Flaw.ThrowsOnAnEmptyRun) throw new InvalidOperationException("Nothing to send.");

        var run = _flaw == Flaw.DropsTheLastOwner && summaries.Count > 0 ? summaries.Take(summaries.Count - 1).ToList() : summaries;
        var unreached = new List<string>();
        foreach (var summary in run)
        {
            if (_unreachable.Contains(summary.OwnerPrincipal))
            {
                if (_flaw == Flaw.StopsAtTheFirstUnreachableOwner) throw new InvalidOperationException($"Cannot reach {summary.OwnerPrincipal}.");
                unreached.Add(summary.OwnerPrincipal);
                continue;
            }
            foreach (var part in _flaw == Flaw.DeliversEveryoneEverything ? run : [summary])
                foreach (var item in part.Stale.Concat(part.InReview).Concat(part.Unowned))
                    Write(summary.OwnerPrincipal, item.Path);
        }
        // Every owner it could reach has their reminders before it says which it could not.
        return unreached.Count == 0
            ? Task.CompletedTask
            : Task.FromException(new InvalidOperationException($"Could not reach {string.Join(", ", unreached)}."));
    }

    private void Write(string owner, string line) => _outbox.GetOrAdd(owner, _ => new StringBuilder()).AppendLine(line);
}
