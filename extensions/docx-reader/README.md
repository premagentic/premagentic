# docx-reader

A first-party extension that reads Word documents (`.docx`). It uses the zip
and XML readers of the .NET base library and no other package. It ships in
this repository and is built with the solution, and like any extension it
loads only once an administrator allows it.

## What it reads

- Headings, by their style or outline level, as Markdown headings, so a
  passage's heading path, which a citation carries, is the document's own.
- Paragraphs, list items, and the text of table cells (one row to a line, the
  cells separated by `|`; a table inside a cell is read into that cell, once),
  in document order.
- Tracked changes as they would print once accepted: inserted text is read and
  deleted text is not.
- The text of hyperlinks, and the results of fields.
- Text boxes, once each, however the file writes them (Word writes each one
  twice, as a drawing and as an older shape, and only the first is read): a
  text box's paragraphs and tables right after the paragraph it is anchored
  in, and one anchored in a table cell on that cell's line. A text box inside
  a text box is read after the paragraph of the box around it that anchors
  it. The limits below hold for them as for the body: the depth limit refuses
  text boxes nested past it, and the text and time limits count every box.
- Footnotes and endnotes, after the body, under the headings `Footnotes` and
  `Endnotes`.
- The title the file states in its properties, when it states one.

## What it does not read or do

- Comments, headers and footers, pictures and embedded objects are not read,
  and neither is a text box inside a field's instruction, which does not print.
- Nothing is followed: a hyperlink's address, a field's instruction (such as
  one that includes another file), a linked template and an external picture
  are never resolved. No part is parsed with a document type definition, so an
  entity cannot name a file or an address. The reader's tests check that a file
  pointing at an address on this machine is read without anything connecting
  to it.
- Macros are never read or run. A `.docm`, or any file that carries a macro
  project whatever its name, is skipped with the reason `macro-enabled`.
- A legacy `.doc` is skipped with the reason `legacy .doc`.
- It never reads part of a file. A file over a limit is reported unreadable
  with the limit it crossed, and its existing index entry is kept.

## Limits

A `.docx` is a zip, and a zip can be built to expand without end, so most
limits are about the container; the last is about how deep its XML nests.

| Limit | Default | Reported as |
|---|---|---|
| File size | 200 MB | `larger than 200 MB, the most this reader reads` |
| Entries in the zip, counted from its end record before it is opened | 5000 | `holds N entries, more than the 5000 this reader opens` |
| What the zip says it expands to, every entry together | 1024 MB | `says it expands to more than 1024 MB, the most this reader opens` |
| What the parts it reads really expand to, counted as they are read | 64 MB | `its text parts expand past 64 MB, the most this reader reads` |
| How far one of those parts expands past 1 MB, against its size in the file | 100 times | `the part word/document.xml expands more than 100 times its size in the file` |
| Text | 10,000,000 characters | `gives more than 10000000 characters of text, the most this reader keeps` |
| Time per file, checked at every paragraph and table cell | 60 s | `took longer than 60 seconds to read` |
| How deep the XML of a part it reads nests, measured before anything is walked | 256 | `the part word/document.xml nests its XML more than 256 deep, the most this reader follows` |

A part it reads that is itself an archive, and two parts with one name, are
refused. A password-protected file is reported unreadable as
`password protected`, and a damaged one as `damaged, or not a Word file`.

The nesting is measured with a reader that keeps nothing, before a part is
loaded. The reader walks a part by recursion, and a part built to nest tens of
thousands deep, small enough to pass every size limit, would otherwise end the
process with a stack overflow, which nothing can catch. Word's own documents
nest far less.

## Installing it

Build the solution in Release, then copy the build output to the deployment's
extensions folder (the setting `extensions.folder`, or `PREM_EXTENSIONS_DIR`)
and allow it:

```bash
dotnet build extensions/docx-reader -c Release
cp -r extensions/docx-reader/bin/Release/net10.0 /opt/premagentic/extensions/docx-reader
prem extensions allow /opt/premagentic/extensions/docx-reader
```

It is used from the next start of the service. The manifest declares reader
seam version 2, which this version offers.
