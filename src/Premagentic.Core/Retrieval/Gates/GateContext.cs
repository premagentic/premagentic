using Premagentic.Core.Okf;
using Premagentic.Core.Security;

namespace Premagentic.Core.Retrieval.Gates;

/// <summary>
/// What a gate knows about one read: the options the caller searched with, and
/// the one instant the search runs at. Each gate reads only the part that is
/// its own, so a new gate never changes how this is built.
/// <para>
/// <see cref="Now"/> is fixed when the context is built, from
/// <see cref="SearchOptions.AsOf"/> or else the clock, and never read again. Every
/// gated read of one search, and the stale flag on its hits, see the same
/// instant; SQL <c>now()</c> is never used, because it would move between reads
/// and could not be pinned by a test.
/// </para>
/// </summary>
internal sealed record GateContext
{
    public GateContext(SearchOptions options, TimeProvider? clock = null)
    {
        Options = options;
        Now = (options.AsOf ?? (clock ?? TimeProvider.System).GetUtcNow()).ToUniversalTime();
    }

    public SearchOptions Options { get; }

    public DateTimeOffset Now { get; }

    public AccessScope Scope => Options.Scope;

    public bool IncludeHistorical => Options.IncludeHistorical;

    public TrustPolicy Trust => Options.EffectiveTrust;
}
