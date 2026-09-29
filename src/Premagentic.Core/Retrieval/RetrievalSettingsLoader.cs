using System.Collections.Concurrent;
using Premagentic.Core.Identity;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Retrieval;

/// <summary>
/// The stored retrieval tuning, read for every search, so a change applies to
/// the next query with nothing cached and nothing to invalidate. Create one per
/// process and share it, as <see cref="HybridSearch"/> is shared.
/// <para>
/// A stored value that cannot be used never stops a search: its code default
/// applies, and <paramref name="report"/> hears of it once per key for the life
/// of this instance, not once per search.
/// </para>
/// </summary>
/// <param name="report">Told of each unusable key once; a host logs it. Null tells nobody.</param>
public sealed class RetrievalSettingsLoader(PremagenticDatabase db, Action<RetrievalSettingProblem>? report = null)
{
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    /// <summary>The tuning in force for <paramref name="tenantId"/> now: one small read.</summary>
    public async Task<RetrievalSettingsReading> LoadAsync(Guid tenantId, CancellationToken ct = default)
    {
        var reading = await RetrievalSettings.LoadAsync(new SettingsStore(db, tenantId), ct);
        foreach (var problem in reading.Problems)
            if (_reported.TryAdd(problem.Key, 0))
                report?.Invoke(problem);
        return reading;
    }
}
