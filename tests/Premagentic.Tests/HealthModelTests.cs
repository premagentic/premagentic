using System.Net;
using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Embeddings;

namespace Premagentic.Tests;

/// <summary>
/// The health report names the model this process embeds with, and for a
/// local model the folder it came from and its revision; the open
/// <c>/health</c> says the name and revision and not the folder. Requires a
/// running Docker daemon for the endpoint test.
/// </summary>
public sealed class HealthModelTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    [Fact]
    public void A_model_folder_says_its_name_and_revision_from_prem_model_json()
    {
        var folder = Directory.CreateTempSubdirectory("prem-model-").FullName;
        try
        {
            Assert.Equal(("local:all-MiniLM-L6-v2", (string?)null), LocalOnnxEmbeddingProvider.Describe(folder));

            File.WriteAllText(Path.Combine(folder, "prem-model.json"),
                """{ "name": "local:bge-small-en-v1.5", "pooling": "cls", "dimensions": 384, "revision": "a1b2c3" }""");
            Assert.Equal(("local:bge-small-en-v1.5", "a1b2c3"), LocalOnnxEmbeddingProvider.Describe(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_provider_with_no_folder_is_named_and_nothing_more()
    {
        var provider = new HashEmbeddingProvider();

        Assert.Equal(new LoadedModel(provider.Name, null, null), HealthReport.Model(provider));
    }

    [Fact]
    public async Task The_open_health_page_names_the_model_and_not_where_it_lives()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var client = w.Host.Client();

        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var model = body.RootElement.GetProperty("model");

        Assert.False(string.IsNullOrEmpty(model.GetProperty("name").GetString()));
        Assert.True(model.TryGetProperty("revision", out _));
        Assert.False(model.TryGetProperty("folder", out _));
    }
}
