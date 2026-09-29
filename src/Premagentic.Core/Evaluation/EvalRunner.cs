using System.Text;
using System.Text.Json;
using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Security;

namespace Premagentic.Core.Evaluation;

/// <summary>One question of a golden set, as the file writes it.</summary>
public sealed record GoldenCase(
    string Id,
    string Question,
    string Category,
    string[] ExpectedSourcePaths,
    string? ExpectedHeading,
    string[] ForbiddenSourcePaths,
    bool ArchiveAllowed,
    bool ExpectNoAnswer,
    string Notes,
    string[]? As = null);

/// <summary>How one case was judged.</summary>
public sealed record EvalVerdict(
    bool Pass, bool ExpectedTop5, bool ExpectedTop3, bool ForbiddenHit, bool HeadingMatch, bool NoAnswerBehavior);

/// <summary>One case of a run.</summary>
/// <param name="NotRun">Why the case was not run, or null when it ran.</param>
/// <param name="Result">
/// The search, carrying the retrieval settings it was ranked under, which are
/// the settings the case was judged under; null when the case did not run.
/// </param>
public sealed record EvalCaseResult(GoldenCase Case, EvalVerdict? Verdict, string? NotRun, string AccessLabel, SearchResult? Result)
{
    public bool Pass => Verdict?.Pass ?? false;
}

/// <summary>What a run of a golden set found.</summary>
public sealed record EvalSummary(IReadOnlyList<EvalCaseResult> Cases)
{
    public int Passed => Cases.Count(c => c.Pass);

    /// <summary>The cases that expect no answer.</summary>
    public int NoAnswerCases => Cases.Count(c => c.Case.ExpectNoAnswer);

    /// <summary>The cases that expect no answer and were given none.</summary>
    public int NoAnswerCorrect => Cases.Count(c => c.Case.ExpectNoAnswer && c.Verdict is { NoAnswerBehavior: true });

    /// <summary>The ids of the cases that did not pass, in the order of the set.</summary>
    public IReadOnlyList<string> Failed => [.. Cases.Where(c => !c.Pass).Select(c => c.Case.Id)];

    /// <summary>Mean search time over every case, the ones not run counting as zero.</summary>
    public long MeanLatencyMs => Cases.Sum(c => c.Result?.ElapsedMs ?? 0) / Math.Max(Cases.Count, 1);

    /// <summary>The settings the first case that ran was ranked and judged under; null when none ran.</summary>
    public RetrievalSettingsReading? Settings => Cases.Select(c => c.Result?.Settings).FirstOrDefault(s => s is not null);

    /// <summary>
    /// True when the stored settings changed between two cases, so not every
    /// case ran under <see cref="Settings"/>. Each case was still judged by the
    /// settings its own search ranked under.
    /// </summary>
    public bool SettingsChanged
    {
        get
        {
            var ran = Cases.Select(c => c.Result?.Settings).OfType<RetrievalSettingsReading>().ToArray();
            return ran.Zip(ran.Skip(1)).Any(pair => !RetrievalReadings.Same(pair.First, pair.Second));
        }
    }
}

/// <summary>
/// Runs a deployment's golden-question set against its index. <see cref="EvaluateAsync"/>
/// judges the cases and returns what it found; <see cref="RunAsync"/> also
/// writes a Markdown report and returns exit code 0 when every case passes, 1
/// otherwise, so the eval can gate a change before it reaches the customer.
/// <para>
/// The golden set is the deliverable that makes a quality claim testable. It is
/// written per deployment against that corpus, because a set written for
/// another organization's documents proves nothing about this one.
/// </para>
/// <para>
/// A case may also assert authorization: give it <c>as</c> principals and list
/// the documents it must NOT reach in <c>forbiddenSourcePaths</c>. That turns
/// "the HR folder does not leak to everyone" into a test that fails loudly
/// rather than a promise in a statement of work.
/// </para>
/// <para>
/// The <c>as</c> principals are written the way people write them, by name
/// (<c>group:hr</c>), and translated exactly as <c>prem search --as</c>
/// translates them, because access rules name groups by id. A name that does
/// not resolve fails its case without running it: run as a caller holding
/// nothing, an access-denied case would pass for the wrong reason.
/// </para>
/// <para>
/// Each case is judged by the no-answer floor of the settings its own search
/// ranked under (<see cref="SearchResult.Settings"/>), so the judging and the
/// ranking always agree, even if an administrator saves a change mid-run.
/// </para>
/// </summary>
public sealed class EvalRunner(HybridSearch search, Guid tenantId, PrincipalNames names)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static readonly RetrievalSettingsReading CodeDefaults = new(RetrievalTuning.Default, AuthorityWeights.Flat, []);

    /// <summary>The cases of the golden set at <paramref name="goldenPath"/>.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="JsonException">The file is not JSON of the golden set's shape.</exception>
    /// <exception cref="InvalidOperationException">The file holds JSON null.</exception>
    public static IReadOnlyList<GoldenCase> LoadCases(string goldenPath) =>
        JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(goldenPath), Options)
        ?? throw new InvalidOperationException("Could not parse the golden question set.");

    /// <summary>
    /// The cases of the golden set at <paramref name="goldenPath"/>, or a plain
    /// sentence saying why not. The sentence never quotes the file or the
    /// parser, so it can be shown to whoever asked for the run.
    /// </summary>
    public static bool TryLoadCases(string goldenPath, out IReadOnlyList<GoldenCase> cases, out string? problem)
    {
        cases = [];
        try
        {
            var loaded = LoadCases(goldenPath);
            problem = loaded.Count == 0
                ? $"The golden set at {goldenPath} holds no questions."
                : loaded.Any(c => string.IsNullOrWhiteSpace(c?.Id) || string.IsNullOrWhiteSpace(c.Question)
                                  || c.ExpectedSourcePaths is null || c.ForbiddenSourcePaths is null)
                    ? $"The golden set at {goldenPath} has a question without an id, a question, expectedSourcePaths or forbiddenSourcePaths."
                    : TooLongQuestion(goldenPath, loaded);
            if (problem is null) cases = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"The golden set at {goldenPath} cannot be read by this process.";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            problem = $"The golden set at {goldenPath} is not a JSON list of golden questions.";
        }
        return problem is null;
    }

    /// <summary>
    /// The sentence that refuses the golden set at <paramref name="goldenPath"/>
    /// for holding a question a search would refuse, being longer than
    /// <see cref="QueryLimits.MaxLength"/>; null when it holds none, and when it
    /// cannot be read at all, which the run then meets as it always has.
    /// </summary>
    public static string? OverLongQuestion(string goldenPath)
    {
        try
        {
            return TooLongQuestion(goldenPath, LoadCases(goldenPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // A search refuses a longer question, so a run could not ask it.
    private static string? TooLongQuestion(string goldenPath, IReadOnlyList<GoldenCase> cases) =>
        cases.FirstOrDefault(c => c?.Question is { Length: > QueryLimits.MaxLength }) is { } tooLong
            ? $"The golden set at {goldenPath} has a question over {QueryLimits.MaxLength.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters, '{tooLong.Id}', which a search refuses."
            : null;

    /// <summary>Runs and judges every case. Nothing is written.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled before the run ended.</exception>
    public async Task<EvalSummary> EvaluateAsync(IReadOnlyList<GoldenCase> cases, CancellationToken ct = default)
    {
        var results = new List<EvalCaseResult>(cases.Count);
        foreach (var c in cases)
        {
            ct.ThrowIfCancellationRequested();

            // A case with no principals runs unrestricted so a retrieval-quality
            // case is not silently filtered by the access gate. A case that
            // names principals is testing the gate itself.
            AccessScope scope;
            if (c.As is { Length: > 0 })
            {
                var (principals, error) = await TranslateAsync(c.As);
                if (error is not null)
                {
                    results.Add(new EvalCaseResult(c, null, error, "", null));
                    continue;
                }
                scope = AccessScope.ForPrincipals($"eval:{c.Id}", principals);
            }
            else
            {
                scope = AccessScope.UnrestrictedAudited($"eval:{c.Id}");
            }

            var result = await search.SearchAsync(tenantId, c.Question,
                new SearchOptions(scope, TopK: 5, IncludeHistorical: c.ArchiveAllowed), ct);
            results.Add(new EvalCaseResult(c, Judge(c, result), null, scope.AuditLabel, result));
        }
        return new EvalSummary(results);
    }

    /// <summary>Runs the golden set, writes the Markdown report, and returns 0 when every case passes.</summary>
    public async Task<int> RunAsync(string goldenPath, string reportPath)
    {
        var cases = LoadCases(goldenPath);
        var summary = await EvaluateAsync(cases);

        var report = new StringBuilder();
        report.AppendLine($"# Premagentic retrieval evaluation, {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine();
        report.AppendLine($"Cases: {cases.Count}. No-answer distance floor: {(summary.Settings ?? CodeDefaults).Tuning.NoAnswerDistanceFloor}.");
        if (summary.SettingsChanged)
            report.AppendLine(
                "The stored retrieval settings changed during the run, so each case below names the settings it was " +
                "ranked and judged under.");
        report.AppendLine("Latency includes query-embedding time.");
        report.AppendLine();

        foreach (var r in summary.Cases)
        {
            var c = r.Case;
            if (r.NotRun is { } error)
            {
                report.AppendLine($"## {c.Id}, FAIL ({c.Category})");
                report.AppendLine();
                report.AppendLine($"**Q:** {c.Question}");
                report.AppendLine();
                report.AppendLine($"- not run: {error}");
                report.AppendLine();
                Console.WriteLine($"{c.Id}: FAIL, not run: {error}");
                continue;
            }

            var verdict = r.Verdict!;
            var result = r.Result!;
            report.AppendLine($"## {c.Id}, {(verdict.Pass ? "PASS" : "FAIL")} ({c.Category})");
            report.AppendLine();
            report.AppendLine($"**Q:** {c.Question}");
            report.AppendLine();
            report.AppendLine(
                $"- expected-in-top5: {Mark(verdict.ExpectedTop5)}  |  expected-in-top3: {Mark(verdict.ExpectedTop3)}  " +
                $"|  no-forbidden: {Mark(!verdict.ForbiddenHit)}  |  heading: {Mark(verdict.HeadingMatch)}  " +
                $"|  no-answer-behavior: {Mark(verdict.NoAnswerBehavior)}");
            report.AppendLine($"- access: {r.AccessLabel}, latency: {result.ElapsedMs} ms, historical={c.ArchiveAllowed}");
            if (summary.SettingsChanged)
                report.AppendLine($"- settings: {RetrievalReadings.Describe(result.Settings)}");
            report.AppendLine();
            foreach (var hit in result.Hits)
                report.AppendLine(
                    $"  {hit.FusedScore:F4}  {hit.Citation}  (lex={LexMark(hit)}, " +
                    $"vec={hit.VectorRank?.ToString() ?? "-"}, dist={hit.CosineDistance?.ToString("F3") ?? "-"})");
            report.AppendLine();
        }

        report.Insert(0, $"**Result: {summary.Passed}/{cases.Count} passed. Mean latency {summary.MeanLatencyMs} ms.**\n\n");
        File.WriteAllText(reportPath, report.ToString());
        Console.WriteLine($"{summary.Passed}/{cases.Count} passed.");
        return summary.Passed == cases.Count ? 0 : 1;
    }

    /// <summary>The case's names as principals, or the first name that does not resolve and why.</summary>
    private async Task<(string[] Principals, string? Error)> TranslateAsync(string[] asNames)
    {
        var principals = new List<string>();
        foreach (var name in asNames)
        {
            try
            {
                principals.Add((await names.ToPrincipalAsync(name)).ToString());
            }
            catch (ArgumentException ex)
            {
                return ([], $"'as' name '{name}' does not resolve: {ex.Message}");
            }
        }
        return (principals.ToArray(), null);
    }

    private static EvalVerdict Judge(GoldenCase c, SearchResult result)
    {
        var top5Paths = result.Hits.Select(h => h.Path).ToArray();
        var top3Paths = top5Paths.Take(3).ToArray();
        var floor = result.Settings.Tuning.NoAnswerDistanceFloor;

        // OR-fallback lexical hits are degraded matches on any single term, so
        // only primary AND-pass hits count as lexical evidence of an answer.
        var answered = result.Hits.Any(h =>
            (h.LexicalRank is not null && !h.LexicalFallback)
            || (h.CosineDistance is { } d && d < floor));

        if (c.ExpectNoAnswer)
        {
            var pass = !answered && !Forbidden(c, top5Paths);
            return new EvalVerdict(pass, ExpectedTop5: true, ExpectedTop3: true,
                ForbiddenHit: Forbidden(c, top5Paths), HeadingMatch: true, NoAnswerBehavior: !answered);
        }

        var expectedTop5 = c.ExpectedSourcePaths.Length == 0 || c.ExpectedSourcePaths.Any(top5Paths.Contains);
        var expectedTop3 = c.ExpectedSourcePaths.Length == 0 || c.ExpectedSourcePaths.Any(top3Paths.Contains);
        var forbidden = Forbidden(c, top5Paths);
        var headingMatch = c.ExpectedHeading is null || result.Hits.Any(h =>
            c.ExpectedSourcePaths.Contains(h.Path) &&
            h.HeadingPath.Contains(c.ExpectedHeading, StringComparison.OrdinalIgnoreCase));

        return new EvalVerdict(
            Pass: expectedTop5 && !forbidden,
            ExpectedTop5: expectedTop5,
            ExpectedTop3: expectedTop3,
            ForbiddenHit: forbidden,
            HeadingMatch: headingMatch,
            NoAnswerBehavior: true);
    }

    private static bool Forbidden(GoldenCase c, string[] paths) =>
        c.ForbiddenSourcePaths.Any(paths.Contains);

    private static string Mark(bool ok) => ok ? "PASS" : "FAIL";

    // "~N" marks an OR-fallback lexical rank, "N" a primary AND-pass rank.
    private static string LexMark(SearchHit hit) =>
        hit.LexicalRank is null ? "-" : hit.LexicalFallback ? $"~{hit.LexicalRank}" : hit.LexicalRank.ToString()!;
}
