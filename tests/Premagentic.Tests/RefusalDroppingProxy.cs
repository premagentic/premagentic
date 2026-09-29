using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Premagentic.Tests;

/// <summary>
/// A proxy in front of a test PostgreSQL that, while a connection is still
/// authenticating, ends the connection instead of passing on the server's
/// error: a refused password then arrives as a closed connection, with no
/// 28P01, as a PostgreSQL on Windows was seen to answer. Everything else is
/// passed through. Plain connections only: <see cref="ConnectionString"/>
/// turns TLS and GSS encryption off, so every message can be read.
/// <para>
/// The same server that was seen to answer that way sometimes does it itself,
/// ending the connection before the proxy has read its error. Either way the
/// client meets a login that ends with no error, and the proxy then ends the
/// client's connection the same way, so what the client sees does not depend
/// on which of the two happened, and <see cref="Dropped"/> counts both.
/// </para>
/// </summary>
internal sealed class RefusalDroppingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _dropped;

    private RefusalDroppingProxy(string upstream)
    {
        var builder = new NpgsqlConnectionStringBuilder(upstream);
        (_upstreamHost, _upstreamPort) = (builder.Host!, builder.Port);
        _listener.Start();
        ConnectionString = new NpgsqlConnectionStringBuilder(upstream)
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
        _accepting = AcceptAsync();
    }

    /// <summary>A proxy in front of the server <paramref name="upstream"/> names.</summary>
    public static RefusalDroppingProxy Start(string upstream) => new(upstream);

    /// <summary><c>upstream</c>, reached through this proxy.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// How many logins ended before the server authenticated them with no
    /// error passed on to the client: the proxy dropped the server's error, or
    /// the server ended the connection first.
    /// </summary>
    public int Dropped => Volatile.Read(ref _dropped);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = RelayAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            try
            {
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _stop.Token);
                var toServer = client.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token);
                await ServerToClientAsync(upstream.GetStream(), client);
                await Task.WhenAny(toServer, Task.Delay(TimeSpan.FromSeconds(1)));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task ServerToClientAsync(NetworkStream server, TcpClient client)
    {
        var authenticated = false;
        var head = new byte[5];
        while (true)
        {
            byte[] body;
            try
            {
                if (!await ReadAllAsync(server, head)) break;
                body = new byte[BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1)) - 4];
                if (!await ReadAllAsync(server, body)) break;
            }
            catch (IOException)
            {
                // The server reset the connection, which can take its error with it.
                break;
            }
            if (head[0] == (byte)'R' && BinaryPrimitives.ReadInt32BigEndian(body) == 0) authenticated = true;
            if (head[0] == (byte)'E' && !authenticated) break;
            var stream = client.GetStream();
            await stream.WriteAsync(head, _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
        }
        if (authenticated) return;
        // A login that ended before the server authenticated it: its error, if
        // the server sent one, is never passed on, and the client's connection
        // is ended at once, whichever side ended first.
        Interlocked.Increment(ref _dropped);
        client.Client.LingerState = new LingerOption(true, 0);
        client.Client.Close();
    }

    private async Task<bool> ReadAllAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), _stop.Token);
            if (got == 0) return false;
            read += got;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}
