using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Mcp;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// What a deployment calls its MCP tools. It is refused at the command when it
/// cannot be used, so it is never found broken at a start. Requires a running
/// Docker daemon.
/// </summary>
public sealed class McpToolTextTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private async Task<(PremagenticDatabase Db, Guid Tenant)> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // --- The tool text ---

    [Theory]
    [InlineData("""{"search_knowledge": "Search the Contoso handbook."}""")]
    [InlineData("""{"get_document_section": "Fetch a cited section."}""")]
    [InlineData("""{"search_knowledge": "a", "get_document_section": "b"}""")]
    [InlineData("{}")]
    public void Tool_text_a_deployment_may_set(string json) =>
        Assert.Null(McpToolDescriptions.Problem(Json(json)));

    [Theory]
    [InlineData("""{"search_everything": "no such tool"}""", "no tool")]
    [InlineData("""{"search_knowledge": ""}""", "not blank")]
    [InlineData("""{"search_knowledge": "   "}""", "not blank")]
    [InlineData("""{"search_knowledge": 5}""", "not a number")]
    [InlineData("\"a text\"", "where an object belongs")]
    [InlineData("[]", "not a list")]
    public void Tool_text_a_deployment_may_not_set(string json, string says)
    {
        var problem = McpToolDescriptions.Problem(Json(json));
        Assert.NotNull(problem);
        Assert.Contains(says, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_text_has_a_ceiling_so_a_tool_list_cannot_carry_a_wall_of_text()
    {
        var atTheLimit = new string('x', McpToolDescriptions.MaxLength);
        Assert.Null(McpToolDescriptions.Problem(Json($$"""{"search_knowledge": "{{atTheLimit}}"}""")));

        var overIt = new string('x', McpToolDescriptions.MaxLength + 1);
        Assert.Contains("at most", McpToolDescriptions.Problem(Json($$"""{"search_knowledge": "{{overIt}}"}"""))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_stored_and_something_unusable_both_keep_the_shipped_text()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var settings = new SettingsStore(db, tenant);

        var nothing = await McpToolDescriptions.ReadAsync(settings);
        Assert.Empty(nothing.ByTool);
        Assert.Null(nothing.Problem);
        Assert.Null(nothing.For(McpToolDescriptions.SearchKnowledge));

        // Written straight past the command's validation, as an older version or
        // a hand at the prompt could leave it.
        await settings.SetAsync(McpToolDescriptions.Key, Json("""{"search_knowledge": ""}"""));
        var unusable = await McpToolDescriptions.ReadAsync(settings);
        Assert.Empty(unusable.ByTool);
        Assert.NotNull(unusable.Problem);
        Assert.Null(unusable.For(McpToolDescriptions.SearchKnowledge));

        // The control: a value that can be used is used.
        await settings.SetAsync(McpToolDescriptions.Key, Json("""{"search_knowledge": "Search the Contoso handbook."}"""));
        var used = await McpToolDescriptions.ReadAsync(settings);
        Assert.Null(used.Problem);
        Assert.Equal("Search the Contoso handbook.", used.For(McpToolDescriptions.SearchKnowledge));
        Assert.Null(used.For(McpToolDescriptions.GetDocumentSection));
    }

    [Fact]
    public async Task The_command_refuses_tool_text_it_cannot_use_and_writes_nothing()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var tuning = new TuningSettingsStore(db, tenant);

        await Assert.ThrowsAsync<ArgumentException>(() => tuning.SetAsync(
            McpToolDescriptions.Key, Json("""{"search_everything": "no such tool"}"""), AdminActor.Cli()));
        Assert.Null(await new SettingsStore(db, tenant).GetAsync(McpToolDescriptions.Key));

        // The control: the same command with a value it can use writes it.
        var change = await tuning.SetAsync(
            McpToolDescriptions.Key, Json("""{"search_knowledge": "Search the handbook."}"""), AdminActor.Cli());
        Assert.True(change.Changed);
        Assert.NotNull(await new SettingsStore(db, tenant).GetAsync(McpToolDescriptions.Key));
    }
}
