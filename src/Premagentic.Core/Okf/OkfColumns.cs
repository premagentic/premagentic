using Premagentic.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Okf;

/// <summary>
/// The OKF values stored on each <c>prem_index.document</c> row (migration 0010),
/// derived once from a document's metadata so the write and the unchanged
/// check cannot disagree about them.
/// <para>
/// A document with no metadata stores what an ordinary file is: no concept
/// id, unverified, unknown authorship, never stale.
/// </para>
/// </summary>
internal sealed record OkfColumns(
    string? ConceptId,
    OkfTrustTier TrustTier,
    OkfAuthorship Authorship,
    DateTimeOffset? StaleAfter,
    bool StaleAfterUnreadable,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? LastVerifiedAt,
    FrontmatterState FrontmatterState)
{
    public static OkfColumns For(OkfMetadata? okf) => okf is null
        ? new OkfColumns(null, OkfTrustTier.Unverified, OkfAuthorship.Unknown, null, false, null, null, FrontmatterState.Absent)
        : new OkfColumns(
            okf.ConceptId,
            okf.TrustTier,
            okf.Authorship,
            okf.StaleAfter,
            okf.StaleAfterUnreadable,
            okf.Generated?.At,
            okf.LastVerifiedAt,
            okf.FrontmatterState);

    /// <summary>
    /// Adds the parameters the upsert and the unchanged check name:
    /// <c>@okfConceptId</c>, <c>@okfTrustTier</c>, <c>@okfAuthorship</c>,
    /// <c>@okfStaleAfter</c>, <c>@okfGeneratedAt</c>, <c>@okfLastVerifiedAt</c>
    /// and <c>@okfFrontmatterState</c>.
    /// </summary>
    public void AddParameters(NpgsqlCommand command)
    {
        // Typed explicitly: a null parameter has no type of its own, and the
        // enums are stored as smallint in their numeric order.
        command.Parameters.Add(new NpgsqlParameter("okfConceptId", NpgsqlDbType.Text) { Value = (object?)ConceptId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("okfTrustTier", NpgsqlDbType.Smallint) { Value = (short)TrustTier });
        command.Parameters.Add(new NpgsqlParameter("okfAuthorship", NpgsqlDbType.Smallint) { Value = (short)Authorship });
        command.Parameters.Add(new NpgsqlParameter("okfStaleAfter", NpgsqlDbType.TimestampTz) { Value = StoredStaleAfter });
        command.Parameters.Add(new NpgsqlParameter("okfGeneratedAt", NpgsqlDbType.TimestampTz) { Value = Instant(GeneratedAt) });
        command.Parameters.Add(new NpgsqlParameter("okfLastVerifiedAt", NpgsqlDbType.TimestampTz) { Value = Instant(LastVerifiedAt) });
        command.Parameters.Add(new NpgsqlParameter("okfFrontmatterState", NpgsqlDbType.Smallint) { Value = (short)FrontmatterState });
    }

    // An unreadable stale_after is stored as the earliest instant there is.
    // Npgsql writes DateTimeOffset.MinValue as -infinity, so "stale" stays the
    // one comparison stale_after <= now and a date nobody can read is stale at
    // every instant. With Npgsql's infinity conversion switched off it would be
    // written as the year 1 instead, which is stale at every instant too.
    private object StoredStaleAfter => StaleAfterUnreadable ? DateTimeOffset.MinValue : Instant(StaleAfter);

    // timestamptz takes UTC only. The parser already normalizes, and this keeps
    // a value built any other way from failing the write.
    private static object Instant(DateTimeOffset? value) =>
        value is { } instant ? instant.ToUniversalTime() : DBNull.Value;
}
