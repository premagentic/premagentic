namespace Premagentic.Core.Extensions;

/// <summary>
/// What an extension assembly offers this process. Every public type in the
/// assembly that implements this is made and asked to register, so one
/// assembly may carry more than one.
/// <para>
/// An extension adds ways to read, cut, reach and embed documents. It is not a
/// way around the gates: what a caller may read is decided in SQL before any
/// ranking, and nothing an extension registers is asked. The one registration
/// that can add to what a caller holds is a principal mapper, and the host
/// bounds it: an outside principal it maps becomes a live group an
/// administrator made, never <c>everyone</c>, a user, a role or a group
/// PremAgentic maintains itself. Everything else an extension brings may
/// narrow what a caller sees, never widen it.
/// </para>
/// <para>
/// <see cref="Register"/> is called once, while the host is starting, before
/// anything is served. It should do nothing but register: no file it does not
/// own, no network, no work that can hang. An exception thrown from it refuses
/// the whole extension, and nothing it registered before throwing is kept.
/// </para>
/// </summary>
public interface IExtension
{
    /// <summary>
    /// What this one calls itself, for a person reading a log. The name that
    /// decides whether it may load is the manifest's, which is what an
    /// administrator allows.
    /// </summary>
    string Name { get; }

    /// <summary>Adds this extension's readers, chunkers, embedding providers, sign-in adapters, reminder sinks and principal mapper.</summary>
    void Register(ExtensionRegistrations registrations);
}
