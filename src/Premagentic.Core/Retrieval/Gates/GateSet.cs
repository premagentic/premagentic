using Npgsql;

namespace Premagentic.Core.Retrieval.Gates;

/// <summary>
/// The gates joined with AND, applied at every gated read: the text leg, the
/// vector leg's permitted-id read, the final passage read, and
/// <see cref="SectionFetcher"/>. A new gate joins <see cref="Default"/> and from
/// then on reaches every one of those reads without any of them changing.
/// </summary>
internal sealed class GateSet
{
    public static GateSet Default { get; } = new(new AccessGate(), new LifecycleGate(), new TrustGate(), new FreshnessGate());

    private readonly IGate[] _gates;

    public GateSet(params IGate[] gates)
    {
        if (gates.Select(g => g.Name).Distinct(StringComparer.Ordinal).Count() != gates.Length)
            throw new ArgumentException("Two gates share a name.", nameof(gates));
        _gates = gates;
        // An empty set is TRUE, never an empty string that would leave a
        // dangling AND in the statement.
        Sql = gates.Length == 0 ? "TRUE" : string.Join("\n  AND ", gates.Select(g => g.Sql));
    }

    public string Sql { get; }

    public IReadOnlyList<string> Names => _gates.Select(g => g.Name).ToArray();

    public void AddParameters(NpgsqlCommand command, GateContext context)
    {
        foreach (var gate in _gates)
            gate.AddParameters(command, context);
    }

    /// <summary>The same set less one gate, for a read that reports that gate's outcome instead of hiding by it.</summary>
    public GateSet Without(string name)
    {
        if (!_gates.Any(g => g.Name == name))
            throw new ArgumentException($"No gate named '{name}' in this set.", nameof(name));
        return new GateSet(_gates.Where(g => g.Name != name).ToArray());
    }
}
