using Premagentic.Core.Okf;
using Premagentic.Core.Sources;

namespace Premagentic.Tests;

/// <summary>A clock that always reads the same instant, so staleness can be tested on both sides of it.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public class OkfActorTests
{
    [Theory]
    [InlineData("human:avery", OkfActorKind.Human, "avery", null)]
    [InlineData("  human:avery  ", OkfActorKind.Human, "avery", null)]
    [InlineData("process:sensor-audit", OkfActorKind.Process, "sensor-audit", null)]
    [InlineData("scouting_agent/2.1", OkfActorKind.Agent, "scouting_agent", "2.1")]
    [InlineData("human:", OkfActorKind.Unknown, null, null)]
    [InlineData("Human:avery", OkfActorKind.Unknown, null, null)]
    [InlineData("team:growers", OkfActorKind.Unknown, null, null)]
    [InlineData("avery", OkfActorKind.Unknown, null, null)]
    [InlineData("a/b/c", OkfActorKind.Unknown, null, null)]
    [InlineData("scouting agent/2.1", OkfActorKind.Unknown, null, null)]
    [InlineData("/2.1", OkfActorKind.Unknown, null, null)]
    [InlineData("", OkfActorKind.Unknown, null, null)]
    public void Actors_parse_by_the_spec_convention(string raw, OkfActorKind kind, string? name, string? version)
    {
        var actor = OkfActor.Parse(raw);

        Assert.Equal(kind, actor.Kind);
        Assert.Equal(name, actor.Name);
        Assert.Equal(version, actor.Version);
        Assert.Equal(raw.Trim(), actor.Raw);
    }
}

public class OkfMetadataTests
{
    private static OkfMetadata Meta(string yaml) =>
        OkfMetadata.FromFrontmatter(Frontmatter.Parse($"---\ntype: Reference\n{yaml}\n---\n# Body\n"));

    [Theory]
    [InlineData("", OkfTrustTier.Unverified)]
    [InlineData("verified: []", OkfTrustTier.Unverified)]
    [InlineData("verified:", OkfTrustTier.Unverified)]
    [InlineData("verified:\n  - { by: process:sensor-audit, at: 2026-05-12T02:00:00Z }", OkfTrustTier.MachineConfirmed)]
    [InlineData("verified:\n  - { by: checking_agent/3.0, at: 2026-05-12T02:00:00Z }", OkfTrustTier.MachineConfirmed)]
    [InlineData("verified:\n  - { by: process:sensor-audit, at: 2026-05-12T02:00:00Z }\n  - { by: human:avery, at: 2026-05-13T09:00:00Z }", OkfTrustTier.HumanReviewed)]
    [InlineData("verified: { by: human:jordan, at: 2026-05-19T09:00:00Z }", OkfTrustTier.HumanReviewed)]
    [InlineData("verified: { by: process:sensor-audit, at: 2026-05-19T09:00:00Z }", OkfTrustTier.MachineConfirmed)]
    // Not a person by the convention, so it cannot make a concept human-reviewed.
    [InlineData("verified: { by: Human:jordan, at: 2026-05-19T09:00:00Z }", OkfTrustTier.MachineConfirmed)]
    [InlineData("verified: { by: \"human:\", at: 2026-05-19T09:00:00Z }", OkfTrustTier.MachineConfirmed)]
    // Shapes that name no actor count as no verification at all.
    [InlineData("verified: yes", OkfTrustTier.Unverified)]
    [InlineData("verified: { at: 2026-05-19T09:00:00Z }", OkfTrustTier.Unverified)]
    [InlineData("verified: [human:jordan]", OkfTrustTier.Unverified)]
    public void Trust_tier_is_derived_from_verified_as_section_5_3_gives_it(string yaml, OkfTrustTier expected)
    {
        Assert.Equal(expected, Meta(yaml).TrustTier);
    }

    [Theory]
    [InlineData("", OkfAuthorship.Unknown)]
    [InlineData("generated:", OkfAuthorship.Unknown)]
    [InlineData("generated: { by: human:avery, at: 2026-05-02T08:00:00Z }", OkfAuthorship.Human)]
    [InlineData("generated: { by: scouting_agent/2.1, at: 2026-05-10T06:15:00Z }", OkfAuthorship.Machine)]
    [InlineData("generated: { by: process:nightly-export, at: 2026-05-10T06:15:00Z }", OkfAuthorship.Machine)]
    // Present but not naming a person in a readable form: kept inside the trust setting's reach.
    [InlineData("generated: { by: Human:avery }", OkfAuthorship.Machine)]
    [InlineData("generated: { at: 2026-05-10T06:15:00Z }", OkfAuthorship.Machine)]
    [InlineData("generated: written by an agent", OkfAuthorship.Machine)]
    public void Authorship_is_derived_from_generated(string yaml, OkfAuthorship expected)
    {
        Assert.Equal(expected, Meta(yaml).Authorship);
    }

    [Fact]
    public void A_bare_verified_mapping_is_a_one_element_list()
    {
        var meta = Meta("verified: { by: human:jordan, at: 2026-05-19T09:00:00Z }");

        var only = Assert.Single(meta.Verified);
        Assert.Equal(OkfActorKind.Human, only.By.Kind);
        Assert.Equal("jordan", only.By.Name);
        Assert.Equal(new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero), only.At);
        Assert.Empty(meta.MalformedFields);
    }

    [Fact]
    public void Staleness_turns_on_at_the_instant_and_not_before()
    {
        var meta = Meta("stale_after: 2026-09-23T00:00:00Z");
        var instant = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

        Assert.False(meta.IsStale(new FixedClock(instant.AddTicks(-1))));
        Assert.True(meta.IsStale(new FixedClock(instant)));
        Assert.True(meta.IsStale(new FixedClock(instant.AddDays(30))));
    }

    [Fact]
    public void Without_stale_after_a_concept_is_never_stale()
    {
        Assert.False(Meta("").IsStaleAt(DateTimeOffset.MaxValue));
        Assert.False(Meta("stale_after:").IsStaleAt(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void A_timestamp_with_no_offset_is_read_as_utc()
    {
        var meta = Meta("stale_after: 2026-07-01T12:00:00");

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), meta.StaleAfter);
        Assert.False(meta.IsStaleAt(new DateTimeOffset(2026, 7, 1, 11, 59, 59, TimeSpan.Zero)));
        Assert.True(meta.IsStaleAt(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.FromHours(-4))));
        Assert.Empty(meta.MalformedFields);
    }

    [Fact]
    public void A_timestamp_with_an_offset_compares_as_the_same_instant()
    {
        var meta = Meta("stale_after: 2026-07-01T12:00:00+02:00");

        Assert.False(meta.IsStaleAt(new DateTimeOffset(2026, 7, 1, 9, 59, 59, TimeSpan.Zero)));
        Assert.True(meta.IsStaleAt(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void An_unreadable_stale_after_counts_as_stale()
    {
        var meta = Meta("stale_after: next spring");

        Assert.True(meta.StaleAfterUnreadable);
        Assert.True(meta.IsStaleAt(DateTimeOffset.MinValue));
        Assert.Contains("stale_after", meta.MalformedFields);
    }

    [Fact]
    public void Sources_keep_their_credibility_signals()
    {
        var meta = Meta("""
            sources:
              - id: seed-guide
                resource: https://example.com/guides/seed-starting
                title: Seed starting guide
                author: human:morgan
                usage_count: 120
                last_modified: 2026-04-30T00:00:00Z
              - resource: all watering logs from last season
            """);

        Assert.Equal(2, meta.Sources.Count);
        var guide = meta.Sources[0];
        Assert.Equal("seed-guide", guide.Id);
        Assert.Equal("https://example.com/guides/seed-starting", guide.Resource);
        Assert.Equal(OkfActorKind.Human, guide.Author!.Kind);
        Assert.Equal(120, guide.UsageCount);
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero), guide.LastModified);
        Assert.Equal("all watering logs from last season", meta.Sources[1].Resource);
        Assert.Empty(meta.MalformedFields);
    }

    [Fact]
    public void The_latest_verification_is_how_recently_it_was_verified()
    {
        var meta = Meta("""
            verified:
              - { by: human:avery, at: 2026-05-16T10:00:00Z }
              - { by: process:sensor-audit, at: 2026-05-18T02:00:00Z }
              - { by: process:sensor-audit }
            """);

        Assert.Equal(new DateTimeOffset(2026, 5, 18, 2, 0, 0, TimeSpan.Zero), meta.LastVerifiedAt);
        Assert.Null(Meta("").LastVerifiedAt);
    }

    [Fact]
    public void Descriptive_fields_are_read_and_unknown_keys_are_ignored()
    {
        var meta = OkfMetadata.FromFrontmatter(Frontmatter.Parse("""
            ---
            type: Greenhouse Visitor Note
            title: Visitor notes
            description: Where visitors park and who escorts them.
            resource: https://example.com/visitors
            tags: [visitors, parking]
            status: draft
            mood: sunny
            extras: { badge_color: green }
            ---
            # Visitor notes
            """));

        Assert.Equal("Greenhouse Visitor Note", meta.Type);
        Assert.Equal("Visitor notes", meta.Title);
        Assert.Equal("Where visitors park and who escorts them.", meta.Description);
        Assert.Equal("https://example.com/visitors", meta.Resource);
        Assert.Equal(["visitors", "parking"], meta.Tags);
        Assert.Equal("draft", meta.Status);
        Assert.Equal(FrontmatterState.Parsed, meta.FrontmatterState);
        Assert.Empty(meta.MalformedFields);
    }

    [Fact]
    public void Odd_shapes_are_read_as_far_as_they_go_and_never_throw()
    {
        var meta = OkfMetadata.FromFrontmatter(Frontmatter.Parse("""
            ---
            type: [not, a, scalar]
            title: { nested: title }
            tags: { a: b }
            sources: just a sentence
            verified: [ { by: human:avery }, plain text ]
            generated: [ one, two ]
            stale_after: { at: tomorrow }
            ---
            # Body
            """));

        Assert.Null(meta.Type);
        Assert.Null(meta.Title);
        Assert.Empty(meta.Tags);
        Assert.Empty(meta.Sources);
        Assert.True(Assert.Single(meta.Verified).By.IsHuman);
        Assert.Equal(OkfAuthorship.Machine, meta.Authorship);
        Assert.True(meta.IsStaleAt(DateTimeOffset.MinValue));
        Assert.Equal(["generated", "sources", "stale_after", "tags", "verified"], meta.MalformedFields.Order());
    }

    [Fact]
    public void Metadata_from_a_document_with_no_frontmatter_is_empty_and_derives_nothing()
    {
        var meta = OkfMetadata.FromFrontmatter(Frontmatter.Parse("# Plain\n"));

        Assert.Equal(FrontmatterState.Absent, meta.FrontmatterState);
        Assert.Null(meta.Type);
        Assert.Equal(OkfTrustTier.Unverified, meta.TrustTier);
        Assert.Equal(OkfAuthorship.Unknown, meta.Authorship);
        Assert.False(meta.IsStaleAt(DateTimeOffset.MaxValue));
    }

    [Theory]
    [InlineData("care/watering.md", "care/watering")]
    [InlineData("glossary.md", "glossary")]
    [InlineData("/care/Watering.MD", "care/Watering")]
    [InlineData("care\\humidity.md", "care/humidity")]
    public void Concept_id_is_the_path_in_the_bundle_without_md(string path, string expected)
    {
        Assert.Equal(expected, OkfMetadata.ConceptIdFor(path));
    }

    // The rule belongs to the metadata itself, so a connector that sets the flag
    // on a document outside a bundle still cannot hold it back.
    [Theory]
    [InlineData(false, false, OkfAuthorship.Unknown)]
    [InlineData(false, true, OkfAuthorship.Unknown)]
    [InlineData(true, false, OkfAuthorship.Unknown)]
    [InlineData(true, true, OkfAuthorship.Machine)]
    public void Undeclared_authorship_is_machine_only_for_a_bundle_concept_whose_source_says_so(
        bool bundleConcept, bool undeclaredIsMachine, OkfAuthorship expected)
    {
        var silent = new OkfMetadata
        {
            FrontmatterState = FrontmatterState.Parsed,
            BundleConcept = bundleConcept,
            UndeclaredIsMachine = undeclaredIsMachine,
        };
        var signed = silent with { Generated = new OkfGeneration(OkfActor.Parse("human:avery"), null) };

        Assert.Equal(expected, silent.Authorship);
        Assert.Equal(bundleConcept, silent.DeclaresNoAuthor);
        Assert.Equal(OkfAuthorship.Human, signed.Authorship);
        Assert.False(signed.DeclaresNoAuthor);
    }
}
