namespace Premagentic.Core.Embeddings;

/// <summary>Which load of the local embedding model failed.</summary>
internal enum ModelLoadStage
{
    /// <summary>ONNX Runtime's native library, loaded the first time anything in the process touches the runtime.</summary>
    Runtime,

    /// <summary>The model file itself, read by the runtime.</summary>
    ModelFile,

    /// <summary>The model's vocabulary, vocab.txt, read by the tokenizer.</summary>
    Vocabulary,
}

/// <summary>
/// The one sentence a failed load of the local embedding model is refused
/// with: what could not be loaded, as the innermost exception says it, on one
/// line, and what to do. Pure, so each shape is tested without a broken
/// machine. It carries library names and paths only, never a secret.
/// </summary>
internal static class ModelLoadFailure
{
    /// <summary>
    /// What a Windows release carries in <c>bin\</c> for the runtime to load:
    /// the runtime itself and the Visual C++ runtime it links. Windows's own
    /// message names none of them, so the ones not there are named.
    /// </summary>
    internal static readonly string[] WindowsRuntimeFiles =
        ["onnxruntime.dll", "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    /// <summary>
    /// Whether an exception from the runtime's first load is the runtime
    /// failing to load: its type initializer throwing, or the library missing,
    /// built for another platform, or of another version.
    /// </summary>
    internal static bool IsRuntimeLoadFailure(Exception ex) =>
        ex is TypeInitializationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;

    /// <summary>
    /// Whether an exception from reading the vocabulary is the vocabulary
    /// failing to load: the tokenizer refusing it (empty, or without one of
    /// its special tokens, which it reports as an argument), or the file not
    /// being readable.
    /// </summary>
    internal static bool IsVocabularyLoadFailure(Exception ex) =>
        ex is ArgumentException or IOException or UnauthorizedAccessException;

    /// <param name="binDir">The folder the program runs from, where a release keeps the runtime's files.</param>
    /// <param name="fileExists">Whether a file is there; a test passes its own.</param>
    internal static string Sentence(
        ModelLoadStage stage, Exception ex, string modelDir, string binDir, Func<string, bool> fileExists, bool isWindows)
    {
        var cause = OneLine(Innermost(ex).Message);
        if (stage == ModelLoadStage.ModelFile)
            return $"The local embedding model in {modelDir} could not be loaded: {cause}. The model file is damaged or is not " +
                   "the model: reinstall Premagentic from its archive, or in a source checkout run scripts/download-model.sh " +
                   "or scripts/download-model.ps1 again.";

        if (stage == ModelLoadStage.Vocabulary)
            return $"The local embedding model in {modelDir} could not be loaded, because its vocabulary, vocab.txt, could " +
                   $"not be: {cause}. The vocabulary is damaged or is not the model's: reinstall Premagentic from its archive, " +
                   "or in a source checkout run scripts/download-model.sh or scripts/download-model.ps1 again.";

        if (!isWindows)
            return $"The local embedding model could not be loaded, because ONNX Runtime's native library could not be: {cause}. " +
                   "Reinstall Premagentic from its archive, whose bin/ holds libonnxruntime.so; a library of the system's " +
                   "that the message names is installed with the system's package manager.";

        var missing = WindowsRuntimeFiles.Where(file => !fileExists(Path.Combine(binDir, file))).ToArray();
        return $"The local embedding model could not be loaded, because ONNX Runtime's native library could not be: {cause}. " +
               (missing.Length > 0 ? $"Not in {binDir}: {string.Join(", ", missing)}. " : "") +
               "Reinstall Premagentic from its archive: on Windows, bin\\ holds onnxruntime.dll and the four Visual C++ " +
               "runtime files msvcp140.dll, msvcp140_1.dll, vcruntime140.dll and vcruntime140_1.dll.";
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }

    /// <summary>A message on one line: a loader's text can run over several, listing every path it tried.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
}
