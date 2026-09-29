# pdf-reader

A first-party extension that reads the text layer of PDF files (`.pdf`). It
ships in this repository and is built with the solution, and like any
extension it loads only once an administrator allows it.

## What it reads

- The text of each page that has any, in the order the file draws it, under
  a heading `Page N`. A passage's heading path, which a citation carries,
  names its page.
- The title the file states, when it states one; otherwise the connector falls
  back to the file name.

## What it does not read or do

- It never follows anything: links, launch, submit and go-to actions, scripts
  and embedded files are not read and not opened. The library it uses,
  [PdfPig](https://github.com/UglyToad/PdfPig) 0.1.16 (Apache 2.0), has no
  network code in it, and the reader's tests check that a file pointing at an
  address on this machine is read without anything connecting to it.
- It does not recognize characters in pictures. A file with no text on any
  page is skipped with the reason `no text layer`, so a folder of scans shows
  up in the run's counts instead of looking empty.
- It never reads part of a file. A file over a limit is reported unreadable
  with the limit it crossed, and its existing index entry is kept.

## Limits

| Limit | Default | Reported as |
|---|---|---|
| File size | 200 MB | `larger than 200 MB, the most this reader reads` |
| Pages | 5000 | `has N pages, more than the 5000 this reader reads` |
| Bytes every compressed stream decodes to, together | 512 MB | `its compressed streams expand past 512 MB, the most this reader decodes` |
| Text | 10,000,000 characters | `gives more than 10000000 characters of text, the most this reader keeps` |
| Time per file | 60 s, checked between pages and at every stream | `took longer than 60 seconds to read` |
| How deep a page's content nests (arrays inside one another) | 256 | `nests its structure more than 256 deep, the most this reader follows` |

A stream compressed with Flate is measured before the library decodes it, so
a small file built to inflate to gigabytes is refused before the memory is
taken. If the library does not come back to a check within 30 seconds past the
time limit, the run reports the file and moves on to the next one.

The library parses nesting by recursion and stops at the depth it is given, so
a file built to nest without end is refused rather than overflowing the stack,
which would end the process. Nesting that deep in the file's own objects,
rather than in a page's content, is dropped by the library's lenient parser,
and such a page reads as having no text.

A password-protected file is reported unreadable as `password protected`, and
a damaged one as `damaged, or not a PDF`.

## Installing it

Build the solution in Release, then copy the build output to the deployment's
extensions folder (the setting `extensions.folder`, or `PREM_EXTENSIONS_DIR`)
and allow it:

```bash
dotnet build extensions/pdf-reader -c Release
cp -r extensions/pdf-reader/bin/Release/net10.0 /opt/premagentic/extensions/pdf-reader
prem extensions allow /opt/premagentic/extensions/pdf-reader
```

`allow` measures the reader's assembly and each of the seven PdfPig
assemblies its manifest lists, and allows the extension by one hash that
covers all of them. It is used from the next start of the service.

The manifest declares reader seam version 2, which this version offers.
