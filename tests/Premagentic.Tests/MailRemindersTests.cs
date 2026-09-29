using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Premagentic.Core.Extensions;
using Premagentic.Core.Reminders;

namespace Premagentic.Tests;

/// <summary>
/// The reminder seam end to end through a loaded extension: the mail-reminders
/// sample, installed in a folder, allowed by its hash and loaded by the
/// extension host, delivers a run to an SMTP stand-in the test listens with on
/// loopback. One message per owner, the documents nobody owns to the
/// administrators' address, nothing on <c>--plan</c>. Requires a running Docker
/// daemon, for the invented deployment the run is computed from.
/// </summary>
public sealed class MailRemindersTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>, IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-mail-extension-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An assembly a load context still holds open; the temp folder's to clean.
        }
    }

    [Fact]
    public async Task A_delivered_run_sends_one_message_per_owner_and_the_unowned_to_the_administrators()
    {
        var (db, tenant, _) = await World.CreateAsync(server);
        await using var _db = db;
        using var smtp = new SmtpStandIn();
        var host = Load(smtp.Port, userDomain: "example.org");

        var job = new RemindersJob(db, tenant, host.ReminderSinks);
        await job.RunAsync(plan: false, CancellationToken.None);

        Assert.Empty(job.Failures);
        var sent = smtp.Messages.ToArray();
        Assert.Equal(["alice@example.org", "bob@example.org", "it@example.org"], sent.Select(m => m.To).Order());
        Assert.Contains("greenhouse/watering.md", sent.Single(m => m.To == "alice@example.org").Data);
        Assert.Contains("nursery/seeds.md", sent.Single(m => m.To == "bob@example.org").Data);
        var administrators = sent.Single(m => m.To == "it@example.org").Data;
        Assert.Contains("office/both.md", administrators);
        Assert.Contains("loose/notes.md", administrators);
        // The built-in sink still kept the run first.
        Assert.NotNull(await new ReminderSummaries(db, tenant).LatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_planned_run_sends_nothing()
    {
        var (db, tenant, _) = await World.CreateAsync(server);
        await using var _db = db;
        using var smtp = new SmtpStandIn();
        var host = Load(smtp.Port, userDomain: "example.org");
        Assert.Contains(host.ReminderSinks, s => s.Name == "mail");

        await new RemindersJob(db, tenant, host.ReminderSinks).RunAsync(plan: true, CancellationToken.None);

        Assert.Empty(smtp.Messages);
        Assert.Equal(0, smtp.Connections);
    }

    [Fact]
    public async Task An_owner_with_no_address_is_the_runs_failure_after_the_others_are_sent()
    {
        var (db, tenant, _) = await World.CreateAsync(server);
        await using var _db = db;
        using var smtp = new SmtpStandIn();
        var host = Load(smtp.Port, userDomain: null);

        var job = new RemindersJob(db, tenant, host.ReminderSinks);
        await job.RunAsync(plan: false, CancellationToken.None);

        Assert.Equal(["it@example.org"], smtp.Messages.Select(m => m.To));
        var failure = Assert.Single(job.Failures);
        Assert.Equal("mail", failure.Sink);
        Assert.Contains("user:alice", failure.Message);
        Assert.Contains("user:bob", failure.Message);
    }

    [Fact]
    public void A_password_in_mail_json_refuses_the_extension()
    {
        var host = Load(port: 25, userDomain: "example.org", extra: "\"password\": \"hunter2\",");

        Assert.Empty(host.Loaded);
        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.DidNotLoad, refused.Reason);
        Assert.Contains("holds a password", refused.Detail);
        Assert.DoesNotContain("hunter2", refused.Detail);
    }

    /// <summary>The sample installed, with a mail.json pointing at <paramref name="port"/> on loopback, allowed and loaded.</summary>
    private ExtensionHost Load(int port, string? userDomain, string extra = "")
    {
        var extensions = Directory.CreateDirectory(Path.Combine(_root.FullName, Guid.NewGuid().ToString("N")));
        var folder = Directory.CreateDirectory(Path.Combine(extensions.FullName, "mail-reminders")).FullName;
        var assembly = Path.Combine(folder, "MailReminders.dll");
        File.Copy(BuiltPath(), assembly);
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), $$"""
            { "name": "mail-reminders", "version": "0.1.0", "assemblyFile": "MailReminders.dll",
              "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly)))}}",
              "seams": { "reminder": 1 } }
            """);
        var domain = userDomain is null ? "" : $"\"userDomain\": \"{userDomain}\",";
        File.WriteAllText(Path.Combine(folder, "mail.json"), $$"""
            { "host": "127.0.0.1", "port": {{port}}, "tls": false, "from": "premagentic@example.org", {{domain}} {{extra}}
              "addresses": { "administrators": "it@example.org" } }
            """);
        var measured = ExtensionAllowList.Measure(folder);
        return ExtensionHost.Load(extensions.FullName, [(measured.Name, measured.Sha256)]);
    }

    private static string BuiltPath()
    {
        var path = typeof(MailRemindersTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Sample.MailReminders.Path")
            .Value;
        Assert.True(File.Exists(path), "the mail sample was not built where the test project recorded it: " + path);
        return path!;
    }

    /// <summary>
    /// Just enough SMTP on loopback to take messages: every command is
    /// answered as accepted, and each message is kept with its recipient and
    /// its text. Nothing leaves this computer.
    /// </summary>
    internal sealed class SmtpStandIn : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _connections;

        public SmtpStandIn()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref _connections);

        public ConcurrentQueue<(string To, string Data)> Messages { get; } = new();

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 stand-in");
            var to = "";
            while (await reader.ReadLineAsync() is { } line)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                if (verb == "RCPT") to = line[(line.IndexOf('<') + 1)..line.IndexOf('>')];
                if (verb == "DATA")
                {
                    await writer.WriteLineAsync("354 go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } body && body != ".") data.AppendLine(body);
                    Messages.Enqueue((to, data.ToString()));
                    await writer.WriteLineAsync("250 kept");
                    continue;
                }
                if (verb == "QUIT")
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                await writer.WriteLineAsync("250 ok");
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
