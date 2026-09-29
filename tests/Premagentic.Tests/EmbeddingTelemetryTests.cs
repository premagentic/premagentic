using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Tests that change the process environment. The runtime reads it when it
/// starts, so they run on their own, after every other test and never beside
/// one that could be starting it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "process environment";
}

/// <summary>
/// The local embedding provider switches off the ONNX Runtime's own telemetry
/// and creates the runtime's environment at log level ERROR before anything
/// else, so both hold even where the model files are missing and the provider
/// cannot be made.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class EmbeddingTelemetryTests
{
    [Fact]
    public void Making_the_local_provider_switches_the_runtime_telemetry_off_first()
    {
        Assert.Throws<StartupRefusedException>(() => new LocalOnnxEmbeddingProvider(Nowhere()));

        Assert.True(OnnxTelemetry.Disabled);
    }

    // Some hosts make the runtime warn while it creates its environment, before
    // a later setting could stop it; on standard error that warning would come
    // before every command's output and break the one line a refusal is.
    [Fact]
    public void Making_the_local_provider_creates_the_runtime_environment_at_log_level_error()
    {
        Assert.Throws<StartupRefusedException>(() => new LocalOnnxEmbeddingProvider(Nowhere()));

        Assert.True(OrtEnv.IsCreated);
        Assert.Equal(OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, OrtEnv.Instance().EnvLogLevel);
    }

    // On Linux and macOS .NET keeps a copy of the environment of its own, so a
    // value set only there would pass a managed read and still leave the
    // telemetry on; this reads what the runtime reads.
    [Fact]
    public void Making_the_local_provider_sets_the_runtime_variable_where_native_code_reads_it()
    {
        UnsetForNativeCode(OnnxTelemetry.Variable);
        Assert.Null(ReadAsNativeCode(OnnxTelemetry.Variable));

        Assert.Throws<StartupRefusedException>(() => new LocalOnnxEmbeddingProvider(Nowhere()));

        Assert.Equal("1", ReadAsNativeCode(OnnxTelemetry.Variable));
    }

    [Fact]
    public void A_value_the_operator_set_is_left_as_it_is()
    {
        try
        {
            SetForNativeCode(OnnxTelemetry.Variable, "0");

            Assert.Throws<StartupRefusedException>(() => new LocalOnnxEmbeddingProvider(Nowhere()));

            Assert.Equal("0", ReadAsNativeCode(OnnxTelemetry.Variable));
        }
        finally
        {
            SetForNativeCode(OnnxTelemetry.Variable, "1");
        }
    }

    private static string Nowhere() =>
        Path.Combine(Path.GetTempPath(), "premagentic-no-model-" + Guid.NewGuid().ToString("N"));

    private static string? ReadAsNativeCode(string name) => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable(name)
        : Marshal.PtrToStringUTF8(getenv(name));

    private static void SetForNativeCode(string name, string value)
    {
        if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable(name, value);
        else Assert.Equal(0, setenv(name, value, 1));
    }

    private static void UnsetForNativeCode(string name)
    {
        if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable(name, null);
        else Assert.Equal(0, unsetenv(name));
    }

    [DllImport("libc")]
    private static extern IntPtr getenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libc")]
    private static extern int setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

    [DllImport("libc")]
    private static extern int unsetenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
