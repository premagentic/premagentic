using Premagentic.Core.Okf;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Retrieval.Gates;

/// <summary>
/// Trust. Content a machine wrote is out unless its trust tier reaches the
/// policy's minimum, which by default means a person has reviewed it, so one
/// agent's unreviewed output cannot become another agent's input.
/// <para>
/// Human-written content, and content whose authorship is unknown, which is
/// every ordinary file, are never held back here. The tier is stored per
/// document at ingest and the minimum arrives with each search, so a change of
/// policy takes effect on the next search with no re-ingest.
/// </para>
/// <para>
/// OKF frontmatter is self-declared and unsigned. This gate is as strong as
/// control over who can write the files, and no stronger.
/// </para>
/// </summary>
internal sealed class TrustGate : IGate
{
    public const string GateName = "trust";

    public string Name => GateName;

    public string Sql => $"(d.authorship <> {(short)OkfAuthorship.Machine} OR d.trust_tier >= @trust_min_tier)";

    public void AddParameters(NpgsqlCommand command, GateContext context) =>
        command.Parameters.Add(new NpgsqlParameter("trust_min_tier", NpgsqlDbType.Smallint)
        {
            Value = (short)context.Trust.MinimumMachineTier,
        });
}
