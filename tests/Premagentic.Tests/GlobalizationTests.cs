using System.Diagnostics;
using System.Reflection;
using Premagentic.Cli.Setup;
using Premagentic.Core;
using Premagentic.Core.Identity;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Storage;

namespace Premagentic.Tests;

/// <summary>
/// Text handling that must not depend on the machine. Password hashes and chunk
/// boundaries are stored, so a server that normalized or compared text
/// differently would fail to verify a password another server hashed, or cut a
/// document another way. The known answers are pinned by value, not by comparing
/// two runs: invariant globalization mode, which does not normalize at all,
/// fails each of them.
/// </summary>
public sealed class GlobalizationTests
{
    // PBKDF2-SHA512, 220,000 iterations, salt "premagentic-kat-1", over the UTF-8
    // bytes of "caf\u00E9-file-AB": the NFKC form of every password below.
    // Computed outside .NET, so the value does not come from the code under test.
    private const string KnownHash =
        "$pbkdf2-sha512$v=1$i=220000$cHJlbWFnZW50aWMta2F0LTE$zmfyY4VNjnPLSfv3BTMSE2jPUCgswslJI-lK7r3BnZp55m1YodHtuo_eVxMmmxDvXDKBXc4JBJJRUSfJLmAOPA";

    [Theory]
    [InlineData("caf\u00E9-file-AB")]                     // already NFKC
    [InlineData("cafe\u0301-file-AB")]                    // a decomposed accent
    [InlineData("caf\u00E9-\uFB01le-AB")]                 // the fi ligature
    [InlineData("caf\u00E9-file-\uFF21\uFF22")]           // fullwidth letters
    [InlineData("cafe\u0301-\uFB01le-\uFF21\uFF22")]      // all three at once
    public void A_password_verifies_against_a_fixed_hash_however_its_characters_were_composed(string typed)
    {
        Assert.True(new PasswordHasher().Verify(typed, KnownHash));
    }

    [Theory]
    [InlineData("cafe-file-AB")]         // the accent dropped: a different password
    [InlineData("caf\u00E9-file-ab")]    // case is not folded
    public void A_different_password_does_not_verify_against_the_fixed_hash(string typed)
    {
        Assert.False(new PasswordHasher().Verify(typed, KnownHash));
    }

    [Fact]
    public void A_fence_line_with_an_ignorable_character_before_it_still_opens_a_fence()
    {
        // A soft hyphen (U+00AD) is invisible and ignorable. The chunker's fence
        // test ignores it, as ICU does on every platform Premagentic ships for, so
        // the "# inside the fence" line is code, not a heading. Under invariant
        // globalization the same document is cut into different chunks under a
        // heading that does not exist. If the fence test is ever made ordinal on
        // purpose, this expectation changes with it, knowingly.
        var chunks = MarkdownChunker.Chunk("# Notes\n\n\u00AD```\n# inside the fence\n```\n\n## After\ntext after the fence\n");

        Assert.Equal(
            [
                new DocumentChunk(0, "Notes", "\u00AD```\n# inside the fence\n```"),
                new DocumentChunk(1, "Notes > After", "text after the fence"),
            ],
            chunks);
    }

    [Fact]
    public void This_process_normalizes_text_the_way_setup_requires()
    {
        Assert.Null(TextCheck.Problem());
    }

    [Fact]
    public async Task Setup_refuses_before_touching_the_database_on_a_runtime_that_does_not_normalize()
    {
        // The real CLI in a child process, as a customer runs it, once as built and
        // once in invariant globalization mode. The admin connection leads nowhere:
        // the text check comes first, so a refusal never reaches it.
        var nowhere = Path.Combine(Path.GetTempPath(), $"premagentic-nowhere-{Guid.NewGuid():N}.conn");
        await File.WriteAllTextAsync(nowhere, "connection=Host=127.0.0.1;Port=1;Username=x;Password=y;Timeout=2\n");
        try
        {
            // On a failure the whole child output is the message, because the
            // assertion's own excerpt is too short to show what the CLI did.
            var (asBuilt, asBuiltOutput) = await RunCliAsync(nowhere, invariant: false);
            Assert.True(asBuiltOutput.Contains("[checked] preflight.text"), asBuiltOutput);
            Assert.True(asBuiltOutput.Contains("preflight.postgres"), asBuiltOutput);
            Assert.True(asBuilt == 1, "exit " + asBuilt + Environment.NewLine + asBuiltOutput);

            var (invariant, invariantOutput) = await RunCliAsync(nowhere, invariant: true);
            Assert.True(invariantOutput.Contains("[FAILED]  preflight.text"), invariantOutput);
            Assert.True(invariantOutput.Contains("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"), invariantOutput);
            Assert.True(!invariantOutput.Contains("preflight.postgres"), invariantOutput);
            Assert.True(invariant == 1, "exit " + invariant + Environment.NewLine + invariantOutput);
        }
        finally
        {
            File.Delete(nowhere);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(string adminConnectionFile, bool invariant)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { CliPath(), "setup", "--plan", "--admin-connection-file", adminConnectionFile })
            start.ArgumentList.Add(argument);
        start.Environment["PREM_EMBEDDING_PROVIDER"] = "hash";
        start.Environment.Remove("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
        if (invariant) start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    // The CLI's own build output, recorded by the test project at build time (see
    // its project file). The copy of prem.dll in this folder cannot run on its
    // own: the assemblies beside it are this project's, not the CLI's.
    private static string CliPath()
    {
        var path = typeof(GlobalizationTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "Premagentic.Cli.Path")
            .Value;
        Assert.True(File.Exists(path), "the CLI was not built where the test project recorded it: " + path);
        return path!;
    }
}
