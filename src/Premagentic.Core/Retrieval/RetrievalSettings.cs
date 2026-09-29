using System.Text.Json;
using Premagentic.Core.Identity;

namespace Premagentic.Core.Retrieval;

/// <summary>A stored retrieval setting that cannot be used, so its code default applies.</summary>
/// <param name="Problem">One sentence naming what is allowed.</param>
public sealed record RetrievalSettingProblem(string Key, string Problem);

/// <summary>The retrieval tuning in force, and every stored value that could not be used.</summary>
public sealed record RetrievalSettingsReading(
    RetrievalTuning Tuning,
    AuthorityWeights Authority,
    IReadOnlyList<RetrievalSettingProblem> Problems);

/// <summary>
/// The retrieval tuning a deployment may change, kept in
/// <c>prem_config.setting</c> as JSON:
/// <list type="bullet">
/// <item><c>retrieval.no_answer_distance_floor</c>: a number greater than 0 and
/// less than 2. See <see cref="RetrievalTuning.NoAnswerDistanceFloor"/>.</item>
/// <item><c>retrieval.rrf_k</c>: a whole number from 1 to 1000.</item>
/// <item><c>retrieval.fallback_rrf_weight</c>: a number from 0 to 1.</item>
/// <item><c>retrieval.authority_weights</c>: an object such as
/// <c>{"default": 1.0, "by_class": {"runbook": 1.2}}</c>, every weight greater
/// than 0, class names compared ignoring case. Either member may be left out.</item>
/// </list>
/// A key with nothing stored keeps the code default, and so does a stored value
/// that cannot be used, which is reported rather than thrown: a mistyped
/// setting changes how results are ordered, never whether a search runs.
/// <para>
/// Nothing here ships a weighting table. The default is flat; see
/// <see cref="AuthorityWeights"/> for why.
/// </para>
/// </summary>
public static class RetrievalSettings
{
    public const string NoAnswerDistanceFloor = "retrieval.no_answer_distance_floor";
    public const string RrfK = "retrieval.rrf_k";
    public const string FallbackRrfWeight = "retrieval.fallback_rrf_weight";
    public const string Authority = "retrieval.authority_weights";

    public static IReadOnlyList<string> Keys { get; } = [NoAnswerDistanceFloor, RrfK, FallbackRrfWeight, Authority];

    private const string DefaultMember = "default";
    private const string ByClassMember = "by_class";

    // The longest stored value a problem sentence quotes back, so a pasted
    // document cannot fill a log line.
    private const int QuotedLength = 40;

    /// <summary>What <paramref name="key"/> accepts, as a phrase that completes "takes ...".</summary>
    /// <exception cref="ArgumentException">The key is not a retrieval setting.</exception>
    public static string Allowed(string key) => key switch
    {
        NoAnswerDistanceFloor => "a number greater than 0 and less than 2",
        RrfK => "a whole number from 1 to 1000",
        FallbackRrfWeight => "a number from 0 to 1",
        Authority => """an object such as {"default": 1.0, "by_class": {"runbook": 1.2}}, every weight a number greater than 0""",
        _ => throw UnknownKey(key),
    };

    /// <summary>The code default for <paramref name="key"/>, as it would be stored.</summary>
    /// <exception cref="ArgumentException">The key is not a retrieval setting.</exception>
    public static JsonElement DefaultValue(string key) => key switch
    {
        NoAnswerDistanceFloor => JsonSerializer.SerializeToElement(RetrievalTuning.Default.NoAnswerDistanceFloor),
        RrfK => JsonSerializer.SerializeToElement(RetrievalTuning.Default.RrfK),
        FallbackRrfWeight => JsonSerializer.SerializeToElement(RetrievalTuning.Default.FallbackRrfWeight),
        Authority => JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [DefaultMember] = 1.0,
            [ByClassMember] = new Dictionary<string, double>(),
        }),
        _ => throw UnknownKey(key),
    };

    /// <summary>
    /// True when <paramref name="value"/> is allowed for <paramref name="key"/>.
    /// Otherwise <paramref name="problem"/> is one sentence naming what is
    /// allowed. An unknown key is not allowed, and the sentence names the keys.
    /// </summary>
    public static bool TryParse(string key, JsonElement value, out string? problem) =>
        TryRead(key, value, out _, out problem);

    /// <summary>
    /// The tuning given each key's stored value. Pure: a missing key keeps its
    /// code default, and so does a stored value that cannot be used, which is
    /// then listed in <see cref="RetrievalSettingsReading.Problems"/>. Keys that
    /// are not retrieval settings are ignored.
    /// </summary>
    public static RetrievalSettingsReading FromStored(IReadOnlyDictionary<string, JsonElement> stored)
    {
        var tuning = RetrievalTuning.Default;
        var authority = AuthorityWeights.Flat;
        var problems = new List<RetrievalSettingProblem>();

        foreach (var key in Keys)
        {
            if (!stored.TryGetValue(key, out var value)) continue;
            if (!TryRead(key, value, out var parsed, out var problem))
            {
                problems.Add(new RetrievalSettingProblem(key, problem!));
                continue;
            }
            switch (key)
            {
                case NoAnswerDistanceFloor: tuning = tuning with { NoAnswerDistanceFloor = (double)parsed! }; break;
                case RrfK: tuning = tuning with { RrfK = (int)parsed! }; break;
                case FallbackRrfWeight: tuning = tuning with { FallbackRrfWeight = (double)parsed! }; break;
                case Authority: authority = (AuthorityWeights)parsed!; break;
            }
        }

        return new RetrievalSettingsReading(tuning, authority, problems);
    }

    /// <summary>
    /// The tuning as it is stored now, all four keys in one read. Nothing is
    /// cached, so a change applies to the next search that loads it.
    /// </summary>
    public static async Task<RetrievalSettingsReading> LoadAsync(SettingsStore settings, CancellationToken ct = default) =>
        FromStored(await settings.GetManyAsync(Keys, ct));

    private static bool TryRead(string key, JsonElement value, out object? parsed, out string? problem)
    {
        parsed = null;
        problem = null;
        switch (key)
        {
            case NoAnswerDistanceFloor:
                if (TryNumber(value, out var floor) && floor > 0 && floor < 2) parsed = floor;
                break;
            case RrfK:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var k) && k is >= 1 and <= 1000) parsed = k;
                break;
            case FallbackRrfWeight:
                if (TryNumber(value, out var weight) && weight is >= 0 and <= 1) parsed = weight;
                break;
            case Authority:
                if (TryAuthority(value, out var authority, out problem)) parsed = authority;
                break;
            default:
                problem = UnknownKey(key).Message;
                return false;
        }

        if (parsed is not null) return true;
        problem ??= $"{key} takes {Allowed(key)}, not {Quote(value)}.";
        return false;
    }

    private static bool TryAuthority(JsonElement value, out AuthorityWeights? authority, out string? problem)
    {
        authority = null;
        problem = null;
        if (value.ValueKind != JsonValueKind.Object) return false;

        var defaultWeight = 1.0;
        var byClass = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in value.EnumerateObject())
        {
            if (!members.Add(member.Name))
                return Refuse(out problem, $"{Authority} takes each member once, not \"{Clip(member.Name)}\" twice.");
            switch (member.Name)
            {
                case DefaultMember:
                    if (!TryWeight(member.Value, out defaultWeight))
                        return Refuse(out problem, $"{Authority} takes a default weight greater than 0, not {Quote(member.Value)}.");
                    break;
                case ByClassMember:
                    if (member.Value.ValueKind != JsonValueKind.Object)
                        return Refuse(out problem, $"{Authority} takes an object of class weights as \"{ByClassMember}\", not {Quote(member.Value)}.");
                    foreach (var entry in member.Value.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(entry.Name))
                            return Refuse(out problem, $"{Authority} takes a class name that is not blank.");
                        if (!TryWeight(entry.Value, out var classWeight))
                            return Refuse(out problem,
                                $"{Authority} takes a weight greater than 0 for every class, not {Quote(entry.Value)} for \"{Clip(entry.Name)}\".");
                        if (!byClass.TryAdd(entry.Name, classWeight))
                            return Refuse(out problem,
                                $"{Authority} takes each class once, ignoring case, not \"{Clip(entry.Name)}\" twice.");
                    }
                    break;
                default:
                    return Refuse(out problem,
                        $"{Authority} takes only \"{DefaultMember}\" and \"{ByClassMember}\", not \"{Clip(member.Name)}\".");
            }
        }

        authority = new AuthorityWeights(byClass, defaultWeight);
        return true;
    }

    private static bool Refuse(out string? problem, string sentence)
    {
        problem = sentence;
        return false;
    }

    private static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static bool TryWeight(JsonElement value, out double weight) => TryNumber(value, out weight) && weight > 0;

    private static string Quote(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => Clip(value.GetRawText()),
        JsonValueKind.String => $"the text \"{Clip(value.GetString() ?? "")}\"",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    private static string Clip(string text) =>
        text.Length <= QuotedLength ? text : string.Concat(text.AsSpan(0, QuotedLength), "...");

    private static ArgumentException UnknownKey(string key) =>
        new($"There is no retrieval setting '{Clip(key)}': the retrieval settings are {string.Join(", ", Keys)}.");
}
