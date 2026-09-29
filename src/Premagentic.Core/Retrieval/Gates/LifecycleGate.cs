using Npgsql;

namespace Premagentic.Core.Retrieval.Gates;

/// <summary>
/// Lifecycle. Superseded, archived, draft and expired material is out unless
/// historical access was explicitly requested. The flag is never inferred from
/// the wording of a query.
/// </summary>
internal sealed class LifecycleGate : IGate
{
    public const string GateName = "lifecycle";

    public string Name => GateName;

    public string Sql => "(@historical OR d.lifecycle_status = 'active')";

    public void AddParameters(NpgsqlCommand command, GateContext context) =>
        command.Parameters.AddWithValue("historical", context.IncludeHistorical);
}
