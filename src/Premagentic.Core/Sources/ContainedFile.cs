using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Premagentic.Core.Ingestion.Readers;

namespace Premagentic.Core.Sources;

/// <summary>
/// Opens a file a folder source listed, and keeps it only when what was
/// opened is a regular file whose final path is exactly the one listed: the
/// source's root, as the system resolves it, joined with the listed relative
/// path. The listing leaves links and junctions out, but a link can be put in
/// a file's place, or in a folder's, between the listing and the open, and
/// opening follows it; so the check is made on the handle, after the open,
/// where it describes the very file that would be read. A link that stays
/// inside the root is refused too: who may read a document is decided from
/// its listed path, so a link from one folder to another inside the same
/// source would otherwise index the second folder's text under the first
/// folder's rule.
/// <para>
/// On Windows the final path is the handle's own
/// (<c>GetFinalPathNameByHandle</c>), which resolves junctions, symbolic links
/// and object-manager links alike, and a regular file is one of type disk. On
/// Linux the file is opened without blocking, so a pipe cannot hold the run,
/// its final path is the link <c>/proc/self/fd/N</c>, and a regular file is
/// one that can seek. Elsewhere the file is opened as it was listed, with the
/// listing's check alone.
/// </para>
/// </summary>
internal static class ContainedFile
{
    /// <summary>Why a file whose final path is not the listed one was not read.</summary>
    public const string NotAsListed =
        "it resolves through a link or a junction to a file other than the one listed, so it was not read";

    /// <summary>Why a pipe, a device or anything else that is not a regular file was not read.</summary>
    public const string NotRegular =
        "it is not a regular file (a pipe, a device or a socket), so it was not read";

    /// <summary>Whether this platform checks the handle after the open.</summary>
    public static bool Verifies => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// The root as the operating system resolves it, links and junctions
    /// followed, to compare final paths with; null where this platform does
    /// not check.
    /// </summary>
    /// <exception cref="IOException">The root cannot be opened to ask.</exception>
    public static string? FinalRoot(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            // Opened as .NET opens a path itself: with the long-path prefix,
            // so a folder of any length opens, and anonymous as the security
            // quality of service, so a pipe reached by the name cannot act as
            // this account.
            using var handle = CreateFileW(Extended(Path.GetFullPath(root)), FileReadAttributes,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                FileFlagBackupSemantics | SecuritySqosPresent | SecurityAnonymous, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException($"The source folder {root} could not be opened to resolve it: {LastError()}");
            return WindowsFinalPath(handle);
        }
        if (OperatingSystem.IsLinux())
        {
            using var handle = LinuxOpen(Path.GetFullPath(root), ORdOnly | OCloExec);
            return LinuxFinalPath(handle)
                ?? throw new IOException($"The source folder {root} could not be resolved through /proc/self/fd.");
        }
        return null;
    }

    /// <summary>
    /// The file, open for reading, when it is a regular file whose final path
    /// is <paramref name="finalRoot"/> joined with <paramref name="relative"/>.
    /// </summary>
    /// <param name="file">The path the listing gave.</param>
    /// <param name="finalRoot">What <see cref="FinalRoot"/> returned for the source's root.</param>
    /// <param name="relative">The listed path relative to the root, with the platform's separators.</param>
    /// <exception cref="UnreadableDocumentException">
    /// It is not a regular file, or its final path is not the listed one. The
    /// reading step records the path as unreadable with this reason and keeps
    /// its earlier index entry.
    /// </exception>
    /// <exception cref="IOException">It could not be opened.</exception>
    public static Stream Open(string file, string? finalRoot, string relative)
    {
        if (finalRoot is null || !Verifies) return File.OpenRead(file);

        if (OperatingSystem.IsWindows())
        {
            SafeFileHandle handle;
            try
            {
                handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (NotSupportedException)
            {
                // .NET may refuse a device path itself, before the check
                // below; the answer is the same.
                throw new UnreadableDocumentException(NotRegular);
            }
            try
            {
                if (GetFileType(handle) != FileTypeDisk) throw new UnreadableDocumentException(NotRegular);
                if (!IsListed(WindowsFinalPath(handle), finalRoot, relative)) throw new UnreadableDocumentException(NotAsListed);
                return new FileStream(handle, FileAccess.Read);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        // Linux. Opened without blocking, so a pipe is refused here instead of
        // waiting for a writer forever while the run holds the rules lock. A
        // pipe cannot seek; a socket cannot be opened at all; a device can be
        // made inside the folder only by root, and one reached through a link
        // is not the listed file, which the path check refuses.
        var opened = LinuxOpen(file, ORdOnly | ONonBlock | OCloExec);
        try
        {
            var stream = new FileStream(opened, FileAccess.Read);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw new UnreadableDocumentException(NotRegular);
            }
            if (LinuxFinalPath(opened) is not { } final || !IsListed(final, finalRoot, relative))
            {
                stream.Dispose();
                throw new UnreadableDocumentException(NotAsListed);
            }
            // The shared lock .NET takes when it opens a file to read, so a
            // file another program holds exclusively is unreadable here as it
            // is to File.OpenRead. The lock is advisory, as .NET's is: only
            // the answer "held by someone else" refuses the file.
            if (flock((int)opened.DangerousGetHandle(), LockShared | LockNonBlocking) != 0 && Marshal.GetLastPInvokeError() == EWouldBlock)
            {
                stream.Dispose();
                throw new IOException($"The process cannot access the file '{file}' because it is being used by another process.");
            }
            return stream;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when <paramref name="final"/> is exactly the root joined with the
    /// listed relative path, compared as the platform compares paths: ignoring
    /// case on Windows, exactly on Linux. The listing skips links and
    /// junctions, so a file opened through none has exactly this path, and a
    /// file anywhere else, inside the root or outside it, does not.
    /// </summary>
    internal static bool IsListed(string final, string finalRoot, string relative)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(final, Path.Join(finalRoot, relative), comparison);
    }

    // --- Windows ---

    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint SecuritySqosPresent = 0x00100000;
    private const uint SecurityAnonymous = 0;
    private const uint FileTypeDisk = 1;

    /// <summary>A full path with the prefix that lifts the length limit: \\?\, or \\?\UNC\ for a share.</summary>
    private static string Extended(string full) =>
        full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ? full
        : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
        : @"\\?\" + full;

    private static string WindowsFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) throw new IOException($"The final path of an opened file could not be read: {LastError()}");
            if (length < buffer.Length) return Plain(new string(buffer, 0, (int)length));
            buffer = new char[length + 1];
        }
    }

    /// <summary>The path without the \\?\ prefix the call gives, a share's as \\server\share.</summary>
    private static string Plain(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    private static string LastError() => Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    // --- Linux ---

    // The same values on every Linux architecture .NET runs on (the generic
    // fcntl.h, sys/file.h and errno.h ones, which x86-64 and arm64 share).
    private const int ORdOnly = 0;
    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;
    private const int LockShared = 1;
    private const int LockNonBlocking = 4;
    private const int EWouldBlock = 11;

    private static SafeFileHandle LinuxOpen(string path, int flags)
    {
        var fd = open(path, flags);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new IOException($"{path} could not be opened: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    /// <summary>The path the kernel holds for an open descriptor, or null when it cannot be read.</summary>
    private static string? LinuxFinalPath(SafeFileHandle handle)
    {
        var descriptor = handle.DangerousGetHandle().ToInt64();
        try
        {
            return new FileInfo($"/proc/self/fd/{descriptor}").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // "libc" is the C library on Linux; .NET maps the name.
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);
}
