using Premagentic.Core.Sources;

namespace Premagentic.Tests;

public class OkfFrontmatterTests
{
    [Fact]
    public void Nested_values_are_reachable_and_flat_fields_are_unchanged()
    {
        var result = Frontmatter.Parse("""
            ---
            type: Procedure
            title: Bench watering
            tags: [watering, daily]
            generated: { by: human:avery, at: 2026-05-02T08:00:00Z }
            verified:
              - { by: human:jordan, at: 2026-05-03T16:30:00Z }
            ---
            # Bench watering
            """);

        Assert.Equal(FrontmatterState.Parsed, result.State);

        // Fields keeps exactly what it held before: scalar top-level fields only.
        Assert.Equal(["title", "type"], result.Fields.Keys.Order());
        Assert.Equal("Procedure", result.Fields["TYPE"]);
        Assert.Equal("# Bench watering", result.Body);

        var generated = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(result.Values["generated"]);
        Assert.Equal("human:avery", generated["by"]);
        var verified = Assert.IsAssignableFrom<IReadOnlyList<object>>(result.Values["verified"]);
        var first = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(Assert.Single(verified));
        Assert.Equal("2026-05-03T16:30:00Z", first["at"]);
        Assert.Equal(new object[] { "watering", "daily" }, Assert.IsAssignableFrom<IReadOnlyList<object>>(result.Values["tags"]));
        Assert.Equal("Bench watering", result.Values["title"]);
    }

    [Fact]
    public void A_document_without_a_block_is_absent()
    {
        var result = Frontmatter.Parse("# Just a heading\n");

        Assert.Equal(FrontmatterState.Absent, result.State);
        Assert.Empty(result.Fields);
        Assert.Empty(result.Values);
        Assert.Equal("# Just a heading\n", result.Body);
    }

    [Theory]
    [InlineData("---\ntitle: [unclosed\n---\n# Body\n")]
    [InlineData("---\ntitle:\n\t- tabbed\n---\n# Body\n")]
    [InlineData("---\ntitle: one\ntitle: two\n---\n# Body\n")]
    [InlineData("---\ntitle: *nowhere\n---\n# Body\n")]
    [InlineData("---\ntitle: never closed\n# Body\n")]
    [InlineData("---")]
    public void Bad_yaml_degrades_to_no_frontmatter(string markdown)
    {
        var result = Frontmatter.Parse(markdown);

        Assert.Equal(FrontmatterState.Unparseable, result.State);
        Assert.Empty(result.Fields);
        Assert.Empty(result.Values);
        Assert.Equal(markdown, result.Body);
    }

    // Each of these made the YAML loader overflow the stack or expand without
    // bound, and a stack overflow ends the process no matter what catches it.
    // If the guard regresses, this test does not fail: the test run dies.
    [Theory]
    [InlineData("? &k [*k]\n: 1\n")]
    [InlineData("a: &k [1, *k]\n")]
    [InlineData("a: &k { self: *k }\n")]
    [InlineData("a: &a [x, x, x, x, x, x, x, x, x, x]\nb: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a, *a]\nc: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b, *b]\nd: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c, *c]\ne: &e [*d, *d, *d, *d, *d, *d, *d, *d, *d, *d]\nf: [*e, *e]\n")]
    public void Structures_that_cannot_be_loaded_safely_are_refused(string yaml)
    {
        var markdown = $"---\ntype: Reference\n{yaml}---\n# Body\n";

        var result = Frontmatter.Parse(markdown);

        Assert.Equal(FrontmatterState.Unparseable, result.State);
        Assert.Equal(markdown, result.Body);
    }

    [Fact]
    public void Deep_nesting_is_refused_before_it_reaches_the_loader()
    {
        const int depth = 5_000;
        var markdown = $"---\na: {new string('[', depth)}{new string(']', depth)}\n---\n# Body\n";

        Assert.Equal(FrontmatterState.Unparseable, Frontmatter.Parse(markdown).State);
    }

    [Fact]
    public void Aliases_of_one_long_value_are_counted_by_the_text_they_expand_to()
    {
        // Twelve nodes, and 1.2 million bytes of text once every alias is
        // expanded: few enough nodes to pass the node limit, too much text for
        // the bytes limit, so the block reads as unparseable, as any block over
        // a limit does.
        var anchored = $"long: &long {new string('x', 100_000)}\n";
        var tooMany = $"---\n{anchored}copies: [{string.Join(", ", Enumerable.Repeat("*long", 11))}]\n---\n# Body\n";
        Assert.Equal(FrontmatterState.Unparseable, Frontmatter.Parse(tooMany).State);

        // The control: the same value aliased five times, 600,000 bytes, parses.
        var few = $"---\n{anchored}copies: [{string.Join(", ", Enumerable.Repeat("*long", 5))}]\n---\n# Body\n";
        var parsed = Frontmatter.Parse(few);
        Assert.Equal(FrontmatterState.Parsed, parsed.State);
        Assert.Equal(100_000, parsed.Fields["long"].Length);
    }

    [Fact]
    public void Nesting_and_aliases_an_honest_document_uses_still_parse()
    {
        var result = Frontmatter.Parse("""
            ---
            type: &kind Reference
            also: *kind
            a: { b: { c: { d: [1, [2, [3]]] } } }
            ---
            # Body
            """);

        Assert.Equal(FrontmatterState.Parsed, result.State);
        Assert.Equal("Reference", result.Fields["also"]);
        Assert.True(result.Values.ContainsKey("a"));
    }

    [Fact]
    public void A_mapping_with_a_non_scalar_key_keeps_its_other_entries()
    {
        var result = Frontmatter.Parse("---\n? [a, b]\n: 1\ntype: Reference\n---\n# Body\n");

        Assert.Equal(FrontmatterState.Parsed, result.State);
        Assert.Equal("Reference", result.Values["type"]);
        Assert.Single(result.Values);
    }
}
