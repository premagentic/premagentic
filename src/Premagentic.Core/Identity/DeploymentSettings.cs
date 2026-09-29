namespace Premagentic.Core.Identity;

/// <summary>A setting an extension added, and the extension that added it.</summary>
public sealed record ContributedSetting(string Extension, SettingDefinition Definition);

/// <summary>
/// Every setting this deployment defines: the built-in ones of
/// <see cref="SettingsCatalog"/>, then the ones the loaded extensions added.
/// The extension host makes one at startup (<c>ExtensionHost.Settings</c>),
/// and a store given it accepts an extension's keys while that extension is
/// loaded and refuses them once it is not.
/// <para>
/// An extension's definition says what its key means, what it takes, and what
/// applies when nothing is stored. Its <see cref="SettingDefinition.Problem"/>
/// is asked before a value is written by <c>prem settings</c>, by the tuning
/// settings store, and by the store a command an extension adds is handed; a
/// <see cref="SettingsStore"/> made directly checks only that the key is
/// defined, as it does for a built-in key. How the extension reads a value
/// that cannot be used is its own to decide, in the safe direction.
/// </para>
/// </summary>
public sealed class DeploymentSettings
{
    private readonly Dictionary<string, SettingDefinition> _byKey;
    private readonly Dictionary<string, string> _extensionOf;

    internal DeploymentSettings(IReadOnlyList<ContributedSetting> contributed)
    {
        Contributed = contributed;
        All = [.. SettingsCatalog.All, .. contributed.Select(c => c.Definition)];
        _byKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);
        _extensionOf = contributed.ToDictionary(c => c.Definition.Key, c => c.Extension, StringComparer.Ordinal);
    }

    /// <summary>The built-in settings alone, for a host that loads no extensions.</summary>
    public static DeploymentSettings BuiltIn { get; } = new([]);

    /// <summary>Every definition, the built-in ones first, then each extension's in the order they loaded.</summary>
    public IReadOnlyList<SettingDefinition> All { get; }

    /// <summary>The settings the loaded extensions added.</summary>
    public IReadOnlyList<ContributedSetting> Contributed { get; }

    public bool Contains(string key) => _byKey.ContainsKey(key);

    /// <summary>The definition of <paramref name="key"/>, or null when this deployment defines no such key.</summary>
    public SettingDefinition? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>The name of the extension that added <paramref name="key"/>, or null for a built-in key or none.</summary>
    public string? ExtensionOf(string key) => _extensionOf.GetValueOrDefault(key);
}
