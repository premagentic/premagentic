namespace Premagentic.Core.Storage;

/// <summary>
/// A configuration a host cannot start with: no database, a credentials file
/// that cannot be read, a retired or malformed setting. Its message is the one
/// sentence that says what to set, for the person reading the console or the
/// journal.
/// <para>
/// It is an <see cref="InvalidOperationException"/>, so code that already
/// catches that keeps working. A host turns it into the sentence and
/// <see cref="ExitCode"/> with <see cref="ExitCleanlyWhenUnhandled"/>, while a
/// test that starts the host in process sees the exception itself.
/// </para>
/// </summary>
public sealed class StartupRefusedException(string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    /// <summary>The exit code for a refusal: restarting would not help, so a service manager should not.</summary>
    public const int ExitCode = 2;

    private static int _installed;

    /// <summary>
    /// When a refusal reaches the top of the process unhandled, print
    /// "<paramref name="program"/> cannot start:" and the sentence to standard
    /// error, and exit with <see cref="ExitCode"/>, instead of the runtime's
    /// stack trace. Every other exception is left as it is. Called once, first
    /// thing in a host's entry point; later calls do nothing.
    /// </summary>
    public static void ExitCleanlyWhenUnhandled(string program)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is not StartupRefusedException refused) return;
            Console.Error.WriteLine($"{program} cannot start: {refused.Message}");
            Environment.Exit(ExitCode);
        };
    }
}
