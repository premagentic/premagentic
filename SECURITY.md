# Security policy

PremAgentic exists to keep an organization's documents inside that
organization and in front of only the people allowed to read them. A flaw in
that is the most serious kind of bug this project can have, and a report of
one is always answered. This policy covers PremAgentic and
PremAgentic for Teams.

## Reporting

Use GitHub's private vulnerability reporting: the **Report a vulnerability**
button under this repository's **Security** tab. If you cannot use it, write
to premagentic@agaveis.com. Please do not open a public issue for a suspected
vulnerability.

Say what you ran, what you saw, and the version or commit it ran on. The exact
sentence the program printed, or the request that got through, makes a report
the fastest to confirm.

## What counts

- Any way for a caller to retrieve, cite or fetch a document that its own
  groups, grants and the folder rules do not give it, through any surface: the
  library, the command line, the HTTP API or the MCP endpoint.
- Any way to act as a person or an agent without that person's password, a
  live session, or a live agent token: an ended session, a revoked or expired
  token, or a disabled account still reaching anything.
- Any way to learn from PremAgentic's answers which sign-in names exist. An
  unknown name is checked against a dummy hash made with the same work factor
  as a real one, so its answer costs what a wrong password costs; a way past
  that is in scope.
- A request that changes state under a session without the session's
  anti-forgery token.
- Any way for an agent to write, store or change anything through the MCP
  endpoint.
- Any way to read a row through the search role that its bound caller session
  does not permit, or with no session bound.
- A way to change a user, group, agent, token, rule or setting through the
  portal without a row in the change record, or as a member or an auditor.
- Any way to tell that a document exists when the caller may not read it.
- Any way around the lifecycle gate that serves superseded, archived, draft or
  expired material as current without the historical flag.
- A path by which document content leaves the machine. At runtime, with the
  default `local` embedding provider, there should be no outbound call at all,
  apart from the revocation status (OCSP) and missing intermediate
  certificates the runtime may fetch for an HTTPS certificate issued by a
  certificate authority; the passages each search returns reach the assistant
  that asked, and an assistant on a hosted model receives them.
- A way to make the API honor the sign-in header when `PREM_SIGN_IN_HEADER`
  is not set.
- Any way for an agent registered as using a hosted model to read a folder
  whose rule denies the hosted-model group, or for allowing that group to
  give any agent anything.
- Any way for a way of signing in, built in or added, to create an account,
  grant a role, or carry a group into PremAgentic other than through the
  deployment's principal mapper; any way for a principal mapper to make a
  caller hold anything but a live group an administrator made, or to count a
  principal it was not asked about; and any way for a deployment with no
  mapper to read an outside group or principal as meaning anything.
- Any way to load an extension whose name and hash are not in
  `extensions.allowed`, or to run an assembly, or any file an extension
  loads, other than the bytes that were hashed against it.

## What does not

The trusted-header mode honors the header named by `PREM_SIGN_IN_HEADER`
because it is designed to sit behind an authenticating proxy that sets it.
Exposing that port directly with the mode on is a deployment error, described
on the manual's Surfaces page (`docs/surfaces.md`), not a vulnerability in
PremAgentic.

Account lockout lets anyone who knows a sign-in name keep that account locked,
one guess every fifteen minutes. That is the documented cost of lockout. The
agent rate limit and the per-address sign-in throttle are held by each API
process separately.

`PREM_ALLOW_HTTP_SIGN_IN=1` sends passwords unencrypted by design. It is for
development on one machine.

When PremAgentic for Teams brings sign-in through a directory and
connectors that read a source's own permissions, they talk to the directory
and the sources an administrator points them at, by design.

PremAgentic enforces access twice: in each query, and again in the database,
which refuses every row a caller may not read. In the full-text match,
which runs inside a function so the text index stays in use, the two lines
meet in one condition: the session's lists cut down to the lists the query
asked for. Each still restricts without the other, and the tests prove it by
removing each in turn. Search and section reads connect as a third role, bound for the length of one read to a caller session.
The session holds the caller's permitted access lists, recomputed for every
read, so a revoked membership applies to the next query. The session is stored
only as a SHA-256 hash of its id. It is opened for five minutes and closed when
the read ends; the database refuses a session longer than ten. The API refuses
to start with a search role the policy would not bind: a superuser, a role
with BYPASSRLS, or one listed in `prem_config.index_writer`. The application
role, which ingests, reads the whole index by design; it is the one role
listed there. The owner, like any table owner, is not bound; use it only for
migrate and rebuild.

**The hosted-model group can only take access away.** PremAgentic maintains
one group holding every agent registered as using a hosted model. That
group is never among the principals an agent holds; it is weighed beside the
agent's own identity, on the side of the decision that narrows. So a folder
rule that denies it holds those agents back, and a rule that allows it gives
nobody anything. Marking an agent's model hosted can therefore never widen
what it reaches, and an agent acting for a person can never reach further than
that person can. An agent registered before this was added is read as hosted,
because nothing is known about it and hosted is the reading that keeps it out
of a folder kept from hosted-model agents.

**A sign-in adapter can say who you are, never what you may read.** Every way
of signing in, built in or added, answers one question: which PremAgentic
account is this request. The account has to exist; no adapter creates one, and
no adapter carries a role or a group. Groups an outside system reports become
PremAgentic groups only through a principal mapper, an extension an
administrator allowed, and only as live groups an administrator made; with no
mapper, which is how PremAgentic ships, every such group is ignored. An
adapter that lied about every name it saw would still reach only accounts an
administrator made, under the rules those accounts already sit under.

A credential that fails refuses the request where it failed. It is not passed
to the next way of signing in, so an expired session cannot become an
anonymous request, or somebody else's.

**Nothing from outside names a group here.** A sign-in adapter reports the
groups its directory saw, and a connector can report the principals a file's
permissions carry. Neither becomes a PremAgentic group except through the
deployment's principal mapper, which can name only live groups an
administrator made, and with no mapper not at all. A principal nobody mapped
is ignored at sign-in and reaches nobody at ingest, so a directory that
reported more than it should still grants nothing, and a source whose groups
are unknown is readable by no one rather than by everyone. Groups PremAgentic
maintains for itself cannot be mapped into at all.

**The core never deletes the audit trail.** Apart from `prem remove --purge`,
which drops the whole database, there is no timer, no flag and no command in
it that empties the trail. A retention, and the prune that enforces it, come
with PremAgentic for Teams; each prune is itself written to the change record,
with how many rows it deleted and how far back it went.

**An extension loads only when an administrator has allowed it with `prem
extensions allow`, and then only the bytes that were allowed.** Allowing
measures the extension's assembly and every other file its manifest lists,
and the allowed hash covers every listed file, the assembly alone when nothing
is listed, so allow list entries written before files could be listed still
match. A file changed afterwards, a file dropped beside the extension that the
manifest does not list, or a file replaced together with its line in the
manifest, is not loaded, and the extension is refused with a sentence naming
the file. A managed file is loaded from the bytes that were measured; an
unmanaged library can only be loaded by path, so it is measured again just
before it is loaded, and that is the one place the check and the load are two
steps. What the host already provides, such as the .NET runtime and
PremAgentic's own library, comes from the host. Anything else is refused with
a reason and the deployment starts with the built-ins. An extension cannot
take a built-in embedding provider's name, so a provider name in the
configuration can never quietly stand for something other than the local,
offline model. A native library an extension ships goes under
`runtimes/<platform>/native/` in its folder and is listed in its manifest
with its hash like any other file; the host loads only the listed file for
the platform it runs on, measures it again just before loading it by path,
and refuses the extension if any file under `runtimes/` is not listed.

A seam version is the least an extension needs: an extension that calls a
member added in a later version declares that version, so an older host
refuses it at load with a reason instead of failing it partway through a run.
An extension built against a newer PremAgentic than the one it runs on is
refused at load the same way, with both versions named, since it could call a
member this host does not have; one built against the same or an older
version is given the host's own contracts library.

**The PDF, Word and Excel readers fetch and follow nothing outside the file.**
They are first-party extensions under `extensions/`, and they load only when
an administrator allows them, like any other. The Word reader resolves no
relationship to anything outside the file: a hyperlink's address, a linked
template, an external picture and a field's instruction, such as one that
includes another file, are never followed. It parses no part with a document
type definition and gives its XML reader no resolver, so no entity can name a
file or an address, and every part it reads comes from inside the file,
whatever the file's relationships name. The PDF reader opens no link, no
launch, submit or go-to action, no script and no embedded file, and the
library it uses has no network code in it. The Excel reader opens no link to
another workbook, no data connection, web query or query table, no hyperlink
and no picture by address, and reads a value that came through one only as it
was saved; it parses no part with a document type definition either. All three
readers' tests point a file at an address on the machine the tests run on and
check that nothing connects to it, beside a control that shows a connection
would have been seen.

**Macros and embedded content are never read or run.** A `.docm`, and any Word
file that carries a macro project or declares its content macro-enabled
whatever its name says, is skipped with the reason `macro-enabled` and not
read further; so is a `.xlsm`, and any workbook that carries a macro project
or an Excel 4 macro sheet whatever its name. Pictures and embedded objects in
a Word file, and embedded files and attachments in a PDF, are never opened.

**A Word file's limits are counted on what really comes out of it.** A `.docx`
is a zip, and the sizes a zip states are written by whoever made it. Before
the archive is opened, the reader refuses a file over its size limit, and one
whose end record states more entries than its limit; once it is open, it
counts the entries again and holds the sizes the zip states to a total. The
parts it reads are then counted as they come out of the decompressor, against
a total for all of them together and, past a floor, against a ratio to each
part's compressed size. A part that is itself an archive, and two parts with
one name, refuse the file. An Excel workbook, `.xlsx` or `.xlsb`, is a zip
too, and is held to the same container limits.

**A PDF is held to limits on its size, its pages, the bytes it decodes, its
text and its time.** Every compressed stream counts toward one total, and a
stream compressed with Flate is measured before the library decodes it, so a
small file built to inflate to gigabytes is refused before that memory is
taken. The time limit is checked between pages and at every stream. A reading
that does not come back to a check is reported thirty seconds past the limit,
and the run moves on without waiting for it. Each reader's limits, and the
sentence each one is reported with, are listed in
`extensions/pdf-reader/README.md`, `extensions/docx-reader/README.md` and
`extensions/xlsx-reader/README.md`.

**Document text cannot pose as a heading or hide one.** A heading a reader
writes, such as a PDF's `Page 2` or a Word document's own heading, is the
locator a citation carries. A line of document text that begins with `#`
would pose as one, and a line that begins with three backticks would open a
fence that hides every heading after it, so such a line is escaped with a
backslash and read as text. Control characters other than tab and the line
break are removed.

**A reader that refuses a file or throws on it does not stop the run.** A file
over any limit is reported unreadable with the limit it crossed, never read in
part, so a document is never indexed without its last pages, and its existing
index entry is kept. Every reader, first-party or added, runs inside the
ingest process, with no process of its own, and nothing outside it bounds its
time or its memory yet. The first-party readers are bounded by their limits on
the input and on what a file inflates to, and each checks its own time limit
as it reads, so a reading stuck between checks runs on. A reader that throws
on a file has that file reported unreadable, its index entry stays, and the
next file is read. Of what a reader throws, only the run's own cancellation
passes through.

The `openai` embedding provider sends passage text to a third party by design
and is off unless selected.

## Supported versions

The project is pre-1.0. Fixes land on `main` and in the next tagged release.
