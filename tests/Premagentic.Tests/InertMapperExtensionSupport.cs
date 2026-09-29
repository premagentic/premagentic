using System.Reflection;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity;

namespace Premagentic.Tests;

/// <summary>
/// Lays out the test extension that registers a principal mapper and nothing
/// else, the way an administrator would install it: the assembly, and a
/// manifest beside it carrying the hash of those bytes. Nothing here
/// references its types, so it is loaded only the way a real extension is.
/// </summary>
internal static class InertMapperExtension
{
    public const string Name = "inert-mapper";
    public const string AssemblyFile = "InertMapper.dll";
    public const string MapperName = "maps-nothing";

    /// <summary>The extension's assembly, where the test project recorded it at build time.</summary>
    public static string BuiltPath
    {
        get
        {
            var path = typeof(InertMapperExtension).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "Premagentic.Test.InertMapper.Path")
                .Value;
            Assert.True(File.Exists(path), "the inert mapper extension was not built where the test project recorded it: " + path);
            return path!;
        }
    }

    /// <summary>One extension folder under <paramref name="extensionsFolder"/>, and the pair an allow list needs for it.</summary>
    /// <param name="manifestName">The name the manifest gives, which is the name an allow list holds.</param>
    /// <param name="declarePrincipals">Whether the manifest declares the principals seam its mapper needs.</param>
    public static (string Name, string Sha256) InstallInto(
        string extensionsFolder, string folderName = Name, string manifestName = Name, bool declarePrincipals = true)
    {
        var directory = Directory.CreateDirectory(Path.Combine(extensionsFolder, folderName));
        var assembly = Path.Combine(directory.FullName, AssemblyFile);
        File.Copy(BuiltPath, assembly, overwrite: true);

        var sha256 = SampleExtension.Hash(File.ReadAllBytes(assembly));
        File.WriteAllText(Path.Combine(directory.FullName, ExtensionManifest.FileName), $$"""
            {
              "name": "{{manifestName}}",
              "version": "0.1.0",
              "assemblyFile": "{{AssemblyFile}}",
              "sha256": "{{sha256}}",
              "seams": { {{(declarePrincipals ? "\"principals\": 1" : "")}} }
            }
            """);
        return (manifestName, sha256);
    }

    /// <summary>The mapper's own count of the times it was asked. It lives in the extension's load context, so it is asked by name.</summary>
    public static int TimesAsked(IPrincipalMapper mapper) =>
        (int)mapper.GetType().GetProperty("TimesAsked")!.GetValue(mapper)!;
}
