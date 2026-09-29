using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Embeddings;

/// <summary>
/// OpenAI text-embedding-3-small over raw HTTPS. The API key comes from the
/// OPENAI_API_KEY environment variable and is never persisted anywhere. A
/// provider that has no key, cannot be reached or does not answer with vectors
/// is a <see cref="StartupRefusedException"/> that names it, so a host stops
/// with that sentence rather than a stack trace.
/// </summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const string Model = "text-embedding-3-small";
    private const int BatchSize = 64;
    private readonly HttpClient _http;

    public string Name => $"openai:{Model}";
    public int Dimensions => 1536;

    public OpenAiEmbeddingProvider(string? apiKey = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new StartupRefusedException($"The embedding provider openai:{Model} needs OPENAI_API_KEY, which is not set.");
        _http = new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var all = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(BatchSize))
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync("v1/embeddings",
                    new { model = Model, input = batch }, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new StartupRefusedException(
                    $"The embedding provider {Name} could not be reached at {_http.BaseAddress} ({ex.Message}). Nothing was embedded.", ex);
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new StartupRefusedException(
                    $"The embedding provider {Name} answered {(int)response.StatusCode} ({Truncate(body)}). Nothing was embedded.");
            }
            var parsed = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct)
                ?? throw new InvalidOperationException("Empty embeddings response.");
            // API returns items with an index field; order by it rather than trusting array order.
            all.AddRange(parsed.Data.OrderBy(d => d.Index).Select(d => d.Embedding));
        }
        return [.. all];
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];

    public void Dispose() => _http.Dispose();

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem> Data);
    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
