using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Premagentic.Api.Callers;
using Premagentic.Core.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Premagentic.Tests;

/// <summary>
/// A document form whose whole body is past its bound, over HTTP/1.1 on
/// Kestrel, spoken byte by byte on a raw socket so the test decides when each
/// part is sent: within the cap the host reads and discards the body before it
/// answers, so the answer arrives after the body as a browser needs it; past
/// the cap, from another origin, or with no declared length, it answers at once
/// as before, reading nothing more; a sender that stops is answered at the time
/// limit and the connection ends. The refusal is the Clients page with the
/// flow's sentence throughout. Requires a running Docker daemon.
/// </summary>
public sealed class PortalFormDrainTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>
{
    private const int OneMegabyte = 1024 * 1024;
    private const string Path = "/portal/oauth/clients/document";

    [Fact]
    public void A_body_is_drained_only_over_http_1_1_from_this_origin_past_its_bound_and_within_the_cap()
    {
        const long bound = 327_680, cap = 30_000_000;
        Assert.True(PortalFormDrain.Drains("HTTP/1.1", bound + 1, bound, cap, fromThisOrigin: true, expectsContinue: false));
        Assert.True(PortalFormDrain.Drains("HTTP/1.1", cap, bound, cap, fromThisOrigin: true, expectsContinue: false));
        Assert.False(PortalFormDrain.Drains("HTTP/2", bound + 1, bound, cap, fromThisOrigin: true, expectsContinue: false));
        Assert.False(PortalFormDrain.Drains("HTTP/1.1", bound + 1, bound, cap, fromThisOrigin: false, expectsContinue: false));
        Assert.False(PortalFormDrain.Drains("HTTP/1.1", null, bound, cap, fromThisOrigin: true, expectsContinue: false));
        Assert.False(PortalFormDrain.Drains("HTTP/1.1", bound, bound, cap, fromThisOrigin: true, expectsContinue: false));
        Assert.False(PortalFormDrain.Drains("HTTP/1.1", cap + 1, bound, cap, fromThisOrigin: true, expectsContinue: false));
        Assert.False(PortalFormDrain.Drains("HTTP/1.1", bound + 1, null, cap, fromThisOrigin: true, expectsContinue: false));
        Assert.False(PortalFormDrain.Drains("HTTP/1.1", bound + 1, bound, cap, fromThisOrigin: true, expectsContinue: true));
    }

    [Fact]
    public async Task Within_the_cap_the_answer_waits_for_the_body_and_arrives_after_it()
    {
        var logs = new List<string>();
        var (p, stand) = await NewAsync(PortalFormDrain.Default, logs);
        await using var _ = p;
        using var socket = await OpenAsync(p);
        var stream = socket.GetStream();

        await stream.WriteAsync(Head(p, OneMegabyte, Origin(p)));
        await stream.WriteAsync(Body(OneMegabyte / 2));
        // Half the body is in: the host is still reading it, and has not answered.
        Assert.Null(await AnswerWithinAsync(stream, TimeSpan.FromSeconds(1.5)));

        await stream.WriteAsync(Body(OneMegabyte - OneMegabyte / 2));
        var answer = await AnswerWithinAsync(stream, TimeSpan.FromSeconds(20));

        AssertTheRefusal(answer);
        Assert.DoesNotContain("Connection: close", answer!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stand.Clients.AddedFromDocument);
        lock (logs) Assert.Single(logs, line => line.Contains("the form passed its bound (the whole body)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Past_the_cap_the_answer_comes_at_once_as_before()
    {
        var (p, stand) = await NewAsync(Patient with { CapBytes = OneMegabyte }, logs: null, patientWithSlowSenders: true);
        await using var _ = p;
        using var socket = await OpenAsync(p);
        var stream = socket.GetStream();

        // Only the head: an answer now is an answer that read nothing of the body.
        await stream.WriteAsync(Head(p, 2 * OneMegabyte, Origin(p)));

        AssertTheRefusal(await AnsweredAtOnceAsync(stream));
        Assert.Empty(stand.Clients.AddedFromDocument);
    }

    [Fact]
    public async Task A_cross_site_post_within_the_cap_is_answered_at_once_and_nothing_is_read()
    {
        var (p, stand) = await NewAsync(Patient, logs: null, patientWithSlowSenders: true);
        await using var _ = p;
        using var socket = await OpenAsync(p);
        var stream = socket.GetStream();

        // The origin the portal's access filter refuses: the host reads no byte of the body.
        await stream.WriteAsync(Head(p, OneMegabyte, "http://elsewhere.example"));

        AssertTheRefusal(await AnsweredAtOnceAsync(stream));
        Assert.Empty(stand.Clients.AddedFromDocument);
    }

    [Fact]
    public async Task A_client_that_asks_for_100_continue_is_answered_at_once_and_never_told_to_send()
    {
        var (p, stand) = await NewAsync(Patient, logs: null, patientWithSlowSenders: true);
        await using var _ = p;
        using var socket = await OpenAsync(p);
        var stream = socket.GetStream();

        // Same origin, within the cap, but holding the body back: a drain would
        // tell it to send (100 Continue) and then read what it sent. The host
        // answers at once instead, and never says continue.
        await stream.WriteAsync(Head(p, OneMegabyte, Origin(p), expectContinue: true));

        var answer = await AnsweredAtOnceAsync(stream);
        Assert.DoesNotContain("100 Continue", answer, StringComparison.OrdinalIgnoreCase);
        AssertTheRefusal(answer);
        Assert.Empty(stand.Clients.AddedFromDocument);
    }

    [Fact]
    public async Task A_sender_that_stops_is_answered_at_the_time_limit_and_the_connection_ends()
    {
        var (p, _) = await NewAsync(PortalFormDrain.Default with { Time = TimeSpan.FromSeconds(2) }, logs: null);
        await using var __ = p;
        using var socket = await OpenAsync(p);
        var stream = socket.GetStream();

        var clock = Stopwatch.StartNew();
        await stream.WriteAsync(Head(p, OneMegabyte, Origin(p)));
        await stream.WriteAsync(Body(100 * 1024));
        var answer = await AnswerWithinAsync(stream, TimeSpan.FromSeconds(20));
        clock.Stop();

        AssertTheRefusal(answer);
        Assert.Contains("Connection: close", answer!, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_body_with_no_declared_length_is_not_drained()
    {
        var (p, _) = await NewAsync(PortalFormDrain.Default, logs: null);
        await using var __ = p;
        using var socket = await OpenAsync(p);
        var stream = socket.GetStream();

        // Chunked, one chunk past the bound, and no last chunk: the host answers
        // once the bound is passed, with the body unfinished.
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Head(p, length: null, Origin(p))));
        var chunk = Body(400 * 1024);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:x}\r\n"));
        await stream.WriteAsync(chunk);
        await stream.WriteAsync("\r\n"u8.ToArray());

        AssertTheRefusal(await AnswerWithinAsync(stream, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Two_hosts_on_Kestrel_start_at_once_each_on_a_port_of_its_own()
    {
        // The Kestrel worlds of different test classes run beside each other, so
        // each must listen where the system puts it, never on a fixed port.
        var worlds = await Task.WhenAll(
            ApiWorld.NewAsync(server, new ApiHostOptions { Kestrel = true }),
            ApiWorld.NewAsync(server, new ApiHostOptions { Kestrel = true }));
        try
        {
            using var first = worlds[0].Host.Client();
            using var second = worlds[1].Host.Client();
            Assert.NotEqual(first.BaseAddress!.Port, second.BaseAddress!.Port);
            // Not Kestrel's default either, which is where a host lands that was given no port.
            Assert.DoesNotContain(5000, new[] { first.BaseAddress.Port, second.BaseAddress.Port });
            using (var search = await Api.SearchAsync(first, bearer: worlds[0].BotToken)) Assert.Equal(System.Net.HttpStatusCode.OK, search.StatusCode);
            using (var search = await Api.SearchAsync(second, bearer: worlds[1].BotToken)) Assert.Equal(System.Net.HttpStatusCode.OK, search.StatusCode);
        }
        finally
        {
            foreach (var world in worlds) await world.DisposeAsync();
        }
    }

    /// <param name="patientWithSlowSenders">
    /// Turns off Kestrel's minimum body data rate, which would otherwise end a
    /// silent sender after its five-second grace and so cut short a drain a
    /// test means to see wait.
    /// </param>
    private async Task<(PortalWorld World, PortalOAuthPagesTests.StandIns Stand)> NewAsync(
        PortalFormDrain drain, List<string>? logs, bool patientWithSlowSenders = false)
    {
        var stand = new PortalOAuthPagesTests.StandIns();
        var options = stand.Options();
        options.Kestrel = true;
        options.AllowHttpSignIn = true;
        options.Logs = logs;
        var standIns = options.Services;
        options.Services = services =>
        {
            standIns?.Invoke(services);
            services.RemoveAll<PortalFormDrain>();
            services.AddSingleton(drain);
            if (patientWithSlowSenders)
                services.Configure<KestrelServerOptions>(kestrel => kestrel.Limits.MinRequestBodyDataRate = null);
        };
        return (await PortalWorld.NewAsync(server, options: options), stand);
    }

    private static async Task<TcpClient> OpenAsync(PortalWorld p)
    {
        var address = p.Client.BaseAddress!;
        var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port);
        return socket;
    }

    private static string Origin(PortalWorld p) => p.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// A document form's request head as a browser sends it over HTTP/1.1, with a
    /// declared length or chunked; with <paramref name="expectContinue"/>, as a
    /// client that holds its body back until the host says go.
    /// </summary>
    private static byte[] Head(PortalWorld p, long length, string origin, bool expectContinue = false) =>
        Encoding.ASCII.GetBytes(Head(p, (long?)length, origin, expectContinue));

    private static string Head(PortalWorld p, long? length, string origin, bool expectContinue = false) =>
        $"POST {Path} HTTP/1.1\r\n" +
        $"Host: {p.Client.BaseAddress!.Authority}\r\n" +
        $"Cookie: {CallerMiddleware.SessionCookie}={p.Admin.Cookie}\r\n" +
        $"Origin: {origin}\r\n" +
        "Content-Type: multipart/form-data; boundary=prem-test-boundary\r\n" +
        (length is { } declared ? $"Content-Length: {declared}\r\n" : "Transfer-Encoding: chunked\r\n") +
        (expectContinue ? "Expect: 100-continue\r\n" : "") +
        "\r\n";

    private static byte[] Body(int bytes) => Enumerable.Repeat((byte)'x', bytes).ToArray();

    /// <summary>
    /// How long a test waits for the answer to a head alone. The hosts of those
    /// tests leave a slow sender be (Kestrel's minimum body data rate off) and
    /// give a drain a minute, so a host that waited on the body would answer
    /// only after a minute, far past this, while the answer that read nothing
    /// comes in milliseconds. The margin is the minute, not a guess at timing.
    /// </summary>
    private static readonly TimeSpan AtOnce = TimeSpan.FromSeconds(10);

    /// <summary>A drain's time for the tests of an answer at once: long enough that waiting on the body cannot pass for one.</summary>
    private static readonly PortalFormDrain Patient = PortalFormDrain.Default with { Time = TimeSpan.FromSeconds(60) };

    /// <summary>The host's answer to a head alone, which must arrive within <see cref="AtOnce"/>.</summary>
    private static async Task<string> AnsweredAtOnceAsync(NetworkStream stream)
    {
        var clock = Stopwatch.StartNew();
        var answer = await AnswerWithinAsync(stream, AtOnce);
        clock.Stop();
        Assert.True(answer is not null,
            $"no answer within {AtOnce.TotalSeconds:0} s (waited {clock.Elapsed.TotalSeconds:0.0} s): the host is waiting on the body");
        return answer!;
    }

    /// <summary>The status line and headers of the host's answer, or null when none arrives within <paramref name="wait"/>.</summary>
    private static async Task<string?> AnswerWithinAsync(NetworkStream stream, TimeSpan wait)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();
        using var limit = new CancellationTokenSource(wait);
        try
        {
            while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, limit.Token);
                if (read == 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }
        return text.Length == 0 ? null : text.ToString();
    }

    /// <summary>The refusal: the Clients page with the flow's sentence.</summary>
    private static void AssertTheRefusal(string? answer)
    {
        Assert.NotNull(answer);
        Assert.StartsWith("HTTP/1.1 302", answer, StringComparison.Ordinal);
        var location = answer.Split("\r\n").Single(line => line.StartsWith("Location: ", StringComparison.OrdinalIgnoreCase))["Location: ".Length..];
        Assert.StartsWith(OAuthPaths.Clients + "?", location, StringComparison.Ordinal);
        var error = location[(location.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&')
            .Select(pair => pair.Split('=', 2)).Single(pair => pair[0] == "error")[1];
        Assert.Equal(OAuthClientDocument.TooLarge, Uri.UnescapeDataString(error));
    }
}
