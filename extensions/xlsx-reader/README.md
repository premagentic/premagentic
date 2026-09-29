# xlsx-reader

A first-party extension that reads Excel workbooks (`.xlsx`, and the binary
`.xlsb`). It uses the zip and XML readers of the .NET base library and no
other package; the binary workbook's records are read by the reader's own
code. It ships in
this repository and is built with the solution, and like any extension it
loads only once an administrator allows it.

## What it reads

- Each sheet, in the workbook's order, under a heading of its name, with the
  workbook's file name as the heading above them all. A passage's heading
  path, which a citation carries, is `stock-count.xlsx > Dock`.
- Each row as one paragraph, its cells left to right with a tab between them,
  and an empty cell left out. A tab is what a spreadsheet puts between cells
  when a range is copied, so a cited row pasted back into one lands in
  columns; and since a tab or a line break inside a cell is read as a space,
  a tab in a row always means the next cell, which a space could not.
- A cell as its value was last saved. A shared or inline string is its text,
  its phonetic guide left out. A number reads as Excel's General format shows
  it (`0.3`, `1234.5`), and as a date or a time (`2026-03-01`,
  `2026-03-01 18:00`, `13:30`) when the cell is styled as one, in either of
  Excel's date systems; elapsed time (`[h]:mm`) stays a number. `TRUE` and
  `FALSE` read as themselves, and an error as written (`#DIV/0!`).
- A formula is never evaluated. Its saved result is the text, and a formula
  saved with no result is an empty cell. The formula itself is not read.
- Hidden sheets and hidden rows are read, as a person searching would expect:
  who may see a workbook is decided by the folder rules, not by how its sheets
  are shown.
- The title the file states in its properties, when it states one.
- A binary workbook (`.xlsb`), whose sheets, shared strings and styles are
  Excel's own binary records (the format Microsoft documents as MS-XLSB)
  rather than XML, reads to the same text as the same workbook saved as
  `.xlsx`. The workbook's main part, not its name, decides which it is: a
  binary workbook under a `.xlsx` name is read as one, and a `.xlsx` under a
  `.xlsb` name is read as XML. Its text is kept as the file holds it, with
  control characters read as spaces.

## What it does not read or do

- Comments, charts and chart sheets, pictures, pivot caches and defined names
  are not read.
- Nothing is followed. A link to another workbook, a data connection, a web
  query, a query table, a hyperlink and a picture by address are never
  opened, and a value that came through one is read only as it was saved. No
  part is parsed with a document type definition, so an entity cannot name a
  file or an address. The reader's tests check that a workbook pointing at an
  address on this machine in each of these ways is read without anything
  connecting to it.
- Macros are never read or run. A `.xlsm`, or any workbook that carries a
  macro project or an Excel 4 macro sheet whatever its name, is skipped with
  the reason `macro-enabled`.
- A legacy `.xls` is skipped with the reason `legacy .xls`.
- It never reads part of a file. A file over a limit is reported unreadable
  with the limit it crossed, and its existing index entry is kept.

## Limits

A `.xlsx` or a `.xlsb` is a zip, so the first limits are about the
container, the same ones the Word reader keeps; the rest are about the
workbook inside it, and hold for both. A binary workbook's record parts are
flat, one record after another, so the depth limit holds for its XML parts
only.

| Limit | Default | Reported as |
|---|---|---|
| File size | 200 MB | `larger than 200 MB, the most this reader reads` |
| Entries in the zip, counted from its end record before it is opened | 5000 | `holds N entries, more than the 5000 this reader opens` |
| What the zip says it expands to, every entry together | 1024 MB | `says it expands to more than 1024 MB, the most this reader opens` |
| What the parts it reads really expand to, counted as they are read | 64 MB | `its text parts expand past 64 MB, the most this reader reads` |
| How far one of those parts expands past 1 MB, against its size in the file | 100 times | `the part xl/worksheets/sheet1.xml expands more than 100 times its size in the file` |
| How deep the XML of a part it reads nests, measured before it is read | 256 | `the part xl/sharedStrings.xml nests its XML more than 256 deep, the most this reader follows` |
| Sheets | 256 | `holds N sheets, more than the 256 this reader reads` |
| Cells read, over every sheet, empty cells included | 1,000,000 | `holds more than 1000000 cells, the most this reader reads` |
| Entries in the shared string table | the cell limit | `holds more than 1000000 shared strings, more than the cells this reader reads` |
| Text | 10,000,000 characters | `gives more than 10000000 characters of text, the most this reader keeps` |
| Time per file, checked at every row | 60 s | `took longer than 60 seconds to read` |

Empty cells count toward the cell limit because a sheet of them gives no text
for the text limit to count: a sheet of every row Excel allows with one empty
cell each is refused by the cell limit, and by the time limit without it. A
sheet's own statement of its size is not read, so a sheet that says it holds
a billion cells costs only the cells it has. A part it reads that is itself
an archive, and two parts with one name, are refused. A password-protected
file is reported unreadable as `password protected`, and a damaged one as
`damaged, or not an Excel file`.

A binary workbook is refused whole, never read in part, when a record runs
past the end of its part (`the part xl/worksheets/sheet1.bin holds a record
that runs past its end`), when a cell names a shared string past the end of
the table (`holds a cell that names shared string N, past the M its table
holds`), and as damaged when a field runs past the end of its record or a
part does not begin as its kind must.

## Installing it

Build the solution in Release, then copy the build output to the deployment's
extensions folder (the setting `extensions.folder`, or `PREM_EXTENSIONS_DIR`)
and allow it:

```bash
dotnet build extensions/xlsx-reader -c Release
cp -r extensions/xlsx-reader/bin/Release/net10.0 /opt/premagentic/extensions/xlsx-reader
prem extensions allow /opt/premagentic/extensions/xlsx-reader
```

It is used from the next start of the service. The manifest declares reader
seam version 2, which this version offers.
