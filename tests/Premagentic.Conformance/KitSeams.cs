using System.Reflection;
using Premagentic.Core.Extensions;

namespace Premagentic.Conformance;

/// <summary>
/// The seam versions this kit proves, written by its build from the
/// <see cref="SeamVersions"/> of the tree it was built from, as
/// <c>reader=2;chunker=1;source=1;embedding=1;signin=1;reminder=1</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class KitSeamsAttribute(string seams) : Attribute
{
    public string Seams { get; } = seams;
}

/// <summary>
/// The tie between this kit and the PremAgentic an extension builds against.
/// The kit's version is the product version it shipped with; the seams it
/// proves are in <see cref="KitSeamsAttribute"/>. Each fixture checks, when it
/// runs, that its seam is the one the extension's PremAgentic offers, so a
/// kit from one release cannot pass an extension built against another in
/// silence.
/// </summary>
public static class KitSeams
{
    /// <summary>The seam versions this kit proves, by seam name.</summary>
    public static IReadOnlyDictionary<string, int> Proven { get; } = Parse(
        typeof(KitSeams).Assembly.GetCustomAttribute<KitSeamsAttribute>()?.Seams
        ?? throw new InvalidOperationException("This kit carries no KitSeams attribute, so it cannot say which seams it proves."));

    /// <summary>This kit's version, the product version it was built with.</summary>
    public static string KitVersion { get; } =
        (typeof(KitSeams).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
        .Split('+')[0];

    /// <summary>
    /// The version of a seam that the PremAgentic loaded in this process
    /// offers, read from its metadata. A plain read of the constant would give
    /// the value compiled into this kit, which is the thing being checked.
    /// </summary>
    /// <param name="constant">The constant's name in <see cref="SeamVersions"/>, such as <c>Reader</c>.</param>
    public static int Offered(string constant) =>
        typeof(SeamVersions).GetField(constant, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() is int version
            ? version
            : throw new InvalidOperationException($"The PremAgentic this extension builds against has no seam constant {constant}.");

    /// <summary>
    /// Null when the kit proves the version offered, or the sentence to fail
    /// with when it does not.
    /// </summary>
    public static string? Mismatch(string seam, int proven, int offered, string kitVersion) =>
        proven == offered
            ? null
            : $"This kit, Premagentic.Conformance {kitVersion}, proves version {proven} of the \"{seam}\" seam, and the " +
              $"PremAgentic this extension builds against offers version {offered}. Use the kit packed from that " +
              $"PremAgentic, whose \"{seam}\" seam is version {offered}.";

    /// <summary>Fails the calling test when this kit does not prove the seam's version offered here.</summary>
    internal static void AssertProves(string seam, string constant)
    {
        Assert.True(Proven.TryGetValue(seam, out var proven), $"This kit does not prove the \"{seam}\" seam at all.");
        if (Mismatch(seam, proven, Offered(constant), KitVersion) is { } problem) Assert.Fail(problem);
    }

    internal static IReadOnlyDictionary<string, int> Parse(string seams) =>
        seams.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('='))
            .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
}
