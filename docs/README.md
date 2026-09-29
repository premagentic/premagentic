# PremAgentic manual

One page per subject, in plain Markdown. This folder is the manual as GitHub renders it and as a contributor edits it beside the code, and the website renders the same pages. `toc.json` beside this file lists the same pages in the same order; `scripts/check-docs.sh` checks that the two agree, and the website's generator refuses to build when they differ.

## Start

- [What PremAgentic is](what-it-is.md): What PremAgentic does, the claim it makes about the network and how that claim is tested, what setup needs once, and what it does not do.
- [Getting started](getting-started.md): From a fresh clone to a cited answer, with the access gate proven, in the first ten minutes.
- [Connect an assistant](connect-an-assistant.md): How an assistant connects over MCP with an agent token, what it can reach, and the portal's connect page where a person does it for themself.

## How it decides

- [Access](access.md): How who may read a document is decided: ordered access lists and folder rules, agents and where their models run, and the second line of access control in the database.
- [Lifecycle, trust and freshness](lifecycle-trust-freshness.md): The lifecycle, trust and freshness gates, Open Knowledge Format frontmatter, undeclared authorship in a bundle, and the review queue.
- [Administration](administration.md): The administration portal, every setting in one catalog, the change record, the audit trail with its usage page, self-serve agents, reminders, with screenshots.

## Operate

- [Installing](installing.md): Installing on a PostgreSQL you already run, the roles and files setup makes, HTTPS from the first start, the bundled PostgreSQL on Windows, and installing from an archive built from the source.
- [Running it](running.md): Running the API as a Windows service or a systemd unit, and what to set behind a reverse proxy.
- [Upgrading and removing](upgrading-and-removing.md): How to upgrade and roll back, and how prem remove takes away the services with or without the data.
- [Configuration](configuration.md): The environment variables every host reads, their defaults, and what switching the embedding provider means.
- [The authorization flow](authorization-flow.md): The MCP authorization flow, built and off by default: what turning it on takes, what off means, how an assistant registers and a person approves it, how a grant ends, and the settings.
- [Storage](storage.md): Plain PostgreSQL with no extension, the two schemas and their promises, migrations, the audit trail, and the vectors held in memory.

## Extend

- [Sources, readers and chunkers](sources-readers-chunkers.md): Registering a folder as a source with its owner and hosted-model switch, the readers that read its files and the chunkers that cut them, and what a run did not read.
- [Extensions](extensions.md): How a reader, a chunker, an embedding provider or a way of signing in is added from a folder, allowed by hash with every file it loads, and refused with a reason.
- [Writing an extension](writing-an-extension.md): A walkthrough for writing an extension, proving it with the conformance kit, and allowing it in a deployment.
- [Connectors](connectors.md): The IDocumentSource extension point and the filesystem reference connector.
- [Profiles](profiles.md): A folder of plain files carrying a whole configuration, validated as a whole, applied as a whole, and compared with a deployment.
- [Tuning and the golden set](tuning-and-the-golden-set.md): The retrieval settings that are per deployment, how to judge a change with the golden set, and why the golden set is the deliverable.

## Reference

- [Surfaces](surfaces.md): The projects in the repository, the three ways a caller reaches the HTTP surface, the portal's pages, agents over MCP, and your own words on the tools.
- [The prem command](cli.md): Every prem command with its arguments and options, as the program prints them.
- [Measuring it yourself](benchmark.md): The benchmark in the repository: what it times, how to run it on a throwaway database, and why every number depends on the machine.
- [Known state](status.md): What is built and tested, how the guards were checked, what the clean-install script and the archive proofs show, and what is not built.
- [Roadmap](roadmap.md): What comes next for PremAgentic.
