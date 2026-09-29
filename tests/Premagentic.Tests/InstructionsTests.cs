using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Evaluation;
using Premagentic.Core.Identity;
using Premagentic.Core.Profiles;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A deployment's instructions for assistants (<c>mcp.instructions</c>):
/// what the setting accepts, how it is read back with who set it, and a
/// profile carrying it, and the tool text, which a profile could not carry
/// before. Requires a running Docker daemon.
/// </summary>
public sealed class InstructionsTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private static JsonElement Text(string text) => JsonSerializer.SerializeToElement(text);

    private async Task<(PremagenticDatabase Db, Guid Tenant)> EmptyAsync()
    {
        var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        return (db, await db.EnsureTenantAsync("t", "T"));
    }

    [Fact]
    public void The_setting_takes_text_with_line_breaks_up_to_its_bound()
    {
        Assert.Null(McpSettings.InstructionsProblem(Text("Cite every passage.\r\nNever guess a path.\nAsk when unsure.")));
        Assert.Null(McpSettings.InstructionsProblem(Text(new string('x', McpSettings.InstructionsMaxLength))));

        Assert.Contains("at most", McpSettings.InstructionsProblem(Text(new string('x', McpSettings.InstructionsMaxLength + 1))));
        Assert.Contains("no other control characters", McpSettings.InstructionsProblem(Text("a\tb")));
        Assert.Contains("no other control characters", McpSettings.InstructionsProblem(Text("a\u0007b")));
        Assert.NotNull(McpSettings.InstructionsProblem(Text("   ")));
        Assert.NotNull(McpSettings.InstructionsProblem(JsonSerializer.SerializeToElement(42)));
    }

    [Fact]
    public async Task The_reader_gives_the_text_when_it_was_set_and_by_whom_and_nothing_when_unset()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var reader = new SettingsInstructionsReader(db);

        Assert.Null(await reader.ReadAsync(tenant, default));

        await new TuningSettingsStore(db, tenant).SetAsync(
            McpSettings.Instructions, Text("Cite every passage."), new AdminActor("cli", "an-admin"));
        var read = await reader.ReadAsync(tenant, default);

        Assert.NotNull(read);
        Assert.Equal("Cite every passage.", read.Text);
        Assert.Equal("cli (an-admin)", read.UpdatedBy);
        Assert.True(DateTimeOffset.UtcNow - read.UpdatedAt < TimeSpan.FromMinutes(5));
        Assert.Contains(await new ChangeRecord(db, tenant).ListAsync(10), e => e.Target == McpSettings.Instructions);

        // A value written past the validation, as a hand at the prompt could, reads as none.
        await new SettingsStore(db, tenant).SetAsync(McpSettings.Instructions, Text("a\u0007b"));
        Assert.Null(await reader.ReadAsync(tenant, default));
    }

    [Fact]
    public async Task A_profile_carries_the_instructions_and_the_tool_text_and_not_what_code_loads()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var folder = Directory.CreateTempSubdirectory("premagentic-profile-").FullName;
        File.WriteAllText(Path.Combine(folder, "profile.json"), """{ "name": "office", "version": "1", "description": "An invented office." }""");
        File.WriteAllText(Path.Combine(folder, "settings.json"), """
            { "mcp.instructions": "Cite the file of every passage.",
              "mcp.tool_descriptions": { "search_knowledge": "Search the office handbook." } }
            """);

        Assert.True(ProfileReader.TryRead(folder, out var profile, out var read), string.Join(" | ", read));
        var (plan, problems) = await ProfilePlanner.BuildAsync(profile!, db, tenant, Path.GetTempPath(), null);
        Assert.True(plan is not null, string.Join(" | ", problems));
        var report = await ProfileApply.RunAsync(plan, db, tenant, new AdminActor("cli", "an-admin"), null);
        Assert.True(report.Complete, report.Problem);

        var settings = new SettingsStore(db, tenant);
        Assert.Equal("Cite the file of every passage.", (await settings.GetAsync(McpSettings.Instructions))!.Value.GetString());
        Assert.Equal("Search the office handbook.",
            (await settings.GetAsync("mcp.tool_descriptions"))!.Value.GetProperty("search_knowledge").GetString());

        // Applying it again changes nothing.
        var (again, againProblems) = await ProfilePlanner.BuildAsync(profile!, db, tenant, Path.GetTempPath(), null);
        Assert.DoesNotContain(again!.Changes, c => c is SettingChange);

        // What code the server loads is not a profile's to say in this build.
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{ "extensions.folder": "/opt/extensions" }""");
        Assert.True(ProfileReader.TryRead(folder, out var loads, out var loadsRead), string.Join(" | ", loadsRead));
        var (none, refused) = await ProfilePlanner.BuildAsync(loads!, db, tenant, Path.GetTempPath(), null);
        Assert.Null(none);
        Assert.Contains(refused, p => p.ToString().Contains("extensions.folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_profile_refuses_instructions_the_setting_would_refuse()
    {
        var (db, tenant) = await EmptyAsync();
        await using var _ = db;
        var folder = Directory.CreateTempSubdirectory("premagentic-profile-").FullName;
        File.WriteAllText(Path.Combine(folder, "profile.json"), """{ "name": "office", "version": "1", "description": "An invented office." }""");
        File.WriteAllText(Path.Combine(folder, "settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["mcp.instructions"] = new string('x', McpSettings.InstructionsMaxLength + 1) }));

        Assert.True(ProfileReader.TryRead(folder, out var profile, out var readProblems), string.Join(" | ", readProblems));
        var (plan, problems) = await ProfilePlanner.BuildAsync(profile!, db, tenant, Path.GetTempPath(), null);

        Assert.Null(plan);
        Assert.Contains(problems, p => p.ToString().Contains("at most", StringComparison.Ordinal));
    }
}
