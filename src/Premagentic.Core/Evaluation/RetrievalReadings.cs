using System.Globalization;
using System.Text.Json;
using Premagentic.Core.Retrieval;

namespace Premagentic.Core.Evaluation;

/// <summary>
/// Retrieval readings compared by value and written down in the shape the
/// settings take, so a run's record says exactly what it ran under.
/// </summary>
public static class RetrievalReadings
{
    /// <summary>
    /// True when both readings rank alike: the same tuning, the same default
    /// weight, and the same weight for every class. Problems are not compared,
    /// since a value that is not used changes nothing.
    /// </summary>
    public static bool Same(RetrievalSettingsReading a, RetrievalSettingsReading b) =>
        a.Tuning == b.Tuning
        && a.Authority.DefaultWeight.Equals(b.Authority.DefaultWeight)
        && a.Authority.ByClass.Count == b.Authority.ByClass.Count
        && a.Authority.ByClass.All(entry =>
            b.Authority.ByClass.FirstOrDefault(other => string.Equals(other.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
                is { Key: not null } match && match.Value.Equals(entry.Value));

    /// <summary>
    /// The reading keyed by the settings' own names, the class weights in the
    /// shape <see cref="RetrievalSettings.Authority"/> takes, and the keys whose
    /// stored value was not used.
    /// </summary>
    public static Dictionary<string, object> ToValue(RetrievalSettingsReading reading) => new()
    {
        [RetrievalSettings.NoAnswerDistanceFloor] = reading.Tuning.NoAnswerDistanceFloor,
        [RetrievalSettings.RrfK] = reading.Tuning.RrfK,
        [RetrievalSettings.FallbackRrfWeight] = reading.Tuning.FallbackRrfWeight,
        [RetrievalSettings.Authority] = new Dictionary<string, object>
        {
            ["default"] = reading.Authority.DefaultWeight,
            ["by_class"] = new SortedDictionary<string, double>(
                reading.Authority.ByClass.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal),
        },
        ["not_used"] = reading.Problems.Select(p => p.Key).ToArray(),
    };

    /// <summary>The reading on one line, for a report.</summary>
    public static string Describe(RetrievalSettingsReading reading) =>
        string.Create(CultureInfo.InvariantCulture,
            $"no-answer floor {reading.Tuning.NoAnswerDistanceFloor}, rrf_k {reading.Tuning.RrfK}, " +
            $"fallback weight {reading.Tuning.FallbackRrfWeight}, class weights " +
            $"{JsonSerializer.Serialize(ToValue(reading)[RetrievalSettings.Authority])}");
}
