using System.Text;
using Premagentic.Core;
using Premagentic.Core.Extensions;
using Premagentic.Core.Identity.SignIn;
using Premagentic.Core.Ingestion;
using Premagentic.Core.Ingestion.Readers;

namespace Premagentic.Samples.Chunking;

/// <summary>
/// A whole extension, as small as one can be, across three seams: a chunker
/// that cuts at sentence ends, a reader for a format the built-in readers do
/// not claim, and a way of signing in that claims nothing. Build it, put the
/// assembly and the extension.json beside it in a folder under the extensions
/// folder, run <c>prem extensions allow</c> on that folder, and restart. A
/// source can then be set to cut with <c>sentence</c>, and every connector
/// gains <c>.csv</c> at once, because connectors find paths and readers read
/// them.
/// <para>
/// Copy this project to start your own. What makes it an extension is the
/// manifest, the type below, and nothing else: no attribute, no registration
/// file, no name the host has to be told. One extension may register as many
/// or as few seams as it likes; the manifest declares the version of each one
/// it was built for.
/// </para>
/// </summary>
public sealed class SentenceChunkerExtension : IExtension
{
    public string Name => "sentence-chunker";

    public void Register(ExtensionRegistrations registrations)
    {
        registrations.AddChunker(new SentenceChunker());
        registrations.AddReader(new CommaSeparatedReader());
        registrations.AddSignInAdapter(new NoOpSignInAdapter());
    }
}

/// <summary>
/// A way of signing in that claims nothing. It is asked about every request
/// that gets as far as signing in, and always answers "not mine", so the next
/// adapter decides and a deployment that installs this extension signs people
/// in exactly as it did before installing it.
/// <para>
/// It is here to show the shape of the seam, and it is deliberately inert. A
/// real adapter reads a header or a cookie that its own system set, checks it
/// against that system, and resolves it to a Premagentic account that already
/// exists. Copy the shape, never the answer: an adapter that resolves a name
/// out of an unverified header hands the deployment to anyone who can set one.
/// </para>
/// </summary>
public sealed class NoOpSignInAdapter : ISignInAdapter
{
    private int _timesAsked;

    public string Name => "sample-no-op";

    /// <summary>
    /// How many requests this has been asked about. It exists so a test can
    /// tell a host that collected this adapter from a host that actually asks
    /// it, which is the difference between the seam being wired and the seam
    /// being present.
    /// </summary>
    public int TimesAsked => Volatile.Read(ref _timesAsked);

    public Task<SignInResolution?> ResolveAsync(ISignInRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _timesAsked);
        return Task.FromResult<SignInResolution?>(null);
    }
}

/// <summary>
/// Reads a comma separated file as the text of its values: the separators and
/// the quoting come off, each row's values are kept in order, and nothing else
/// is added. A small reference table, a list of codes or an exported register
/// becomes searchable without anyone converting it first.
/// <para>
/// A reader returns text taken from the file. It writes no heading, no column
/// name and no summary of its own, because what a reader returns is what a
/// search serves and cites.
/// </para>
/// </summary>
public sealed class CommaSeparatedReader : IDocumentReader
{
    public string Name => "csv";

    public IReadOnlyList<string> Extensions { get; } = [".csv"];

    public async Task<ReadDocument> ReadAsync(Stream content, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
        return new ReadDocument(Values(await reader.ReadToEndAsync(ct)), null, null);
    }

    /// <summary>
    /// Each row's values in order, separated by a space, one row to a line. A
    /// quoted value keeps the commas and line breaks inside it, and a doubled
    /// quote inside one is a single quote, as the format has it.
    /// </summary>
    private static string Values(string text)
    {
        var rows = new StringBuilder();
        var value = new StringBuilder();
        var quoted = false;
        var rowHasValues = false;

        void EndValue()
        {
            if (value.Length == 0) return;
            if (rowHasValues) rows.Append(' ');
            rows.Append(value);
            rowHasValues = true;
            value.Clear();
        }

        void EndRow()
        {
            EndValue();
            if (rowHasValues) rows.Append('\n');
            rowHasValues = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    value.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
                continue;
            }

            switch (c)
            {
                case '"': quoted = true; break;
                case ',': EndValue(); break;
                case '\r': break;
                case '\n': EndRow(); break;
                default: value.Append(c); break;
            }
        }

        EndRow();
        return rows.ToString();
    }
}

/// <summary>
/// Cuts a document at the end of each sentence, for a corpus of short answers
/// where a whole section is more than a reader needs. A sentence ends at a
/// full stop, a question mark or an exclamation mark followed by a space or the
/// end of the text.
/// <para>
/// Every chunk is a piece of the document exactly as it was written, trimmed.
/// A chunker cuts and does nothing else: it writes no text of its own, and
/// nothing in the document is executed or interpreted.
/// </para>
/// </summary>
public sealed class SentenceChunker : IChunker
{
    public string Name => "sentence";

    public IReadOnlyList<DocumentChunk> Chunk(SourceDocument document)
    {
        var chunks = new List<DocumentChunk>();
        var seq = 0;
        foreach (var sentence in Sentences(document.Text))
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length > 0) chunks.Add(new DocumentChunk(seq++, "", trimmed));
        }
        return chunks;
    }

    private static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            // The end of a sentence, not a decimal point or an initial: the
            // mark is followed by whitespace or by nothing at all.
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;

            yield return text[start..(i + 1)];
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }
}
