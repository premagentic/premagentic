using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Retrieval.Gates;

/// <summary>
/// Freshness. Content past its <c>stale_after</c> is out unless the policy
/// includes stale content, in which case it is served with a stale flag. A
/// concept is stale when <c>now &gt;= stale_after</c> (OKF section 5.5), so it
/// is fresh only while <c>stale_after &gt; now</c>.
/// <para>
/// "Now" is the one instant fixed in <see cref="GateContext"/> for the whole
/// search. A document with no <c>stale_after</c> is never stale. One whose
/// <c>stale_after</c> could not be read is stored as <c>-infinity</c> and is stale
/// at every instant.
/// </para>
/// </summary>
internal sealed class FreshnessGate : IGate
{
    public const string GateName = "freshness";

    public string Name => GateName;

    public string Sql => "(@freshness_include_stale OR d.stale_after IS NULL OR d.stale_after > @freshness_now)";

    public void AddParameters(NpgsqlCommand command, GateContext context)
    {
        command.Parameters.AddWithValue("freshness_include_stale", context.Trust.IncludeStale);
        command.Parameters.Add(new NpgsqlParameter("freshness_now", NpgsqlDbType.TimestampTz) { Value = context.Now });
    }
}
