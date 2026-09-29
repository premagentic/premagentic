using Premagentic.Core;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Okf;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;

namespace Premagentic.Tests;

/// <summary>
/// Reads the invented bundle under Fixtures/okf-bundle, a greenhouse's
/// operating notes with one file per case the reader has to handle.
/// </summary>
public class OkfBundleTests
{
    private static readonly string BundleRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "okf-bundle");

    // When the fixture clock reads, fertilizer prices are stale and the seed order is not.
    private static readonly FixedClock June = new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly string[] Concepts =
    [
        "glossary.md",
        "care/watering.md",
        "care/pest-scouting.md",
        "care/humidity.md",
        "care/propagation.md",
        "procedures/closing.md",
        "procedures/old-closing.md",
        "supplies/fertilizer-prices.md",
        "supplies/seed-order.md",
        "supplies/delivery-window.md",
        "misc/visitor-notes.md",
        "misc/scratch.md",
        "misc/broken.md",
        "misc/untyped.md",
        "misc/garbled-trust.md",
        "computations/weekly-water-use.md",
    ];

    private static readonly string[] Reserved = ["index.md", "log.md", "care/index.md", "care/log.md"];

    private static async Task<Dictionary<string, SourceDocument>> ReadAll(FileSystemSource source)
    {
        var byPath = new Dictionary<string, SourceDocument>();
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn))
        {
            Assert.Null(read.Failure);
            byPath.Add(read.Document!.Path, read.Document!);
        }
        return byPath;
    }

    private static FileSystemSource Bundle(string root, string prefix = "") =>
        new(root, DocumentAccess.Everyone, prefix) { OkfBundle = true };

    [Fact]
    public async Task Bundle_mode_yields_every_concept_and_no_reserved_file()
    {
        var docs = await ReadAll(Bundle(BundleRoot));

        Assert.Equal(Concepts.Order(), docs.Keys.Order());
        Assert.All(docs.Values, d => Assert.Equal(OkfMetadata.ConceptIdFor(d.Path), d.Okf!.ConceptId));
    }

    [Fact]
    public async Task Concept_ids_are_relative_to_the_bundle_not_the_index()
    {
        var docs = await ReadAll(Bundle(BundleRoot, "greenhouse"));

        Assert.Equal("care/watering", docs["greenhouse/care/watering.md"].Okf!.ConceptId);
        Assert.Equal("glossary", docs["greenhouse/glossary.md"].Okf!.ConceptId);
    }

    [Theory]
    [InlineData("glossary.md", OkfTrustTier.Unverified, OkfAuthorship.Unknown)]
    [InlineData("care/watering.md", OkfTrustTier.HumanReviewed, OkfAuthorship.Human)]
    [InlineData("care/pest-scouting.md", OkfTrustTier.Unverified, OkfAuthorship.Machine)]
    [InlineData("care/humidity.md", OkfTrustTier.MachineConfirmed, OkfAuthorship.Machine)]
    [InlineData("care/propagation.md", OkfTrustTier.HumanReviewed, OkfAuthorship.Machine)]
    [InlineData("procedures/closing.md", OkfTrustTier.HumanReviewed, OkfAuthorship.Machine)]
    [InlineData("misc/scratch.md", OkfTrustTier.Unverified, OkfAuthorship.Unknown)]
    // Unreadable frontmatter in a bundle fails closed: machine-written, unverified.
    [InlineData("misc/broken.md", OkfTrustTier.Unverified, OkfAuthorship.Machine)]
    [InlineData("misc/garbled-trust.md", OkfTrustTier.Unverified, OkfAuthorship.Machine)]
    [InlineData("computations/weekly-water-use.md", OkfTrustTier.HumanReviewed, OkfAuthorship.Machine)]
    public async Task Each_concept_derives_its_trust_tier_and_authorship(string path, OkfTrustTier tier, OkfAuthorship authorship)
    {
        var okf = (await ReadAll(Bundle(BundleRoot)))[path].Okf!;

        Assert.Equal(tier, okf.TrustTier);
        Assert.Equal(authorship, okf.Authorship);
    }

    [Fact]
    public async Task Deprecated_is_superseded_and_stable_is_current()
    {
        var docs = await ReadAll(Bundle(BundleRoot));

        Assert.Equal(DocumentLifecycle.Superseded, docs["procedures/old-closing.md"].LifecycleStatus);
        Assert.Equal(DocumentLifecycle.Active, docs["care/watering.md"].LifecycleStatus);
        Assert.Equal(DocumentLifecycle.Active, docs["glossary.md"].LifecycleStatus);
    }

    [Fact]
    public async Task Staleness_is_evaluated_against_the_clock_passed_in()
    {
        var docs = await ReadAll(Bundle(BundleRoot));

        Assert.True(docs["supplies/fertilizer-prices.md"].Okf!.IsStale(June));
        Assert.False(docs["supplies/seed-order.md"].Okf!.IsStale(June));
        Assert.False(docs["care/watering.md"].Okf!.IsStale(June));

        // Written with no offset, read as UTC.
        var window = docs["supplies/delivery-window.md"].Okf!;
        Assert.False(window.IsStale(new FixedClock(new DateTimeOffset(2026, 7, 1, 11, 59, 59, TimeSpan.Zero))));
        Assert.True(window.IsStale(new FixedClock(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public async Task Unknown_types_unknown_keys_and_broken_links_are_accepted()
    {
        var bundle = Bundle(BundleRoot);
        var visitor = (await ReadAll(bundle))["misc/visitor-notes.md"];

        Assert.Equal("Greenhouse Visitor Note", visitor.Okf!.Type);
        Assert.Equal("Greenhouse Visitor Note", visitor.DocClass);
        Assert.Contains("/site/loading-dock.md", visitor.Text);
        Assert.Empty(visitor.Okf.MalformedFields);
        Assert.DoesNotContain(bundle.BundleReport!.Issues, i => i.Path == "misc/visitor-notes.md");
    }

    [Fact]
    public async Task Nonconforming_files_are_reported_and_still_ingested()
    {
        var bundle = Bundle(BundleRoot, "greenhouse");
        var docs = await ReadAll(bundle);

        var report = bundle.BundleReport!;
        Assert.Equal(4, report.Issues.Count);
        Assert.Contains(new OkfConformanceIssue("greenhouse/misc/scratch.md", OkfConformanceProblem.NoFrontmatter), report.Issues);
        Assert.Contains(new OkfConformanceIssue("greenhouse/misc/broken.md", OkfConformanceProblem.UnparseableFrontmatter), report.Issues);
        Assert.Contains(new OkfConformanceIssue("greenhouse/misc/untyped.md", OkfConformanceProblem.MissingType), report.Issues);

        var garbled = Assert.Single(report.Issues, i => i.Problem == OkfConformanceProblem.MalformedField);
        Assert.Equal("greenhouse/misc/garbled-trust.md", garbled.Path);
        Assert.Equal(["generated", "stale_after", "verified"], garbled.Detail!.Split(", ").Order());

        foreach (var issue in report.Issues) Assert.True(docs.ContainsKey(issue.Path));

        // Unreadable frontmatter leaves the whole file as the body, as it always has.
        var broken = docs["greenhouse/misc/broken.md"];
        Assert.StartsWith("---", broken.Text);
        Assert.Equal(FrontmatterState.Unparseable, broken.Okf!.FrontmatterState);
    }

    [Fact]
    public async Task The_root_index_declares_the_version()
    {
        var bundle = Bundle(BundleRoot);
        Assert.Null(bundle.BundleReport);

        await ReadAll(bundle);

        Assert.Equal("0.2", bundle.BundleReport!.OkfVersion);
        Assert.True(bundle.BundleReport.VersionKnown);
    }

    [Fact]
    public async Task An_attested_computation_is_read_as_text_and_nothing_is_run()
    {
        var doc = (await ReadAll(Bundle(BundleRoot)))["computations/weekly-water-use.md"];

        Assert.Equal("Attested Computation", doc.Okf!.Type);
        Assert.Contains("SELECT SUM(liters) AS water_used", doc.Text);

        // The contract fields are plain text in the frontmatter. The attester
        // they name is not in the bundle, and reading the bundle succeeded,
        // because nothing ever tries to follow or run it.
        var raw = Frontmatter.Parse(File.ReadAllText(Path.Combine(BundleRoot, "computations", "weekly-water-use.md")));
        var attester = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(raw.Values["attester"]);
        Assert.Equal("references/attesters/water-use-check.py", attester["resource"]);
        Assert.Equal("postgres", raw.Values["runtime"]);
        Assert.False(File.Exists(Path.Combine(BundleRoot, "references", "attesters", "water-use-check.py")));
    }

    [Fact]
    public async Task Bundle_mode_off_leaves_todays_behavior_unchanged()
    {
        var plain = new FileSystemSource(BundleRoot, DocumentAccess.Everyone);
        var off = await ReadAll(plain);
        var on = await ReadAll(Bundle(BundleRoot));

        // Every file is a document, reserved names included, and there is no report.
        Assert.Equal(Concepts.Concat(Reserved).Order(), off.Keys.Order());
        Assert.Null(plain.BundleReport);
        Assert.All(off.Values, d => Assert.Null(d.Okf?.ConceptId));
        Assert.Equal("Care", off["care/index.md"].Title);

        // Only Markdown with readable frontmatter carries metadata outside a bundle.
        Assert.Null(off["misc/scratch.md"].Okf);
        Assert.Null(off["misc/broken.md"].Okf);
        Assert.Null(off["care/log.md"].Okf);
        Assert.NotNull(off["care/watering.md"].Okf);

        // Bundle mode changes which files are documents and adds the concept id,
        // and nothing else about any document.
        foreach (var path in Concepts)
            Assert.Equal(on[path] with { Okf = null }, off[path] with { Okf = null });
    }

    [Fact]
    public async Task An_unknown_version_is_read_best_effort()
    {
        var root = Directory.CreateTempSubdirectory("premagentic-okf-").FullName;
        File.WriteAllText(Path.Combine(root, "index.md"), "---\nokf_version: \"9.1\"\n---\n# Everything\n");
        File.WriteAllText(Path.Combine(root, "note.md"), "---\ntype: Reference\n---\n# Note\n");

        var bundle = Bundle(root);
        var docs = await ReadAll(bundle);

        Assert.Equal(["note.md"], docs.Keys);
        Assert.Equal("9.1", bundle.BundleReport!.OkfVersion);
        Assert.False(bundle.BundleReport.VersionKnown);
        Assert.Empty(bundle.BundleReport.Issues);
    }

    [Fact]
    public async Task Only_the_root_index_can_declare_the_version()
    {
        var root = Directory.CreateTempSubdirectory("premagentic-okf-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "index.md"), "---\nokf_version: \"0.2\"\n---\n# Sub\n");
        File.WriteAllText(Path.Combine(root, "sub", "note.md"), "---\ntype: Reference\n---\n# Note\n");

        var bundle = Bundle(root);
        await ReadAll(bundle);

        Assert.Null(bundle.BundleReport!.OkfVersion);
        Assert.False(bundle.BundleReport.VersionKnown);
    }

    // What is stored for a concept that does not say who wrote it: machine-written
    // only when the source says so, and only in a bundle. A concept that does say
    // is read as it says, whatever the setting.
    [Theory]
    [InlineData(false, false, "glossary.md", OkfAuthorship.Unknown)]
    [InlineData(false, true, "glossary.md", OkfAuthorship.Unknown)]
    [InlineData(false, true, "misc/scratch.md", OkfAuthorship.Unknown)]
    [InlineData(true, false, "glossary.md", OkfAuthorship.Unknown)]
    [InlineData(true, false, "misc/scratch.md", OkfAuthorship.Unknown)]
    [InlineData(true, true, "glossary.md", OkfAuthorship.Machine)]
    [InlineData(true, true, "misc/scratch.md", OkfAuthorship.Machine)]
    [InlineData(true, true, "misc/untyped.md", OkfAuthorship.Machine)]
    [InlineData(true, true, "care/watering.md", OkfAuthorship.Human)]
    [InlineData(true, true, "care/pest-scouting.md", OkfAuthorship.Machine)]
    [InlineData(true, true, "misc/broken.md", OkfAuthorship.Machine)]
    public async Task Undeclared_authorship_is_machine_only_when_the_source_says_so_and_only_in_a_bundle(
        bool bundle, bool undeclaredIsMachine, string path, OkfAuthorship stored)
    {
        var source = new FileSystemSource(BundleRoot, DocumentAccess.Everyone)
        {
            OkfBundle = bundle,
            UndeclaredAuthorshipIsMachine = undeclaredIsMachine,
        };

        var okf = (await ReadAll(source))[path].Okf;

        Assert.Equal(stored, OkfColumns.For(okf).Authorship);
        // Held back is not the same as signed off: the tier is untouched.
        if (path == "glossary.md") Assert.Equal(OkfTrustTier.Unverified, OkfColumns.For(okf).TrustTier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_concepts_that_declare_no_author_are_the_same_with_the_setting_on_or_off(bool undeclaredIsMachine)
    {
        var bundle = new FileSystemSource(BundleRoot, DocumentAccess.Everyone)
        {
            OkfBundle = true,
            UndeclaredAuthorshipIsMachine = undeclaredIsMachine,
        };
        var plain = new FileSystemSource(BundleRoot, DocumentAccess.Everyone) { UndeclaredAuthorshipIsMachine = undeclaredIsMachine };

        Assert.Equal(
            SourcesTests.Undeclared.Order(StringComparer.Ordinal),
            (await ReadAll(bundle)).Values.Where(d => d.Okf is { DeclaresNoAuthor: true }).Select(d => d.Path).Order(StringComparer.Ordinal));
        Assert.DoesNotContain((await ReadAll(plain)).Values, d => d.Okf is { DeclaresNoAuthor: true });
    }
}
