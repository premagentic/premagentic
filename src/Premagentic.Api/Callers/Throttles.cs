using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Premagentic.Api.Callers;

/// <summary>
/// Each agent's <c>requests_per_minute</c>, counted over a sliding minute.
/// <para>
/// Held in this process's memory only. Two API processes in front of one
/// database each allow an agent its full rate, and a restart forgets the
/// window. A refused request does not count against the next minute.
/// </para>
/// </summary>
internal sealed class AgentRateLimiter(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _recent = new();

    /// <returns>True when the request may proceed; otherwise how long until one would.</returns>
    public bool TryAcquire(Guid agentId, int requestsPerMinute, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        var recent = _recent.GetOrAdd(agentId, _ => new Queue<DateTimeOffset>());
        lock (recent)
        {
            while (recent.Count > 0 && recent.Peek() <= now - Window) recent.Dequeue();
            if (recent.Count >= requestsPerMinute)
            {
                retryAfter = recent.Peek() + Window - now;
                return false;
            }
            recent.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

/// <param name="MaxFailures">Failed sign-ins one address may make within <paramref name="Window"/>.</param>
/// <param name="Window">How far back failures count.</param>
/// <param name="MaxAddresses">How many addresses are remembered at once, so a flood of addresses cannot exhaust memory.</param>
internal sealed record SignInThrottleOptions(int MaxFailures, TimeSpan Window, int MaxAddresses)
{
    /// <summary>Twenty failures in ten minutes from one address, then that address waits.</summary>
    public static SignInThrottleOptions Default { get; } = new(20, TimeSpan.FromMinutes(10), 10_000);
}

/// <summary>
/// Throttles failed password sign-ins by the address they come from, beside the
/// per-account lockout, so one address cannot try many names.
/// <para>
/// An IPv6 client is counted by its /64, the block one subscriber usually
/// holds. Behind a reverse proxy every request comes from the proxy's address
/// unless the proxy's forwarding headers are trusted, and then the throttle
/// covers everyone behind it at once. Held in this process's memory only.
/// </para>
/// </summary>
internal sealed class SignInThrottle(TimeProvider time, SignInThrottleOptions options)
{
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public bool IsBlocked(IPAddress? address, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            retryAfter = TimeSpan.Zero;
            if (!_failures.TryGetValue(Key(address), out var failures)) return false;
            Expire(failures, now);
            if (failures.Count < options.MaxFailures) return false;
            retryAfter = failures.Peek() + options.Window - now;
            return true;
        }
    }

    public void RecordFailure(IPAddress? address)
    {
        var now = time.GetUtcNow();
        var key = Key(address);
        lock (_lock)
        {
            if (!_failures.TryGetValue(key, out var failures))
            {
                if (_failures.Count >= options.MaxAddresses) MakeRoom(now);
                _failures[key] = failures = new Queue<DateTimeOffset>();
            }
            failures.Enqueue(now);
            while (failures.Count > options.MaxFailures) failures.Dequeue();
        }
    }

    /// <summary>The address as the throttle counts it: IPv4 as written, IPv6 by its /64.</summary>
    internal static string Key(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        return Convert.ToHexStringLower(address.GetAddressBytes().AsSpan(0, 8)) + "/64";
    }

    private void Expire(Queue<DateTimeOffset> failures, DateTimeOffset now)
    {
        while (failures.Count > 0 && failures.Peek() <= now - options.Window) failures.Dequeue();
    }

    // Forget addresses with nothing left in the window, and if that is not
    // enough, the address whose newest failure is oldest.
    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var (key, failures) in _failures.ToArray())
        {
            Expire(failures, now);
            if (failures.Count == 0) _failures.Remove(key);
        }
        if (_failures.Count < options.MaxAddresses) return;
        var stalest = _failures.MinBy(kv => kv.Value.Last()).Key;
        _failures.Remove(stalest);
    }
}
