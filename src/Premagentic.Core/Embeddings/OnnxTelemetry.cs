using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Premagentic.Core.Embeddings;

/// <summary>
/// Switches off ONNX Runtime's own telemetry for this process, before the first
/// inference session exists. Premagentic makes no outbound call at runtime, and
/// the runtime is the one dependency that can report on its own: some builds of
/// its native library carry a telemetry client that uploads to its publisher
/// over HTTPS from a long-running process. Called by every provider that loads
/// the runtime, so no host and no operator has to remember it. It is also where
/// the runtime's environment is created, at log level ERROR.
/// </summary>
/// <remarks>
/// The switch is the <c>ORT_DISABLE_TELEMETRY</c> variable, which the runtime
/// reads when it starts. It is set to 1 unless the operator has set it already,
/// in the environment native code reads: on Linux and macOS that is not the one
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/> changes, which
/// is a copy kept by .NET, so there it is set through the C library. The
/// runtime's own API switch, <c>DisableTelemetryEvents</c>, is called as well,
/// as a second line only: on Linux it was measured not to stop the upload.
/// </remarks>
public static class OnnxTelemetry
{
    /// <summary>The variable the runtime reads when it starts.</summary>
    public const string Variable = "ORT_DISABLE_TELEMETRY";

    private static readonly Lock Gate = new();
    private static bool _disabled;

    /// <summary>True once this process has switched the runtime's telemetry off.</summary>
    public static bool Disabled => Volatile.Read(ref _disabled);

    public static void Disable()
    {
        // One caller at a time: a second one waits here until the environment
        // exists, instead of going on to a session that would create it at the
        // runtime's default level while the first is still creating it. Only a
        // call that got through marks it done, so after a runtime that could
        // not be loaded the next call meets the same failure, not a bare return.
        lock (Gate)
        {
            // Every time and first, before anything below can load the runtime.
            SetForNativeCodeUnlessSet(Variable, "1");
            if (_disabled) return;
            EnvironmentAtErrorLevel().DisableTelemetryEvents();
            Volatile.Write(ref _disabled, true);
        }
    }

    // The runtime's environment, created here at log level ERROR unless it
    // exists already. The level has to be given at creation: on some hosts the
    // runtime warns while it creates the environment (a PCI path it cannot
    // read, for one), too early for a later EnvLogLevel to stop, and the
    // warning would come before every command's output and break the one line
    // a refusal is. Its errors still reach the operator. An environment made
    // outside this class, between the check and the create, is taken as it is.
    private static OrtEnv EnvironmentAtErrorLevel()
    {
        if (OrtEnv.IsCreated) return OrtEnv.Instance();
        var options = new EnvironmentCreationOptions { logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        try
        {
            return OrtEnv.CreateInstanceWithOptions(ref options);
        }
        catch (OnnxRuntimeException) when (OrtEnv.IsCreated)
        {
            return OrtEnv.Instance();
        }
    }

    private static void SetForNativeCodeUnlessSet(string name, string value)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Environment.GetEnvironmentVariable(name) is null) Environment.SetEnvironmentVariable(name, value);
            return;
        }
        if (setenv(name, value, 0) != 0)
            throw new InvalidOperationException(
                $"Could not set {name} for the ONNX Runtime (errno {Marshal.GetLastPInvokeError()}), so its telemetry " +
                "could not be switched off. The local embedding provider does not start without it.");
    }

    // "libc" is the C library on Linux and macOS alike; .NET maps the name.
    // With overwrite 0, setenv leaves a value that is already there.
    [DllImport("libc", SetLastError = true)]
    private static extern int setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);
}
