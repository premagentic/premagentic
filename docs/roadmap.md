# Roadmap

This page says where PremAgentic is going: what is built, what comes next,
and what is under consideration. It gives directions; the dates mark what is
already built. What is proven today is on the [Known state](status.md) page,
and what changed, and when, is in the repository's CHANGELOG.

## What is built

The four gates, access, lifecycle, trust and freshness, as hard SQL
conditions over stock PostgreSQL with no extension; users, groups, agents
and tokens administered from the command line and the portal; folder rules;
password sign-in with sessions, agent tokens, and MCP over HTTP; one seam for
the ways a person signs in, and one for what an outside directory's groups
mean here, with directory group mapping itself coming with the business
add-on; a filesystem connector with permission-change detection;
readers for Markdown and plain text behind a reader seam; chunkers chosen per
source; hybrid retrieval with the vector leg in process; the extension host
with its allow list, the sample extension and the conformance kit (a test
project since 2026-09-22, a package since 2026-09-25); profiles
validated, applied and shown; tuning per deployment, judged by a golden set;
the review queue for machine-written content; an append-only change record;
audit retention, which now comes with the business add-on; setup on an
existing PostgreSQL with least-privilege
roles and row-level security; HTTPS, service hosting and a bundled PostgreSQL
on Windows; removal; and an install proven on a bare container with no
network route out. Since 2026-09-24: a command line that explains itself with no
database, with the manual page generated from it; every setting in one
catalog; self-serve agents and the portal's connect page; the usage read
model, the usage page and the windowed audit export (which now comes with
the business add-on); the reminders seam and
job, with the latest run on the Review page; every file an extension loads
listed and hashed; groups in a profile; one golden set. Later that day: a
deployment's instructions for assistants, sent at connect and shown on the
connect page; who made each agent; every administrator verb at the command
line in the change record; native libraries in extensions with a sample for
two platforms; a reminder sink that sends mail, as a sample extension; the
fully local stack with its offline loop proof; the connect configurations
checked against each client's documentation. Since 2026-09-25: PDF and Word
readers as first-party extensions an administrator allows, with limits
against hostile files, on a reader seam that can say why a file was skipped
or could not be read; the conformance kit as a package versioned with the
product, which an extension author packs from the source and takes by
package reference; the release archives for Linux and Windows, built by
`scripts/release/build.sh` with the embedding model and the license texts
inside and proven to install and run on the .NET runtime they carry, and a
release workflow that makes a draft release from a version tag (no release
has been published); the bundled PostgreSQL and credentials file tests in
CI on a Windows runner; and the MCP authorization flow, built and off by
default, for an assistant on a person's own computer that signs in by
OAuth ([The authorization flow](authorization-flow.md)), which no
assistant has been run against here yet. Later that day: an Excel reader beside
the PDF and Word readers, on container guards the three share; the
conformance kit proving the sign-in and reminder seams; a benchmark in the
repository that times the search path in two modes; the mutant lists in the
repository with their runner and a CI check; the flow's loose ends (a profile
carries its settings, an assistant is registered from its metadata document
by hand, the framework's request lines are off); one shared runtime in the
release archives; the release built with the SDK `global.json` pins; setup's
Windows tests on a Windows runner; and the clean-install script's wait and
control. Since 2026-09-26: the clean-install proof installs the Linux
release archive itself, archive to archive on the upgrade; a request bound
on `/mcp` of the deployment's own; an assistant registered from its metadata
document, or its document replaced, from the portal as from the command
line; a binary workbook stated as skipped and Word text boxes read; the
ingest rate in the benchmark; the runner's self-test in CI and a benchmark
workflow on the hosted runner; the import libraries out of the Windows
archive. Since 2026-09-27: binary workbooks read; the Windows archive
carrying the Visual C++ runtime files its model library needs, proven on a
clean Windows as a service and removed again; the release workflow proving
the archive it built before a draft; a too-large document form answered with
a page; the suite always ending in a verdict.

## What comes next

- **A connector that reads a file share's own permissions,** so a document's
  access comes from the share that holds it rather than from a rule written
  beside it.
- **Sign-in through a company directory,** as a way of signing in on the
  existing seam: the directory says who you are, and a principal mapper,
  such as the business add-on's directory group mapping, says what that
  means here.
- **An installer,** so a deployment starts from a package rather than from
  an archive and the steps in its `INSTALL.txt`. The archives do not carry
  the bundled PostgreSQL.
- **The clean Windows machine proof:** the Windows service and the bundled
  database service starting under their own accounts, and removal taking
  them away, on a machine that has never seen the code.
- **A calibration command for the no-answer floor,** so a deployment can
  set, from its own golden set, the score below which search says it found
  nothing.
- **A real run of Claude Desktop and VS Code Copilot** against the connect
  page's configurations, after which their sentences change from "checked
  against the documentation" to "run"; and a chat client tried against the fully local layout (a real
  local model server has been; the Known state page says which).
- **Managed libraries built per platform** under `runtimes/<rid>/lib/<tfm>/`
  in an extension, loaded for the platform the host runs on; today such a library is refused when the extension is allowed and at load,
  with the way round named.

## Directions under consideration

- **An administration API, reads first,** with writes as reviewed profiles:
  an assistant may propose a configuration, validation names what it would
  change, and a person applies it.

The invariants do not move: agents never write, the store holds no generated
text, and nothing from a document is executed.
