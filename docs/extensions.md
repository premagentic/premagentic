# Extensions

How a reader, a chunker, an embedding provider, a way of signing in, a command, a setting or a principal mapper is added from a folder without a change to this repository, allowed by hash, loaded from the bytes that were checked, and refused with a reason.

A reader, a chunker, an embedding provider, a way of signing in, a `prem`
command, a setting or a principal mapper can be added without a change to
this repository. An extension is a folder holding
one assembly and an `extension.json` beside it:

```json
{
  "name": "sentence-chunker",
  "version": "1.0.0",
  "assemblyFile": "SentenceChunker.dll",
  "sha256": "0a1b...",
  "seams": { "chunker": 1, "reader": 1, "signin": 1 },
  "files": [ { "file": "SentenceRules.dll", "sha256": "9c2d..." } ]
}
```

`files` is optional: every other file the extension loads, managed or
unmanaged, by name in the extension's own folder, each with its SHA-256. A
manifest with no `files` list loads its assembly alone.

A manifest is read to 64 KB at most; a larger one is refused as a bad
manifest, `extension.json is larger than 64 KB, the most a manifest may be.`

`seams` gives, for each seam the extension uses, the version it was built
for, and a version is the least the extension needs: a host that offers
version 2 of a seam loads an extension built for version 1 or 2 of it. One
built for a later version than the host offers, or for a seam the host does
not have, is refused as `seam too new`, with a sentence naming the seam and,
when the host has it, the version it was built for and the version the host
offers. A seam's version goes up when its contract changes in a way an
extension built for the older one cannot meet, and also when a member is
added that an extension may call:
an extension that uses the new member declares the new version, so an older
host refuses it with a reason instead of failing it on the missing member
partway through a run. Adding a member an extension neither implements nor
calls leaves the version as it is. The PremAgentic an extension was built
against is checked too: one built against a newer PremAgentic than the host is
refused as `core too new`, with both versions named, and one built against the
same or an older version is given the host's own contracts library. This version offers reader 2, and version
1 of chunker, source, embedding, signin, reminder, command, setting and principals.

Reader version 2 added two members a reader may use.
`ReadDocument.Skipped("reason")` is for a file the reader recognizes and will
not index: the run counts it under its extension and the reason, as
`.pdf (no text layer)`. `UnreadableDocumentException("reason")` is for a file
that exists and cannot be read, such as a password-protected or damaged one:
the run reports the file with the reason and keeps its existing index entry.
The reason is shown to an administrator, so it says what is wrong with the
file and quotes none of its content. Whatever else a reader throws on a file
is contained to that file, which is reported unreadable, and the next file is
read. A reader that uses either member declares reader 2. One built for
reader 1 loads on this version and reads as before:
the `sentence-chunker` sample still declares reader 1, and this repository's
tests load its built manifest.

An extension may ship native libraries, one per platform, under
`runtimes/<rid>/native/` in its folder: `runtimes/win-x64/native/helper.dll`,
`runtimes/linux-x64/native/libhelper.so`. Each is listed in the manifest's
`files` with its SHA-256, by its path under the folder, with forward
slashes. The host loads the one for the platform it runs on (`<os>-<arch>`,
then `<os>`, then beside the assembly), measures it again just before
loading it, and refuses the extension if a file under `runtimes/` is not
listed. A path that leaves the folder, or that uses a backslash, a drive or
`..`, is a bad manifest. `samples/extensions/native-lines` is the shape,
with its two small binaries committed and reproducible from one C file by
`scripts/build-native-sample.sh`.

Managed libraries are loaded from beside the extension's assembly only. A
package with one managed assembly per platform lays them out under
`runtimes/<rid>/lib/<tfm>/`; an extension that carries such a library,
listed or not, is refused as a bad manifest with a sentence naming the file.
Publish the extension for its platform (`dotnet publish -r linux-x64`, for
example), which puts that platform's assembly beside the extension's own,
and list it in `files` like any other.

`prem extensions allow` refuses such a folder with the same sentence and allows
nothing, so the refusal comes when the extension is allowed rather than at the
next start.

A reminder sink (seam `reminder`, version 1) receives each delivered run of
`prem reminders run`, after the built-in sink has kept it for the portal.
The `mail-reminders` sample sends one message per owner over SMTP; its
settings are `mail.json` beside its manifest, and its password is in a
credentials file, never in `mail.json`, the manifest or a setting. `--plan`
delivers to no sink.

A command (seam `command`, version 1) adds to `prem`: a command of the
extension's own, such as `prem greeting`, or subcommands under `prem groups`,
the one built-in command that takes them. It adds and never replaces: a
built-in command, a built-in subcommand, and a command another loaded
extension already brought are refused with a sentence naming it. Its usage
text is written as a built-in one is, with a `prem <command> <subcommand>`
line for every subcommand, and an extension whose usage names other
subcommands than it registers is refused. `prem <command> --help` prints the
text with the extension that added it, and a flag the text does not name is
refused with it before anything runs. Help about a built-in command needs no
database; help about an extension's command is answered once the extension
loads, which needs the database, since the allow list is kept there. A
subcommand that changes something says so, and makes its change and its row
in the change record in one transaction, by the command line's actor, as a
built-in one does.

A setting (seam `setting`, version 1) is listed, read, checked and changed
with `prem settings` like a built-in one, and every change is in the change
record. The extension says what its key means, what it takes, what applies
when nothing is stored, and the check a value must pass before it is stored.
A built-in key, and a key under `trust.` or `extensions.`, are refused. The
key exists only while the extension is loaded: without it `prem settings`
takes no value for it, a value already stored is kept and not used, and a
profile does not set an extension's setting in this version.

An extension that registers a command or a setting and does not declare
that seam in its manifest is refused, so an older PremAgentic refuses it by
the seam's name. `prem extensions list` names what each loaded extension
adds. `samples/extensions/greeting-command` adds a command of its own, a
subcommand under `prem groups` and a setting, and this repository's tests
load it through the host and run it through the command line.

A principal mapper (seam `principals`, version 1) says what principals from
outside mean here: the groups a sign-in adapter reports, and the principals a
connector names for a file. It answers with the ids of PremAgentic groups,
and the host keeps an answer only for a principal it asked about and only
when it names a live group an administrator made in this deployment, so a
mapper can never hand anybody `everyone`, a user, a role or a group
PremAgentic maintains itself. A deployment has one mapper: a second extension
that brings one is refused as `name taken`, with a sentence naming the first.
With no mapper, which is what PremAgentic ships with, every such group and
principal means nothing: a reported group is ignored, a file readable only by
outside principals is readable by nobody, and nothing reads an outside name
as a PremAgentic one. A mapper that throws fails the request or the ingest
run that asked; it is never read as mapping everything or nothing. An
extension that registers a mapper declares the `principals` seam, as one that
registers a command or a setting declares that seam, and is refused if it
does not.

The folder goes under the extensions folder, which is the setting
`extensions.folder` or the environment variable `PREM_EXTENSIONS_DIR`, the
setting winning. Neither one means no extensions, which is not an error.

Nothing loads until an administrator allows it:

```bash
prem extensions allow /opt/premagentic/extensions/sentence-chunker
prem extensions list
prem extensions disallow sentence-chunker
```

Documents an extension's reader indexed stay indexed after it is disallowed:
an ingest keeps them and says so, and one ingest with `--remove-unread`
removes them.

`allow` reads the manifest, computes the SHA-256 of the assembly and of every
listed file itself, prints each, and writes the name and the allowed hash to
the setting `extensions.allowed`, with an entry in the change record. The
allowed hash covers every listed file, and is the assembly's hash alone when
nothing is listed, so entries written before files could be listed still
match. It refuses when the manifest's hashes and the files disagree, rather
than writing a pair that could never match.

At startup the host reads the assembly once, hashes those bytes, and loads
the assembly from the same bytes, so the file cannot be exchanged between
the check and the load. A listed managed file is loaded the same way; an
unmanaged library can only be loaded by path, so it is measured again just
before it is loaded. A file in the folder the manifest does not list is never
loaded, and a listed file whose bytes differ refuses the whole extension with
a sentence naming the file. The pair of name and allowed hash must be in
`extensions.allowed`. An extension is refused, with a reason, when its
manifest cannot be read or used, when it names an assembly outside its own
folder, when the hash does not match, when nobody allowed the pair, when it
needs a seam, or a version of a seam, that this version does not offer, when
it will not run, when what it registers is named the same as something
already loaded, or when it brings a second principal mapper. A refusal never
stops the deployment: it starts with the built-in readers, chunkers,
embedding providers and ways of signing in, and no principal mapper, and
`prem extensions list` and the health page say what was refused and why.

Each extension loads into its own context and takes the contracts it
implements from the process, so two extensions can carry different versions
of one library and neither can bring its own copy of the core. An extension
may add an embedding provider under a new name and may not take a built-in
one, so naming a provider can never quietly stand in for the local, offline
model. An extension that brings a way of signing in is named in the log at
every start, the way the trusted-header mode is, and so is a principal
mapper. With a way of signing in from an extension and no principal mapper,
the start says so too: any group those ways report means nothing here and is
ignored, and the log names each such group once, the first time it is
reported.

An upgrade is a new assembly with a new hash, so it is allowed again on
purpose. Changing either setting applies when the service is next started,
which is also the only safe moment to replace an assembly on disk.

`samples/extensions/sentence-chunker` is a whole extension that registers a
chunker, a reader for `.csv` files and a sign-in adapter that claims nothing;
it is loaded in this repository's tests to prove the refusals and that each
seam is used end to end. Three first-party extensions, `extensions/pdf-reader`,
`extensions/docx-reader` and `extensions/xlsx-reader`, read PDF, Word and
Excel files; what they read, their limits and how to allow them are on
[Sources, readers and chunkers](sources-readers-chunkers.md#pdf-word-and-excel-files).

The conformance kit, `tests/Premagentic.Conformance`, holds xunit fixtures to
inherit that prove a reader, a chunker, a connector, an embedding provider,
a sign-in adapter or a reminder sink
keeps its contract; the built-ins and the PDF and Word readers are held to
them here. It packs as the package `Premagentic.Conformance`. Its version is
the product version it is built with, the MSBuild property `Version` (from
`Directory.Build.props`, or `-p:Version=<version>` given to `dotnet pack`),
and it carries no copy of the core: the contracts come from the PremAgentic
the extension builds against. The kit records the seam versions it proves,
read from `SeamVersions.cs` when it is built, and each fixture has a test,
`The_kit_proves_the_seams_this_extension_builds_against`, that fails when the
PremAgentic the extension builds against offers another version of that
seam, with a sentence naming the kit's version and the version to match. No
package feed carries the kit; [Writing an extension](writing-an-extension.md)
packs it into a local folder and restores it from there.

The connector fixture proves on every machine that one item a connector
cannot read does not throw and does not become a document. Left alone,
`WithAnUnreadableItemAsync` wraps the connector's source in the kit's
`OneUnreadableItem`, which holds the first item the connector hands to a
reader unreadable. Returning null from it fails the test rather than passing
it over, and so does a connector that reads its items itself, since the kit
then has nothing to hold; such a connector overrides it with a source that
holds a real unreadable item.
