using Premagentic.Core.Security.Acl;
using Xunit.Abstractions;

namespace Premagentic.Tests;

/// <summary>
/// The mutation check for the permission evaluator, with no tool dependency.
/// <para>
/// The corpus passing against the real evaluator proves little on its own: a
/// corpus of weak cases passes against a weak evaluator too. So each mutant
/// below is a plausible way to get the evaluator wrong, and each MUST fail at
/// least one case. A mutant that passes the whole corpus means the corpus has a
/// hole, and this suite fails until a case is added that catches it.
/// </para>
/// </summary>
public class AclConformanceTests(ITestOutputHelper output)
{
    public static readonly IReadOnlyDictionary<string, Func<AclSet, PrincipalSet, bool>> Mutants =
        new Dictionary<string, Func<AclSet, PrincipalSet, bool>>
        {
            ["always allow"] = (_, _) => true,

            ["always deny"] = (_, _) => false,

            ["deny wins regardless of order"] = (set, caller) =>
            {
                var held = set.Entries.Where(e => caller.Contains(e.Principal)).ToList();
                return held.Any(e => e.Effect == AclEffect.Allow) && held.All(e => e.Effect != AclEffect.Deny);
            },

            ["allow wins regardless of order"] = (set, caller) =>
                set.Entries.Any(e => e.Effect == AclEffect.Allow && caller.Contains(e.Principal)),

            // Drops the deny entries and then evaluates correctly. For a single
            // read right this decides exactly like "allow wins"; it is kept
            // separate because it is the bug a connector that only copied allow
            // entries would introduce.
            ["ignore deny entries"] = (set, caller) =>
                AclEvaluator.CanRead(AclSet.Create(set.Entries.Where(e => e.Effect != AclEffect.Deny)), caller),

            // Beyond the ones the plan names.
            ["last match wins"] = (set, caller) =>
                set.Entries.LastOrDefault(e => caller.Contains(e.Principal)) is { Effect: AclEffect.Allow },

            ["no match allows"] = (set, caller) =>
                set.Entries.FirstOrDefault(e => caller.Contains(e.Principal)) is not { Effect: AclEffect.Deny },

            ["a caller with no principals skips the check"] = (set, caller) =>
                caller.Count == 0 || AclEvaluator.CanRead(set, caller),

            ["compare values and ignore the kind"] = (set, caller) =>
            {
                var values = caller.Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
                return set.Entries.FirstOrDefault(e => values.Contains(e.Principal.Value)) is { Effect: AclEffect.Allow };
            },

            ["compare values ignoring case"] = (set, caller) =>
                set.Entries.FirstOrDefault(e => caller.Any(p =>
                    p.Kind == e.Principal.Kind && string.Equals(p.Value, e.Principal.Value, StringComparison.OrdinalIgnoreCase)))
                    is { Effect: AclEffect.Allow },
        };

    public static TheoryData<string> MutantNames => new(Mutants.Keys);

    [Fact]
    public void The_real_evaluator_passes_every_case()
    {
        var failures = AclConformanceCorpus.Failures(AclEvaluator.CanRead);
        output.WriteLine($"{AclConformanceCorpus.Cases.Count} cases, {failures.Count} failures");
        Assert.Empty(failures);
    }

    [Theory]
    [MemberData(nameof(MutantNames))]
    public void Every_mutant_fails_at_least_one_case(string mutant)
    {
        var failures = AclConformanceCorpus.Failures(Mutants[mutant]);
        output.WriteLine($"MUTANT {mutant}: fails {failures.Count} of {AclConformanceCorpus.Cases.Count}");
        foreach (var name in failures) output.WriteLine($"  - {name}");
        Assert.NotEmpty(failures);
    }

    [Fact]
    public void The_malformed_input_cases_catch_a_parser_that_skips_bad_lines()
    {
        // A lenient parser keeps what it can read. The corpus must notice,
        // because the line it skipped might have been a deny.
        static AclSet LenientEntries(string[] lines) => AclSet.Create(
            lines.Select(l => AclEntry.TryParse(l, out var e) ? e : null).OfType<AclEntry>());
        static PrincipalSet LenientCaller(string[] texts) => PrincipalSet.From(
            texts.Select(t => Principal.TryParse(t, out var p) ? p : null).OfType<Principal>());

        var failures = AclConformanceCorpus.Failures(AclEvaluator.CanRead, LenientEntries, LenientCaller);

        Assert.Equal(
            ["unknown kind in the access list rejects the whole list", "unknown kind in the caller rejects the whole caller"],
            failures);
    }

    [Fact]
    public void Case_names_are_unique()
    {
        var names = AclConformanceCorpus.Cases.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Both_outcomes_are_represented()
    {
        // A corpus of only denials passes against an evaluator that denies
        // everything, which is the failure mode that looks like security.
        Assert.Contains(AclConformanceCorpus.Cases, c => c.MayRead);
        Assert.Contains(AclConformanceCorpus.Cases, c => !c.MayRead);
    }
}
