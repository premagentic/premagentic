using Microsoft.ML.OnnxRuntime;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// A local embedding model that cannot be loaded refuses the start with one
/// sentence naming what could not be loaded and what to do. The broken shapes
/// are invented here: a runtime loader that throws what .NET throws, a model
/// file that is not a model, and a vocabulary without one of its special tokens
/// beside a model that loads. Nothing on the machine is touched.
/// </summary>
public sealed class ModelLoadFailureTests : IDisposable
{
    private const string Bin = @"C:\Program Files\Premagentic\bin";

    /// <summary>What .NET throws when ONNX Runtime's native library is missing on Windows.</summary>
    private static TypeInitializationException WindowsLoadFailure() =>
        new("Microsoft.ML.OnnxRuntime.NativeMethods", new DllNotFoundException(
            "Unable to load DLL 'onnxruntime' or one of its dependencies: The specified module could not be found. (0x8007007E)"));

    /// <summary>What .NET throws on Linux when a library the runtime links is missing: the loader's text, over several lines.</summary>
    private static TypeInitializationException LinuxLoadFailure() =>
        new("Microsoft.ML.OnnxRuntime.NativeMethods", new DllNotFoundException(
            "Unable to load shared library 'onnxruntime' or one of its dependencies. In order to help diagnose loading problems, " +
            "consider using a tool like strace. If you're using glibc, consider setting the LD_DEBUG environment variable: \n" +
            "libgomp.so.1: cannot open shared object file: No such file or directory\n" +
            "/opt/premagentic/bin/onnxruntime.so: cannot open shared object file: No such file or directory\n"));

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders)
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>A folder holding a model.onnx that is not a model and a vocabulary, for a real load that fails.</summary>
    private string DamagedModelFolder()
    {
        var folder = Directory.CreateTempSubdirectory("prem-damaged-model-").FullName;
        _folders.Add(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), "not a model, invented bytes"u8.ToArray());
        File.WriteAllText(Path.Combine(folder, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhangar\n");
        return folder;
    }

    /// <summary>A folder holding a model the runtime loads and the given vocabulary, so a load gets as far as the vocabulary.</summary>
    private string LoadableModelFolder(string vocabulary)
    {
        var folder = Directory.CreateTempSubdirectory("prem-vocabulary-").FullName;
        _folders.Add(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), IdentityOnnxModel.Bytes());
        File.WriteAllText(Path.Combine(folder, "vocab.txt"), vocabulary);
        return folder;
    }

    [Fact]
    public void On_windows_the_sentence_names_the_load_the_runtime_files_not_in_bin_and_the_reinstall_on_one_line()
    {
        var present = new[] { "onnxruntime.dll", "msvcp140_1.dll", "vcruntime140.dll" };
        var sentence = ModelLoadFailure.Sentence(ModelLoadStage.Runtime, WindowsLoadFailure(), @"C:\models", Bin,
            path => present.Contains(Path.GetFileName(path.Replace('\\', '/'))), isWindows: true);

        Assert.Equal(
            "The local embedding model could not be loaded, because ONNX Runtime's native library could not be: " +
            "Unable to load DLL 'onnxruntime' or one of its dependencies: The specified module could not be found. (0x8007007E). " +
            $"Not in {Bin}: msvcp140.dll, vcruntime140_1.dll. " +
            "Reinstall Premagentic from its archive: on Windows, bin\\ holds onnxruntime.dll and the four Visual C++ runtime files " +
            "msvcp140.dll, msvcp140_1.dll, vcruntime140.dll and vcruntime140_1.dll.",
            sentence);
        // The runtime's own wording for its type initializer is not what a person needs.
        Assert.DoesNotContain("type initializer", sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void On_windows_with_every_runtime_file_in_bin_no_file_is_named_missing()
    {
        var sentence = ModelLoadFailure.Sentence(ModelLoadStage.Runtime, WindowsLoadFailure(), @"C:\models", Bin, _ => true, isWindows: true);

        Assert.DoesNotContain("Not in", sentence, StringComparison.Ordinal);
        Assert.Contains("Reinstall Premagentic from its archive", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void On_linux_the_sentence_carries_the_loaders_own_text_naming_the_library_on_one_line()
    {
        var sentence = ModelLoadFailure.Sentence(ModelLoadStage.Runtime, LinuxLoadFailure(), "/opt/premagentic/models/minilm",
            "/opt/premagentic/bin/", _ => false, isWindows: false);

        Assert.Contains("ONNX Runtime's native library could not be: Unable to load shared library 'onnxruntime'", sentence, StringComparison.Ordinal);
        Assert.Contains("libgomp.so.1: cannot open shared object file: No such file or directory", sentence, StringComparison.Ordinal);
        Assert.Contains("Reinstall Premagentic from its archive, whose bin/ holds libonnxruntime.so", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("Not in", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void For_the_model_file_the_sentence_names_its_folder_and_how_to_get_it_back()
    {
        var sentence = ModelLoadFailure.Sentence(ModelLoadStage.ModelFile, new InvalidDataException("Protobuf parsing failed."),
            "/opt/premagentic/models/minilm", "/opt/premagentic/bin/", _ => true, isWindows: false);

        Assert.Equal(
            "The local embedding model in /opt/premagentic/models/minilm could not be loaded: Protobuf parsing failed. The model " +
            "file is damaged or is not the model: reinstall Premagentic from its archive, or in a source checkout run " +
            "scripts/download-model.sh or scripts/download-model.ps1 again.",
            sentence);
    }

    [Fact]
    public void For_the_vocabulary_the_sentence_names_the_file_its_folder_and_how_to_get_it_back()
    {
        var sentence = ModelLoadFailure.Sentence(ModelLoadStage.Vocabulary,
            new ArgumentException("The special token '[MASK]' is not in the vocabulary."),
            "/opt/premagentic/models/minilm", "/opt/premagentic/bin/", _ => true, isWindows: false);

        Assert.Equal(
            "The local embedding model in /opt/premagentic/models/minilm could not be loaded, because its vocabulary, vocab.txt, " +
            "could not be: The special token '[MASK]' is not in the vocabulary. The vocabulary is damaged or is not the model's: " +
            "reinstall Premagentic from its archive, or in a source checkout run scripts/download-model.sh or " +
            "scripts/download-model.ps1 again.",
            sentence);
    }

    [Fact]
    public void Only_the_vocabularys_load_failures_count_as_one()
    {
        Assert.True(ModelLoadFailure.IsVocabularyLoadFailure(new ArgumentException("The special token '[MASK]' is not in the vocabulary.")));
        Assert.True(ModelLoadFailure.IsVocabularyLoadFailure(new IOException()));
        Assert.True(ModelLoadFailure.IsVocabularyLoadFailure(new UnauthorizedAccessException()));
        Assert.False(ModelLoadFailure.IsVocabularyLoadFailure(new InvalidOperationException()));
    }

    [Fact]
    public void Only_the_runtimes_load_failures_count_as_one()
    {
        Assert.True(ModelLoadFailure.IsRuntimeLoadFailure(WindowsLoadFailure()));
        Assert.True(ModelLoadFailure.IsRuntimeLoadFailure(new DllNotFoundException()));
        Assert.True(ModelLoadFailure.IsRuntimeLoadFailure(new BadImageFormatException()));
        Assert.True(ModelLoadFailure.IsRuntimeLoadFailure(new EntryPointNotFoundException()));
        Assert.False(ModelLoadFailure.IsRuntimeLoadFailure(new InvalidOperationException()));
    }

    [Fact]
    public void A_runtime_that_cannot_be_loaded_refuses_the_start_with_the_sentence_and_keeps_the_original()
    {
        var failure = WindowsLoadFailure();

        var refused = Assert.Throws<StartupRefusedException>(() => new LocalOnnxEmbeddingProvider("unused", () => throw failure));

        Assert.StartsWith("The local embedding model could not be loaded, because ONNX Runtime's native library could not be: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Unable to load DLL 'onnxruntime'", refused.Message, StringComparison.Ordinal);
        Assert.Same(failure, refused.InnerException);
    }

    [Fact]
    public void A_model_file_that_is_not_a_model_refuses_the_start_with_the_sentence_and_keeps_the_runtimes_error()
    {
        var folder = DamagedModelFolder();

        var refused = Assert.Throws<StartupRefusedException>(() => new LocalOnnxEmbeddingProvider(folder));

        Assert.StartsWith($"The local embedding model in {folder} could not be loaded: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("The model file is damaged or is not the model", refused.Message, StringComparison.Ordinal);
        Assert.IsType<OnnxRuntimeException>(refused.InnerException);
    }

    [Fact]
    public void A_vocabulary_without_a_special_token_refuses_the_start_with_the_sentence_and_keeps_the_tokenizers_error()
    {
        var folder = LoadableModelFolder("[PAD]\n[UNK]\n[CLS]\n[SEP]\nhangar\n");

        var refused = Assert.Throws<StartupRefusedException>(() => new LocalOnnxEmbeddingProvider(folder));

        Assert.StartsWith($"The local embedding model in {folder} could not be loaded, because its vocabulary, vocab.txt, could not be: ",
            refused.Message, StringComparison.Ordinal);
        Assert.Contains("[MASK]", refused.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<ArgumentException>(refused.InnerException);
    }

    // The control: the same model with every special token in its vocabulary
    // is made, so the refusal above is the vocabulary's and not the model's.
    [Fact]
    public void The_same_model_with_a_whole_vocabulary_is_made()
    {
        var folder = LoadableModelFolder("[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhangar\n");

        using var provider = new LocalOnnxEmbeddingProvider(folder);

        Assert.Equal(Path.GetFullPath(folder), provider.Folder);
    }
}
