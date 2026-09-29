using Premagentic.Core.Embeddings;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// The local model's folder, found by one rule for the command line and the
/// API: the variable, then models/minilm under the current folder, then under
/// the executable's folder and each parent. Laid out in a temporary tree that
/// stands in for a clone with the model downloaded at its root.
/// </summary>
public sealed class ModelFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("prem-model-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Model(params string[] under)
    {
        var folder = Folder([.. under, "models", "minilm"]);
        foreach (var file in ModelFolder.ModelFiles) File.WriteAllText(Path.Combine(folder, file), "invented");
        return folder;
    }

    [Fact]
    public void The_api_under_dotnet_run_finds_the_model_at_the_repository_root()
    {
        var atRoot = Model();
        var project = Folder("src", "Premagentic.Api");
        var output = Folder("src", "Premagentic.Api", "bin", "Release", "net10.0");

        var lookup = ModelFolder.Find(null, currentDirectory: project, executableDirectory: output);

        Assert.Equal(atRoot, lookup.Found);
        // The current folder is looked in first, then the walk up from the executable.
        Assert.Equal(Path.Combine(project, "models", "minilm"), lookup.LookedIn[0]);
        Assert.Equal(atRoot, lookup.LookedIn[^1]);
    }

    [Fact]
    public void The_current_folder_wins_over_a_parent_and_the_variable_wins_over_both()
    {
        Model();
        var here = Model("work");
        var output = Folder("bin");

        Assert.Equal(here, ModelFolder.Find(null, Path.Combine(_root, "work"), output).Found);
        Assert.Equal("/somewhere/else", ModelFolder.Find("/somewhere/else", Path.Combine(_root, "work"), output).Found);
    }

    [Fact]
    public void A_folder_with_only_one_of_the_files_does_not_count()
    {
        var half = Folder("models", "minilm");
        File.WriteAllText(Path.Combine(half, "model.onnx"), "invented");

        var lookup = ModelFolder.Find(null, _root, Folder("bin"));

        Assert.Null(lookup.Found);
        Assert.Contains(half, lookup.LookedIn);
    }

    [Fact]
    public void With_no_model_anywhere_the_refusal_names_every_folder_it_looked_in()
    {
        var project = Folder("src", "Premagentic.Api");
        var output = Folder("src", "Premagentic.Api", "bin");

        var lookup = ModelFolder.Find(null, project, output);
        var refused = Assert.Throws<StartupRefusedException>(() => EmbeddingProviderFactory.Local(lookup));

        Assert.Null(lookup.Found);
        Assert.Contains(Path.Combine(_root, "models", "minilm"), lookup.LookedIn);
        Assert.All(lookup.LookedIn, folder => Assert.Contains(folder, refused.Message));
        Assert.Contains(ModelFolder.Variable, refused.Message);
    }

    [Fact]
    public void The_refusal_says_where_a_release_carries_the_model_and_that_the_download_scripts_are_for_a_source_checkout()
    {
        // Someone who installed from a release archive has no repository and no
        // download script; the model shipped in the archive, beside bin/.
        var lookup = ModelFolder.Find(null, Folder("install", "work"), Folder("install", "bin"));
        var refused = Assert.Throws<StartupRefusedException>(() => EmbeddingProviderFactory.Local(lookup));

        Assert.Contains("An installed release carries the model in models/minilm beside bin/", refused.Message, StringComparison.Ordinal);
        Assert.Contains("From a source checkout, run scripts/download-model.sh or scripts/download-model.ps1", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Run scripts/download-model", refused.Message, StringComparison.Ordinal);
    }
}
