using System.Reflection;
using System.Security.Cryptography;
using Premagentic.Core.Extensions;

namespace Premagentic.Tests;

/// <summary>
/// Lays out the built sample extension in a folder, the way an administrator
/// would: the assembly, and a manifest beside it carrying the hash of exactly
/// those bytes. Nothing here references the sample's types, so it is loaded
/// only the way a real extension is.
/// </summary>
internal static class SampleExtension
{
    public const string Name = "sentence-chunker";
    public const string AssemblyFile = "SentenceChunker.dll";
    public const string ChunkerName = "sentence";
    public const string ReaderName = "csv";
    public const string SignInAdapterName = "sample-no-op";

    /// <summary>The file extension the sample's reader claims and no built-in does.</summary>
    public const string ReaderExtension = ".csv";

    /// <summary>The sample's assembly, where the test project recorded it at build time.</summary>
    public static string BuiltPath
    {
        get
        {
            var path = typeof(SampleExtension).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "Premagentic.Sample.SentenceChunker.Path")
                .Value;
            Assert.True(File.Exists(path), "the sample extension was not built where the test project recorded it: " + path);
            return path!;
        }
    }

    /// <summary>
    /// One extension folder under <paramref name="extensionsFolder"/>, and the
    /// pair an allow list needs for it.
    /// </summary>
    /// <param name="chunkerSeam">The chunker seam version the manifest claims.</param>
    /// <param name="tamper">
    /// Change one byte after the manifest is written, which is what an assembly
    /// exchanged behind an administrator's back looks like.
    /// </param>
    public static (string Name, string Sha256) InstallInto(
        string extensionsFolder, string folderName = Name, int chunkerSeam = 1, bool tamper = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(extensionsFolder, folderName));
        var assembly = Path.Combine(directory.FullName, AssemblyFile);
        File.Copy(BuiltPath, assembly, overwrite: true);

        var bytes = File.ReadAllBytes(assembly);
        var sha256 = Hash(bytes);
        File.WriteAllText(Path.Combine(directory.FullName, ExtensionManifest.FileName), $$"""
            {
              "name": "{{Name}}",
              "version": "0.1.0",
              "assemblyFile": "{{AssemblyFile}}",
              "sha256": "{{sha256}}",
              "seams": { "chunker": {{chunkerSeam}}, "reader": 1, "signin": 1 }
            }
            """);

        if (tamper)
        {
            bytes[bytes.Length / 2]++;
            File.WriteAllBytes(assembly, bytes);
        }
        return (Name, sha256);
    }

    /// <summary>A host with the sample installed and allowed, in a folder of its own.</summary>
    public static ExtensionHost LoadedOnce()
    {
        var folder = Directory.CreateTempSubdirectory("prem-sample-loaded-");
        var allowed = InstallInto(folder.FullName);
        return ExtensionHost.Load(folder.FullName, [allowed]);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
