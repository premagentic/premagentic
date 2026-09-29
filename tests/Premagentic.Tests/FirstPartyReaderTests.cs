using System.Reflection;
using System.Runtime.Loader;
using Premagentic.Core;
using Premagentic.Core.Embeddings;
using Premagentic.Core.Extensions;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Security;
using Premagentic.Core.Sources;
using Premagentic.Core.Storage;
using Premagentic.Readers.Tests;

namespace Premagentic.Tests;

/// <summary>
/// The first-party readers as a deployment runs them: each one's build output
/// copied to a folder, allowed by the hash of every file its manifest lists,
/// and loaded by the host into its own context. This project references
/// neither reader, so the library the PDF reader ships can only come from its
/// own folder, and a test here can tell.
/// </summary>
internal static class FirstPartyReaders
{
    public static string BuiltFolder(string name) =>
        Path.GetDirectoryName(typeof(FirstPartyReaders).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == $"Premagentic.Extension.{name}.Path").Value!)!;

    /// <summary>Copies a reader's build output into its own folder under <paramref name="root"/> and returns the pair to allow.</summary>
    public static (string Name, string Sha256) InstallInto(string root, string name)
    {
        var from = BuiltFolder(name);
        Assert.True(ExtensionManifest.TryRead(from, out var manifest, out var problem), problem);
        var to = Directory.CreateDirectory(Path.Combine(root, manifest!.Name)).FullName;
        foreach (var file in manifest.Files.Select(f => f.File).Prepend(manifest.AssemblyFile).Append(ExtensionManifest.FileName))
            File.Copy(Path.Combine(from, file), Path.Combine(to, file), overwrite: true);
        return (manifest.Name, ExtensionManifest.ExtensionSha256(manifest.AssemblyFile, manifest.Sha256, manifest.Files));
    }
}

public sealed class FirstPartyReaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("prem-first-party-readers-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A loaded assembly's folder may still be held; the temp folder's to clean.
        }
    }

    private ExtensionHost LoadAll() =>
        ExtensionHost.Load(_root.FullName, [
            FirstPartyReaders.InstallInto(_root.FullName, "PdfReader"),
            FirstPartyReaders.InstallInto(_root.FullName, "DocxReader"),
            FirstPartyReaders.InstallInto(_root.FullName, "XlsxReader"),
        ]);

    [Fact]
    public void Every_reader_loads_by_hash_and_claims_its_formats()
    {
        var host = LoadAll();

        Assert.Empty(host.Refused);
        Assert.Equal(["docx-reader", "pdf-reader", "xlsx-reader"], host.Loaded.Select(l => l.Name).Order());
        Assert.Equal("pdf", host.Readers.ForPath("scans/receipt.PDF")?.Name);
        Assert.Equal("docx", host.Readers.ForPath("letters/offer.docx")?.Name);
        Assert.Equal("docx", host.Readers.ForPath("old/minutes.doc")?.Name);
        Assert.Equal("docx", host.Readers.ForPath("forms/request.docm")?.Name);
        Assert.Equal("xlsx", host.Readers.ForPath("stores/stock-count.XLSX")?.Name);
        Assert.Equal("xlsx", host.Readers.ForPath("old/ledger.xls")?.Name);
        Assert.Equal("xlsx", host.Readers.ForPath("finance/tracker.xlsm")?.Name);
        Assert.Equal("xlsx", host.Readers.ForPath("finance/rates.xlsb")?.Name);
    }

    [Fact]
    public void Every_manifest_declares_reader_version_2_and_the_pdf_reader_lists_every_library_it_loads()
    {
        foreach (var name in new[] { "PdfReader", "DocxReader", "XlsxReader" })
        {
            Assert.True(ExtensionManifest.TryRead(FirstPartyReaders.BuiltFolder(name), out var manifest, out var problem), problem);
            Assert.Equal(2, manifest!.Seams["reader"]);
        }

        Assert.True(ExtensionManifest.TryRead(FirstPartyReaders.BuiltFolder("PdfReader"), out var pdf, out _));
        Assert.Equal(
            ["UglyToad.PdfPig.Core.dll", "UglyToad.PdfPig.DocumentLayoutAnalysis.dll", "UglyToad.PdfPig.Fonts.dll",
             "UglyToad.PdfPig.Package.dll", "UglyToad.PdfPig.Tokenization.dll", "UglyToad.PdfPig.Tokens.dll", "UglyToad.PdfPig.dll"],
            pdf!.Files.Select(f => f.File).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_pdf_library_is_the_one_in_the_readers_folder_and_never_the_processes()
    {
        var host = LoadAll();

        using var file = new MemoryStream(PdfFixtures.Text(["The dock opens at seven."]));
        var read = await host.Readers.ForPath("dock.pdf")!.ReadAsync(file, "dock.pdf", CancellationToken.None);
        Assert.Contains("The dock opens at seven.", read.Text);

        var holders = AssemblyLoadContext.All
            .Where(c => c.Assemblies.Any(a => a.GetName().Name == "UglyToad.PdfPig"))
            .ToList();
        Assert.NotEmpty(holders);
        Assert.DoesNotContain(AssemblyLoadContext.Default, holders);
    }

    [Fact]
    public void A_library_changed_in_the_readers_folder_refuses_the_reader_and_names_the_file()
    {
        var allowed = FirstPartyReaders.InstallInto(_root.FullName, "PdfReader");
        var library = Path.Combine(_root.FullName, "pdf-reader", "UglyToad.PdfPig.Fonts.dll");
        var bytes = File.ReadAllBytes(library);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(library, bytes);

        var host = ExtensionHost.Load(_root.FullName, [allowed]);

        var refused = Assert.Single(host.Refused);
        Assert.Equal(ExtensionRefusal.HashMismatch, refused.Reason);
        Assert.Contains("UglyToad.PdfPig.Fonts.dll", refused.Detail);
        Assert.Null(host.Readers.ForPath("dock.pdf"));
    }
}

/// <summary>
/// A folder of every kind of file the readers meet, ingested through the loaded
/// readers into a real database. Requires a running Docker daemon.
/// </summary>
public sealed class FirstPartyReaderIngestTests(DatastoreTestDatabase server) : IClassFixture<DatastoreTestDatabase>, IDisposable
{
    private readonly DirectoryInfo _extensions = Directory.CreateTempSubdirectory("prem-first-party-ingest-");

    public void Dispose()
    {
        try
        {
            _extensions.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_folder_of_pdf_word_and_excel_files_is_read_counted_and_cited_by_page_heading_and_sheet()
    {
        var host = ExtensionHost.Load(_extensions.FullName, [
            FirstPartyReaders.InstallInto(_extensions.FullName, "PdfReader"),
            FirstPartyReaders.InstallInto(_extensions.FullName, "DocxReader"),
            FirstPartyReaders.InstallInto(_extensions.FullName, "XlsxReader"),
        ]);
        Assert.Empty(host.Refused);

        var folder = Directory.CreateTempSubdirectory("prem-first-party-docs-").FullName;
        File.WriteAllBytes(Path.Combine(folder, "dock.pdf"), PdfFixtures.Text(["Deliveries sign in at the gate."], ["Returns go to bay four."]));
        File.WriteAllBytes(Path.Combine(folder, "scan.pdf"), PdfFixtures.Scanned(1));
        File.WriteAllBytes(Path.Combine(folder, "locked.pdf"), PdfFixtures.Encrypted());
        File.WriteAllBytes(Path.Combine(folder, "leave.docx"), DocxFixtures.Package(
            DocxFixtures.Heading(1, "Leave") + DocxFixtures.P("Annual leave is booked two weeks ahead.")));
        File.WriteAllBytes(Path.Combine(folder, "minutes.doc"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "request.docm"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "stock.xlsx"), XlsxFixtures.Workbook(
            XlsxFixtures.Ws("Dock", XlsxFixtures.Row(XlsxFixtures.Str("Pallets waiting"), XlsxFixtures.Num("14")))));
        File.WriteAllBytes(Path.Combine(folder, "ledger.xls"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "tracker.xlsm"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "rates.xlsb"), XlsxFixtures.BinaryWorkbook(
            XlsxFixtures.Bs("Rates", XlsxFixtures.BRow(XlsxFixtures.BStr("Night rate per pallet"), XlsxFixtures.BNum(4.5)))));

        await using var db = new PremagenticDatabase(await server.CreateDatabaseAsync());
        await db.InitializeAsync();
        var tenant = await db.EnsureTenantAsync("t", "T");
        var summary = await new IngestPipeline(db, new HashEmbeddingProvider(), readers: host.Readers)
            .RunAsync(tenant, new FileSystemSource(folder, DocumentAccess.Everyone, "office"));

        Assert.Equal(4, summary.Ingested);
        Assert.Equal(5, summary.Skipped);
        Assert.Equal(
            new Dictionary<string, int>
            {
                [".doc (legacy .doc)"] = 1,
                [".docm (macro-enabled)"] = 1,
                [".pdf (no text layer)"] = 1,
                [".xls (legacy .xls)"] = 1,
                [".xlsm (macro-enabled)"] = 1,
            },
            summary.SkippedFormats);
        Assert.Equal("office/locked.pdf", Assert.Single(summary.FailureList).Path);
        Assert.Equal("password protected", summary.FailureList[0].Reason);

        Assert.Equal("Page 2", await ScalarAsync(db,
            "SELECT c.heading_path FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id " +
            "WHERE d.path = 'office/dock.pdf' AND c.content LIKE '%bay four%'"));
        Assert.Equal("Leave", await ScalarAsync(db,
            "SELECT c.heading_path FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id " +
            "WHERE d.path = 'office/leave.docx' AND c.content LIKE '%two weeks ahead%'"));
        Assert.Equal("stock.xlsx > Dock", await ScalarAsync(db,
            "SELECT c.heading_path FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id " +
            "WHERE d.path = 'office/stock.xlsx' AND c.content LIKE '%Pallets waiting%'"));
        Assert.Equal("rates.xlsb > Rates", await ScalarAsync(db,
            "SELECT c.heading_path FROM prem_index.chunk c JOIN prem_index.document d ON d.id = c.document_id " +
            "WHERE d.path = 'office/rates.xlsb' AND c.content LIKE '%Night rate per pallet%'"));
    }

    private static async Task<string?> ScalarAsync(PremagenticDatabase db, string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
