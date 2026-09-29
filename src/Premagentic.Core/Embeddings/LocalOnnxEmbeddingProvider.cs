using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Premagentic.Core.Storage;

namespace Premagentic.Core.Embeddings;

/// <summary>
/// In-process sentence embeddings via ONNX Runtime: no external service, no
/// API key, fully offline. Expects a BERT-family export in a model directory
/// containing model.onnx and vocab.txt (scripts/download-model.ps1 fetches
/// them). An optional prem-model.json ({"name","pooling","dimensions"}) in
/// the directory identifies the model; without it the directory is assumed
/// to be the original all-MiniLM-L6-v2 setup (mean pooling, 384 dims).
/// Output is L2-normalized either way.
/// </summary>
public sealed class LocalOnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const int MaxTokens = 384; // model max is 512; headroom keeps memory flat
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _clsPooling;

    public string Name { get; }
    public int Dimensions { get; }

    /// <summary>The folder the model was loaded from, which the walk up from the executable may have found.</summary>
    public string Folder { get; }

    /// <summary>The model's revision as <c>prem-model.json</c> states it, or null when it does not.</summary>
    public string? Revision { get; }

    private sealed record ModelConfig(string Name, string Pooling, int Dimensions, string? Revision = null);

    /// <summary>
    /// What <c>prem-model.json</c> in <paramref name="modelDir"/> says the model
    /// is, or the original MiniLM setup when there is no such file. Reads the
    /// file only; nothing is loaded.
    /// </summary>
    internal static (string Name, string? Revision) Describe(string modelDir)
    {
        var config = ReadConfig(modelDir);
        return (config.Name, string.IsNullOrWhiteSpace(config.Revision) ? null : config.Revision);
    }

    private static ModelConfig ReadConfig(string modelDir)
    {
        var configPath = Path.Combine(modelDir, "prem-model.json");
        return File.Exists(configPath)
            ? JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? throw new InvalidOperationException($"Could not parse {configPath}.")
            : new ModelConfig("local:all-MiniLM-L6-v2", "mean", 384);
    }

    public LocalOnnxEmbeddingProvider(string modelDir) : this(modelDir, OnnxTelemetry.Disable)
    {
    }

    /// <summary>
    /// A provider whose first touch of the runtime is <paramref name="loadRuntime"/>,
    /// so a test can make that load fail without a broken machine.
    /// <para>
    /// A load that fails, of the runtime's native library, of the model file or
    /// of its vocabulary, refuses the start with one sentence that says what
    /// could not be loaded and what to do, the original kept as the inner
    /// exception. It is never
    /// retried and never replaced by another provider: without the model there
    /// is nothing to search with.
    /// </para>
    /// </summary>
    internal LocalOnnxEmbeddingProvider(string modelDir, Action loadRuntime)
    {
        // First, before anything can load the runtime: see OnnxTelemetry.
        try
        {
            loadRuntime();
        }
        catch (Exception ex) when (ModelLoadFailure.IsRuntimeLoadFailure(ex))
        {
            throw new StartupRefusedException(ModelLoadFailure.Sentence(ModelLoadStage.Runtime, ex, modelDir,
                AppContext.BaseDirectory, File.Exists, OperatingSystem.IsWindows()), ex);
        }

        var modelPath = Path.Combine(modelDir, "model.onnx");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
            throw new StartupRefusedException(
                $"The local embedding provider needs model.onnx and vocab.txt in '{modelDir}', and they are missing. " +
                "Run scripts/download-model.ps1 or scripts/download-model.sh first.",
                new FileNotFoundException("ONNX model files missing.", File.Exists(modelPath) ? vocabPath : modelPath));

        var config = ReadConfig(modelDir);

        Name = config.Name;
        Dimensions = config.Dimensions;
        Folder = Path.GetFullPath(modelDir);
        Revision = string.IsNullOrWhiteSpace(config.Revision) ? null : config.Revision;
        _clsPooling = config.Pooling.Equals("cls", StringComparison.OrdinalIgnoreCase);

        // No spin-waiting after each inference. The vector math that follows a
        // query embedding runs in this process on the same cores, and threads
        // left spinning would starve it.
        using var options = new SessionOptions();
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        try
        {
            _session = new InferenceSession(modelPath, options);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new StartupRefusedException(ModelLoadFailure.Sentence(ModelLoadStage.ModelFile, ex, modelDir,
                AppContext.BaseDirectory, File.Exists, OperatingSystem.IsWindows()), ex);
        }
        try
        {
            _tokenizer = BertTokenizer.Create(vocabPath);
        }
        catch (Exception ex) when (ModelLoadFailure.IsVocabularyLoadFailure(ex))
        {
            // No provider is made, so nothing else would free the loaded model.
            _session.Dispose();
            throw new StartupRefusedException(ModelLoadFailure.Sentence(ModelLoadStage.Vocabulary, ex, modelDir,
                AppContext.BaseDirectory, File.Exists, OperatingSystem.IsWindows()), ex);
        }
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            results[i] = EmbedOne(texts[i]);
        }
        return Task.FromResult(results);
    }

    private float[] EmbedOne(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, MaxTokens, out _, out _).ToArray();
        var seqLen = ids.Length;

        var inputIds = new DenseTensor<long>([1, seqLen]);
        var attentionMask = new DenseTensor<long>([1, seqLen]);
        var tokenTypeIds = new DenseTensor<long>([1, seqLen]);
        for (var t = 0; t < seqLen; t++)
        {
            inputIds[0, t] = ids[t];
            attentionMask[0, t] = 1;
            tokenTypeIds[0, t] = 0;
        }

        using var output = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        });

        // last_hidden_state: [1, seqLen, dims] → pool → L2 normalize.
        // CLS pooling (token 0) for bge-family models; mean pooling otherwise.
        var hidden = output.First().AsTensor<float>();
        var vec = new float[Dimensions];
        if (_clsPooling)
        {
            for (var d = 0; d < Dimensions; d++) vec[d] = hidden[0, 0, d];
        }
        else
        {
            for (var t = 0; t < seqLen; t++)
                for (var d = 0; d < Dimensions; d++)
                    vec[d] += hidden[0, t, d];
            for (var d = 0; d < Dimensions; d++) vec[d] /= seqLen;
        }

        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
            for (var d = 0; d < Dimensions; d++) vec[d] /= norm;
        return vec;
    }

    public void Dispose() => _session.Dispose();
}
