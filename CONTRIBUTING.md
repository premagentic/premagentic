# Contributing

PremAgentic is search over an organization's own documents that returns
cited passages and generates no prose, built to run inside that
organization's network. The help most wanted right now: try the quick start
in the README and report where it stumbled; fix the docs where they lost
you; write a reader, a chunker or a connector as an extension, which needs
no change to this repository; contribute a golden set for a kind of corpus
you know well, such as a policy manual, a wiki export or a support archive,
so retrieval can be judged against it; and report bugs with the exact
sentence the program printed.

## Ways to take part

- **Discussions** are for questions and ideas: how the gates should treat a
  case, whether a seam is missing something, what you tried and what
  happened.
- **Issues** are for bugs and for concrete proposals. The templates ask for
  what a maintainer needs to reproduce or weigh one; fill them in.
- **Pull requests** carry a change. The next section says how to open one.
- **Security reports** go through the private channel in `SECURITY.md`,
  never through an issue or a discussion, and are always answered.

Issues and pull requests are read and triaged, without a response-time
commitment. See the Support section of the README for what this repository
does and does not promise.

## Your first contribution

1. **Fork** the repository and clone your fork.
2. **Branch** from `main`, one change per branch.
3. **Build and test** as the next section says. The tests need a running
   Docker daemon; every run starts its own PostgreSQL.
4. **Read the rules** under "Rules a change has to keep" before you write
   code. They are the product. A change that bends one is declined however
   good the code.
5. **Write the test that fails without your change**, then make it pass.
   Break your own test once, on purpose, and watch it fail before you open
   the pull request: a guard that cannot fail proves nothing.
6. **Open the pull request** against `main`. The template asks for what and
   why in a sentence, which rules the change touches, the test that fails
   without it, and the test count from your run.

Review looks for four things: the change does what its sentence says and
nothing more; every rule below still holds; a named test fails without it;
and nothing in it came from a real document, a real path or a real person.
Code, comments, commit messages and fixtures are in US English with no em or
en dashes, and every sample document is invented.

Two labels mark work set aside for newcomers. `good first issue` is a change
one person can finish in an evening, with the file or seam named and what
done looks like spelled out in the issue. `help wanted` is a larger item the
maintainers would take a pull request for but have not scheduled. Say in the
issue that you are taking it, so two people do not do the same work.

## Building and testing

```sh
docker compose up -d                     # stock PostgreSQL 17 on 5434
./scripts/download-model.sh              # or scripts/download-model.ps1
dotnet build Premagentic.slnx
dotnet test Premagentic.slnx              # needs a running Docker daemon
```

CI runs the same build with NuGet audit findings as errors, so a dependency
with a known advisory fails the build.

## Rules a change has to keep

- **The gates stay hard SQL conditions.** Access, lifecycle, trust and
  freshness define the candidate set before retrieval runs. None may become a
  ranking signal, a post-filter, or something a relevance score can outweigh.
- **The second line stays behind the gates.** A new gate, or a new parameter
  on one, needs a migration that drops `prem_index.text_matches` and creates it
  with the new arguments; migrate refuses to install a body that does not
  fit. A change to a gate's SQL needs no migration, but it changes the
  checked-in text in `TextMatchFunctionTests`. Under a row policy, PostgreSQL
  will not use an index for an operator that is not leakproof (the full-text
  match is not): after changing a policy or an index on `chunk`, re-run
  EXPLAIN as the search role with a bound session.
- **Access has no permissive default.** A connector states each document's
  access explicitly, and one that cannot read the source's permissions returns
  `DocumentAccess.NoOne`. Do not add a fallback to public.
- **Anything that reads a document applies the same filter as search.** A
  caller who cannot find a document must not be able to fetch it another way,
  and must not be able to learn that it exists.
- **Tests assert both directions.** A test that only proves a caller is denied
  passes just as well against a build that returns nothing to anybody. Show the
  authorized caller succeeding in the same test or beside it.
- **Connectors never write to the source**, and every path a connector yields
  sits under its declared prefix.
- **No customer content, ever.** Evaluation reports name real questions, paths
  and headings from the corpus they ran against, and are ignored by git. Sample documents in this repository are invented.

## Extensions are the door

Most of what an installation needs that this repository does not have is a
reader for another format, a chunker for another shape of document, a
connector for another kind of source, an embedding provider, or a way of
signing in through another system. Each is a seam, and each ships as an
extension: a folder holding one assembly and a manifest, allowed by an
administrator by name and hash, loaded by every host at once. An extension
needs no change here, no review from anyone but its own administrator, and
no release of PremAgentic. `docs/writing-an-extension.md` takes you from an
empty folder to an extension the host loads, with the conformance fixtures
that prove it keeps its contract. If a seam is missing something you need,
that is a concrete proposal for an issue.

A release is made by the maintainer following `scripts/release/README.md`,
the release checklist; its workflow proves the Linux archive it built before
a draft is made.

## Licensing of contributions

PremAgentic is licensed under the GNU Affero General Public License 3.0 only,
and Agave Information Solutions, LLC also offers it under a commercial
license. So that a contribution can go into both, contributions are accepted
under a contributor license agreement:

- `CLA-INDIVIDUAL.md`, for a person contributing their own work.
- `CLA-ENTITY.md`, for a company contributing through its employees.

You keep the copyright in what you contribute. The agreement gives Agave a
license to use it, including under the commercial license, and Agave agrees
to keep it available under the license the project uses on the day you
contribute.

The first time you open a pull request, a check asks you to sign the
individual agreement by posting the one comment it gives you, and records
your GitHub account and the date. You sign once; later pull requests pass the
check on their own.

If you contribute as part of your job, your employer either approves your
signing the individual agreement or signs the entity agreement once. To sign
for a company, write to premagentic@agaveis.com with the company's legal
name, the signer's name and title, and the GitHub accounts it covers. Once the
agreement is signed, those accounts are added to the check, so it passes for
them without an individual signature.

## Work you did not write

If a pull request includes code, text or images that you did not write, say
so in its description: what it is, where it came from, who holds the
copyright, and its license, and mark that part "Not a Contribution". Work
under a license that can be combined with the GNU Affero General Public
License 3.0, such as MIT, BSD or the Apache License 2.0, can be accepted, and
is recorded in `THIRD-PARTY-NOTICES.md`.
