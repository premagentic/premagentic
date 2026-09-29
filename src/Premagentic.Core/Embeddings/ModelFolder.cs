namespace Premagentic.Core.Embeddings;

/// <summary>Where the local model was found, and every folder looked in to find it.</summary>
/// <param name="Found">The folder holding the model files, or null when none does.</param>
/// <param name="LookedIn">Every folder looked in, in order, so a refusal can name them all.</param>
public sealed record ModelFolderLookup(string? Found, IReadOnlyList<string> LookedIn);

/// <summary>
/// The one rule the command line and the API use for the local model's
/// folder. <c>PREM_ONNX_MODEL_DIR</c> wins when it is set, and is used as it
/// is. Otherwise the default is <c>models/minilm</c> under the current folder,
/// then under the executable's folder and each of its parents, stopping at
/// the first that holds the model files.
/// <para>
/// The walk up from the executable is what finds the model the quick start
/// downloaded at the repository root when the API runs under <c>dotnet run</c>,
/// whose current folder is the project's own, and it finds the same folder
/// for an installed service, whose model sits beside the executable.
/// </para>
/// </summary>
public static class ModelFolder
{
    public const string Variable = "PREM_ONNX_MODEL_DIR";

    /// <summary>The files a folder must hold to be the model's.</summary>
    public static IReadOnlyList<string> ModelFiles { get; } = ["model.onnx", "vocab.txt"];

    private static readonly string[] Default = ["models", "minilm"];

    /// <summary>The lookup for this process.</summary>
    public static ModelFolderLookup FromEnvironment() =>
        Find(Environment.GetEnvironmentVariable(Variable), Directory.GetCurrentDirectory(), AppContext.BaseDirectory);

    /// <summary>The lookup given the variable, the current folder and the executable's folder.</summary>
    public static ModelFolderLookup Find(string? variable, string currentDirectory, string executableDirectory)
    {
        if (!string.IsNullOrWhiteSpace(variable)) return new ModelFolderLookup(variable, [variable]);

        var looked = new List<string>();
        foreach (var candidate in Candidates(currentDirectory, executableDirectory))
        {
            if (looked.Contains(candidate, PathComparer)) continue;
            looked.Add(candidate);
            if (Holds(candidate)) return new ModelFolderLookup(candidate, looked);
        }
        return new ModelFolderLookup(null, looked);
    }

    /// <summary>True when <paramref name="folder"/> holds every one of <see cref="ModelFiles"/>.</summary>
    public static bool Holds(string folder) => ModelFiles.All(f => File.Exists(Path.Combine(folder, f)));

    private static IEnumerable<string> Candidates(string currentDirectory, string executableDirectory)
    {
        yield return Under(currentDirectory);
        for (var folder = new DirectoryInfo(Path.GetFullPath(executableDirectory)); folder is not null; folder = folder.Parent)
            yield return Under(folder.FullName);
    }

    private static string Under(string folder) => Path.GetFullPath(Path.Combine([folder, .. Default]));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
