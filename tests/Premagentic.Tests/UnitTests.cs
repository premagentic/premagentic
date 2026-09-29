using Premagentic.Core;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;

namespace Premagentic.Tests;

public class DocumentAccessTests
{
    [Fact]
    public void NoOne_denies_everyone()
    {
        Assert.True(DocumentAccess.NoOne.DeniesEveryone);
    }

    [Fact]
    public void Everyone_does_not_deny()
    {
        Assert.False(DocumentAccess.Everyone.DeniesEveryone);
    }

    [Fact]
    public void For_with_only_blank_principals_denies_everyone()
    {
        // A connector that resolved an empty or whitespace group list has not
        // granted access to anybody, and must not be mistaken for public.
        Assert.True(DocumentAccess.For("", "   ").DeniesEveryone);
    }

    [Fact]
    public void For_deduplicates_and_drops_blanks()
    {
        var access = DocumentAccess.For("group:hr", "group:hr", "", "user:1");
        Assert.Equal(["group:hr", "user:1"], access.AllowedPrincipals);
        Assert.False(access.Public);
    }
}

public class AccessScopeTests
{
    [Fact]
    public void PublicOnly_is_not_unrestricted()
    {
        Assert.False(AccessScope.PublicOnly.Unrestricted);
        Assert.Empty(AccessScope.PublicOnly.Principals);
    }

    [Fact]
    public void Unrestricted_carries_its_reason_into_the_audit_label()
    {
        Assert.Equal("unrestricted:nightly-report", AccessScope.UnrestrictedAudited("nightly-report").AuditLabel);
    }

    [Fact]
    public void ForPrincipals_is_never_unrestricted()
    {
        Assert.False(AccessScope.ForPrincipals("t", "group:everything").Unrestricted);
    }
}

public class LifecycleTests
{
    [Theory]
    [InlineData("draft", DocumentLifecycle.Draft)]
    [InlineData("WIP", DocumentLifecycle.Draft)]
    [InlineData("superseded", DocumentLifecycle.Superseded)]
    [InlineData("deprecated", DocumentLifecycle.Superseded)]
    [InlineData(" Deprecated ", DocumentLifecycle.Superseded)]
    [InlineData("Retired", DocumentLifecycle.Archived)]
    [InlineData("obsolete", DocumentLifecycle.Expired)]
    [InlineData("active", DocumentLifecycle.Active)]
    [InlineData("stable", DocumentLifecycle.Active)]
    [InlineData(null, DocumentLifecycle.Active)]
    [InlineData("anything else", DocumentLifecycle.Active)]
    public void Source_status_maps_onto_a_lifecycle_bucket(string? status, string expected)
    {
        Assert.Equal(expected, DocumentLifecycle.FromSourceStatus(status));
    }
}

public class AuthorityWeightsTests
{
    [Fact]
    public void Flat_weights_every_class_equally()
    {
        Assert.Equal(1.0, AuthorityWeights.Flat.For("runbook"));
        Assert.Equal(1.0, AuthorityWeights.Flat.For(null));
        Assert.Equal(1.0, AuthorityWeights.Flat.For("anything"));
    }

    [Fact]
    public void A_configured_table_applies_and_falls_back()
    {
        var weights = new AuthorityWeights(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["policy"] = 1.0, ["chat-log"] = 0.5 },
            defaultWeight: 0.85);

        Assert.Equal(1.0, weights.For("Policy"));
        Assert.Equal(0.5, weights.For("chat-log"));
        Assert.Equal(0.85, weights.For("meeting-notes"));
        Assert.Equal(0.85, weights.For(null));
    }
}

public class MarkdownChunkerTests
{
    [Fact]
    public void Chunks_are_scoped_by_heading_path()
    {
        var chunks = MarkdownChunker.Chunk("""
            # Handbook

            ## Travel

            ### Per diem
            Forty dollars a day.
            """);

        Assert.Contains(chunks, c => c.HeadingPath == "Handbook > Travel > Per diem"
                                  && c.Content.Contains("Forty dollars"));
    }

    [Fact]
    public void A_hash_inside_a_code_fence_is_not_a_heading()
    {
        var chunks = MarkdownChunker.Chunk("""
            ## Setup

            ```bash
            # install the thing
            apt install thing
            ```
            """);

        Assert.All(chunks, c => Assert.Equal("Setup", c.HeadingPath));
    }

    [Fact]
    public void Empty_input_yields_no_chunks()
    {
        Assert.Empty(MarkdownChunker.Chunk("   \n\n  "));
    }
}

public class FileSystemSourceTests
{
    [Fact]
    public async Task Reads_frontmatter_status_and_applies_the_prefix()
    {
        var root = Directory.CreateTempSubdirectory("premagentic-fs-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "policies"));
        File.WriteAllText(Path.Combine(root, "policies", "old.md"), """
            ---
            title: Old policy
            status: superseded
            type: policy
            ---
            # Old policy

            ## Body
            Superseded content.
            """);

        var source = new FileSystemSource(root, DocumentAccess.Everyone, "hr");
        var docs = new List<SourceDocument>();
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn))
        {
            Assert.Null(read.Failure);
            docs.Add(read.Document!);
        }

        var only = Assert.Single(docs);
        Assert.Equal("hr/policies/old.md", only.Path);
        Assert.Equal("Old policy", only.Title);
        Assert.Equal(DocumentLifecycle.Superseded, only.LifecycleStatus);
        Assert.Equal("policy", only.DocClass);
        Assert.True(only.Access.Public);
    }

    [Fact]
    public async Task A_per_path_resolver_can_vary_access_within_one_tree()
    {
        var root = Directory.CreateTempSubdirectory("premagentic-fs2-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "restricted"));
        File.WriteAllText(Path.Combine(root, "open.md"), "# Open\n\n## Body\nOpen.\n");
        File.WriteAllText(Path.Combine(root, "restricted", "secret.md"), "# Secret\n\n## Body\nSecret.\n");

        var source = new FileSystemSource(root, rel =>
            rel.StartsWith("restricted/") ? DocumentAccess.For("group:hr") : DocumentAccess.Everyone);

        var byPath = new Dictionary<string, SourceDocument>();
        await foreach (var read in source.ReadThroughAsync(ReaderRegistry.BuiltIn)) byPath[read.Document!.Path] = read.Document!;

        Assert.True(byPath["open.md"].Access.Public);
        Assert.False(byPath["restricted/secret.md"].Access.Public);
        Assert.Equal(["group:hr"], byPath["restricted/secret.md"].Access.AllowedPrincipals);
    }
}
