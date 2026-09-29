using System.Text.Json;
using Premagentic.Core.Extensions;

namespace Premagentic.Tests;

/// <summary>
/// The two settings that decide what may load, read the way the retrieval keys
/// are read: a stored value that cannot be used is reported and the safe
/// default applies. For the allow list the safe default is an empty one, so a
/// typo loads no extension rather than the wrong one. Pure; no database.
/// </summary>
public sealed class ExtensionSettingsTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);

    [Fact]
    public void An_allow_list_gives_back_the_pairs_it_holds_with_the_hash_in_lower_case()
    {
        var pairs = ExtensionSettings.AllowedFrom(Json($$"""
            [{"name": "sentence-chunker", "sha256": "{{Hash.ToUpperInvariant()}}"},
             {"name": "sentence-chunker", "sha256": "{{OtherHash}}"},
             {"sha256": "{{Hash}}", "name": "excel-reader"}]
            """), out var problem);

        Assert.Null(problem);
        Assert.Equal(
            [("sentence-chunker", Hash), ("sentence-chunker", OtherHash), ("excel-reader", Hash)],
            pairs);
    }

    [Fact]
    public void An_allow_list_that_cannot_be_used_allows_nothing_and_says_why()
    {
        string[] unusable =
        [
            "{}",
            "\"a list\"",
            "3",
            "null",
            "[3]",
            """["sentence-chunker"]""",
            $$"""[{"name": "sentence-chunker", "sha256": "{{Hash}}", "notes": "mine"}]""",
            $$"""[{"sha256": "{{Hash}}"}]""",
            """[{"name": "sentence-chunker"}]""",
            $$"""[{"name": "two words", "sha256": "{{Hash}}"}]""",
            $$"""[{"name": "", "sha256": "{{Hash}}"}]""",
            $$"""[{"name": 7, "sha256": "{{Hash}}"}]""",
            """[{"name": "sentence-chunker", "sha256": "abc"}]""",
            $$"""[{"name": "sentence-chunker", "sha256": "{{new string('z', 64)}}"}]""",
            $$"""[{"name": "sentence-chunker", "sha256": {{'"'}}{{Hash}}{{'"'}}}, {"name": "SENTENCE-CHUNKER", "sha256": "{{Hash}}"}]""",
        ];

        foreach (var text in unusable)
        {
            var value = Json(text);
            Assert.Empty(ExtensionSettings.AllowedFrom(value, out var problem));
            Assert.NotNull(problem);
            Assert.Contains(ExtensionSettings.Allowed, problem);
            Assert.False(ExtensionSettings.TryParse(ExtensionSettings.Allowed, value, out var same));
            Assert.Equal(problem, same);
        }

        Assert.Empty(ExtensionSettings.AllowedFrom(null, out var absent));
        Assert.Null(absent);
        Assert.True(ExtensionSettings.TryParse(ExtensionSettings.Allowed, Json("[]"), out var none));
        Assert.Null(none);
    }

    [Fact]
    public void An_allow_list_is_written_in_the_shape_it_is_read_in()
    {
        var stored = ExtensionSettings.ToStored([("sentence-chunker", Hash.ToUpperInvariant()), ("excel-reader", OtherHash)]);

        Assert.True(ExtensionSettings.TryParse(ExtensionSettings.Allowed, stored, out var problem));
        Assert.Null(problem);
        Assert.Equal(
            [("sentence-chunker", Hash), ("excel-reader", OtherHash)],
            ExtensionSettings.AllowedFrom(stored, out _));
    }

    [Fact]
    public void The_setting_says_where_extensions_are_and_the_environment_says_it_when_the_setting_does_not()
    {
        var set = Path.Combine(Path.GetTempPath(), "prem-settings-folder");
        var fromEnvironment = Path.Combine(Path.GetTempPath(), "prem-environment-folder");

        Assert.Equal(set, ExtensionSettings.FolderFrom(Json(JsonSerializer.Serialize(set)), fromEnvironment, out var problem));
        Assert.Null(problem);

        Assert.Equal(fromEnvironment, ExtensionSettings.FolderFrom(null, fromEnvironment, out problem));
        Assert.Null(problem);

        Assert.Null(ExtensionSettings.FolderFrom(null, null, out problem));
        Assert.Null(problem);
        Assert.Null(ExtensionSettings.FolderFrom(null, "   ", out problem));
        Assert.Null(problem);
    }

    [Fact]
    public void A_folder_that_cannot_be_used_falls_back_to_the_environment_and_says_why()
    {
        var fromEnvironment = Path.Combine(Path.GetTempPath(), "prem-environment-folder");

        foreach (var text in new[] { "\"\"", "\"   \"", "7", "null", "[]", "{}", "\"extensions\"", """ "./extensions" """ })
        {
            var value = Json(text);
            Assert.Equal(fromEnvironment, ExtensionSettings.FolderFrom(value, fromEnvironment, out var problem));
            Assert.NotNull(problem);
            Assert.Contains(ExtensionSettings.Folder, problem);
            Assert.False(ExtensionSettings.TryParse(ExtensionSettings.Folder, value, out var same));
            Assert.Equal(problem, same);

            // With nothing in the environment either, an unusable setting means
            // no extensions, which is what no setting at all means.
            Assert.Null(ExtensionSettings.FolderFrom(value, null, out _));
        }
    }

    [Fact]
    public void A_key_that_is_not_an_extension_setting_is_refused_by_name()
    {
        Assert.False(ExtensionSettings.TryParse("extensions.everything", Json("[]"), out var problem));
        Assert.Contains(ExtensionSettings.Allowed, problem);
        Assert.Contains(ExtensionSettings.Folder, problem);
        Assert.Throws<ArgumentException>(() => ExtensionSettings.Accepts("retrieval.rrf_k"));
        Assert.Equal([ExtensionSettings.Allowed, ExtensionSettings.Folder], ExtensionSettings.Keys);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
