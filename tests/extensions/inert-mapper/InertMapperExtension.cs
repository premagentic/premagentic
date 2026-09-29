using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;

namespace Premagentic.Tests.Extensions;

/// <summary>
/// A test extension that registers a principal mapper and nothing else, so
/// the tests can load a mapper through the real host, from a folder and by
/// hash, and see a second one refused for being a second mapper and for
/// nothing it shares with the first.
/// </summary>
public sealed class InertMapperExtension : IExtension
{
    public string Name => "inert-mapper";

    public void Register(ExtensionRegistrations registrations) =>
        registrations.AddPrincipalMapper(new MapsNothingPrincipalMapper());
}

/// <summary>
/// A principal mapper that maps nothing: every principal it is asked about
/// means nothing here, so a deployment that loads it treats outside
/// principals as one with no mapper does. It counts the times it is asked, so
/// a test can tell a host that collected it from a host that asks it.
/// </summary>
public sealed class MapsNothingPrincipalMapper : IPrincipalMapper
{
    private int _timesAsked;

    public string Name => "maps-nothing";

    public int TimesAsked => Volatile.Read(ref _timesAsked);

    public Task<IReadOnlyDictionary<string, Guid>> MapAsync(PrincipalMapRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _timesAsked);
        return Task.FromResult<IReadOnlyDictionary<string, Guid>>(new Dictionary<string, Guid>());
    }
}
