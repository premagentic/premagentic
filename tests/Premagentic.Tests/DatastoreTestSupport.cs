using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Premagentic.Tests;

/// <summary>
/// One stock PostgreSQL container per test class, and an empty database per test
/// that asks for one, so a test that drops a schema or races two migration
/// runners never sees another test's state.
/// Requires a running Docker daemon.
/// </summary>
public sealed class DatastoreTestDatabase : IAsyncLifetime
{
    /// <summary>
    /// Stock PostgreSQL at the minor version docker-compose.yml pins. The vector
    /// extension is not available in this image, which is the point.
    /// </summary>
    public const string Image = "postgres:17.11@sha256:d74eeac9a635390a49bc21bd49fccd973de707e2a53a76ac49b552b8712ec46f";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>The container's superuser, for tests that create roles and databases themselves.</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using var cmd = admin.CreateCommand($"CREATE DATABASE {name}");
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

/// <summary>
/// A deterministic embedder whose vectors are uniform noise seeded by the text,
/// scaled so they are NOT unit length, which makes ingest's normalization
/// observable. The same text always yields the same vector, so a query that
/// repeats a passage word for word lands on it exactly.
/// </summary>
internal sealed class SeededEmbeddingProvider(int dimensions = 64) : IEmbeddingProvider
{
    public string Name => $"test-seeded-{dimensions}";
    public int Dimensions => dimensions;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        Task.FromResult(texts.Select(Vector).ToArray());

    public float[] Vector(string text)
    {
        var rng = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0));
        var v = new float[dimensions];
        for (var i = 0; i < v.Length; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1) * 3f;
        return v;
    }
}

/// <summary>A connector over a fixed list of reads, including failures and nothing at all.</summary>
internal sealed class DatastoreSource(string pathPrefix, IReadOnlyList<SourceDocument> docs) : IDocumentSource
{
    public string Name => "test-datastore";
    public string PathPrefix { get; } = pathPrefix;

    public async IAsyncEnumerable<SourceRead> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            yield return SourceRead.Ok(doc);
        }
        await Task.CompletedTask;
    }

    public static SourceDocument Doc(string path, string text, DocumentAccess access, string? title = null) =>
        new(path, title ?? path, text,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), access);
}
