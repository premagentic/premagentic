# Sources, readers and chunkers

How a folder is registered as a source with its owner and its hosted-model switch, how each source's files are read and cut into passages, and what a run reports it did not read.

## Sources

`prem sources add <name> <folder> [--prefix p] [--chunker name] [--okf-bundle]
[--undeclared-as-machine]` registers a folder, and `prem ingest --source
<name>` reads it with its registered settings. Two sources may not share or
nest a prefix. `prem sources status <name>` shows the last run. Removing a
source leaves its documents indexed; ingest an empty folder over the same
prefix with `--allow-empty-source`, or rebuild the index, to remove them.
`prem ingest <folder>` still works and registers nothing. Every run of either
form is recorded.

Each source also carries an owner and a switch. The owner is the person
answerable for those documents, the one the review queue and the sources pages
name; it decides nothing, because what anybody may read is the folder rule and
there is only one place that decides that.

The switch is "may be served to hosted models". Turned off, it holds the
source's folder back from every agent registered as using a hosted model (the
reserved hosted-model agents group): no document of the source at or beneath
the folder is served to one, whatever folder rule decides that document. A
longer rule beneath the folder, or a rule written at the folder itself, does
not undo it, because the hold is kept beside the rules and no rule write
touches it.

```
prem sources set handbook --owner dana
prem sources set handbook --hosted no      (never served to a hosted-model agent)
prem sources list                          (both, in words)
```

The hold is enforced twice, and either alone keeps the documents from a
hosted-model agent. Each held document's access list carries the denial of the
hosted-model agents group first, so the gate, the second line in the database
and the portal's Why page all say so; and when a hosted-model agent reads, the
lists of every held document are taken out of what it may read before the read
starts. A folder with no rule may be held too: the hold is not a rule and
invents none. A missing reserved group is refused, in both reading and setting:
it is created with the tenant and cannot be removed, so its absence means the
configuration database was edited by hand, and answering "may be served" there
would be a reassuring sentence about a question this cannot answer.

`prem rules list` and the Permissions page name the hold beside every rule it
reaches, so the entries of a rule are not read as the whole story. Every turn
of the switch is in the change record, as `source.hosted`, and so is turning it
off again on a folder already held when that stamps a document stored without
the denial. Turning it back on gives every document the rules decide the list
those rules give; a document whose list its connector decided keeps the denial
until its next ingest, which fails closed.

A source's folder is held when a hold covers it: its own, or one on a folder
above it, which the words for its state name (`never served to hosted-model
agents, by the switch on filesystem:docs`). Turning the switch back on
releases the source's own hold only; a hold above is its own source's to
release.

Removing a source keeps its documents indexed under their folder rules, so a
held source's hold stays too, and keeps them from hosted models. `prem sources
list` names such a hold, and `prem sources hosted-release --prefix <folder>`
releases it, recorded like any turn of the switch. A folder a registered
source reads is turned with `prem sources set` instead.

A source a profile ADDS is held back unless the profile says otherwise; a
source already registered is left as it is. A profile may set the switch with
or without stating a rule. Applying a profile holds folders first and releases
them last, so no rule or source it writes is ever served unheld in between.

Before this version the switch was a deny entry at the top of the folder's own
rule. Upgrading makes a hold, marked legacy, of every such entry at the folder
of a registered source, which is the only place the switch wrote it; the entry
stays in its rule, where it changes nothing more, and goes when that hold is
released. The same entry anywhere else was written by hand and stays an
ordinary entry, and no other release touches a rule.

`prem sources list` and the portal's Sources page tell three states apart,
not two: `never served to hosted-model agents`,
`may be served to hosted models`, and a denial somebody wrote into the folder's
rule by hand rather than with the switch, reported as
`not held by the switch: a deny entry for hosted-model agents in this folder's rule decides only where no entry before it, and no rule beneath the folder, decides first`.
The third is reported in its own words because an entry above it in the rule
may allow first, and a longer rule beneath the folder is not held back.

## Chunkers

Each source is cut into passages by a chunker, chosen by name.
`markdown` is built in and is the default: it cuts at headings and splits a
long section at blank lines. One paragraph longer than 4,000 characters, twice
the passage size of 2,000, is cut into passages of at most 2,000 characters,
after the last line break in the second half of each when there is one, else
after the last space there, so no word is cut in two; a table or a code block
up to 4,000 characters stays whole. Kept whole, a paragraph of a few megabytes
would be one passage with more search terms than PostgreSQL holds in one
search vector (1 MB), and its file could never be stored. `prem sources add <name> <folder> --chunker
<name>` chooses one when a source is registered, `prem sources set <name>
--chunker <name>` changes it, `prem sources chunkers` lists the ones this
installation has, and the portal's source forms offer the same list. A name
the installation does not have is refused when the source is added or
changed; if a source names one anyway (a chunker removed from the build, or a
second machine built without it), its run stops before it reads a file and
is recorded as failed with the name, and the index is left as it was.
Changing a source's chunker cuts, embeds and stores every one of its
documents again at its next run.

To add a chunker, implement `IChunker` (a name, and a method that returns a
document's chunks, each one text taken from the document) and ship it as an
extension (see [Extensions](extensions.md)), which every host takes from the same composition
point. A chunker whose output changes for the same text takes a new name,
since the name is what tells the next run to cut again.

## Readers

Before a document is cut it is read, by a reader chosen by the
file's extension: Markdown and plain text are built in, and a reader an
extension brings (the sample's `.csv` reader, and the PDF, Word and Excel
readers below) reaches the CLI, the API and the portal alike. A file no reader claims
is counted as skipped, by extension, and never guessed at. A reader returns
the document's text and, when it has one, its frontmatter; the content hash is
the SHA-256 of the file's bytes. Before any reader sees a file, the run reads
no more than 256 MB of it: a larger file is unreadable as `larger than 256 MB,
the most the pipeline reads`, and a file that states its size is refused
before a byte is read.
The run reads each file once, and a reader is handed those bytes: the
first-party readers read the array the run read the file into rather than a
copy of it, so a file is held once while it is read. After a reader returns, a document holding a NUL or
half of a character, which the index cannot store, is unreadable as `holds a
NUL byte or half of a character, which the index cannot store`. Either is one
unreadable file; the run goes on. So is a document that was read and cut and
that the index still refuses to store, such as a passage from an extension's
chunker, or a title or a heading, too large for one search vector: it is
unreadable as `the index could not store it (54000: ...)`, with the database's
code and its message, put on one line and cut at 200 characters, it keeps its existing index entry, and
the rest of the source is still stored and its deletions still reconciled.

Frontmatter is read only within limits: 32 levels of nesting, counted as the
block is parsed, and 100,000 nodes (each value, list and mapping is one) and
1 MB of text, both counted with every alias expanded, since a few aliases of
one long value are few nodes and a great deal of text. A block over any of them is read as frontmatter that could not be
parsed: the document is indexed as if it had none, and a bundle run reports it
as unparseable.

A reader can decline a file it recognizes and say why, and the run counts it
under its extension and that reason, as `.pdf (no text layer)`. A reader can
also report a file unreadable with a reason, such as `password protected`;
that file's existing index entry is kept. A reader that fails on one file
never stops the run: the file is reported unreadable, and the next file is
read.

## PDF, Word and Excel files

PDF, Word and Excel files are read by three extensions that ship in this
repository, `extensions/pdf-reader`, `extensions/docx-reader` and
`extensions/xlsx-reader`. Like any extension, none loads until an
administrator allows it (see [Extensions](extensions.md)). All three read
text and nothing else: no link, script, macro or embedded file in a document
is ever followed or run, and a file over a limit is reported unreadable with
the limit it crossed, never read in part.

To allow them from a clone of this repository, build each one, copy its build
output into a folder of its own under the extensions folder (the setting
`extensions.folder`, or `PREM_EXTENSIONS_DIR`), and allow that folder:

```bash
dotnet build extensions/pdf-reader -c Release
dotnet build extensions/docx-reader -c Release
dotnet build extensions/xlsx-reader -c Release
cp -r extensions/pdf-reader/bin/Release/net10.0 /opt/premagentic/extensions/pdf-reader
cp -r extensions/docx-reader/bin/Release/net10.0 /opt/premagentic/extensions/docx-reader
cp -r extensions/xlsx-reader/bin/Release/net10.0 /opt/premagentic/extensions/xlsx-reader
prem extensions allow /opt/premagentic/extensions/pdf-reader
prem extensions allow /opt/premagentic/extensions/docx-reader
prem extensions allow /opt/premagentic/extensions/xlsx-reader
```

`allow` measures each reader's assembly, and for the PDF reader each of the
seven PdfPig assemblies its manifest lists, and allows each extension by one
hash that covers what it measured. A reader is used from the next time a
PremAgentic process starts, so restart the service after allowing it.
`scripts/release/build.sh` builds all three into the archive it makes, each in a
folder of its own under the archive's `extensions` folder; there, set
`extensions.folder` to that folder's full path and allow each one the same
way. `prem extensions disallow pdf-reader` (or `docx-reader`, `xlsx-reader`) takes one away
again; the documents it indexed stay, and are still found by search, until
one ingest of their folder runs with `--remove-unread`. The limits below are
fixed in each reader; they are not settings.

### The PDF reader

The PDF reader claims `.pdf`. It reads the text of each page that has any, in
the order the file draws it, under a heading `Page N`, and the title the file
states when it states one. The heading path a passage carries, and so every
citation of it, names its page: a passage from the second page is cited under
`Page 2`. It recognizes no characters in pictures, so a file whose pages carry
no text is skipped as `.pdf (no text layer)` rather than indexed as empty. A
password-protected file is unreadable as `password protected`, and a damaged
one as `damaged, or not a PDF`.

| Limit | Value | Reported as |
|---|---|---|
| File size | 200 MB | `larger than 200 MB, the most this reader reads` |
| Pages | 5000 | `has N pages, more than the 5000 this reader reads` |
| Bytes every compressed stream decodes to, together | 512 MB | `its compressed streams expand past 512 MB, the most this reader decodes` |
| Text | 10,000,000 characters | `gives more than 10000000 characters of text, the most this reader keeps` |
| Time per file, checked between pages and at every stream | 60 s | `took longer than 60 seconds to read` |
| How deep a page's content may nest | 256 | `nests its structure more than 256 deep, the most this reader follows` |

Nesting that deep in the file's own objects, outside a page's content, is
dropped by the library's lenient parser, and such a page reads as having no
text. A stream compressed with Flate is measured before it is decoded, so a small
file built to inflate far past the limit is refused before the memory is
taken. If a reading does not come back to a check within 30 seconds past the
time limit, the run reports the file with the same reason and moves on to the
next one.

### The Word reader

The Word reader claims `.docx`, `.docm` and `.doc`, and reads a `.docx` with
the zip and XML readers of the .NET base library and no other package. It
reads headings, found by their style (through the styles it is based on) or
by outline level, as headings, so a passage's heading path is the document's
own; paragraphs, list items and the text of table cells (one row to a line,
the cells separated by `|`), in document order; tracked changes as they would
print once accepted, so inserted text is read and deleted text is not; the
text of hyperlinks and the results of fields; footnotes and endnotes after the
body, under the headings `Footnotes` and `Endnotes`; and the title the file
states in its properties.

A text box is read once, after the paragraph it is anchored in, or on the
line of the table cell it is anchored in; a text box inside a text box is
read after the paragraph of the box around it. Comments, headers and footers,
pictures and embedded objects are not read. A hyperlink's address, a field's instruction, a linked template and
an external picture are never resolved, and no part is parsed with a document
type definition, so an entity cannot name a file or an address.

A `.doc` is skipped as `.doc (legacy .doc)`, and so is a legacy file saved
under a `.docx` name, as `.docx (legacy .doc)`. A `.docm`, or any file that
carries a macro project whatever its name, is skipped as `macro-enabled` and
opened no further; its macros are never read or run. A password-protected file
is unreadable as `password protected`, and a damaged one as
`damaged, or not a Word file`. A file that holds two parts under a name the
reader reads is unreadable as `holds two parts named <name>`, and so is one
where a part the reader reads is itself an archive, as
`holds an archive where the part <name> should be`.

A `.docx` is a zip, and a zip can be built to expand without end, so most of
the limits are about the container:

| Limit | Value | Reported as |
|---|---|---|
| File size | 200 MB | `larger than 200 MB, the most this reader reads` |
| Entries in the zip, counted from its end record before it is opened, and again once it is open | 5000 | `holds N entries, more than the 5000 this reader opens` |
| What the zip says it expands to, every entry together | 1024 MB | `says it expands to more than 1024 MB, the most this reader opens` |
| What the parts it reads really expand to, together, counted as they are read | 64 MB | `its text parts expand past 64 MB, the most this reader reads` |
| How many times its size in the file one of those parts may expand to, once it is past 1 MB | 100 times | `the part <name> expands more than 100 times its size in the file` |
| Text | 10,000,000 characters | `gives more than 10000000 characters of text, the most this reader keeps` |
| Time per file, checked as it reads | 60 s | `took longer than 60 seconds to read` |
| How deep a part's XML may nest, measured before anything in it is read | 256 | `the part word/document.xml nests its XML more than 256 deep, the most this reader follows` |

A table inside a cell is read into that cell once, however many tables
surround it.

### The Excel reader

The Excel reader claims `.xlsx`, `.xlsm`, `.xlsb` and `.xls`, and reads a `.xlsx` or a `.xlsb` with
the zip and XML readers of the .NET base library and no other package, on the
same container limits as the Word reader. The workbook's file name is the
first heading and each worksheet a heading under it, so a passage from the
sheet "Dock" of `stock-count.xlsx` is cited under `stock-count.xlsx > Dock`.
Each row is a paragraph, its cells left to right with a tab between them and
empty cells left out; a tab or line break inside a cell reads as a space, so
a tab always means the next cell. A cell reads as its value was last saved:
text as text, a number as Excel's General format shows it, or as a date or
time (`2026-03-01`, `13:30`) when the cell is styled as one, TRUE or FALSE,
and an error as written. A formula is never evaluated; its saved value is the
text, and one saved with no value is empty. Hidden sheets and hidden rows are
read: who may see a workbook is decided by the folder rules, not by how its
sheets are shown.

Comments, charts, pictures, pivot caches and defined names are not read. A
link to another workbook, a data connection, a web query and a hyperlink are
never opened, and no part is parsed with a document type definition. A `.xls`
is skipped as `.xls (legacy .xls)`. A `.xlsm`, or any workbook carrying a
macro project or an Excel 4 macro sheet, is skipped as `macro-enabled`; its
macros are never read or run. A password-protected file is unreadable as
`password protected`, and a damaged one as `damaged, or not an Excel file`.

A binary workbook (`.xlsb`) keeps its sheets, shared strings and styles in
Excel's own binary records (MS-XLSB) rather than XML; the Excel reader reads
those records, with no package, to the same text as the same workbook saved
as `.xlsx`, under the same limits. Its main part decides how a workbook is
read, not its name, so a binary workbook under a `.xlsx` name is read as one.
A binary workbook is refused whole when a record runs past the end of its
part or a cell names a shared string past the end of the table.

| Limit | Value | Reported as |
|---|---|---|
| The container limits | as for the Word reader | as for the Word reader |
| Sheets | 256 | `holds N sheets, more than the 256 this reader reads` |
| Cells read, over every sheet, empty cells included | 1,000,000 | `holds more than 1000000 cells, the most this reader reads` |
| Entries in the shared string table | the cell limit | `holds more than 1000000 shared strings, more than the cells this reader reads` |
| Text | 10,000,000 characters | `gives more than 10000000 characters of text, the most this reader keeps` |
| Time per file, checked at every row | 60 s | `took longer than 60 seconds to read` |

Empty cells count because a sheet of them gives no text for the text limit
to count; a sheet's own statement of its size is never read.

## What a run did not read

A file in a format no loaded reader reads (a PDF, Word or Excel file while its
reader is not loaded) is not indexed, and every run says so, by
extension, so a folder of them does not look like an empty success. A file a
reader recognized and would not index is counted under its extension and the
reason, as `.docm (macro-enabled)`. The run's summary, the portal's run and
documents pages and the support bundle all show these counts. What a skip
does to a document indexed earlier at the same path depends on who decided. A
file in a format no loaded reader reads keeps its earlier entry, and the run
names it and counts it as kept without a reader: a reader that is not
allowed, or was refused when the process started, costs nothing that was
indexed, and `prem ingest ... --remove-unread` is the only way those entries
go. A file a reader read and declined, such as a PDF saved again without its
text layer, is removed at the end of the run, named as "removed, now skipped"
with the reader's reason, and counted, since the text indexed earlier is no
longer in the file. An empty file is removed too. A file that exists and could not be read is counted as
unreadable, listed with its reason, and keeps its existing index entry. Hidden
files and folders are left out and not counted.

## Links, junctions, pipes and devices

A folder source reads only regular files, each at the path it was listed
under. The listing leaves
out links and junctions, but a file or a folder can be replaced by one between
the listing and the read, and opening a file follows it. So each file is
checked again once it is open, on the open file itself, which is the very file
that would be read:

- A file whose final path, with every link and junction followed, is not
  the source's folder joined with the path it was listed under is unreadable
  as `it resolves through a link or a junction to a file other than the one
  listed, so it was not read`. That holds for a link that stays inside the
  folder too: who may read a document is decided from the path it was listed
  under, so a link from `public/sub` to `hr` would otherwise index HR's text
  under the rule for `public`. The folder itself may be reached through a
  link; it is resolved once, when the run starts.
- A pipe, a device or a socket is unreadable as `it is not a regular file (a
  pipe, a device or a socket), so it was not read`. It is never waited on: a
  pipe with no writer would otherwise hold the run, and the lock the run
  holds, for as long as nothing writes to it.

Either is one unreadable file like any other: the run names it, keeps its
existing index entry and goes on, so a link put in a file's place cannot
remove the file's entry either. The check is made on Windows, from the open
file's final path, and on Linux, where the file is opened without waiting and
its path is read from `/proc/self/fd`. On any other system only the listing's
check is made.

Two limits remain. A folder on a network share is checked only as far as this
machine can see: the file server follows its own junctions and links before
it answers, so a junction on the server leads wherever it points and the path
this machine gets back is still the listed one. Keep a source on a local disk,
or on a share whose junctions and links only people you trust can make. And
on Windows, opening a file waits while another program holds an opportunistic
lock on it and has not yet let it go, as a sync client or a virus scanner can
for a moment; a program that holds one and never lets go holds the run with
it.

The bundle's `index.md`, read for its `okf_version` when a folder is read with
`--okf-bundle`, is opened the same way and read to the same 256 MB limit; an
index that is refused, or larger than that, declares no version.
