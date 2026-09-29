namespace Premagentic.Core.Okf;

public enum OkfActorKind
{
    /// <summary>Anything that does not follow the convention, including <c>human:</c> with no id.</summary>
    Unknown,

    /// <summary><c>human:&lt;id&gt;</c>, a person.</summary>
    Human,

    /// <summary><c>process:&lt;id&gt;</c>, an automated process.</summary>
    Process,

    /// <summary><c>&lt;producer&gt;/&lt;version&gt;</c>, an agent or tool.</summary>
    Agent,
}

/// <summary>
/// Who or what performed an action, in the OKF actor convention (spec section 7).
/// <para>
/// Only <see cref="OkfActorKind.Human"/> is load-bearing: trust tiers and
/// authorship key off it, and every other kind counts as not a person. The
/// prefix is matched exactly as the spec writes it, so <c>Human:pat</c> is
/// unknown rather than a person. Erring that way can only lower a tier, never
/// raise one.
/// </para>
/// </summary>
/// <param name="Raw">The string as written, trimmed.</param>
/// <param name="Name">The id for a person or process, the producer for an agent, null when unknown.</param>
/// <param name="Version">The version for an agent, null otherwise.</param>
public sealed record OkfActor(OkfActorKind Kind, string Raw, string? Name, string? Version)
{
    private const string HumanPrefix = "human:";
    private const string ProcessPrefix = "process:";

    public bool IsHuman => Kind == OkfActorKind.Human;

    public static OkfActor Parse(string raw)
    {
        var text = raw.Trim();

        if (text.StartsWith(HumanPrefix, StringComparison.Ordinal))
            return Prefixed(text, HumanPrefix, OkfActorKind.Human);

        if (text.StartsWith(ProcessPrefix, StringComparison.Ordinal))
            return Prefixed(text, ProcessPrefix, OkfActorKind.Process);

        // Exactly one slash with something on both sides, and no whitespace.
        var slash = text.IndexOf('/');
        if (slash > 0 && slash < text.Length - 1
            && text.IndexOf('/', slash + 1) < 0
            && !text.Any(char.IsWhiteSpace))
            return new OkfActor(OkfActorKind.Agent, text, text[..slash], text[(slash + 1)..]);

        return new OkfActor(OkfActorKind.Unknown, text, null, null);
    }

    private static OkfActor Prefixed(string text, string prefix, OkfActorKind kind)
    {
        var id = text[prefix.Length..].Trim();
        return id.Length == 0
            ? new OkfActor(OkfActorKind.Unknown, text, null, null)
            : new OkfActor(kind, text, id, null);
    }

    public override string ToString() => Raw;
}
