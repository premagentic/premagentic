using System.Globalization;
using Premagentic.Core.Sources;

namespace Premagentic.Core.Okf;

/// <summary>
/// How far a concept has been confirmed, derived from <c>verified</c> exactly as
/// spec section 5.3 gives it. The numeric order is the trust order, so a minimum
/// can be checked with <c>&gt;=</c>. The numbers are stored, so they never change.
/// </summary>
public enum OkfTrustTier
{
    /// <summary>No usable <c>verified</c> entry.</summary>
    Unverified = 0,

    /// <summary>Verified, and only by actors that are not people.</summary>
    MachineConfirmed = 1,

    /// <summary>Verified by at least one <c>human:</c> actor.</summary>
    HumanReviewed = 2,
}

/// <summary>Who wrote the content, derived from <c>generated</c>. The numbers are stored, so they never change.</summary>
public enum OkfAuthorship
{
    /// <summary>No <c>generated</c> field, which is every ordinary file.</summary>
    Unknown = 0,

    /// <summary><c>generated.by</c> is a <c>human:</c> actor.</summary>
    Human = 1,

    /// <summary>
    /// <c>generated</c> names an agent or a process, or is present but does not
    /// name a person in a form this reader understands. Also a concept in an
    /// OKF bundle whose frontmatter could not be read at all, and one that does
    /// not say who wrote it when its source treats undeclared authorship as
    /// machine-written.
    /// </summary>
    Machine = 2,
}

/// <param name="By">Null when <c>generated</c> is present but names no actor.</param>
public sealed record OkfGeneration(OkfActor? By, DateTimeOffset? At);

public sealed record OkfVerification(OkfActor By, DateTimeOffset? At);

/// <summary>One entry of <c>sources</c> (spec section 5.1). Recorded, never followed.</summary>
public sealed record OkfSource(
    string? Id,
    string? Resource,
    string? Title,
    OkfActor? Author,
    long? UsageCount,
    DateTimeOffset? LastModified);

/// <summary>
/// What a document's frontmatter says in Open Knowledge Format terms, and the
/// values derived from it. Everything here is read as text: OKF fields that
/// name code to run, such as an attested computation's executor and attester,
/// are never followed or executed.
/// <para>
/// Frontmatter is self-declared and OKF has no signatures, so a tier or an
/// authorship value is only as trustworthy as control over who can write the
/// file.
/// </para>
/// </summary>
public sealed record OkfMetadata
{
    public FrontmatterState FrontmatterState { get; init; }

    /// <summary>The path inside the bundle with <c>.md</c> removed. Set only for a source read as a bundle.</summary>
    public string? ConceptId { get; init; }

    /// <summary>
    /// The document was read as a concept of a source declared an OKF bundle,
    /// where every concept is expected to carry frontmatter.
    /// </summary>
    public bool BundleConcept { get; init; }

    /// <summary>
    /// The source's undeclared-authorship setting: a bundle concept that does
    /// not say who wrote it counts as machine-written. Has no effect unless
    /// <see cref="BundleConcept"/> is set.
    /// </summary>
    public bool UndeclaredIsMachine { get; init; }

    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Resource { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The status as written. <see cref="DocumentLifecycle.FromSourceStatus"/> maps it.</summary>
    public string? Status { get; init; }

    public DateTimeOffset? StaleAfter { get; init; }

    /// <summary>
    /// <c>stale_after</c> is present and is not a readable instant. Such a
    /// concept counts as stale: a date nobody can read cannot vouch that the
    /// content is still current.
    /// </summary>
    public bool StaleAfterUnreadable { get; init; }

    public OkfGeneration? Generated { get; init; }
    public IReadOnlyList<OkfVerification> Verified { get; init; } = [];
    public IReadOnlyList<OkfSource> Sources { get; init; } = [];

    /// <summary>
    /// OKF fields that are present but not in the shape the spec gives them.
    /// Each was read as far as it could be; none causes the document to be
    /// rejected.
    /// </summary>
    public IReadOnlyList<string> MalformedFields { get; init; } = [];

    public OkfTrustTier TrustTier =>
        Verified.Count == 0 ? OkfTrustTier.Unverified
        : Verified.Any(v => v.By.IsHuman) ? OkfTrustTier.HumanReviewed
        : OkfTrustTier.MachineConfirmed;

    /// <remarks>
    /// A bundle concept whose frontmatter could not be read at all is Machine,
    /// which leaves it unverified and inside the trust setting's reach: a bundle
    /// is where agents write, and a writer that produced broken YAML has lost
    /// the very fields that would say who it was. Outside a bundle the same
    /// file is an ordinary document of unknown authorship.
    /// <para>
    /// A bundle concept that does not say who wrote it is Unknown, and so never
    /// held back, unless its source has <see cref="UndeclaredIsMachine"/> set.
    /// </para>
    /// </remarks>
    public OkfAuthorship Authorship => Generated switch
    {
        _ when BundleConcept && FrontmatterState == FrontmatterState.Unparseable => OkfAuthorship.Machine,
        null when BundleConcept && UndeclaredIsMachine => OkfAuthorship.Machine,
        null => OkfAuthorship.Unknown,
        { By.IsHuman: true } => OkfAuthorship.Human,
        _ => OkfAuthorship.Machine,
    };

    /// <summary>
    /// A bundle concept that does not say who wrote it: no frontmatter, or
    /// frontmatter with no <c>generated</c>. What the undeclared-authorship
    /// setting acts on, whether it is on or off. Frontmatter that cannot be
    /// read is not counted here, because it is held back already.
    /// </summary>
    public bool DeclaresNoAuthor =>
        BundleConcept && Generated is null && FrontmatterState != FrontmatterState.Unparseable;

    /// <summary>The latest <c>verified</c> time, which the spec calls how recently it was verified.</summary>
    public DateTimeOffset? LastVerifiedAt => Verified.Max(v => v.At);

    /// <summary>
    /// Whether the concept is stale at <paramref name="now"/>, meaning
    /// <c>now &gt;= stale_after</c> (spec section 5.5). Staleness depends on
    /// when the question is asked, so it is evaluated then, never stored.
    /// </summary>
    public bool IsStaleAt(DateTimeOffset now) =>
        StaleAfterUnreadable || (StaleAfter is { } staleAfter && now >= staleAfter);

    public bool IsStale(TimeProvider clock) => IsStaleAt(clock.GetUtcNow());

    /// <summary>The concept id for a path inside a bundle: the path with <c>.md</c> removed.</summary>
    public static string ConceptIdFor(string pathInBundle)
    {
        var path = pathInBundle.Replace('\\', '/').TrimStart('/');
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
    }

    /// <summary>
    /// Reads the OKF fields out of parsed frontmatter. Never throws. Unknown
    /// keys, unknown types and a missing <c>type</c> are all accepted (spec
    /// section 11); a malformed field is read as far as it goes and listed in
    /// <see cref="MalformedFields"/>.
    /// </summary>
    public static OkfMetadata FromFrontmatter(FrontmatterResult frontmatter)
    {
        var values = frontmatter.Values;
        var malformed = new List<string>();

        var staleAfter = ReadStaleAfter(values, malformed, out var staleAfterUnreadable);

        return new OkfMetadata
        {
            FrontmatterState = frontmatter.State,
            Type = Text(values, "type"),
            Title = Text(values, "title"),
            Description = Text(values, "description"),
            Resource = Text(values, "resource"),
            Tags = ReadTags(values, malformed),
            Status = Text(values, "status"),
            StaleAfter = staleAfter,
            StaleAfterUnreadable = staleAfterUnreadable,
            Generated = ReadGenerated(values, malformed),
            Verified = ReadVerified(values, malformed),
            Sources = ReadSources(values, malformed),
            MalformedFields = malformed,
        };
    }

    /// <summary>
    /// Reads an OKF timestamp. The spec requires an explicit UTC offset (section
    /// 5). A timestamp written without one is read as UTC and accepted rather
    /// than dropped, because the field this matters for most is
    /// <c>stale_after</c>, and ignoring it would serve stale content as current,
    /// which is the unsafe direction.
    /// </summary>
    public static bool TryParseInstant(string text, out DateTimeOffset instant)
    {
        if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            instant = parsed.ToUniversalTime();
            return true;
        }

        instant = default;
        return false;
    }

    private static DateTimeOffset? ReadStaleAfter(
        IReadOnlyDictionary<string, object> values, List<string> malformed, out bool unreadable)
    {
        unreadable = false;
        if (!values.TryGetValue("stale_after", out var value) || IsNull(value)) return null;

        if (value is string text && TryParseInstant(text, out var instant)) return instant;

        unreadable = true;
        malformed.Add("stale_after");
        return null;
    }

    private static OkfGeneration? ReadGenerated(IReadOnlyDictionary<string, object> values, List<string> malformed)
    {
        if (!values.TryGetValue("generated", out var value) || IsNull(value)) return null;

        // Present but not a mapping still says the content was generated, so it
        // is kept with no actor, which derives as machine-written rather than as
        // an ordinary file that no trust setting applies to.
        if (value is not IReadOnlyDictionary<string, object> map)
        {
            malformed.Add("generated");
            return new OkfGeneration(null, null);
        }

        var by = Text(map, "by") is { } actor ? OkfActor.Parse(actor) : null;
        var at = ReadInstant(map, "at", out var atUnreadable);
        if (by is null || atUnreadable) malformed.Add("generated");
        return new OkfGeneration(by, at);
    }

    private static List<OkfVerification> ReadVerified(IReadOnlyDictionary<string, object> values, List<string> malformed)
    {
        var verified = new List<OkfVerification>();
        if (!values.TryGetValue("verified", out var value) || IsNull(value)) return verified;

        // A bare mapping is a one-element list (spec section 5.2).
        var entries = AsEntries(value);
        if (entries is null)
        {
            malformed.Add("verified");
            return verified;
        }

        // An entry that names no actor is dropped. That can only lower the tier.
        var bad = false;
        foreach (var entry in entries)
        {
            if (entry is IReadOnlyDictionary<string, object> map && Text(map, "by") is { } by)
            {
                var at = ReadInstant(map, "at", out var atUnreadable);
                bad |= atUnreadable;
                verified.Add(new OkfVerification(OkfActor.Parse(by), at));
            }
            else
            {
                bad = true;
            }
        }

        if (bad) malformed.Add("verified");
        return verified;
    }

    private static List<OkfSource> ReadSources(IReadOnlyDictionary<string, object> values, List<string> malformed)
    {
        var sources = new List<OkfSource>();
        if (!values.TryGetValue("sources", out var value) || IsNull(value)) return sources;

        var entries = AsEntries(value);
        if (entries is null)
        {
            malformed.Add("sources");
            return sources;
        }

        var bad = false;
        foreach (var entry in entries)
        {
            if (entry is not IReadOnlyDictionary<string, object> map)
            {
                bad = true;
                continue;
            }

            long? usageCount = null;
            if (Text(map, "usage_count") is { } count)
            {
                if (long.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) usageCount = parsed;
                else bad = true;
            }

            var resource = Text(map, "resource");
            var lastModified = ReadInstant(map, "last_modified", out var lastModifiedUnreadable);
            bad |= resource is null || lastModifiedUnreadable;

            sources.Add(new OkfSource(
                Id: Text(map, "id"),
                Resource: resource,
                Title: Text(map, "title"),
                Author: Text(map, "author") is { } author ? OkfActor.Parse(author) : null,
                UsageCount: usageCount,
                LastModified: lastModified));
        }

        if (bad) malformed.Add("sources");
        return sources;
    }

    private static List<string> ReadTags(IReadOnlyDictionary<string, object> values, List<string> malformed)
    {
        if (!values.TryGetValue("tags", out var value) || IsNull(value)) return [];

        switch (value)
        {
            case string single:
                return [single.Trim()];

            case IReadOnlyList<object> items:
                var tags = new List<string>();
                var bad = false;
                foreach (var item in items)
                {
                    if (item is string tag && !IsNull(tag)) tags.Add(tag.Trim());
                    else bad = true;
                }
                if (bad) malformed.Add("tags");
                return tags;

            default:
                malformed.Add("tags");
                return [];
        }
    }

    private static IReadOnlyList<object>? AsEntries(object value) => value switch
    {
        IReadOnlyDictionary<string, object> one => new object[] { one },
        IReadOnlyList<object> many => many,
        _ => null,
    };

    private static DateTimeOffset? ReadInstant(IReadOnlyDictionary<string, object> map, string key, out bool unreadable)
    {
        unreadable = false;
        if (!map.TryGetValue(key, out var value) || IsNull(value)) return null;
        if (value is string text && TryParseInstant(text, out var instant)) return instant;
        unreadable = true;
        return null;
    }

    private static string? Text(IReadOnlyDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is string text && !IsNull(text) ? text.Trim() : null;

    // YAML's empty, ~ and null all mean no value, so a key written with no
    // value reads as absent rather than as malformed.
    private static bool IsNull(object value) =>
        value is string text && text.Trim() is "" or "~" or "null" or "Null" or "NULL";
}
