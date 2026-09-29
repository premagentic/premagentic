using System.Text.Json;
using Premagentic.Core.Admin;
using Premagentic.Core.Identity;
using Premagentic.Core.Retrieval;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Evaluation;

/// <summary>Where the value of a tuning setting came from.</summary>
public enum TuningSettingSource
{
    /// <summary>No value is stored, so the code default applies.</summary>
    Default,

    /// <summary>A value is stored and is in force.</summary>
    Stored,

    /// <summary>A value is stored and cannot be used, so the code default applies.</summary>
    Unusable,
}

/// <summary>One retrieval setting as it stands.</summary>
/// <param name="Value">The value in force: the stored one when it can be used, otherwise the default.</param>
/// <param name="Stored">The stored value as written, or null when nothing is stored.</param>
/// <param name="Allowed">What the key accepts, as a phrase that completes "takes ...".</param>
/// <param name="Problem">Why a stored value cannot be used, or null.</param>
public sealed record TuningSettingReading(
    string Key,
    JsonElement Value,
    TuningSettingSource Source,
    JsonElement DefaultValue,
    JsonElement? Stored,
    string Allowed,
    string? Problem);

/// <summary>The golden set the evaluation runs.</summary>
/// <param name="Path">The stored path when it can be used, otherwise null.</param>
/// <param name="Problem">Why a stored value cannot be used, or null. Both are null when nothing is stored.</param>
public sealed record GoldenSetPathReading(string? Path, string? Problem);

/// <param name="Before">The stored value before, or null when there was none.</param>
/// <param name="After">The stored value after, or null when it was removed.</param>
/// <param name="Changed">False when nothing was written or recorded.</param>
public sealed record TuningSettingChange(string Key, JsonElement? Before, JsonElement? After, bool Changed);

/// <summary>
/// The settings an administrator tunes retrieval with and judges the tuning
/// by: the four keys of <see cref="RetrievalSettings"/>, and
/// <see cref="GoldenSetPath"/>, the golden question set the evaluation runs.
/// <para>
/// A change and its entry in the change record commit together, under the
/// tenant's administrator-change lock, so each entry has the true old value.
/// Setting a value equal to the stored one, or removing one that is not
/// stored, writes nothing and records nothing. A removed key is recorded as
/// <see cref="UnsetKind"/>, so a reader of the record never has to interpret
/// a missing new value.
/// </para>
/// <para>
/// The golden set path is set from the command line only. The server reads
/// whatever file it names, so the portal shows it and has no form for it.
/// </para>
/// </summary>
/// <param name="settings">
/// The settings this deployment defines, with the ones its loaded extensions
/// added, so an extension's key is set through the same checks and the same
/// change record. Null is the built-in settings alone.
/// </param>
public sealed class TuningSettingsStore(
    PremagenticDatabase db, Guid tenantId, TimeProvider? time = null, DeploymentSettings? settings = null)
{
    public const string GoldenSetPath = "evaluation.golden_set_path";

    /// <summary>The kind the change record gives a stored value, the same as a trust setting's.</summary>
    public const string SetKind = Okf.TrustSettingsStore.ChangeKind;

    /// <summary>The kind the change record gives a removed value.</summary>
    public const string UnsetKind = "setting.unset";

    /// <summary>
    /// Every key the portal's tuning page reads the history of: the retrieval
    /// settings, the golden set path and this deployment's own MCP tool text.
    /// Each is validated before it is written, so a value that could not
    /// be used is refused at the command rather than found at the next start.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } =
        [.. RetrievalSettings.Keys, GoldenSetPath, Mcp.McpToolDescriptions.Key];

    public static bool IsKey(string key) => Keys.Contains(key);

    /// <summary>
    /// True when <paramref name="path"/> may be stored as the golden set path:
    /// an absolute path. A relative one would resolve against the working
    /// directory, which differs between a service and a hand-run command.
    /// </summary>
    public static bool TryCheckGoldenSetPath(string? path, out string? problem)
    {
        problem = string.IsNullOrWhiteSpace(path)
            ? $"{GoldenSetPath} takes an absolute path to a golden question set file, not nothing."
            : !System.IO.Path.IsPathFullyQualified(path)
                ? $"{GoldenSetPath} takes an absolute path, not '{path}': a relative path would be read from whatever " +
                  "folder the reading process started in."
                : null;
        return problem is null;
    }

    /// <summary>The four retrieval settings, with where each value came from.</summary>
    public async Task<IReadOnlyList<TuningSettingReading>> ReadAllAsync(CancellationToken ct = default)
    {
        var stored = await new SettingsStore(db, tenantId).GetManyAsync(RetrievalSettings.Keys, ct);
        return RetrievalSettings.Keys.Select(k => Read(k, stored.TryGetValue(k, out var v) ? v : null)).ToArray();
    }

    /// <exception cref="ArgumentException">The key is not a retrieval setting.</exception>
    public async Task<TuningSettingReading> ReadAsync(string key, CancellationToken ct = default)
    {
        if (!RetrievalSettings.Keys.Contains(key)) throw UnknownKey(key);
        return Read(key, await new SettingsStore(db, tenantId).GetAsync(key, ct));
    }

    public async Task<GoldenSetPathReading> ReadGoldenSetPathAsync(CancellationToken ct = default) =>
        ReadGoldenSetPath(await new SettingsStore(db, tenantId).GetAsync(GoldenSetPath, ct));

    /// <summary>Stores a value and records the change, in one transaction.</summary>
    /// <exception cref="ArgumentException">
    /// The key is not a setting of <see cref="SettingsCatalog"/> outside the
    /// trust settings, or the value is not allowed for it. Nothing is written.
    /// The message says what is allowed.
    /// </exception>
    public async Task<TuningSettingChange> SetAsync(string key, JsonElement value, AdminActor actor, CancellationToken ct = default)
    {
        var definition = Settable(key);
        if (definition.Problem(value) is { } problem) throw new ArgumentException(problem);

        return await new AdminChanges(db, tenantId, time).RunAsync(actor, async change =>
        {
            var store = new SettingsStore(db, tenantId, change.Transaction, settings);
            var before = await store.GetAsync(key, ct);
            if (before is { } old && JsonElement.DeepEquals(old, value))
                return new TuningSettingChange(key, before, before, Changed: false);

            await store.SetAsync(key, value, ct);
            change.Record(SetKind, key, before, value);
            return new TuningSettingChange(key, before, value, Changed: true);
        }, ct);
    }

    /// <summary>Removes a stored value, so the default applies, and records the change, in one transaction.</summary>
    /// <exception cref="ArgumentException">
    /// The key is not a setting of <see cref="SettingsCatalog"/> outside the
    /// trust settings. Nothing is written.
    /// </exception>
    public async Task<TuningSettingChange> UnsetAsync(string key, AdminActor actor, CancellationToken ct = default)
    {
        Settable(key);

        return await new AdminChanges(db, tenantId, time).RunAsync(actor, async change =>
        {
            var store = new SettingsStore(db, tenantId, change.Transaction, settings);
            var before = await store.GetAsync(key, ct);
            if (before is null) return new TuningSettingChange(key, null, null, Changed: false);

            await store.RemoveAsync(key, ct);
            change.Record(UnsetKind, key, before, null);
            return new TuningSettingChange(key, before, null, Changed: true);
        }, ct);
    }

    private static TuningSettingReading Read(string key, JsonElement? stored)
    {
        var defaultValue = RetrievalSettings.DefaultValue(key);
        if (stored is not { } value)
            return new TuningSettingReading(key, defaultValue, TuningSettingSource.Default, defaultValue, null,
                RetrievalSettings.Allowed(key), null);
        return RetrievalSettings.TryParse(key, value, out var problem)
            ? new TuningSettingReading(key, value, TuningSettingSource.Stored, defaultValue, value, RetrievalSettings.Allowed(key), null)
            : new TuningSettingReading(key, defaultValue, TuningSettingSource.Unusable, defaultValue, value,
                RetrievalSettings.Allowed(key), problem);
    }

    private static GoldenSetPathReading ReadGoldenSetPath(JsonElement? stored)
    {
        if (stored is not { } value) return new GoldenSetPathReading(null, null);
        if (value.ValueKind != JsonValueKind.String)
            return new GoldenSetPathReading(null, $"{GoldenSetPath} takes a path as text, and the stored value is not text.");
        var path = value.GetString();
        return TryCheckGoldenSetPath(path, out var problem)
            ? new GoldenSetPathReading(path, null)
            : new GoldenSetPathReading(null, problem);
    }

    /// <summary>
    /// The definition of a key this store may write: every key the deployment
    /// defines except the trust settings, which have their own store.
    /// </summary>
    private SettingDefinition Settable(string key)
    {
        var defined = settings ?? DeploymentSettings.BuiltIn;
        return defined.Find(key) is { IsTrust: false } definition
            ? definition
            : throw new ArgumentException(
                $"There is no setting '{key}' to change here. The settings are: " +
                $"{string.Join(", ", defined.All.Where(d => !d.IsTrust).Select(d => d.Key))}.");
    }

    private static ArgumentException UnknownKey(string key) =>
        new($"There is no tuning setting '{key}'. The tuning settings are: {string.Join(", ", Keys)}.");
}
