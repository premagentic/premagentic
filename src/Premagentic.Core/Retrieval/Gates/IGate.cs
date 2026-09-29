using Npgsql;

namespace Premagentic.Core.Retrieval.Gates;

/// <summary>
/// One hard condition every gated read applies. A gate decides eligibility; it
/// never ranks, so no relevance signal can outweigh it.
/// <para>
/// <see cref="Sql"/> is a boolean SQL expression over the alias <c>d</c> for
/// <c>prem_index.document</c>, and nothing else. A gate never refers to a chunk:
/// the vector leg asks the database which documents a caller may read, in a
/// query with no chunk in it, and scores those documents' chunks from memory.
/// A condition that genuinely varies by chunk cannot be a gate. It names its
/// parameters with <c>@</c> and <see cref="AddParameters"/> supplies them, under
/// names no other gate uses.
/// </para>
/// </summary>
internal interface IGate
{
    /// <summary>Short and stable. Lets one read leave out one gate on purpose, as <see cref="SectionFetcher"/> does.</summary>
    string Name { get; }

    string Sql { get; }

    void AddParameters(NpgsqlCommand command, GateContext context);
}
