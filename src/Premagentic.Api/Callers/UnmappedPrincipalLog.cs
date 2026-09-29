using System.Collections.Concurrent;

namespace Premagentic.Api.Callers;

/// <summary>
/// Says, once per principal for as long as the process runs, that a group a
/// sign-in adapter reported means nothing in this deployment and was ignored:
/// the principal mapper maps it to no group, or no principal mapper is loaded.
/// <para>
/// Once, because an unmapped group arrives again on every request that person
/// makes. A line per request would bury the one line an administrator needs,
/// and the fact does not change between them: nobody has mapped it yet.
/// </para>
/// <para>
/// Ignoring is the right reading and a silent one. A person whose directory
/// groups all mean nothing signs in successfully and then finds less than they
/// expected, which looks like a permissions mistake from every side. This is
/// the line that names the real cause.
/// </para>
/// </summary>
internal sealed class UnmappedPrincipalLog(ILogger<UnmappedPrincipalLog> log)
{
    /// <summary>
    /// How many distinct principals are remembered before the list stops
    /// growing. An adapter decides what it reports, so without a ceiling a
    /// process could be made to remember one string per request forever. Past
    /// it the lines stop rather than the sign-ins: what this does is tell an
    /// administrator something, and an administrator with a thousand unmapped
    /// groups has already been told.
    /// </summary>
    public const int Ceiling = 1000;

    private readonly ConcurrentDictionary<string, byte> _said = new(StringComparer.Ordinal);
    private int _ceilingSaid;

    /// <param name="mapperName">The principal mapper that mapped them to nothing, or null when none is loaded.</param>
    public void Note(string adapterName, string? mapperName, IReadOnlyList<string> unmapped)
    {
        ArgumentNullException.ThrowIfNull(unmapped);
        foreach (var principal in unmapped)
        {
            if (_said.Count >= Ceiling)
            {
                if (Interlocked.Exchange(ref _ceilingSaid, 1) == 0)
                    log.LogWarning(
                        "More than {Ceiling} distinct groups reported by sign-in adapters mean nothing here, and each was " +
                        "ignored. No more will be named until this process restarts.",
                        Ceiling);
                return;
            }

            if (!_said.TryAdd(principal, 0)) continue;
            if (mapperName is null)
                log.LogInformation(
                    "Sign-in adapter {Adapter} reported the group {Principal}, which means nothing here because no " +
                    "principal mapper is loaded, so it was ignored.",
                    adapterName, principal);
            else
                log.LogInformation(
                    "Sign-in adapter {Adapter} reported the group {Principal}, which the principal mapper {Mapper} maps " +
                    "to no group here, so it was ignored.",
                    adapterName, principal, mapperName);
        }
    }
}
