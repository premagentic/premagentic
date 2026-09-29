using Npgsql;

namespace Premagentic.Core.Retrieval.Gates;

/// <summary>
/// Authorization. A document is eligible only if its access list is one the
/// caller may read. Which lists those are is decided in C#, for each read, by
/// <see cref="Security.PermittedSetReader"/> over the tenant's current lists, first
/// match in each list's own order; this gate only compares integers, so the
/// allow and deny logic lives in one engine-neutral place. Applied identically
/// to both retrieval legs, to the final passage read and to
/// <see cref="SectionFetcher"/>, because a citation a caller may not read must
/// not be fetchable either.
/// <para>
/// A document with no list (NULL) equals nothing and reaches nobody.
/// </para>
/// </summary>
internal sealed class AccessGate : IGate
{
    public const string GateName = "access";

    /// <summary>
    /// The gate's parameters. The text match function reads them by name: it
    /// folds them into its own session condition, so the lists reach the
    /// planner once. See <see cref="TextMatchFunction"/>.
    /// </summary>
    public const string UnrestrictedParameter = "unrestricted";

    /// <inheritdoc cref="UnrestrictedParameter"/>
    public const string PermittedParameter = "permitted";

    public string Name => GateName;

    public string Sql => "(@unrestricted OR d.acl_set_id = ANY(@permitted))";

    public void AddParameters(NpgsqlCommand command, GateContext context)
    {
        var scope = context.Scope;

        // A scope nobody resolved would read as "no lists" and return nothing:
        // the right answer for the wrong reason, from a bug that then hides.
        // Refuse it instead.
        var permitted = scope.PermittedSetIds
            ?? (scope.Unrestricted
                ? Array.Empty<long>()
                : throw new InvalidOperationException(
                    "The access scope was not resolved for this read. Call PermittedSetReader.ResolveAsync first."));

        command.Parameters.AddWithValue(UnrestrictedParameter, scope.Unrestricted);
        command.Parameters.AddWithValue(PermittedParameter, permitted.ToArray());
    }
}
