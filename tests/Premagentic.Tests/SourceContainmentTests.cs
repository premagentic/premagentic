using System.Diagnostics;
using System.IO.Pipes;
using Premagentic.Core;
using Premagentic.Core.Ingestion.Readers;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;

namespace Premagentic.Tests;

/// <summary>A case that needs Windows, skipped with its reason elsewhere, where an early return would count as a pass.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "This case uses a Windows junction or pipe; the Linux cases prove the same check there.";
    }
}

/// <summary>A case that needs Linux, skipped with its reason elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "This case uses a Linux symbolic link or pipe; the Windows cases prove the same check there.";
    }
}

/// <summary>
/// A case that makes a symbolic link to a file on Windows, which needs
/// Developer Mode or the right to create one; skipped with that reason where
/// this account has neither.
/// </summary>
public sealed class WindowsFileLinkFactAttribute : FactAttribute
{
    public WindowsFileLinkFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "This case uses a Windows file link; the Linux cases prove the same check there.";
        else if (!SourceContainmentTests.CanLinkAFile())
            Skip = "This Windows account may not create a symbolic link to a file (no Developer Mode and no " +
                   "SeCreateSymbolicLinkPrivilege), so the file-link case runs where it may; the junction case runs here.";
    }
}

/// <summary>
/// A file a folder source listed is read only when what is opened is a regular
/// file at exactly the listed path: a link or a junction put in its place, or
/// in its folder's, after the listing, is refused and named in the run's
/// record, wherever it leads, and a pipe is refused without waiting for a
/// writer. No database.
/// </summary>
public sealed class SourceContainmentTests
{
    private const string Secret = "OUTSIDE-THE-ROOT-SECRET";

    /// <summary>A root with docs/a.md and b.md inside it, and a folder outside it holding a secret under the same names.</summary>
    private static (string Root, string Outside) World()
    {
        var parent = Directory.CreateTempSubdirectory("prem-contain-").FullName;
        var root = Directory.CreateDirectory(Path.Combine(parent, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(parent, "outside")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "a.md"), "# A\n\nInside the root.\n");
        File.WriteAllText(Path.Combine(root, "b.md"), "# B\n\nInside the root.\n");
        Directory.CreateDirectory(Path.Combine(outside, "docs"));
        File.WriteAllText(Path.Combine(outside, "docs", "a.md"), $"# Secret\n\n{Secret}\n");
        File.WriteAllText(Path.Combine(outside, "secret.md"), $"# Secret\n\n{Secret}\n");
        return (root, outside);
    }

    private const string HrOnly = "HR-ONLY-TEXT";

    /// <summary>A root with public/sub/a.md and hr/a.md, whose text only HR may read.</summary>
    private static string PublicAndHr()
    {
        var root = Directory.CreateTempSubdirectory("prem-contain-inside-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "public", "sub"));
        Directory.CreateDirectory(Path.Combine(root, "hr"));
        File.WriteAllText(Path.Combine(root, "public", "sub", "a.md"), "# A\n\nPublic.\n");
        File.WriteAllText(Path.Combine(root, "hr", "a.md"), $"# HR\n\n{HrOnly}\n");
        return root;
    }

    /// <summary>
    /// A folder beside the root whose name is the root's with "2" after it,
    /// holding a secret, so a check that compares only the start of a path
    /// takes it for the root.
    /// </summary>
    private static string Sibling(string root)
    {
        var sibling = Directory.CreateDirectory(root + "2").FullName;
        Directory.CreateDirectory(Path.Combine(sibling, "docs"));
        File.WriteAllText(Path.Combine(sibling, "docs", "a.md"), $"# Secret\n\n{Secret}\n");
        File.WriteAllText(Path.Combine(sibling, "secret.md"), $"# Secret\n\n{Secret}\n");
        return sibling;
    }

    /// <summary>The source's whole listing, finished before anything is swapped, so the swap comes after it.</summary>
    private static async Task<List<SourceRead>> ListAsync(string root)
    {
        var listed = new List<SourceRead>();
        await foreach (var found in new FileSystemSource(root, DocumentAccess.Everyone).EnumerateAsync()) listed.Add(found);
        return listed;
    }

    private static Task<SourceRead> ReadAsync(List<SourceRead> listed, string path) =>
        DocumentSourceReading.ReadAsync(listed.Single(r => r.Content?.Path == path).Content!, ReaderRegistry.BuiltIn);

    private static void AssertRefused(SourceRead read, string reason)
    {
        Assert.Null(read.Document);
        var failure = Assert.IsType<SourceFailure>(read.Failure);
        Assert.Equal(reason, failure.Reason);
        Assert.DoesNotContain(Secret, failure.Reason);
    }

    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var said = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /J failed: " + said);
    }

    [WindowsFact]
    public async Task Windows_a_junction_swapped_in_for_a_folder_after_the_listing_is_refused()
    {
        var (root, outside) = World();
        var listed = await ListAsync(root);

        Directory.Delete(Path.Combine(root, "docs"), recursive: true);
        Junction(Path.Combine(root, "docs"), Path.Combine(outside, "docs"));

        AssertRefused(await ReadAsync(listed, "docs/a.md"), ContainedFile.NotAsListed);
        // The control: the file beside it, never swapped, is read.
        Assert.NotNull((await ReadAsync(listed, "b.md")).Document);
    }

    [WindowsFact]
    public async Task Windows_a_junction_to_another_folder_inside_the_root_is_refused()
    {
        // Who may read a document is decided from its listed path, so a
        // junction from public/sub to hr would put HR's text under public's
        // rule: a file other than the listed one is refused even inside the root.
        var root = PublicAndHr();
        var listed = await ListAsync(root);

        Directory.Delete(Path.Combine(root, "public", "sub"), recursive: true);
        Junction(Path.Combine(root, "public", "sub"), Path.Combine(root, "hr"));

        AssertRefused(await ReadAsync(listed, "public/sub/a.md"), ContainedFile.NotAsListed);
        // The control: the same file, at the path it was listed under, is read.
        Assert.Contains(HrOnly, (await ReadAsync(listed, "hr/a.md")).Document!.Text);
    }

    [WindowsFact]
    public async Task Windows_a_junction_to_a_sibling_folder_whose_name_begins_with_the_roots_is_refused()
    {
        var (root, _) = World();
        var sibling = Sibling(root);
        var listed = await ListAsync(root);

        Directory.Delete(Path.Combine(root, "docs"), recursive: true);
        Junction(Path.Combine(root, "docs"), Path.Combine(sibling, "docs"));

        AssertRefused(await ReadAsync(listed, "docs/a.md"), ContainedFile.NotAsListed);
    }

    [WindowsFact]
    public async Task Windows_a_folder_whose_path_is_longer_than_260_characters_is_read()
    {
        // Without the long-path prefix the root cannot be opened to resolve it
        // where long paths are not switched on for the whole system.
        var root = Directory.CreateTempSubdirectory("prem-contain-long-").FullName;
        while (root.Length <= 300) root = Path.Combine(root, new string('d', 40));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.md"), "# A\n\nFar down.\n");

        var read = await ReadAsync(await ListAsync(root), "a.md");

        Assert.Null(read.Failure);
        Assert.Contains("Far down.", read.Document!.Text);
    }

    [WindowsFileLinkFact]
    public async Task Windows_a_file_link_swapped_in_after_the_listing_is_refused()
    {
        var (root, outside) = World();
        var listed = await ListAsync(root);

        File.Delete(Path.Combine(root, "b.md"));
        File.CreateSymbolicLink(Path.Combine(root, "b.md"), Path.Combine(outside, "secret.md"));

        AssertRefused(await ReadAsync(listed, "b.md"), ContainedFile.NotAsListed);
    }

    [WindowsFact]
    public async Task Windows_a_pipe_is_refused_as_not_a_regular_file()
    {
        var (root, _) = World();
        var finalRoot = ContainedFile.FinalRoot(root);

        // By its device name, and by the same name in the form .NET passes
        // to the system untouched: either way what is opened is a pipe.
        foreach (var path in new Func<string, string>[] { n => $@"\\.\pipe\{n}", n => $@"\\?\pipe\{n}" })
        {
            var name = "prem-contain-" + Guid.NewGuid().ToString("N");
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _ = server.WaitForConnectionAsync();

            var refused = Assert.Throws<UnreadableDocumentException>(() => ContainedFile.Open(path(name), finalRoot, "pipe"));

            Assert.Equal(ContainedFile.NotRegular, refused.Message);
        }
    }

    [LinuxFact]
    public async Task Linux_a_folder_link_or_a_file_link_swapped_in_after_the_listing_is_refused()
    {
        var (root, outside) = World();
        var listed = await ListAsync(root);

        Directory.Delete(Path.Combine(root, "docs"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(root, "docs"), Path.Combine(outside, "docs"));
        File.Delete(Path.Combine(root, "b.md"));
        File.CreateSymbolicLink(Path.Combine(root, "b.md"), Path.Combine(outside, "secret.md"));

        AssertRefused(await ReadAsync(listed, "docs/a.md"), ContainedFile.NotAsListed);
        AssertRefused(await ReadAsync(listed, "b.md"), ContainedFile.NotAsListed);
    }

    [LinuxFact]
    public async Task Linux_a_link_to_another_folder_inside_the_root_or_to_a_sibling_folder_is_refused()
    {
        var root = PublicAndHr();
        var sibling = Sibling(root);
        File.WriteAllText(Path.Combine(root, "public", "b.md"), "# B\n\nPublic.\n");
        var listed = await ListAsync(root);

        Directory.Delete(Path.Combine(root, "public", "sub"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(root, "public", "sub"), Path.Combine(root, "hr"));
        File.Delete(Path.Combine(root, "public", "b.md"));
        File.CreateSymbolicLink(Path.Combine(root, "public", "b.md"), Path.Combine(sibling, "secret.md"));

        AssertRefused(await ReadAsync(listed, "public/sub/a.md"), ContainedFile.NotAsListed);
        AssertRefused(await ReadAsync(listed, "public/b.md"), ContainedFile.NotAsListed);
        // The control: the same file, at the path it was listed under, is read.
        Assert.Contains(HrOnly, (await ReadAsync(listed, "hr/a.md")).Document!.Text);
    }

    [LinuxFact]
    public async Task Linux_a_pipe_in_the_folder_is_refused_without_waiting_for_a_writer()
    {
        var (root, _) = World();
        var fifo = Path.Combine(root, "waiting.md");
        await MakePipeAsync(fifo);
        var listed = await ListAsync(root);

        var reading = Task.Run(() => ReadAsync(listed, "waiting.md"));
        await NotWaitingAsync(reading, fifo);

        AssertRefused(await reading, ContainedFile.NotRegular);
    }

    [LinuxFact]
    public async Task Linux_a_pipe_as_the_bundle_index_is_not_waited_on_and_declares_no_version()
    {
        var root = Directory.CreateTempSubdirectory("prem-contain-index-").FullName;
        var fifo = Path.Combine(root, "index.md");
        await MakePipeAsync(fifo);
        File.WriteAllText(Path.Combine(root, "concept.md"), "---\ntype: Reference\n---\n# Concept\n\nText.\n");
        var source = new FileSystemSource(root, DocumentAccess.Everyone) { OkfBundle = true };

        var listing = Task.Run(async () =>
        {
            await foreach (var _ in source.EnumerateAsync()) { }
        });
        await NotWaitingAsync(listing, fifo);

        await listing;
        Assert.Null(source.BundleReport!.OkfVersion);
    }

    private static async Task MakePipeAsync(string path)
    {
        using var mkfifo = Process.Start("mkfifo", [path])!;
        await mkfifo.WaitForExitAsync();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    /// <summary>
    /// Fails when <paramref name="work"/> is still waiting after 30 seconds. A
    /// pipe opened the ordinary way waits for a writer inside the open itself,
    /// forever, so the work runs on another thread, and a wait fails this
    /// test instead of stopping the run: this test becomes the writer it waits
    /// for, and the waiting thread ends.
    /// </summary>
    private static async Task NotWaitingAsync(Task work, string fifo)
    {
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30))) == work) return;
        using (new FileStream(fifo, FileMode.Open, FileAccess.Write)) { }
        Assert.Fail($"reading the pipe {Path.GetFileName(fifo)} waited for a writer for 30 seconds");
    }

    [Fact]
    public async Task A_file_another_program_holds_exclusively_is_unreadable_as_it_was_to_File_OpenRead()
    {
        // On Linux a share mode is an advisory lock that .NET takes and
        // honors; the checked open honors it too, as the open it replaced did.
        var (root, _) = World();
        var listed = await ListAsync(root);

        await using (new FileStream(Path.Combine(root, "b.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var read = await ReadAsync(listed, "b.md");
            Assert.Null(read.Document);
            Assert.Contains("because it is being used by another process", Assert.IsType<SourceFailure>(read.Failure).Reason);
        }

        // The control: let go, it is read.
        Assert.NotNull((await ReadAsync(listed, "b.md")).Document);
    }

    [Fact]
    public async Task A_bundle_index_is_read_to_the_size_limit_and_one_over_it_declares_no_version()
    {
        var root = Directory.CreateTempSubdirectory("prem-contain-index-").FullName;
        File.WriteAllText(Path.Combine(root, "index.md"), $"---\nokf_version: \"1.0\"\n---\n# Index\n\n{new string('x', 200)}\n");
        File.WriteAllText(Path.Combine(root, "concept.md"), "---\ntype: Reference\n---\n# Concept\n\nText.\n");

        async Task<string?> VersionAsync(long limit)
        {
            var source = new FileSystemSource(root, DocumentAccess.Everyone) { OkfBundle = true, MaxIndexBytes = limit };
            await foreach (var _ in source.EnumerateAsync()) { }
            return source.BundleReport!.OkfVersion;
        }

        Assert.Null(await VersionAsync(64));
        // The control: within the limit, the version is read.
        Assert.Equal("1.0", await VersionAsync(DocumentSourceReading.MaxFileBytes));
    }

    /// <summary>Whether this account may make a symbolic link to a file, found by making one.</summary>
    internal static bool CanLinkAFile()
    {
        var folder = Directory.CreateTempSubdirectory("prem-can-link-");
        try
        {
            var target = Path.Combine(folder.FullName, "target.txt");
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(Path.Combine(folder.FullName, "link.txt"), target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { folder.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
