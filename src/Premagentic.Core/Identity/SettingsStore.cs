using System.Text.Json;
using Premagentic.Core.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Premagentic.Core.Identity;

/// <summary>
/// Per-tenant settings an administrator can change, one JSON value per key.
/// Keys are lower-case letters, digits, dots and underscores, such as
/// <c>trust.stale</c>, and every key is one <see cref="SettingsCatalog"/>
/// defines. A key with no row has no setting, and the code that reads it keeps
/// its own safe default.
/// <para>
/// Keys under <c>trust.</c> cannot be changed here: they decide what agents and
/// people are served, so every change to them goes through the trust settings
/// store, which writes it to the change record with who made it, in the same
/// transaction.
/// </para>
/// </summary>
/// <param name="transaction">
/// A transaction the caller owns, for a change that must commit together with
/// something else, such as its entry in the change record. Every command then
/// runs on its connection, inside it, and nothing here begins, commits or rolls
/// back. Null runs each call on its own, as before.
/// </param>
/// <param name="settings">
/// The settings this deployment defines, with the ones its loaded extensions
/// added (<c>ExtensionHost.Settings</c>). Null is the built-in settings alone.
/// </param>
public sealed class SettingsStore(
    PremagenticDatabase db, Guid tenantId, NpgsqlTransaction? transaction = null, DeploymentSettings? settings = null)
{
    public async Task<JsonElement?> GetAsync(string key, CancellationToken ct = default)
    {
        RequireKey(key);
        await using var cmd = Command(
            "SELECT value::text FROM prem_config.setting WHERE tenant_id = @tenant AND key = @key");
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteScalarAsync(ct) is string json ? JsonDocument.Parse(json).RootElement.Clone() : null;
    }

    /// <summary>The stored values of <paramref name="keys"/>, in one read. A key with no row is absent.</summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> GetManyAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        foreach (var key in keys) RequireKey(key);
        await using var cmd = Command(
            "SELECT key, value::text FROM prem_config.setting WHERE tenant_id = @tenant AND key = ANY(@keys)");
        cmd.Parameters.AddWithValue("keys", keys.ToArray());

        var stored = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var doc = JsonDocument.Parse(reader.GetString(1));
            stored[reader.GetString(0)] = doc.RootElement.Clone();
        }
        return stored;
    }

    /// <summary>
    /// True for the store a command an extension adds is handed
    /// (<c>CommandContext.SettingsStore</c>). It refuses an <c>extensions.</c>
    /// key as it refuses a <c>trust.</c> one, since those decide what code this
    /// server loads, and it writes a value only when the key's definition
    /// takes it. A store made directly checks the key and nothing more.
    /// </summary>
    internal bool ForExtensionCommand { get; init; }

    /// <exception cref="InvalidOperationException">
    /// The key is a trust setting, or, for the store a command is handed, an extension setting.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The key is not defined, or, for the store a command is handed, its definition does not take the value.
    /// </exception>
    public async Task SetAsync(string key, JsonElement value, CancellationToken ct = default)
    {
        RequireKey(key);
        RefuseTrustKey(key);
        if (ForExtensionCommand)
        {
            RefuseExtensionsKey(key);
            if ((settings ?? DeploymentSettings.BuiltIn).Find(key)?.Problem(value) is { } problem)
                throw new ArgumentException(problem, nameof(value));
        }
        await using var cmd = Command("""
            INSERT INTO prem_config.setting(tenant_id, key, value) VALUES(@tenant, @key, @value)
            ON CONFLICT (tenant_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("value", NpgsqlDbType.Jsonb, value.GetRawText());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <returns>False when there was no setting to remove.</returns>
    /// <exception cref="InvalidOperationException">
    /// The key is a trust setting, or, for the store a command is handed, an extension setting.
    /// </exception>
    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        RequireKey(key);
        RefuseTrustKey(key);
        if (ForExtensionCommand) RefuseExtensionsKey(key);
        await using var cmd = Command(
            "DELETE FROM prem_config.setting WHERE tenant_id = @tenant AND key = @key");
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private NpgsqlCommand Command(string sql)
    {
        var cmd = transaction is null
            ? db.DataSource.CreateCommand(sql)
            : new NpgsqlCommand(sql, transaction.Connection, transaction);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        return cmd;
    }

    private static void RefuseTrustKey(string key)
    {
        if (key.StartsWith("trust.", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{key}' is a trust setting. Change it with 'prem settings set', which records who changed it and when.");
    }

    private static void RefuseExtensionsKey(string key)
    {
        if (key.StartsWith("extensions.", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"'{key}' decides what code this server loads, so a command an extension adds cannot change it. Change it with 'prem extensions'.");
    }

    /// <summary>True when <paramref name="key"/> has the shape of a setting key.</summary>
    public static bool IsKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.');

    private void RequireKey(string key)
    {
        if (!IsKey(key))
            throw new ArgumentException("A setting key is lower-case letters, digits, dots and underscores.", nameof(key));
        // Every key is defined in one place, so the commands that list, read and
        // change settings cannot miss one a store reads or writes. An
        // extension's key is defined only while the extension is loaded.
        if (!(settings ?? DeploymentSettings.BuiltIn).Contains(key))
            throw new ArgumentException(
                $"There is no setting '{key}'. A new setting is added to {nameof(SettingsCatalog)} first.", nameof(key));
    }
}
