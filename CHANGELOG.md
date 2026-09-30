# Changelog

## Unreleased

### Added, 2026-09-29

- Principal mapping is an extension point, the `principals` seam, version 1.
  The caller middleware and ingest read the groups a sign-in adapter reports
  and the principals a connector names only through a principal mapper an
  extension brings, and PremAgentic keeps an answer only for a principal it
  asked about and only as a live group an administrator made: never
  `everyone`, a user, or a group it maintains. One mapper per deployment; an
  extension that brings one declares the seam. With no mapper, which is the
  default, a reported group is ignored and a file readable only by outside
  principals is readable by nobody; the start warns when an extension signs
  people in with no mapper, and the log names each ignored group once.
- Every portal page, signed in or not, links to the program's source at its
  foot.

### Changed, 2026-09-29

- The add-on is named PremAgentic for Teams, and the sentence that answers
  for one of its commands, settings or pages names it so.
- The release proof upgrades from the previous release, the nearest earlier
  `v` tag (a pre-release tag only for a pre-release), and when there is none
  it says so in one line that begins `UPGRADE NOT RUN` and passes without the
  upgrade; a shallow clone is refused, since it cannot tell. The release
  workflow runs its steps under bash with `pipefail`, so a failed proof fails
  its step.
- The proof containers are pinned by digest: `postgres:17.11` in the
  development compose file and the tests, and `ubuntu:24.04` and
  `curlimages/curl:8.22.0` in the proof scripts.

### Removed, 2026-09-29

- `prem groups map`, `prem groups unmap` and `prem groups mappings`, and the
  public Core types behind them: `IdentityMappings`, `IdentityMapping`,
  `ResolvedPrincipals` and `AdminChange.Mappings`. Directory group mapping
  comes with PremAgentic for Teams; without it, each command answers
  with one sentence naming the add-on. Mappings already stored stay in the
  database, unread.

### Added, 2026-09-28

- An extension can add `prem` commands and settings (seams `command` and
  `setting`, version 1). A command is a noun of the extension's own or a
  subcommand under `prem groups`; it adds and never replaces a built-in one. A
  setting is listed, checked and recorded by `prem settings` like a built-in
  one while the extension is loaded. `prem extensions list` names what each
  loaded extension adds.

### Removed, 2026-09-28

- Audit retention (`audit.retention_days`), `prem audit prune`,
  `prem audit export` and the portal's audit trail download
  (`/portal/export/audit.jsonl`, whole or for a window). They come with
  PremAgentic for Teams; without it, each answers with one sentence
  naming the add-on. The audit trail, its page, the usage page and the Export
  page's configuration and change record downloads are unchanged.

### Changed, 2026-09-28

- **The license is the GNU Affero General Public License 3.0 only**
  (`AGPL-3.0-only`), in place of the Apache License 2.0. `LICENSE` is the text
  gnu.org publishes, byte for byte; `NOTICE` says the program is also offered
  under a commercial license; the package license expression and the notices
  say the same. No version had been published under the earlier license.
- The release archives' `models/minilm/LICENSE-Apache-2.0.txt` comes from a
  committed copy of the Apache License 2.0, no longer from the program's own
  `LICENSE`, and `verify-archive.sh` refuses an archive whose `LICENSE` or
  model license file is not its text byte for byte.
- The changelog's headings, the roadmap and the Known state page are dated by
  their merges on main.

### Added, 2026-09-27 14:41 UTC

- The Connect page gives a person's own live assistant a new key (Reissue
  key) and removes any assistant of their own (Remove); the agent page
  reissues a live token (Reissue) and removes an agent (Remove agent). Each
  asks first, on a page with a second form and no script; a new key is
  shown once, and the old one is refused on its next call.
- `prem tokens reissue` replaces an agent's live token with a new one in one
  change, lasting as long as the old one was issued for, from now, unless
  `--days` says otherwise; a reissue never brings back a key a disable,
  a revoke or a password change ended.
- `prem agents remove` removes an agent: its tokens and grant end, it leaves
  every list, and its history stays, marked removed; its name is free again.
  `prem agents list --removed` and the agents page's "the removed ones" list
  them with when and by whom, and the audit and usage pages still name a
  removed agent, marked "(removed)".
- `scripts/clean-install/run.sh --offline` can run the fully local loop a
  second time with a real local model server (llama.cpp's llama-server with a
  0.5B model), which answered from the passages, offline, with nothing
  connecting out. It is opted into with `PREM_LLAMA_SERVER` and
  `PREM_LLAMA_MODEL`; the stand-in's run stays the default.
  `loop-client.py --any-answer` checks a real model's answer.
- When the API never starts listening, the proof prints what it was doing
  (thread states, open descriptors, whether 8443 was bound) before the
  container is removed.

### Added, 2026-09-27 00:42 UTC

- The Windows archive ships the four Visual C++ runtime files ONNX Runtime
  needs (`msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`,
  `vcruntime140_1.dll`), unmodified from a pinned Microsoft redistributable,
  so it runs on a Windows that has never had the Visual C++ Redistributable;
  the release build refuses any other bytes. Proven in Windows Sandbox: the
  archive installed on a clean Windows 11 from a prompt and as the Windows
  service, and `prem remove --purge` took it away again.
- The release workflow proves the Linux archive it built before it makes a
  draft: the clean install with no network, the upgrade from an earlier commit's archive and its rollback, and the control. `scripts/release/README.md` is
  the release checklist.
- A document form on the Clients page that is past its own bounds (more
  than 128 KB in a field or a file, or 320 KB in all) is answered on the
  Clients page with the flow's own sentence, instead of a bare error; the
  forms say their bound (at most 64 KB).
- The test launcher's suite step writes a TRX and ends the test host when a
  test runs past the blame limit, with the hung test named; the mutant
  runner grants a killed process ten seconds; a fixture behind
  `PREM_TEST_HANG` proves the limit and is never committed enabled.

### Changed, 2026-09-27 00:42 UTC

- The Excel reader reads binary workbooks (`.xlsb`), from their records, to
  the same text as the same workbook saved as `.xlsx`, with every limit the
  `.xlsx` path keeps; before, it skipped them with the reason `binary
  workbook; to have it read, save it from Excel as .xlsx`.
- `prem extensions allow` refuses an extension that carries a managed
  library built for one platform (`runtimes/<rid>/lib/`), with the sentence
  the host gives, instead of allowing a folder the host then refuses at
  every start.
- Release tooling: a zip time a zip cannot hold is refused by name; a
  runtime pack's license keeps its own file name on every host; the merge
  refuses to decide copies on a 0.0.0.0 version.
- The administrator-verb test theory runs the one verb that reads a secret
  as the command line's own process with the password on a redirected
  stdin, so a hidden console can no longer hold the whole suite (tests
  only).

### Added, 2026-09-26 06:56 UTC

- `scripts/clean-install/run.sh` installs the Linux release archive itself,
  following its `INSTALL.txt` step by step and checking that each step shows
  the commands the proof runs: the archive given as `PREM_ARCHIVE` (with its
  `SHA256SUMS`), or one built from HEAD for the run. The upgrade is archive
  to archive. The run prints the archive's `SHA256SUMS` line, so a proof
  names the bytes it proved.
- `prem oauth clients replace --metadata-file <file> --id <address>`
  replaces a stored metadata document in place. Grants stand, and a code is
  exchanged only for a redirect address the new document lists.
- The clients seam takes a metadata document's bytes, for the add and the
  replace, with the command line's checks and sentences; the portal's
  Clients page shows the hash of a client's stored document and when it was
  stored, registers a client from a pasted or uploaded document, and
  replaces one on its row.
- `mcp.max_request_bytes` bounds a request to `/mcp` at 256 KB by default.
  A larger one is refused with 413 and a JSON-RPC error.
- The Excel reader claims `.xlsb` and skips it with the reason `binary
  workbook; to have it read, save it from Excel as .xlsx`; a binary workbook
  under a `.xlsx` name is skipped the same way.
- The Word reader reads text boxes, once each, after the paragraph each is
  anchored in (in a table cell, on the cell's line).
- `tests/Premagentic.Benchmark ingest`: documents and chunks a second
  through the ingest path, spinning off and on.
- The systemd unit has been run as shipped on a fresh Ubuntu 24.04 with
  systemd: installed by the Linux archive's `INSTALL.txt`, started and
  restarted as its own service account, with its credentials handed over by
  systemd.
- CI checks the mutant runner's own rules (`run.py --self-test`).
- A benchmark workflow, run by hand, measures on GitHub's hosted Linux
  runner; its numbers describe that runner only.

### Changed, 2026-09-26 06:56 UTC

- The Windows archive no longer carries ONNX Runtime's import libraries,
  which no program reads; the archive check refuses any.
- `extension.json` is read to 64 KB at most; a larger one is refused as a
  bad manifest.
- An extension that carries a managed library built for one platform
  (`runtimes/<rid>/lib/`) is refused at load, with the file named and the
  way round; before, a listed one was never loaded and nothing said so.
- A file is held once in memory while a first-party reader reads it, not
  three times: the reader takes the array the run read the file into.
- The metadata document's refusals name "the client id" rather than
  `--id`, so they read the same at the portal.

### Added, 2026-09-25 20:38 UTC

- **Reading Excel files**, `extensions/xlsx-reader`: a first-party extension
  on the same container guards as the Word reader, now shared in
  `extensions/shared`, with no package beyond the base library. Each sheet is
  a heading under the workbook's file name, each row a paragraph with its
  cells separated by tabs, a cell as its value was last saved (a formula is
  never evaluated), hidden sheets and rows read, external links and data
  connections never followed, macro-enabled and legacy files skipped with
  their reasons, and fixed limits for sheets, cells, text and time, each
  shown in a child process against a hostile workbook before and after.
- The conformance kit proves the sign-in and reminder seams:
  `SignInAdapterConformance` and `ReminderSinkConformance`, with
  `KitFakeSignInAdapter` and `KitFakeReminderSink` as examples.
- `tests/Premagentic.Benchmark` times the search path on a synthetic index,
  in Markdown tables that name the machine they ran on, in two modes: the SQL
  gate alone, and as a deployment reads with the search role and a caller
  session ([Measuring it yourself](docs/benchmark.md)).
- `prem oauth clients add --metadata-file <path> --id <https-address>`
  registers an assistant that names itself by a web address from a copy of
  its metadata document, saved by hand. The address must match exactly, and
  nothing is fetched from it. The server keeps the document's SHA-256 and
  only the fields it uses. Migration 0095, additive.
- The mutant lists and their runner are in `tests/mutants/`, so the checks
  the Known state page counts can be rerun from the tree, and CI checks on
  every push that every list still fits the code.
- `sample-docs/spreadsheets/equipment-register.xlsx`, an invented workbook
  for the clean-install proof.

### Changed, 2026-09-25 20:38 UTC

- No file is read past 256 MB before a reader sees it, and a document holding
  a NUL or half of a character is one unreadable file at the read step;
  before, it ended the ingest run at the write.
- A profile may set the MCP authorization flow's settings. It is checked as a
  whole: the flow is never turned on without its public address, on any line
  order. A `null` in a profile's `settings.json` unsets a setting.
- The framework's request lines, which carry each request's query string,
  are off by default: the authorize address carries a client's state and
  challenge. An operator turns them on for one provider by naming their
  category.
- The .NET SDK is pinned in `global.json` (10.0.401). CI and the release
  workflow install exactly that SDK, the release build refuses any other, and
  each archive's `licenses/PACKAGES.txt` begins with the .NET runtime it
  carries (10.0.12). A runtime security patch is taken by raising the SDK
  version in `global.json`.
- The release archives are about 40 percent smaller: the command line, the
  API and the MCP bridge share one folder, `bin/`, and one .NET runtime.
  Where the three carry different copies of a library, the build keeps the
  newer (the runtime pack's copy on a tie) and refuses what it cannot order.
  Registering the Windows service from an archive install needs no
  `--api-path`.
- `INSTALL.txt` leaves out the readers' step for an archive with no readers,
  and the missing-model message says where an installed release keeps the
  model.
- CI runs setup's Windows tests against the Windows runner's own PostgreSQL.
- Every project has a `packages.lock.json`, and the five projects a release
  publishes have one per runtime beside it, so a swapped package id or a
  moved version fails the restore; CI and the release build restore in
  locked mode. After a package update, run `scripts/release/lock-files.sh`
  on the branch (a Dependabot pull request fails the check until someone
  does); `lock-files.sh --check` restores every one in locked mode.
- `scripts/clean-install/run.sh` waits for the API's own listening line (up
  to three minutes) and says how long it took; its offline control names the
  one check it failed at, and `scripts/clean-install/control.sh` counts the
  control only when that is the check it exists for.

### Added, 2026-09-25 10:22 UTC

- **Reading PDF files**, `extensions/pdf-reader`: a first-party extension,
  built with the solution and loaded only once an administrator allows it,
  like any other. It reads the title the file states and the text layer of
  each page, in the order the file draws it, under a heading `Page N`, so the
  heading path every citation carries names the page. A file with no text
  on any page is skipped as `no text layer`, so a folder of scans shows in the
  run's counts instead of looking empty; nothing recognizes characters in
  pictures. Links, actions, scripts and embedded files are never followed,
  read or opened. It uses PdfPig 0.1.16 (Apache 2.0), referenced by its exact
  id, `PdfPig`, and pinned in the reader's project only; its seven assemblies
  sit beside the reader, each listed with its hash in the manifest. A test
  fails if any project references `UglyToad.PdfPig`, a lookalike on nuget.org
  with no license and no project behind it.
- **Reading Word files**, `extensions/docx-reader`, the same way, with the
  zip and XML readers of the .NET base library and no other package:
  headings (by style or outline level) as the heading path, paragraphs, list
  items, table cells, tracked changes as they would print once accepted,
  hyperlink text, field results, footnotes and endnotes, and the title the
  file states. Comments, headers and footers, text boxes, pictures and
  embedded objects are not read. No hyperlink address, field instruction,
  linked template or external picture is resolved, and no part is parsed
  with a document type definition, so an entity cannot name a file or an
  address. A legacy `.doc` is skipped as `legacy .doc`, and a `.docm`, or any
  file that carries a macro project whatever its name, as `macro-enabled`;
  macros are never read or run.
- **Guards against hostile files.** A file over a limit is refused whole,
  never read in part, and reported unreadable with the limit it crossed; so
  are a password-protected file (`password protected`) and a damaged one.
  Unreadable keeps the file's existing index entry, where a skip would remove
  it. The PDF reader limits the file's size, its pages, the bytes its
  compressed streams decode to (a Flate stream is measured before it is
  decoded, so a small file built to inflate to gigabytes is refused before
  the memory is taken), its text and its time, and the run moves on from a
  file whose reading does not come back. The Word reader limits the file's
  size, the zip's entries (counted from its end record before it is opened),
  what the zip says it expands to and what the parts it reads really expand
  to, the compression ratio, the text and the time, and refuses a part that
  is itself an archive and two parts with one name. The limits are fixed in
  each reader, not settings, and tabled in each extension's README. Each
  reader's tests check that a file pointing at an address on this machine is
  read with nothing connecting to it, and both readers are held to the
  conformance kit's reader fixtures.
- **Reader seam version 2.** A reader may return `ReadDocument.Skipped(reason)`
  for a file it recognizes and will not index; the run counts the file under
  its extension and the reason, as `.pdf (no text layer)`, in the skip counts
  a run already reports. It may throw `UnreadableDocumentException(reason)`
  for a file it cannot read, which is reported with that reason and keeps its
  existing index entry, as a locked file's always has. An extension that uses
  either declares reader 2, so an older host refuses it with a reason; a
  reader built for version 1 loads and reads as before, and the
  sentence-chunker sample stays on reader 1 to prove it.
- **The conformance kit as a package.** `tests/Premagentic.Conformance` packs
  as `Premagentic.Conformance`, for an extension author to take by package
  reference rather than from a checkout. Its version is the product version
  it is built with (`Version`, from `Directory.Build.props` or `-p:Version`
  given to `dotnet pack`). The seams it proves are read from `SeamVersions.cs` when it
  is built, into an assembly attribute and the package description, and the
  build fails if one cannot be read; each fixture asserts at run time that
  they are the seams offered by the PremAgentic the extension builds against,
  and a mismatch fails naming the kit's version and the version to match. The
  package carries no copy of the core. Neither the release script nor the
  release workflow publishes it.
- **`OneUnreadableItem`** in the kit: left alone,
  `DocumentSourceConformance.WithAnUnreadableItemAsync` holds the first item
  a reader would read unreadable, so the rule that one unreadable item never
  throws is proved on every machine. An author overrides it with a real
  unreadable item where the system can make one.
- `scripts/clean-install/run.sh` builds both readers from the tree, writes an
  invented PDF, an invented Word file and a `.docm` with
  `make-reader-samples.py` (Python 3 is now one of its needs), shows all
  three skipped before the readers are allowed, allows both
  by hash, ingests the folder (the `.docm` skipped as `macro-enabled`) and
  finds a passage of each over MCP, the PDF's cited at `Page 2`. With
  `--offline` that ingest runs under the connection trace and connects to
  nothing but the database.
- **Release archives**, built by `scripts/release/build.sh --version X.Y.Z
  --out <folder>`: a self-contained `.tar.gz` for Linux x64 and `.zip` for
  Windows x64, and a `SHA256SUMS`. An archive installs with no .NET, no build
  and nothing fetched from the internet. It holds the command line, the API
  with the portal and the MCP bridge, each with its own runtime; the
  embedding model, refused unless both of its files match their pinned
  SHA-256 values, with its revision, license and notice; the starter profile
  and its sample documents; the first-party readers, built into
  `extensions/` and loaded only once allowed; the systemd unit on Linux;
  `LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.md`, and `licenses/` with the
  license text of every package and runtime pack the three programs and the
  readers deploy;
  and `INSTALL.txt`. Neither carries a database: setup uses a PostgreSQL 14
  or later that already runs. The build works from a snapshot of the commit,
  never the working tree, and writes sorted entries with the commit's time
  and fixed modes, so the same commit builds byte-identical archives on the
  same machine with the same SDK. `verify-archive.sh` checks each archive
  once it is written, and `test.sh` shows four of the build's refusals
  firing, each beside a control: a model file off its pin, a package with no
  license text, an archive missing a required file, and a NuGet packages
  folder that does not exist. `scripts/release/licenses/` holds, each with
  its provenance, the upstream license texts of the packages that ship none
  and that the .NET runtime pack's license text does not cover, taken byte
  for byte from each package's repository at the commit its package names.
- **Install proofs.** `scripts/release/prove-linux-archive.sh` follows the
  Linux archive's `INSTALL.txt` in a bare Ubuntu 24.04 container with no
  .NET, no system ICU and no route out, beside a stock PostgreSQL 17.11 on an
  internal network: setup finds the model inside the archive, the starter
  profile is applied and both its sources ingested, the API answers over
  HTTPS, an agent's search over MCP gets what its folder rules give it, and a
  search as the engineering group does not reach the salary bands. With the
  model folder moved away, a search for the same words must stop with the
  one line that names where the model was looked for.
  `scripts/release/prove-windows-archive.ps1` does the same for the Windows
  archive with `dotnet` off the path and no `DOTNET_` variable set, against a
  throwaway database on the development PostgreSQL, and checks that the API's
  runtime was loaded from the archive. The Linux proof then allows both
  readers from inside the archive, as `INSTALL.txt`'s step for them says, and
  finds a PDF passage by `Page 2` and a Word passage over MCP, after a control
  that shows all three invented files skipped before the readers are allowed.
  Each reader ships in the archive exactly as its manifest lists it, each file
  checked against its hash, and is built with the release's version.
  Neither is a clean-machine proof of
  the Windows service or the systemd unit.
- **A release workflow**, `.github/workflows/release.yml`. It builds both
  archives and runs `test.sh` on a pull request that changes the release
  scripts or the workflow (as `0.0.0-pr.<number>`), on a manual run given a
  version, and on a `v*` tag. Only a tag goes on to check the checksums and
  make a draft release of the two archives and `SHA256SUMS`, which stays a
  draft until a person publishes it; the build attestation over `SHA256SUMS`
  runs only when the repository is public. Every action is pinned to a full
  commit, and the only credential is `GITHUB_TOKEN`. No release has been
  published.
- **Windows CI.** A `windows-setup` job in `ci.yml` runs the bundled
  PostgreSQL's setup and removal tests and the credentials file tests on
  `windows-latest`, where their Windows paths run instead of returning at
  once as they do on Linux. The bundle is laid out by
  `installer/windows/fetch-postgresql.ps1` from EDB's archive, cached and
  checked against its pinned SHA-256 either way. A test that returns at once
  also counts as passed, so a second step reads the results and fails the
  run when the bundled server test took under 2 seconds. `SetupInstallTests`
  is not in the job.
- Tests: `MigrateTo`, the product's own migration runner given the embedded
  migrations up to a version, so an upgrade can be tested from any earlier
  schema; with it the 0068 `created_by` backfill is tested across the upgrade
  that runs it (an agent made on the connect page becomes self-made by its
  owner, any other `unknown`). A test holds `prem groups remove` to its
  refusal: a group a rule names is kept, and nothing is recorded, unless
  `--force` is given, and with it the change record row counts the rules
  that named it.

- `prem ingest --remove-unread`; and the run record, `prem sources status`
  and the portal's run page show documents kept without a reader and
  documents removed because they are now skipped (migration 0073).
- The PDF reader names a page whose content nests past its depth limit
  (256) as unreadable.

- **The MCP authorization flow, built and off by default** (OAuth 2.1 with
  PKCE, MCP revision 2026-07-28), for an assistant that runs on a person's
  own computer and signs in by OAuth instead of a pasted token. An
  administrator turns it on with `mcp.oauth.public_url` and
  `mcp.oauth.enabled` and a restart; off, none of its paths is mapped and no
  table of it is read or written, which a test proves from the database's
  own statistics. It makes no outbound request. An assistant registers
  itself, with loopback redirect addresses only unless the administrator
  lists more, or an administrator registers it with `prem oauth clients
  add`; a person approves it on a consent page that answers with a page and
  a link, never a redirect; the assistant then reads through an agent that
  acts for that person, read-only, counted against
  `agents.self_service_max`. `prem oauth clients` and `prem oauth grants`
  manage its clients and grants whether it is on or off. Migration 0080.
  No assistant has been run against it here yet; the manual's page is
  [The authorization flow](docs/authorization-flow.md).
- **A disable is final for a person's own assistants, and a password change
  ends their credentials.** Disabling a person or one of the agents they
  made ends that agent's tokens and grants for good; re-enabling brings none
  back, and the person connects the assistant again. Changing a person's
  password ends their connect-page tokens and authorization grants, as it
  already ended their sessions; the sign-in rehash never does. Service
  agents and agents an administrator made are unchanged.

- **The authorization flow's pages in the portal.** The consent page a
  person lands on after signing in: the assistant's registered name and
  how it registered, the address its answer goes to, the assistant that
  will be made or kept, what it may do, the question where its model runs
  unless an administrator stated it, and Approve and Deny, which answer
  with a page holding one link back to the assistant and never a redirect.
  A Grants page (readers see every grant with its state, refresh count and
  last refresh; administrators revoke) and a Clients page (administrators
  register, disable, enable and remove), both mapped only while the flow
  is on. The connect page lists a person's own grants beside their
  connect-page assistants, with the state of each, counts both against
  `agents.self_service_max`, and gives the one address to hand an
  assistant. The health page's line for the flow says whether it is on in
  the settings and in this process. An agent made through the flow shows
  the assistant it came through and has no token table. Proven from a
  real browser against the flow: sign-in, consent, Approve and Deny each
  reaching the assistant's address, and the code exchanged.

### Changed, 2026-09-25 10:22 UTC

- **What was called leaving the network is named for what it counts:**
  passages served to agents registered as using a hosted model. A source's
  state in `prem sources list` and on the portal's Sources page, "never
  leaves the network", is now "never served to hosted-model agents" (and
  "never served to hosted-model agents (denied by a rule entry, not by the
  switch)"). The
  usage page's figure is "Passages served to hosted-model agents", its column
  "To hosted-model agents", and its note says "Passages served to
  hosted-model agents went to agents registered as using a model outside the
  network; an agent registered as local is not counted, whatever model it
  uses." The audit page filters on "what went to hosted-model agents" and
  marks such a row "to a hosted-model agent"; `prem audit export --hosted` is
  described as "export: only what went to hosted-model agents"; and the
  error a profile gives on a deployment missing the reserved group ends "A
  profile cannot say what hosted-model agents may be served until that is
  put right." Only the words changed; the third state, "may be served to
  hosted models", is as it was.
- A reader that throws on one file no longer ends the ingest run: the file is
  reported unreadable, its existing index entry is kept, and the next file is
  read. Only a canceled run stops. A connector's own failure to finish a
  document is thrown, not reported as the reader's.
- **An extension built against a newer PremAgentic than the host is refused**
  at start as `core too new`, with both versions named, since it could call a
  member the host does not have; one built against the same or an older
  version is given the host's own contracts library.
- What a seam version means, as `SeamVersions` states it: the least an
  extension needs, so a host offering version N loads an extension built for
  any version up to N, as before. A version now also goes up when a member is
  added that an extension may call, so an extension that calls it declares
  the new version and an older host refuses it with a reason, instead of
  failing it on the missing member partway through a run.
- **The connect page on ChatGPT and Claude Desktop.** ChatGPT's text says it
  calls a remote MCP server from OpenAI's cloud, so it cannot reach
  PremAgentic inside the network directly and has no way to send the token
  as a header. It names OpenAI's Secure MCP Tunnel, a program run inside the
  network that connects out to OpenAI and can start a local MCP program such
  as the bridge; says this has not been tried with PremAgentic, and that
  everything ChatGPT reads through it reaches OpenAI, so the assistant is to
  be registered with a hosted model; and gives the bridge's program and its
  two settings. It no longer puts the barrier down to a missing MCP
  authorization flow, which PremAgentic still does not have and which would
  not change where ChatGPT's requests come from. Claude Desktop's text adds
  that a custom connector, in claude.ai, Claude Desktop or the mobile apps,
  is called from Anthropic's cloud and cannot reach a server inside the
  network, so the local configuration with the bridge is the one to use.
  ChatGPT's instructions line no longer says "yet". Each text has a test
  that cites the vendor page it was checked against.
- **The manual, the README and `SECURITY.md` are corrected where they said
  more than the code does.** At runtime, with the default local embedding
  provider, PremAgentic makes no outbound call, and an assistant on a hosted
  model receives the passages each search returns; the optional `openai`
  embedding provider sends passage text to a third party. Access comes from
  folder rules an administrator writes, evaluated in the order written with
  the first entry that names the caller deciding, and no connector reads
  NTFS, POSIX or file-share permissions. The audit trail keeps the text of
  every question. Agents need no third-party API key. The reserved group,
  the source switch and the audit trail's hosted-model record cover agents
  registered as using a hosted model. The ChatGPT sentences no longer promise
  that an MCP authorization flow alone would let it connect.

### Fixed, 2026-09-25 10:22 UTC

- The portal's user form wrote a change record row when an enabled user was
  enabled or a disabled one disabled. It now compares first, under the
  change's lock, as `prem users enable` and `disable` have since 2026-09-24, and
  says "Nothing changed and nothing was recorded."
- The portal's agent form did the same for agents, because the store
  reported writing an agent's current state again as a change. The store now
  writes only an agent not already in the state asked for, and the form
  records nothing for the rest.
- `DocumentSourceConformance` passed when a connector gave no unreadable
  item, so the rule it proves went untested there. Returning null now fails,
  and a connector that reads its own items, leaving the kit's
  `OneUnreadableItem` nothing to hold, fails with a sentence saying to give
  a real unreadable item.

- The Word reader ended the process on a document nested tens of thousands
  deep: each part's nesting is now measured before it is read, and one
  nested past 256 is refused with the limit named. It also read a table
  inside a cell once for each table around it, with the work doubling at
  every level; nested tables are read once each.
- An ingest deleted documents indexed earlier in a format no reader here
  reads now, as after a reader was refused at start or disallowed; they are
  kept and counted, and `prem ingest --remove-unread` is what removes them.

### Added, 2026-09-24 12:52 UTC

- **Instructions for assistants.** `mcp.instructions`, a deployment's own
  words for every assistant that connects (up to 8,000 characters,
  administrator only, carried by a profile), sent to an assistant when it
  connects (in the `server/discover` result under MCP 2026-07-28, and in the
  `initialize` result to a client of an earlier revision), served as the
  resource `premagentic://instructions`, passed
  through the stdio bridge, and shown on the connect page as a paste-in
  block for clients that read a file. The server reads it when it starts.
- **Who made each agent.** `created_by` on agents (migration 0068): the
  command line's account, the portal administrator, the person on the
  connect page, or a profile; the connect page counts and lists a person's
  agents by it, and the agents page shows it with a filter to self-made.
- **The health report names the embedding model**, its folder and its
  revision on the portal's health page; `/health` names the model and its
  revision only, since it answers anyone who can reach the port.
- **Native libraries in extensions.** An extension can ship native libraries
  under `runtimes/<rid>/native/`, listed in its manifest with their hashes;
  the host loads the one for its platform, measured again just before
  loading. `samples/extensions/native-lines` shows it for win-x64 and
  linux-x64; its two small binaries (about 2 KB each) are committed so
  building the solution needs no C compiler, and they are reproducible byte
  for byte from one C file with `scripts/build-native-sample.sh`.
- **A reminder sink that sends mail**, `samples/extensions/mail-reminders`:
  one message per owner over SMTP, installed and allowed like any extension,
  its password in a credentials file and never in a setting; the built-in
  still calls out to nothing. `ExtensionRegistrations.Folder` tells an
  extension where it was loaded from.
- **The fully local stack**, `deploy/fully-local`: PremAgentic, a local
  model server and an MCP chat client on the office's own hardware, with the
  chat client's agent registration and connect snippet, and the offline
  proof running the whole loop.
- **Instructions on the connect page.** When `mcp.instructions` is set, the
  page that shows a new token shows them too, with a copy button and where
  they go for the assistant chosen.
- **The usage page at scale:** the people and assistants tables in pages of
  fifty, with a name filter; **the export page takes a window**, the audit
  trail for the same dates and filter as the usage page.
- **The connect configurations checked against each client's documentation**
  (Claude Desktop, VS Code Copilot, ChatGPT), pinned field by field by tests
  that cite the pages; none has been run on a real client yet, and a runbook
  for that run is filed.

### Changed, 2026-09-24 12:52 UTC

- **The change record is complete.** Every administrator verb at the command
  line (`users`, `groups`, `agents`, `tokens`, as `rules` already did)
  writes its change record row in the same transaction; every administrator
  change on every surface is now recorded, and a command that changes
  nothing records nothing.
- A profile's `settings.json` takes every setting an administrator can
  change, each checked by its definition, except `extensions.*`, which
  stays extensions.json's to say.
- An extension whose folder holds a file under `runtimes/` that its manifest
  does not list is refused.
- `scripts/clean-install/run.sh --offline` runs the whole fully local loop,
  a client asking PremAgentic over MCP and then a stand-in model server,
  and traces the client and the model server too.
- Narrow screens: tables keep words whole and scroll in their own box, and
  configuration boxes and long buttons wrap.

### Fixed, 2026-09-24 12:52 UTC

- A profile could not carry `mcp.tool_descriptions`.
- `prem users enable` on an enabled user, and `disable` on a disabled one,
  wrote a change record row for a change that did not happen.
- `scripts/clean-install/run.sh` had registered its agent without `--model`
  since that flag became required, and gave the upgrade's second setup a
  search role the older install did not use; nothing ran the script in that
  time, so both breaks went unseen. It runs through again. It is run by hand
  after each batch of merges, and by the release workflow.
- ChatGPT on the connect page no longer implies it can connect with a token:
  its connectors take OAuth or no authentication, and PremAgentic has no MCP
  authorization flow yet.

### Added, 2026-09-24 10:05 UTC

- **The command line explains itself with no database.** `prem --help`, `prem
  help`, `prem <command> --help` and `prem --version` are answered before any
  database step. Every command has one usage text that names and explains
  every option it takes, printed in the main usage, by `--help`, and on a call
  the command cannot take; `docs/cli.md` is generated from that text and a
  test fails when the two differ, or when a parser reads a flag its usage does
  not name.
- **`prem reminders run [--plan]`**: per source owner, the documents past
  their stale date and the documents in the review queue; documents whose
  source has no owner go to the administrators. The latest run is kept in the
  database (migration 0048) and shown on the portal's Review page, and an
  extension can add a reminder sink (seam `reminder`, version 1). `--plan`
  prints what would be delivered and writes nothing.
- **An extension's manifest lists the other files it loads** (`files`, each
  with its SHA-256), and the allowed hash covers every listed file. A
  paragraph-chunker sample with a library of its own shows the shape.
- **`prem settings` lists, reads, sets and unsets every setting** from one
  list, the settings catalog: the three trust settings, the four retrieval
  settings, `evaluation.golden_set_path`, `mcp.tool_descriptions`,
  `audit.retention_days`, `extensions.folder`, `extensions.allowed`,
  `agents.self_service_max` and `connect.snippets`. A key the catalog does not
  define is refused on every verb.
- **Self-serve agents** (`agents.self_service_max`, 0 to 10, default 2;
  migration 0053). A person creates an agent that acts as them and nothing
  more, sees its token once, and revokes it; every creation and revocation is
  in the change record with the person as the actor.
- **The usage read model** (migration 0054): questions, people, agents,
  questions with no passage and passages served to hosted-model agents per
  day, week or month; by person and by agent; the most served documents; the
  content gaps. The audit export takes the same window and filter:
  `prem audit export --from YYYY-MM-DD --to YYYY-MM-DD [--hosted]`, and the
  portal's `/portal/export/audit.jsonl`.
- **Connect an assistant.** `/portal/connect`, for every signed-in person: the
  person's own agents with where each model runs, its rate, when it was made
  and last used, and revoke; a form that makes an agent acting as that person
  and shows its token once, inside the configuration for the assistant they
  use (Claude Desktop, ChatGPT, Copilot in VS Code, a coding tool, a local MCP
  client, or other). The templates ship in the portal; `connect.snippets`
  replaces any kind's. At `agents.self_service_max` 0 the form is off. The
  Claude Code and stdio bridge configurations are the ones proven on a real
  client; the others follow each client's documented format.
- **Usage.** `/portal/usage`, for auditors and administrators: questions by
  day, week or month over up to 366 days, people and assistants, the most
  served documents, the questions nothing answered (each a link that asks it
  again as you), what left the network, a hosted-model filter over every
  table, and an audit export for the same window. The page writes nothing.
- **Reminders on the Review page.** What the reminders job last computed, per
  source owner, with the unowned documents for administrators.
- **Groups in a profile.** `groups.json`, a list of group names, applied
  before sources and rules, so a rule may name a group the same profile
  creates. The starter profile carries `hr` and `engineering` and applies to
  a fresh database in one step.
- **`PREM_ALLOW_NO_SEARCH_ROLE`**, the one switch that lets a deployment
  configured by connection string run without a search role.

### Changed, 2026-09-24 10:05 UTC

- A command given an option or subcommand it does not take refuses before it
  connects, and prints its usage.
- `prem eval` with no report path writes the report to the current folder,
  not beside the golden set, and never into a folder that holds a profile;
  `report-*.md` is ignored by git everywhere. The one golden set is the
  starter profile's `golden-set.json`; `eval/golden-questions.sample.json`
  is gone and the quick start names the profile's file.
- An extension loads a file from its folder only when its manifest lists it
  and the bytes match; the allowed hash covers every listed file, the
  assembly alone when nothing is listed, so existing entries still match.
- The model download scripts fetch a named revision and verify the SHA-256 of
  each file, refusing on a mismatch.
- The development database in `docker-compose.yml` is published on
  `127.0.0.1` only.
- A deployment by connection string with no search role refuses to start
  unless `PREM_ALLOW_NO_SEARCH_ROLE=1`; an installed deployment always reads
  through the search role `prem setup` creates; the development database
  keeps its warning.
- `/api`, `/mcp` and `/health` send `nosniff` and `no-store`; HSTS when the
  service terminates TLS with its own certificate and trusts no proxy.
- The local model's folder is found by one rule for the CLI, the API and
  setup: `models/minilm` under the current folder, then beside the program
  and each folder above it; a refusal names every folder tried.
- The MCP server's version is the build's informational version, and
  `prem --version` prints the same one.
- `prem rules set|remove` and the ingest shorthand record their changes, as
  the portal's rule pages do.
- **Applying a profile twice changes nothing.** A golden set already copied
  and in force is no longer copied again, and `prem profile apply` says when
  a deployment already matched the profile.

### Fixed, 2026-09-24 10:05 UTC

- `prem settings set mcp.tool_descriptions` (and `audit.retention_days`)
  stored the value and exited 1; `get`, `unset` and `list` failed or hid them.
- `prem --help` printed "No database is configured", and `prem eval --help`
  took the flag as the golden set's path.
- The API under `dotnet run` did not find the model the quick start
  downloaded at the repository root.
- **Every portal form submitted from a browser was refused** with "A change
  must be made from a portal page" since the portal's security headers
  landed: under `Referrer-Policy: no-referrer` a browser sends `Origin:
  null` on a form post, and the same-origin check compared that with the
  portal's origin. Found by the first real browser post, while taking the
  manual's screenshots; the tests send a real `Origin` and the earlier
  screenshot runs changed things with curl. A `null` origin now counts as
  absent and the request is judged by `Sec-Fetch-Site: same-origin`, which
  page script cannot set, with the anti-forgery token binding the session as
  before; the header is unchanged.

### Documentation

- **The manual moves to `docs/`**, one page per subject, with `docs/toc.json`
  as its index and `docs/README.md` as the same index for people. The README
  is the front door: what it is, the quick start, how to connect an
  assistant, where the manual is, how to get involved. `docs/cli.md` is the
  `prem` command reference, taken from the program's own usage text.
- **`scripts/check-docs.sh`** (and `check-docs.ps1`): every link, image and
  anchor in the manual resolves, `toc.json` and `docs/README.md` agree, and
  each page carries the title the index gives it. CI runs it before the build.
- Two claims the move carried were brought up to date: a bound search is no
  slower than an exempt one (as the 2026-09-22 11:22 UTC entries say), and the
  test count. The Surfaces table lists `remove`, the sources page shows
  `--chunker`, and the configuration table lists `PREM_TENANT_NAME`.
- **Getting started and Connect an assistant**, written from two runs of the
  quick start on a clone nobody had built before (Windows 11 in PowerShell 7,
  a bare Ubuntu 24.04) and from a real MCP client connected both ways, over
  HTTP with the token header and through the stdio bridge: every command and
  every quoted line is from those runs. `scripts/download-model.ps1` now
  fetches the one model the quick start names, with `-All` for the second,
  as the shell script does; `scripts/check-docs.ps1` runs from any folder.
- **Community.** `CONTRIBUTING.md` is now an invitation: what help is
  wanted, the ways to take part, a first contribution end to end and what
  review looks for, with every rule a change has to keep as it was.
  `CODE_OF_CONDUCT.md` adopts the Contributor Covenant 2.1. Issue forms for
  bugs and for concrete proposals (blank issues off; questions go to
  Discussions, vulnerabilities to the security policy) and a pull request
  template. `docs/roadmap.md`, directions without dates.
  `docs/writing-an-extension.md` takes an author from an empty folder to a
  loaded extension and was proven by building one: a `.note` reader, its
  conformance tests, `allow`, `list`, an upgrade, and the refusals on the
  way. With all twenty-one pages written, CI's link check runs without an
  allow list.

### Renamed

- The product is **PremAgentic** (on-premises, for agents). Everything that
  carried the old name changed with it, and nothing had been deployed or
  published under it: namespaces and project names, the `prem` command
  (`prem.exe`), the `PREM_*` environment variables, the `prem_config` and
  `prem_index` schemas, the `prem_agt_` token prefix, the roles
  (`premagentic_owner`, `premagentic_app`, `premagentic_search`), the service
  names (`Premagentic`, `PremagenticDb`), the credentials folder, the cookie
  and header names. Migration files were rewritten in place, so a development
  database from before the rename must be recreated (`docker compose down -v`).

### Added, 2026-09-23 01:24 UTC

- **Readers.** `IDocumentReader` and `ReaderRegistry`, with Markdown and plain
  text built in. The ingest pipeline picks the reader by file extension, and a
  file no reader claims is counted as skipped, as before. A source's reader
  registry is the deployment's, so a reader an extension brings reaches the
  CLI, the API and the portal alike.
- **Extensions.** A reader, a chunker, an embedding provider or a way of
  signing in can be added without a change to this repository. An extension
  is a folder with one assembly and an `extension.json` naming it, its SHA-256
  and the seam versions it was built for. It loads only when the assembly
  hashes to what the manifest claims and the pair of name and hash is in the
  setting `extensions.allowed`, which `prem extensions allow` writes after
  measuring the assembly itself, with an entry in the change record. The bytes
  that are hashed are the bytes that are loaded, so the file cannot be
  exchanged between the check and the load. Everything else is refused with a
  reason and listed by `prem extensions list` and on the health page: a
  manifest that cannot be used, an assembly named outside its own folder, a
  hash that does not match, a pair nobody allowed, a seam version above this
  release's, an assembly that will not run, or a name already taken. A refusal
  never stops a deployment, which then runs with exactly the built-ins. The
  extensions folder is `extensions.folder` or `PREM_EXTENSIONS_DIR`, the
  setting winning; neither one means no extensions and no error. An extension
  that brings a way of signing in is named in the log at every start.
- **One composition point.** The API, the CLI and the portal build their
  reader and chunker registries, their embedding provider and their ways of
  signing in from one extension host, so an installation that adds one no
  longer registers it in each host by hand. With no extensions folder, every
  one of them behaves exactly as before. `prem setup` reads the deployment's
  extensions before it checks the embedding provider the configuration names.
- **A conformance kit**, `tests/Premagentic.Conformance`: xunit fixtures an
  extension author inherits to prove a reader, a chunker, a connector or an
  embedding provider keeps its contract. The built-ins are held to the same
  fixtures here.
- **A sample extension**, `samples/extensions/sentence-chunker`: a whole
  extension registering a `sentence` chunker, a `.csv` reader and a sign-in
  adapter that claims nothing, loaded by the test suite from a folder by hash
  and proven end to end: a `.csv` file is skipped until the extension is
  allowed and found by search once it is, and the adapter is asked on a real
  request and the request is still refused.
- Agents carry where their model runs, `local` or `hosted`, with the vendor of
  a hosted one. `prem agents add` requires it; `prem agents set` changes it.
- A reserved group, `hosted-model agents`, holds every hosted-model agent and
  follows what the agents are set to. A folder rule that denies it keeps those
  agents out of the folder. It cannot be renamed, deleted or edited by hand.
- Every audit row records where the model that received the answer ran, and the
  audit export carries the column.
- The two ways of signing in, a password and a trusted header, now go through
  one seam, so a deployment can add its own without changing the core. What
  each of them does is unchanged.
- Groups from an outside directory can be mapped to PremAgentic groups with
  `prem groups map`. The mapping decides what a sign-in adapter's groups and a
  connector's permissions mean here; anything unmapped reaches nobody, and an
  ingest run counts what it met that meant nothing.
- A deployment can put its own words on the two MCP tools with
  `mcp.tool_descriptions`, read when the server starts.
- The audit trail can be kept for a set number of days with
  `audit.retention_days` and pruned with `prem audit prune`. Unset, which is
  the default, keeps it forever.
- Profiles: a folder of plain files carrying a whole configuration, with
  `prem profile validate`, `prem profile apply` and `prem profile show`. A
  profile can set what an administrator can set and nothing more; it is
  validated as a whole and refused as a whole; `show` lists how a deployment
  differs from a profile. A starter profile is in `samples/profiles/starter`.
- `prem_config.profile_applied`: which profile a deployment was configured
  from, and where from, appended once per apply, so the answer does not age
  out with the audit trail.
- An owner on each source: the person answerable for its documents, shown by
  the sources pages and the review queue. It decides nothing; who may read a
  source is its folder rule.
- A "may be served to hosted models" switch on each source, which is one deny
  entry for the reserved hosted-model agents group at the top of the source's
  folder rule. `prem sources set --hosted yes|no`, and three states told apart
  in `prem sources list`.
- The portal: the health page lists the extensions loaded and refused and the
  profile this deployment was configured from; the audit page filters on what
  left the network; sources show and set their owner and switch; an agent's
  model can be moved between local and hosted.

### Changed, 2026-09-23 01:24 UTC

- A document's content hash is now the SHA-256 of the file's bytes rather than
  of the UTF-8 re-encoding of its decoded text. The two are the same for a
  UTF-8 file with no byte-order mark, which is every file in the sample
  corpus. A file with a byte-order mark, or with bytes that are not valid
  UTF-8, gets a new hash once and is ingested once more after this upgrade.
- A run started from the portal reads through the readers the process
  composed, not the built-in ones. An extension's reader now reaches every
  host: the API, the CLI and the portal.
- An agent registered before this release has no model location recorded and
  is read as hosted until `prem agents set` says otherwise (migration 0040).
- `prem agents add` requires `--model local|hosted`; a hosted model names its
  vendor with `--vendor`, and a local one may not.
- **The sign-in card's mark loops.** The website's logo loop plays in place of
  the still, muted and inline, with the still as its poster and shown in its
  place when the visitor asks for less motion. The content security policy
  gains `media-src 'self'`, the portal's own origin and nothing else, because
  `default-src 'none'` refused the video. The header mark on signed-in pages
  is as it was.

### Added, 2026-09-22 11:22 UTC

- **Retrieval tuning from settings.** `retrieval.rrf_k`,
  `retrieval.fallback_rrf_weight`, `retrieval.authority_weights` and
  `retrieval.no_answer_distance_floor` are read from the settings store on
  every search, so a change applies to the next query. A stored value that
  cannot be used keeps its default and is reported once, never stopping a
  search. Each search result carries the reading it ranked under.
- Chunkers are chosen per source by name. `markdown` is built in and the
  default; `IChunker` and `ChunkerRegistry` let an installation add its own.
  `prem sources add` and `set` take `--chunker`, `prem sources chunkers`
  lists them, and the portal's source forms choose from the list. An unknown
  name is refused when a source is added or changed, and a run under one
  fails before it reads anything, recorded with the name. Changing a
  source's chunker stores every one of its documents again at the next run.
  Migration 0030.
- The review queue in the portal: documents below human-reviewed that a
  person's sign-off would change, with the folder each lives in, filterable
  by source and tier. It writes nothing. The health page shows its count.
- Retrieval tuning from the command line and the portal: `prem settings`
  takes the four `retrieval.*` keys, checked before they are stored, and a
  new `unset` command puts a default back, recorded as `setting.unset`. The
  portal's Tuning page shows each value, where it came from and why a stored
  value is not used, and changes them with the same checks.
- The golden set judged from the portal: `evaluation.golden_set_path`
  (command line only, absolute) and "Run the golden set", one run at a time,
  bounded to two minutes, recorded as `evaluation.run` with its counts,
  failed questions and the settings it ran under. Each question is judged by
  the settings its own search ranked under. `prem eval` now ranks under the
  stored settings too.
- The support bundle names each source's chunker.
- **`prem remove`.** Stops and removes the services and keeps the data by
  default; `--purge --yes` drops the database as its owner and the three
  roles over the administrator connection, deletes a bundled server's data
  folder, and deletes the credentials files last. `--plan` shows every step,
  `--purge` without `--yes` lists and changes nothing, and anything that would
  stop a purge is refused before anything changes.
- **The offline proof.** `scripts/clean-install/run.sh --offline` runs setup
  and the whole quick start with no network, shows the checks can tell, and
  traces every connection of an ingest and the API.
- The clean-install test signs people in over HTTPS, calls the search tool
  over MCP as an agent, and upgrades from a release before row-level security
  (across migrations 0006, 0007 and 0011 to 0014), with a rollback.

### Changed, 2026-09-22 11:22 UTC

- **The portal's look.** Geist and Geist Mono served from the portal's own
  assets, a grouped sidebar in place of the top bar, a page header with the
  page's description, hairline tables with identifiers and timestamps in
  the mono face, one badge component with a fixed color code for trust,
  authorship, stale and lifecycle on every page, a sign-in card, and every
  page fitting a phone width. No route, form, field or message changed, and
  the content security policy is as it was.
- **The portal in the website's character.** The app inside a black-ruled
  panel on a gray page with the mark in the corner square, numbered group
  labels in the sidebar, page titles in Pixelify Sans, black buttons with
  mono text, square corners, the search form as a terminal window and the
  sign-in card with the color logo. The name is written PremAgentic
  wherever the portal shows it. The content security policy gains
  `font-src 'self'`, the portal's own origin and nothing else, because
  `default-src 'none'` had been refusing the self-hosted fonts. A sign-in
  request that gets no answer now says the server did not answer and may
  be restarting, instead of that it could not be reached.
- **A bound search is no slower than an exempt one.** Measured at 100,000
  chunks: the bound OR pass went from 2.68 times the exempt one to equal, with
  the parallel plan back. The text match function applies the caller's and the session's
  access lists as one condition, the session's lists cut down to the
  caller's, so PostgreSQL no longer halves its estimate and drops the
  parallel plan. Both still restrict on their own. The next migrate installs
  the new body.
- **The ONNX Runtime's own telemetry is switched off by the product.** Every
  provider that loads the runtime sets `ORT_DISABLE_TELEMETRY=1`, unless it is
  set already, where native code reads it, before the runtime loads. Measured
  on Linux: version 1.30.0 uploads from the API process unless that variable
  is set, and the runtime's `DisableTelemetryEvents` call does not stop it.
  This release stays on 1.27.0, which was measured making no outside
  connection.
- **An embedding provider that fails is a startup refusal.** No `openai` key,
  an `openai` endpoint that cannot be reached or answers with an error, an
  unknown `PREM_EMBEDDING_PROVIDER`, and missing local model files each name
  the provider in one sentence. The CLI ends such a command with that line
  and exit code 2.
- `prem setup --plan` and the migration runner's pending list show a stale
  generated text match function as pending work of its own kind.
- The sign-in timing test compares medians of five attempts after a warm-up,
  so a loaded test runner no longer fails it.

### Fixed, 2026-09-22 11:22 UTC

- `prem search` with the `openai` provider and no key or no network ended
  with exit code 134 and a stack trace.
- `prem setup --plan` failed at the administrator step when the text match
  function in the database was stale.

### Added, 2026-09-22 04:19 UTC

- **Row-level security on the index as a second line of access control.**
  Search and section reads connect as a search role (`search.credentials`,
  or `PREM_SEARCH_CONNECTION_STRING`) bound to a caller session for one
  read; an explicit, owner-only writer list names the one role that reads
  every row. The full-text index stays in use under the policy: the text
  match, the gates, the order and the limit run inside a function generated
  from the same gate code the application uses, with a checked-in rendering.
- **The administration portal** at `/portal`, in the API process: users,
  groups, agents and tokens, sources and runs, documents, permissions with
  view as and why, settings, audit, the change record, health with a support
  bundle, export, and a search page for people. A member searches, an auditor
  reads every page and changes nothing, an administrator changes things, and
  every change is recorded with the signed-in user in the same transaction.
- **Bundled PostgreSQL on Windows:** `prem setup --bundled-postgres` makes
  and starts a PostgreSQL 17 cluster from the minimal set
  `installer/windows/fetch-postgresql.ps1` lays out, listening on localhost
  only, as a service under its own account with `--windows-service`.
- **Trusted proxies:** `PREM_TRUSTED_PROXIES`, off by default.
- **Advisory locks:** a folder-rule change waits for a running ingest, and two
  runs of one source cannot overlap.
- **One refusal at startup** for every host: the sentence that says what to
  set, and exit code 2.
- The clean-install test now checks an upgrade between two releases and the
  rollback.
- A run that sees only files it skips reconciles; an empty file is counted as
  skipped `(empty)`.

### Changed, 2026-09-22 04:19 UTC

- The settings store refuses keys under `trust.` (use `prem settings set`).
- The application role refuses to start while the text-match function's body
  is not the one its build generates; run `prem migrate` as the owner.
- Password hashing refuses, rather than answering false, in a process that
  cannot normalize Unicode.


### Added, 2026-09-21 23:48 UTC

- Password sign-in over HTTPS with sessions (30 minutes idle, 8 hours at most),
  sign-out and who-am-I; lockout after five wrong passwords and a per-address
  throttle; an anti-forgery token for requests that change state under a
  session.
- Agent tokens on the HTTP surface (`Authorization: Bearer prem_agt_...`), each
  agent held to its requests per minute.
- MCP over HTTP at `/mcp` in the API process, for agent tokens, on MCP revision
  2026-07-28 (stateless Streamable HTTP).
- Search and section results over HTTP, MCP and the CLI carry trust tier,
  authorship, stale flag and concept id.
- Trust settings stored per deployment and read on every search:
  `trust.agents_minimum_tier`, `trust.people_minimum_tier`, `trust.stale`. An
  unreadable value reads as the strictest. `prem settings list|get|set|history`,
  with a warning before a looser value is saved.
- An append-only change record of administrator changes (migration 0011),
  written in the same transaction as each change; update, delete and truncate
  refused by the database.
- The audit row records the trust policy an answer was served under (migration
  0012), and section fetches are recorded beside searches.
- A sources registry and a record of every ingest run (migration 0013);
  `prem sources add|set|remove|list|status`; `prem ingest --source`.
- Ingest counts the files it skips for their format, by extension, in the
  summary, the run record and the output. `SourceRead` can carry a skip.
- Per source setting: undeclared authorship in an OKF bundle as machine-written.
  Off by default.
- **`prem setup`** checks Unicode handling first; requires PostgreSQL 14;
  writes `GSS Encryption Mode=Disable` unless the admin connection chose; makes
  the first administrator (`--admin-user`, password by prompt or file, never a
  default account); makes a self-signed HTTPS certificate and the Kestrel
  settings the API reads; and with `--windows-service` registers the API as a
  Windows service under its own virtual account.
- The API runs as a console process, a Windows service or a systemd unit from
  one build, over HTTPS from setup's settings. `deploy/systemd/` holds a unit
  template.
- `scripts/clean-install/run.sh` now also proves the shipped ICU, the first
  administrator, and the API answering over HTTPS as the application role.

### Changed, 2026-09-21 23:48 UTC

- Every search and section fetch runs as the caller who asked, under that
  caller's trust policy. A request with no caller is refused rather than served
  as public.
- The trusted-header mode (`PREM_SIGN_IN_HEADER`) carries a PremAgentic sign-in
  name, not principals. `PREM_PRINCIPAL_HEADER` is retired and stops the API
  from starting.
- The stdio MCP server is a bridge to the API with an agent token
  (`PREM_API_URL`, `PREM_AGENT_TOKEN_FILE`) and holds no database
  credentials. `PREM_PRINCIPALS` is gone.
- Disabling a user or setting a new password ends that user's sessions.
- **No silent development database.** With no `PREM_CREDENTIALS_FILE` and no
  `PREM_CONNECTION_STRING`, every host refuses to start and says what to set.
  `PREM_DEV_DATABASE=1` asks for the local development instance.
- **Published builds carry ICU 72.1.0.3**, the same Unicode library on Windows
  and Linux, so password hashes and chunk boundaries do not depend on the
  machine, and a Linux without libicu runs. Invariant globalization was
  measured and rejected: it does not normalize Unicode, and 7 of 13 test
  passwords hashed on one server failed to verify on another.
- `ModelContextProtocol` 1.4.0 to 2.2.0; `ModelContextProtocol.AspNetCore` 2.2.0
  added.

### 2026-09-21 21:37 UTC

The datastore moves to plain PostgreSQL. Nothing here needs a database
extension any more.

- **pgvector removed.** The `Pgvector` package, the `vector` column type, the
  extension and the `pgvector/pgvector` development image are gone. Vector
  search is exact cosine search in process over normalized float32 vectors
  held in memory (`System.Numerics.Tensors`), with the database still the
  gate: each search scores only the chunk ids PostgreSQL returns as readable
  by the caller, and reads the returned passages back under the gates again.
- **Migrations.** Numbered SQL files compiled into the build, applied by
  `prem migrate` (and first by every other command) under an advisory lock,
  one transaction each, with checksums: an edited migration or a database from
  a newer build is refused. `init-db` remains as an alias.
- **Two schemas.** `prem_config` (tenant, audit trail) and `prem_index`
  (documents, chunks, vectors). `prem rebuild-index --confirm` empties the
  index for a re-ingest and never touches config.
- **Audit trail.** Each retrieval event records the path, heading path and
  content hash of every passage returned, in place of chunk ids that a
  re-ingest replaces, and has room for the calling user and agent.
- **Search results** carry the content hash of the document version they came
  from.
- **Open Knowledge Format.** Frontmatter is read in OKF terms: trust tier,
  authorship, `stale_after`, `generated`, `verified` and `sources`, with a
  per-source bundle mode (`prem ingest --okf-bundle`) that leaves `index.md`
  and `log.md` out of the index and reports files that do not conform without
  rejecting them. Each document stores its concept id, trust tier, authorship,
  `stale_after`, `generated_at`, `last_verified_at` and frontmatter state
  (migration 0010). OKF's `deprecated` status now maps to superseded, for every
  source, so a document marked that way leaves default results after its next
  ingest. In a bundle, a concept whose frontmatter cannot be parsed is treated
  as machine-written and unverified.
- **Trust gate and freshness gate,** applied to every retrieval read and to
  section fetch. Machine-written content is served only at or above the
  policy's minimum trust tier, and content past its `stale_after` only when the
  policy includes it. `SearchOptions.Trust` and `SearchOptions.AsOf`;
  `TrustPolicy.Resolve` from caller kind, per-agent minimum and deployment
  settings. One instant is fixed per search. Every hit and section result
  carries trust tier, authorship, stale flag and concept id. **Breaking for
  hosts:** a search that names no policy runs strict, so CLI, HTTP and MCP
  searches hide unreviewed machine-written and stale content until they pass a
  caller's policy.
- **Frontmatter loader guard.** A frontmatter block whose shape would overflow
  the YAML loader's stack (a self-referencing key, thousands of nesting levels,
  an alias that expands without bound) is refused before it is loaded and
  degrades to no frontmatter. Before this, one such file ended the ingest
  process.
- **Identity and access.** Users, groups, agents and agent tokens, per tenant,
  with CLI verbs to manage them (migrations 0002 to 0005); passwords are
  PBKDF2-HMAC-SHA512 at 220,000 iterations and tokens are stored only as
  hashes. Folder rules are ordered allow and deny lists per folder, longest
  prefix wins, naming groups by immutable id. The access gate evaluates those
  lists per query, first match, default deny, in place of a public flag and a
  principal array, which are gone; there was no deployed data to migrate. An
  agent acting for a user can only be narrowed, a disabled, expired or revoked
  identity holds nothing at all, and the audit trail records the calling user
  and agent. `prem ingest --public` and `--principals` now set the folder's
  rule, with principals written by name; `prem search --user` and
  `--with-token`. A host-supplied principal that is not `kind:value` makes the
  caller hold nothing rather than being ignored.
- **`prem setup`** installs on an existing PostgreSQL: two roles (an owner for
  migrations and rebuilds, and an application role that reads and writes rows
  and nothing else), grants that also cover tables later migrations add,
  generated passwords written only to per-role credentials files
  (`PREM_CREDENTIALS_FILE`), and a health check. Safe to re-run; `--plan`
  changes nothing. When the schema is current the migration runner only
  verifies it, so the application role starts; when something is pending and
  the role may not apply it, it says to run `prem migrate` with the owner's
  credentials.
- **The vector leg reads permitted documents, not chunks:** about a tenth of
  the rows per search, and two to five times the throughput with five
  concurrent searchers from 25,000 to 500,000 chunks. Gates are written over
  the document alone, and a test holds them to it.
- **Text search ties are ordered** by path and position, so a rebuilt index
  answers the same question the same way.
- `scripts/clean-install/run.sh`: a clean-machine install and search on a bare
  Ubuntu with no network route out.
- **Local embeddings.** ONNX Runtime spin-waiting is turned off, because
  in-process vector math shares the cores the runtime would otherwise spin on.
- **Development database.** Stock `postgres:17.11` on a new volume. The 0.1.0
  volume holds a database with the vector extension and no migrations; it is
  not upgraded in place. Start the new one and re-ingest.

## 0.1.0, 2026-09-20

First tagged state. Not a production release: no connector beyond the
filesystem, no user interface, no deployment packaging.

- Hybrid retrieval: lexical plus vector search fused with Reciprocal Rank
  Fusion, fully local embeddings, no outbound call.
- Authorization gate and lifecycle gate, both hard SQL conditions applied
  before retrieval. Access defaults deny, and a document whose permissions
  could not be read reaches nobody and is counted at ingest.
- `IDocumentSource` connector abstraction with a filesystem reference
  connector. Permission changes land even when content did not change.
- Audit label on every retrieval event.
- Evaluation harness with golden cases that can assert authorization.
- CLI, HTTP and read-only MCP surfaces.
- CI builds with NuGet audit findings as errors and runs the full suite,
  including the authorization tests against a real PostgreSQL.
