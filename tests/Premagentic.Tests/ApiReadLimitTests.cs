using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Premagentic.Api.Callers;
using Premagentic.Core.Retrieval;

namespace Premagentic.Tests;

/// <summary>
/// The bounds on a read over HTTP: a query, a path and a heading of at most
/// <see cref="QueryLimits.MaxLength"/> characters, refused with one sentence
/// before anything is read or recorded, and a body of at most
/// <see cref="ReadRequestLimit.Bytes"/> bytes, refused with 413 whether its
/// length was declared or not. Requires a running Docker daemon.
/// </summary>
public sealed class ApiReadLimitTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    /// <summary>A question of exactly <paramref name="length"/> characters, in words the index holds.</summary>
    private static string Question(int length)
    {
        var words = new StringBuilder();
        while (words.Length < length) words.Append("zeppelin ");
        return words.ToString(0, length);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private static async Task<long> SearchesRecordedAsync(ApiWorld w)
    {
        await using var cmd = w.Db.DataSource.CreateCommand("SELECT count(*) FROM prem_config.retrieval_event");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_query_at_the_limit_is_searched_and_one_character_over_it_is_refused_with_the_sentence_and_not_recorded()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var http = w.Host.Client();

        using (var served = await Api.SearchAsync(http, Question(QueryLimits.MaxLength), bearer: w.BotToken))
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var recorded = await SearchesRecordedAsync(w);

        var tooLong = Question(QueryLimits.MaxLength + 1);
        using var refused = await Api.SearchAsync(http, tooLong, bearer: w.BotToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(
            "A query is at most 4,000 characters and this one has 4,001: the embedding model reads only the start of a longer one, " +
            "and every query is kept whole in the audit trail.",
            await ErrorAsync(refused));
        Assert.Equal(recorded, await SearchesRecordedAsync(w));
    }

    [Fact]
    public async Task A_path_or_heading_over_the_limit_is_refused_and_a_path_at_it_is_only_absent()
    {
        await using var w = await ApiWorld.NewAsync(server);
        using var http = w.Host.Client();
        Task<HttpResponseMessage> SectionAsync(string path, string? heading = null) =>
            http.SendAsync(Api.Request(HttpMethod.Post, "/api/section", new { path, heading }, bearer: w.BotToken));

        using (var absent = await SectionAsync(new string('p', QueryLimits.MaxLength)))
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var recorded = await SearchesRecordedAsync(w);

        using var longPath = await SectionAsync(new string('p', QueryLimits.MaxLength + 1));
        Assert.Equal(HttpStatusCode.BadRequest, longPath.StatusCode);
        Assert.Equal(
            "A path or a heading is at most 4,000 characters and this one has 4,001: every fetch is kept whole in the audit trail.",
            await ErrorAsync(longPath));

        // The same for a heading under a path the caller may read.
        using var longHeading = await SectionAsync(ApiWorld.Handbook, new string('h', QueryLimits.MaxLength + 1));
        Assert.Equal(HttpStatusCode.BadRequest, longHeading.StatusCode);
        Assert.Equal(recorded, await SearchesRecordedAsync(w));
    }

    /// <summary>A request to <paramref name="route"/> padded with white space, which JSON allows, to exactly <paramref name="bytes"/> bytes.</summary>
    private static byte[] Body(string route, int bytes)
    {
        var body = Encoding.UTF8.GetBytes(route == "/api/search" ? """{"query":"zeppelin","topK":5}""" : $$"""{"path":"{{ApiWorld.Handbook}}"}""");
        return body.Concat(Enumerable.Repeat((byte)' ', bytes - body.Length)).ToArray();
    }

    /// <summary>A stream that cannot say its length, so the request goes without Content-Length.</summary>
    private sealed class UnknownLength(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Hands a request on untouched; a client with no redirect handler does not buffer its body to learn its length.</summary>
    private sealed class PassThrough : DelegatingHandler;

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(ApiWorld w, string route, byte[] body, bool declared)
    {
        using var http = declared ? w.Host.Client() : w.Host.Client(true, new PassThrough());
        HttpContent content = declared ? new ByteArrayContent(body) : new StreamContent(new UnknownLength(body));
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + w.BotToken);
        using var response = await http.SendAsync(request);
        Assert.Equal(declared, request.Content.Headers.ContentLength is not null);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/search", true)]
    [InlineData("/api/search", false)]
    [InlineData("/api/section", true)]
    [InlineData("/api/section", false)]
    public async Task A_body_at_the_bound_is_read_and_one_byte_over_it_is_refused_with_413(string route, bool declared)
    {
        await using var w = await ApiWorld.NewAsync(server);

        var (atBound, answer) = await PostAsync(w, route, Body(route, ReadRequestLimit.Bytes), declared);
        Assert.True(atBound == HttpStatusCode.OK, $"answered {atBound}: {answer[..Math.Min(answer.Length, 300)]}");

        var (over, refusal) = await PostAsync(w, route, Body(route, ReadRequestLimit.Bytes + 1), declared);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over);
        Assert.Equal("The request body is over this endpoint's limit of 65536 bytes.",
            JsonDocument.Parse(refusal).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Both_reads_carry_the_bound_for_the_web_servers_own_body_size_feature()
    {
        Assert.Equal(65536, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)ReadRequestLimit.Instance).MaxRequestBodySize);
        Assert.False(ReadRequestLimit.Over(ReadRequestLimit.Bytes));
        Assert.True(ReadRequestLimit.Over(ReadRequestLimit.Bytes + 1));
    }
}
