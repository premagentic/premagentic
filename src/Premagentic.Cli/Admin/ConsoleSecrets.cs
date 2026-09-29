using System.Text;

namespace Premagentic.Cli.Admin;

/// <summary>
/// Reads passwords and tokens without taking them as arguments, where a shell
/// history or a process list would keep them. From standard input when it is
/// redirected (the first line), otherwise from a prompt that does not echo;
/// or, where a command offers it, from a file.
/// </summary>
internal static class ConsoleSecrets
{
    /// <summary>
    /// A secret from the first line of a file, without its line ending. Nothing
    /// else is trimmed, since a space is part of a password, and a UTF-8 byte
    /// order mark is skipped. The file is only read, never written, moved or
    /// deleted, and a refusal never quotes what it holds.
    /// </summary>
    /// <param name="aloneOnItsLine">
    /// True refuses a file with anything after its first line and its line
    /// ending: a second line most likely means the wrong file. False takes the
    /// first line and ignores the rest, as setup's <c>--admin-password-file</c>
    /// is documented to.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The file cannot be read, holds no password on its first line, or, when
    /// <paramref name="aloneOnItsLine"/>, holds more than one line.
    /// </exception>
    public static string ReadFile(string path, bool aloneOnItsLine)
    {
        string? first;
        bool more;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            first = reader.ReadLine();
            more = reader.Peek() >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Cannot read the password file {path}: {ex.GetType().Name}.");
        }
        if (string.IsNullOrEmpty(first))
            throw new ArgumentException($"The password file {path} holds no password on its first line.");
        if (aloneOnItsLine && more)
            throw new ArgumentException($"The password file {path} holds more than one line. Put the password alone in the file.");
        return first;
    }

    public static string? Read(string prompt, bool confirm = false)
    {
        if (Console.IsInputRedirected)
            return Console.In.ReadLine();

        var first = Prompt(prompt);
        if (!confirm) return first;
        var second = Prompt("Again: ");
        if (first != second)
        {
            Console.Error.WriteLine("The two entries differ.");
            return null;
        }
        return first;
    }

    private static string Prompt(string prompt)
    {
        Console.Error.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }
}
