using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Okf;

/// <summary>Where the value of a trust setting came from.</summary>
public enum TrustSettingSource
{
    /// <summary>No value is stored, so the default applies.</summary>
    Default,

    /// <summary>A value is stored and was read.</summary>
    Stored,

    /// <summary>A value is stored and cannot be read, so the strictest value applies.</summary>
    Unreadable,
}

/// <summary>One trust setting as it stands.</summary>
/// <param name="Value">The value in force, in its stored spelling.</param>
/// <param name="StoredJson">The stored JSON as written, or null when nothing is stored.</param>
public sealed record TrustSettingReading(
    string Key,
    string Value,
    TrustSettingSource Source,
    string DefaultValue,
    string? StoredJson,
    IReadOnlyList<string> AllowedValues);

/// <param name="Changed">False when the value was already stored exactly so, and nothing was written or recorded.</param>
public sealed record TrustSettingChange(string Key, TrustSettingReading Before, string NewValue, bool Changed);

/// <summary>
/// The trust settings, kept in <c>prem_config.setting</c> as JSON strings:
/// <list type="bullet">
/// <item><c>trust.agents_minimum_tier</c>: what agents may see of
/// machine-written content. <c>human-reviewed</c> by default.</item>
/// <item><c>trust.people_minimum_tier</c>: the same for people.
/// <c>unverified</c> by default, so people see it, flagged.</item>
/// <item><c>trust.stale</c>: content past its <c>stale_after</c>.
/// <c>shown-to-people-only</c> by default.</item>
/// </list>
/// A key with nothing stored has its default. A stored value that cannot be
/// read is the strictest value, never the default, because a setting nobody
/// can read must not loosen anything.
/// <para>
/// <see cref="LoadAsync"/> reads the database every time it is called, and a
/// host calls it for every search, so a change applies to the very next query.
/// Nothing is cached, so nothing needs invalidating.
/// </para>
/// </summary>
public sealed class TrustSettingsStore(PremagenticDatabase db, Guid tenantId)
{
    public const string AgentsMinimumTier = "trust.agents_minimum_tier";
    public const string PeopleMinimumTier = "trust.people_minimum_tier";
    public const string Stale = "trust.stale";

    /// <summary>The kind the change record gives a setting change.</summary>
    public const string ChangeKind = "setting.set";

    public static IReadOnlyList<string> Keys { get; } = [AgentsMinimumTier, PeopleMinimumTier, Stale];

    private static readonly string[] Tiers =
        [.. new[] { OkfTrustTier.Unverified, OkfTrustTier.MachineConfirmed, OkfTrustTier.HumanReviewed }.Select(TrustPolicy.TierKey)];

    private static readonly string[] StaleValues =
        [.. new[] { StaleVisibility.HiddenFromEveryone, StaleVisibility.ShownToPeopleOnly, StaleVisibility.ShownToEveryone }.Select(StaleKey)];

    /// <summary>The values a key accepts, in their stored spelling, strictest first for the stale key.</summary>
    public static IReadOnlyList<string> AllowedValues(string key) => key switch
    {
        AgentsMinimumTier or PeopleMinimumTier => Tiers,
        Stale => StaleValues,
        _ => throw UnknownKey(key),
    };

    public static string DefaultValue(string key) => key switch
    {
        AgentsMinimumTier => TrustPolicy.TierKey(TrustSettings.Default.AgentMinimumTier),
        PeopleMinimumTier => TrustPolicy.TierKey(TrustSettings.Default.PersonMinimumTier),
        Stale => StaleKey(TrustSettings.Default.Stale),
        _ => throw UnknownKey(key),
    };

    /// <summary>The value that holds back the most, which is what an unreadable stored value reads as.</summary>
    public static string StrictestValue(string key) => key switch
    {
        AgentsMinimumTier or PeopleMinimumTier => TrustPolicy.TierKey(OkfTrustTier.HumanReviewed),
        Stale => StaleKey(StaleVisibility.HiddenFromEveryone),
        _ => throw UnknownKey(key),
    };

    /// <summary>
    /// Reads a value for <paramref name="key"/> the way <see cref="TrustPolicy.TryParseTier"/>
    /// reads a tier: ignoring case, spaces, hyphens and underscores. Gives the
    /// stored spelling. Numbers are not accepted.
    /// </summary>
    public static bool TryNormalize(string key, string? text, out string value)
    {
        switch (key)
        {
            case AgentsMinimumTier or PeopleMinimumTier when TrustPolicy.TryParseTier(text, out var tier):
                value = TrustPolicy.TierKey(tier);
                return true;
            case Stale when TryParseStale(text, out var stale):
                value = StaleKey(stale);
                return true;
            default:
                if (!Keys.Contains(key)) throw UnknownKey(key);
                value = "";
                return false;
        }
    }

    /// <summary>
    /// True when going from <paramref name="from"/> to <paramref name="to"/>
    /// would let more through: a lower tier, or stale content shown to more
    /// callers. Both values are in their stored spelling.
    /// </summary>
    public static bool IsLooser(string key, string from, string to) => key switch
    {
        AgentsMinimumTier or PeopleMinimumTier => Array.IndexOf(Tiers, to) < Array.IndexOf(Tiers, from),
        Stale => Array.IndexOf(StaleValues, to) > Array.IndexOf(StaleValues, from),
        _ => throw UnknownKey(key),
    };

    /// <summary>The spelling a stale value is stored in.</summary>
    public static string StaleKey(StaleVisibility stale) => stale switch
    {
        StaleVisibility.ShownToPeopleOnly => "shown-to-people-only",
        StaleVisibility.HiddenFromEveryone => "hidden-from-everyone",
        StaleVisibility.ShownToEveryone => "shown-to-everyone",
        _ => throw new ArgumentOutOfRangeException(nameof(stale), stale, "Not a stale setting."),
    };

    /// <summary>
    /// The settings a policy is resolved from, given each key's stored JSON.
    /// Pure: a missing key is its default, and a value that cannot be read is
    /// the strictest.
    /// </summary>
    public static TrustSettings FromStored(IReadOnlyDictionary<string, string> storedJson)
    {
        OkfTrustTier TierOf(string key) =>
            TrustPolicy.TryParseTier(Read(key, storedJson.GetValueOrDefault(key)).Value, out var tier) ? tier : OkfTrustTier.HumanReviewed;

        return new TrustSettings(
            AgentMinimumTier: TierOf(AgentsMinimumTier),
            PersonMinimumTier: TierOf(PeopleMinimumTier),
            Stale: TryParseStale(Read(Stale, storedJson.GetValueOrDefault(Stale)).Value, out var stale)
                ? stale
                : StaleVisibility.HiddenFromEveryone);
    }

    /// <summary>
    /// The deployment's settings as they are now, read from the database on
    /// every call. Call it for every search.
    /// </summary>
    public async Task<TrustSettings> LoadAsync(CancellationToken ct = default) =>
        FromStored(await ReadStoredAsync(ct));

    /// <summary>The policy for one caller under the settings as they are now.</summary>
    public async Task<TrustPolicy> ResolveAsync(CallerKind caller, string? agentMinimumTier, CancellationToken ct = default) =>
        TrustPolicy.Resolve(caller, agentMinimumTier, await LoadAsync(ct));

    /// <summary>Every trust setting, with where its value came from.</summary>
    public async Task<IReadOnlyList<TrustSettingReading>> ReadAllAsync(CancellationToken ct = default)
    {
        var stored = await ReadStoredAsync(ct);
        return Keys.Select(k => Read(k, stored.GetValueOrDefault(k))).ToArray();
    }

    public async Task<TrustSettingReading> ReadAsync(string key, CancellationToken ct = default)
    {
        if (!Keys.Contains(key)) throw UnknownKey(key);
        return Read(key, (await ReadStoredAsync(ct)).GetValueOrDefault(key));
    }

    /// <summary>
    /// Stores a value and records the change, in one transaction: the old and
    /// the new value, when, and who. Setting a value that is already stored
    /// exactly so writes nothing and records nothing.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key is not a trust setting, or the value is not one of its allowed
    /// values. Nothing is written. The message lists what is allowed.
    /// </exception>
    public async Task<TrustSettingChange> SetAsync(string key, string value, AdminActor actor, CancellationToken ct = default)
    {
        if (!TryNormalize(key, value, out var normalized))
            throw new ArgumentException(
                $"'{value}' is not a value of {key}. Allowed: {string.Join(", ", AllowedValues(key))}.");
        var newJson = JsonSerializer.Serialize(normalized);

        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ChangeRecord.LockAsync(conn, tx, tenantId, ct);

        string? oldJson;
        await using (var read = new NpgsqlCommand(
            "SELECT value::text FROM prem_config.setting WHERE tenant_id = @tenant AND key = @key", conn, tx))
        {
            read.Parameters.AddWithValue("tenant", tenantId);
            read.Parameters.AddWithValue("key", key);
            oldJson = await read.ExecuteScalarAsync(ct) as string;
        }

        var before = Read(key, oldJson);
        if (before.Source == TrustSettingSource.Stored && StoredString(oldJson) == normalized)
        {
            await tx.CommitAsync(ct);
            return new TrustSettingChange(key, before, normalized, Changed: false);
        }

        await using (var write = new NpgsqlCommand("""
            INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, @key, @value)
            ON CONFLICT (tenant_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """, conn, tx))
        {
            write.Parameters.AddWithValue("tenant", tenantId);
            write.Parameters.AddWithValue("key", key);
            write.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Jsonb) { Value = newJson });
            await write.ExecuteNonQueryAsync(ct);
        }

        await ChangeRecord.AppendAsync(conn, tx, tenantId, ChangeKind, key, oldJson, newJson, actor, ct);
        await tx.CommitAsync(ct);
        return new TrustSettingChange(key, before, normalized, Changed: true);
    }

    private async Task<Dictionary<string, string>> ReadStoredAsync(CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT key, value::text FROM prem_config.setting WHERE tenant_id = @tenant AND key = ANY(@keys)");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("keys", Keys.ToArray());

        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            stored[reader.GetString(0)] = reader.GetString(1);
        return stored;
    }

    private static TrustSettingReading Read(string key, string? storedJson)
    {
        var (value, source) = storedJson is null
            ? (DefaultValue(key), TrustSettingSource.Default)
            : TryNormalize(key, StoredString(storedJson), out var normalized)
                ? (normalized, TrustSettingSource.Stored)
                : (StrictestValue(key), TrustSettingSource.Unreadable);
        return new TrustSettingReading(key, value, source, DefaultValue(key), storedJson, AllowedValues(key));
    }

    // Only a JSON string is a readable value. A number, an object, or text that
    // is not JSON at all reads as nothing, and so as the strictest value.
    private static string? StoredString(string? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseStale(string? text, out StaleVisibility stale)
    {
        var key = new string((text ?? "").Where(c => c is not ('-' or '_' or ' ')).ToArray()).ToLowerInvariant();
        (var ok, stale) = key switch
        {
            "showntopeopleonly" => (true, StaleVisibility.ShownToPeopleOnly),
            "hiddenfromeveryone" => (true, StaleVisibility.HiddenFromEveryone),
            "showntoeveryone" => (true, StaleVisibility.ShownToEveryone),
            _ => (false, StaleVisibility.HiddenFromEveryone),
        };
        return ok;
    }

    private static ArgumentException UnknownKey(string key) =>
        new($"There is no setting '{key}'. The settings are: {string.Join(", ", Keys)}.");
}
