# Known state

What has been built and tested as of the date below, how the guards were checked, what the clean-install script and the archive proofs show, the ONNX Runtime version and why, what is not built, and the CI gate.

## Known state, 2026-09-27

Built and tested: the four gates, users, groups, agents and tokens with CLI
administration, folder rules, password sign-in and sessions, agent tokens and
MCP over HTTP, trust settings with an append-only change record that every
administrator change on every surface writes to, and that the portal's forms
to enable and disable a user or an agent leave alone when they change nothing,
retrieval tuning read from settings on every search, a sources registry with
recorded ingest runs and a chunker and a reader chosen per source, the reader
seam at version 2, where a reader may skip a file it recognizes or report one
unreadable, each with its reason, and a reader that throws on one file is
reported for that file while the run goes on, readers for PDF, Word
(`.docx`) and Excel (`.xlsx` and `.xlsb`) files as first-party extensions that load only once an administrator
allows them, reading text and nothing else and refusing whole a file over any
of their limits, a Word part nested past 256 refused before anything in it
is read and a table inside a cell read once however many tables surround
it, a document whose reader is not loaded now kept with its entry and
removed only by `prem ingest --remove-unread` while a file a reader read
and declined is removed and counted, the extension host with its allow list and `prem extensions`,
every file an extension loads listed in its manifest and covered by the
allowed hash, native libraries per platform under `runtimes/<rid>/native/`,
the conformance kit, with fixtures for the sign-in and reminder seams,
packable as a package whose version is the product version it is built with and whose fixtures check that the seams it proves are
the ones the PremAgentic it runs with offers, and three sample extensions
proven end to end (one with a library of its own, one with a native library
for two platforms, one a reminder sink that sends mail), where each agent's
model runs with the reserved hosted-model group, one seam for the ways a
person signs in, principal mapping as an extension point (the `principals`
seam, with nothing mapped when no mapper is loaded; directory group mapping
itself comes with PremAgentic for Teams), per-deployment tool
text, a deployment's instructions for assistants sent at connect and served as
a resource (in the `server/discover` result, and in the `initialize` result
to a client of an earlier revision), the MCP authorization flow built and
off by default (while off none of its paths is mapped and no table of it is
read or written, proven from the database's own statistics), a disable or
a password change ending a person's own assistant credentials for good,
the usage read model, audit retention with `prem audit prune` and the
windowed audit export with `prem audit export` (both now come with
PremAgentic for Teams), profiles validated, applied
and shown, carrying every settable setting, with `groups.json` applied before
sources and rules and a second apply that changes nothing, sources with an
owner and the hosted-model switch, self-serve agents bounded by
`agents.self_service_max` and told apart by who made them, an agent's key
reissued in one step and an agent removed with its history kept, the reminders seam
and `prem reminders run` with the latest run kept for the portal, every
setting in one catalog read by `prem settings`, a command line that answers
`--help`, `<command> --help` and `--version` with no database and prints one
usage text per command from which `docs/cli.md` is generated, the connector
abstraction, the filesystem connector, ingest with permission-change
detection, prefix-scoped orphan reconciliation and skipped formats counted,
and a reader's skips under their reasons, Open Knowledge Format metadata and
bundle mode, hybrid retrieval with the vector leg in process on stock
PostgreSQL, numbered migrations with a stale text match body reported as
pending work and a backfill tested across the upgrade that runs it, with the
migration runner stopped at the version before, `prem setup` for an existing
PostgreSQL (14 or later, tested on 17) with a least-privilege application
role, a search role behind row-level security that a deployment by connection
string cannot silently run without, the first administrator, HTTPS, service
hosting, a bundled PostgreSQL on Windows and a trusted-proxy setting,
`prem remove`, the administration portal with the review queue, the reminders
view, the tuning page judged by the golden set, the usage page paged and
filtered, the export page (its audit trail download now comes with
PremAgentic for Teams), the connect page with the instructions
block and ChatGPT's and Claude Desktop's texts saying what a client called
from its vendor's cloud cannot reach, the agents page saying who made each
agent, the health page naming the model, the usage, audit and Sources pages and
`prem sources list` describing what they show as served to agents registered
as using a hosted model, the CLI, the HTTP and MCP surfaces, the evaluation
harness, the fully local reference stack with its offline loop proof,
self-contained release archives for Linux x64 and Windows x64 built from a
commit by `scripts/release/build.sh`, with a release workflow that makes only
a draft release from a tag, and the bundled PostgreSQL and credentials file
tests run in CI on a Windows runner. A document form on the Clients page that is past its own bounds (more
than 128 KB in its document or 320 KB in all) is answered on the Clients
page with the flow's sentence, before the page runs and with nothing
written; the whole-body bound is proven on Kestrel, since the in-process
test server does not keep it, and over HTTP/1.1 a browser may show a form
past 320 KB as a dropped connection. Every other portal form past the
framework's own bounds (4 MB a value) is still refused with a bare server
error. In the run of 2026-09-29, 2181 tests pass: 1990 in Premagentic.Tests,
where 7 more were skipped on that machine, and 191 in
Premagentic.Readers.Tests. The guards are checked
by breaking them, and the lists of those breaks are in the repository:
`tests/mutants/` holds 67 lists with the runner that applies them one at a time
and counts a break only when a named test fails between two passing controls
(`python tests/mutants/run.py --check tests/mutants/*.json` checks, on every
push in CI, that every list still fits the code; `run.py <list>` reruns
one). The counts that follow were hand runs made before the runner existed, and
the lists were converted from them, so a list can hold a mutant more or fewer
than the count given; four lists were rerun through the runner on 2026-09-25
(the access gate, the trust and freshness gates, the extension host and the
authorization flow's loose ends), and the lists added since (the binary
workbook skip and later its read, Word's text boxes and how a reader takes the file it is
handed; the Excel reader through the extension host, the manifest's size cap,
what the pipeline hands a reader and the per-platform managed refusal; the
clients seam's document add and replace; the request bound on `/mcp`; the document forms' bounds and the Clients
page they answer with) and
the identity seams list, narrowed to the classes of each area, were run
through it as well, each catching every mutant. The guards were
checked by breaking them: one-line changes to the access gate (fourteen), the
trust and freshness gates (twelve), the sign-in, session and caller guards of
the HTTP and MCP surfaces (twenty-two), setup, the certificate and the service
registration (twenty-two), the settings, change record and sources guards
(forty-two), the bundled database, the proxy and the upgrade (forty), the
second line and the locks (forty-six, of which two survive by design: with the
application's gate removed, the database policy holds), the portal (nineteen),
the text match function's single list condition and the tuning loader
(twenty-three, of which one survives by design for the same reason), the
chunker seam, the review queue, the settings command, the tuning page and the
golden set run (forty-two), removal, the pending kind, the telemetry guard and
the provider refusal (twenty-seven, two of them in a Linux container), the
extension host, the composition point and the sample (fifteen), the model
location, the sign-in seam, the mapping, the tool text and the retention
(forty-three; the mapping and the retention now come with the business
add-on), the reader seam, the profile, the switch and the portal
(thirty-two), the help answered before the database, the usage and parser
agreement, the listed files and their hashes, the pinned download and the
reminders job (eighteen, and the download script's by hand on both scripts),
the settings catalog, the self-serve bounds, the usage window, the rules
record, the search role refusal and the headers (twenty-seven), the two pages,
the token shown once, the form at zero and the profile's groups (twenty-one,
of which one survives by design: the page refuses a post at zero and so does
the store), the form posted with a null origin and the unreadable stale date
(three), the runtime folders, the native measure-before-load, the mail sink's
rules and the loop proof's reach-out control (nine, and the control in the
container), the instructions at connect and as a resource, the agent's origin
by every path, every verb's record row and the health model (twenty-one), the
instructions block, the self-made filter, the paging bound, the export window
and the health page (twelve), the reader seam's second version, the PDF and
Word readers and the packed kit (forty), the forms that change nothing, the
backfill, the migration runner, the renamed labels and the `groups remove`
refusal (nine), the connect page texts (six), the readers' nesting
refusals, the nested table read once and the entries kept without a reader
(twelve), the flow's settings, stores, endpoints, off-proof and credential
generations (eighty), the flow's pages, the connect page's grant list and
count, the health line and the address given to an assistant (twenty-one),
and the earlier-revision client at initialize (one),
the vector leg and the migration lock each fail the suite, and each of the two ingest safety fixes is
backed by a test that fails when that fix is reverted. The release build's
refusals of a model file off its pin, a package with no license text, an
archive missing a required file and a NuGet packages folder that does not
exist were each shown to fire beside a control that passes, and each was
caught when disabled; so were its refusal of a reader file that is missing or
changed and of a build file left beside a reader, and the host's refusal of an
extension built against a newer PremAgentic (five).

`scripts/clean-install/run.sh` installs the Linux release archive itself, the
file a person downloads, on a bare Ubuntu container with no .NET and no
network route out: the archive given to it with its `SHA256SUMS`, or one it
builds from the commit with the release script, and it prints the archive's
`SHA256SUMS` line so a run names the bytes it proved. It follows the archive's
`INSTALL.txt` step by step, printing each step's text and checking that the
step shows every command the run uses for it, and proves the shipped Unicode
library, the first administrator, the API answering over HTTPS as the
application role, people signing in and searching over HTTPS, an agent
searching over MCP with its token, the PDF, Word and Excel readers the archive
ships allowed by hash, and an upgrade to this one from the previous release's
archive, built from its tag, or from a commit `PREM_UPGRADE_FROM` names, on an
install that holds data, with a rollback; with no earlier release it says in
one line that the upgrade did not run. For the readers it writes
an invented PDF, an invented Word file and a macro-enabled file beside the
sample workbook, shows all four skipped before the readers are allowed,
allows the three by hash, ingests the folder, where the macro-enabled file is
skipped with its reason, restarts the API as `INSTALL.txt` says, and has the
agent find a passage of the PDF, cited by its page, of the Word file, and of
the workbook, cited by its sheet, over MCP. With `--offline` it runs all of that
except the upgrade with no network at all: first a deliberate outbound attempt
fails and the cloud embedding provider refuses in one line, then the quick
start and the readers' ingest succeed, then the fully local loop runs, an
agent whose model runs locally asking over MCP and a stand-in model server
answering from the passages, and under strace both ingests (the second through
the readers) and both starts of the API connected to nothing but the database, the loop
client to nothing but the API and the stand-in, and the stand-in to nothing; a
control that makes the stand-in reach out fails the run. It is run by hand
after each batch of merges, and by the release workflow. The last run was green
in both modes on `main` of 2026-09-27, installing the Linux release archive
built from that commit: online, the upgrade from an archive built at an earlier
commit (whose migrations ended at 0080) applied `0095_oauth_client_document`,
the one migration added since, kept the administrator and the documents, served
them, and rolled back; offline, the readers' ingest and the fully local loop
ran under the trace with every connection to the database, the API or the
stand-in and none elsewhere. The control that makes the stand-in reach out
failed the run on that connect, as it must.
The README quick start was last run from an empty database on `main` of
2026-09-24: the pinned download verified, both ingests, the gate both
ways, the golden set 5 of 5 from the starter profile's file.

The release archives were installed outside the source tree.
`scripts/release/prove-linux-archive.sh` gives a bare Ubuntu 24.04 container,
beside a stock PostgreSQL 17.11 on an internal network, nothing but the Linux
archive: no .NET, no system ICU, no route out and no outside name
resolution. Following the archive's `INSTALL.txt` as written, setup found the
model inside the archive, the starter profile was applied and both its
sources ingested, the API answered over HTTPS with the certificate and host
name verified, and an agent's search over MCP found what its folder rules
give it while engineering did not reach the salary bands; with the model
folder moved away, a search for the same words stopped with exit 2 and one
line naming where the model was looked for.
`scripts/release/prove-windows-archive.ps1` did the same with the Windows
archive, unzipped outside the repository with `dotnet` off the path and no
`DOTNET_` variable set, against a throwaway database on a development
PostgreSQL: the API's runtime was loaded from the archive and from no .NET
install, and the throwaway database, its roles and the folder were gone
afterwards. Both passed. Builds of one commit from before the
readers were merged, in different folders on one machine with one SDK, were
byte-identical. The release workflow's dry run on GitHub built both archives
and ran `scripts/release/test.sh` green on ubuntu-latest. A NuGet packages folder that does not exist is one
of the refusals that test proves.
An extension's test project outside the solution restored the packed
conformance kit from a local folder, and all 9 of that project's tests
passed.

Not yet proven: the bundled database service starting under its virtual
account (the bundled PostgreSQL is not in the release archives), and a
managed PostgreSQL. The Windows service and `prem remove` are proven on a
clean Windows machine; see the Sandbox runs below. The systemd unit was run on a
fresh Ubuntu 24.04.5 whose PID 1 is systemd (a WSL2 distribution imported from
Canonical's pinned image for the proof and removed after), with PostgreSQL 16
from Ubuntu's archive at a pinned snapshot: the Linux archive installed by its
`INSTALL.txt` and the unit set up as its own comments say, it ran as its
service account with systemd handing it its four credential files and never
the owner's, answered `/health` and a signed-in search over HTTPS, came back
after `systemctl restart`, and left no password or token in its journal; with
the unit stopped, `/health` got no answer. Both archive proofs start the MCP
bridge from `bin/`; on Linux it searches through the API. Both allow the
readers from inside the archive, after a control that shows them skipped
before they are allowed, and find a PDF passage by its page, a Word passage
and an Excel passage by its sheet over MCP. The Windows archive carries the four
Visual C++ runtime files its ONNX Runtime library imports (`msvcp140.dll`,
`msvcp140_1.dll`, `vcruntime140.dll` and `vcruntime140_1.dll`, version
14.44.35211.0, unmodified from a pinned Microsoft redistributable), because
a clean Windows has none: on an archive without them, `prem setup` on a
clean Windows stops where it first loads the model (shown in Windows
Sandbox on 2026-09-26). On a clean Windows 11 in Windows Sandbox, with no
.NET, no network and no Visual C++ runtime in System32 or on the PATH, the
Windows archive installed by its `INSTALL.txt`, ran from a prompt and as
the Windows service `Premagentic` under `NT SERVICE\Premagentic`, loaded its
local model with the runtime files it ships as the only ones its programs
could find, and answered `/health` over HTTPS; the PostgreSQL the proof ran
kept its own copy of those files beside its own programs. On the same clean
machine, `prem remove --purge --yes --windows-service` took the service,
the database, its three roles and the credentials files away, each checked
before and after; it keeps the credentials folder while anything else is in
it (there, the starter profile's golden set). No release has
been published: archives are built by
`scripts/release/build.sh` and by the release workflow, which makes only a
draft from a tag. Not run on a real client yet: the connect page's Claude
Desktop and VS Code Copilot configurations, checked field by field against
each client's documentation on 2026-09-24; the Claude Code and stdio bridge
ones are the proven pair. Claude's custom connectors are called from
Anthropic's cloud, so Claude Desktop uses the local stdio bridge. ChatGPT
calls MCP servers from OpenAI's cloud and cannot reach a server inside the
network directly; OpenAI documents an outbound tunnel that may carry the
stdio bridge, and it has not been tried here. The offline proof has run with a real local
model server (llama.cpp's `llama-server` b11191 with a 0.5B model), which
answered from the passages, offline, with nothing connecting out
(2026-09-27, twice); Linux builds of `llama-server` need the OpenMP runtime
(`libgomp1`). No chat client has been tried against the fully local layout.

An assistant that names itself by a web address is registered from its
metadata document by hand; the server fetches nothing. A deployment runs one
API process, and the authorization flow's failure throttle and hourly
registration count are held in that process.

ONNX Runtime stays at 1.27.0. Its 1.30.0 build for Linux was measured
uploading usage telemetry to its publisher from a long-running process
(TLS connections to a collector during the API run, none during setup, ingest
or a CLI search), while 1.27.0 made no outbound connection at all; the
`ORT_DISABLE_TELEMETRY` environment variable stops it and the runtime's own
API switch does not. PremAgentic now sets that variable itself before the
runtime loads, in the native environment on Linux, with a test, and a later
version is taken only after the same measurement on Linux and on Windows.

Not built: sign-in through a company directory, any connector beyond the
filesystem, reading a file share's own permissions, recognizing text in
pictures or scanned pages, an installer package, a calibration command for
the no-answer floor, and an administration API. A managed library built for
one platform in an extension is refused when it is allowed and at load,
with the way round named;
loading one per platform is not built. The MCP authorization flow is built
and off by default, and has been run end to end with a
headless browser and a script standing in for the assistant (discovery,
registration, sign-in, consent, the code exchange, a search, a refresh, a
revoke that stops the next call); no assistant of a vendor's has been run
against it yet.

CI builds with NuGet audit findings as errors, so a dependency that picks up a
known advisory fails the build. That gate was checked in both directions: it
failed on `SSH.NET` 2025.1.0 (two high-severity advisories, pulled in by
`Testcontainers.PostgreSql` 4.13.0, test-only) and passes now that the test
project lifts it to the patched 2026.0.0.

CI also runs a Windows job: the bundled PostgreSQL's setup and removal tests
and the credentials file tests on `windows-latest`, where their Windows
paths run instead of returning at once as they do on Linux. A test that
returns at once also counts as passed, so a green run alone cannot tell;
the bundled server test makes, starts and stops a whole cluster, and the job
reads the results and fails when that test took under 2 seconds. On the
hosted runner the job took 170 s and that test 16.95 s, and the check fails
a run where the test returned early. `SetupInstallTests`' Windows tests are
not in the job: that class starts a Linux PostgreSQL container, which this
job does not provide.
