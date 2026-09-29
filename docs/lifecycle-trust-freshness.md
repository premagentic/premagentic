# Lifecycle, trust and freshness

The three gates beside access: lifecycle, which keeps superseded and expired material behind a historical flag; trust and freshness, read from Open Knowledge Format frontmatter; undeclared authorship in a bundle; and the review queue. The four gates, and how every read passes them, are described on [Access](access.md).

## Lifecycle

Superseded, archived, draft and expired material stays indexed
but is reachable only behind an explicit historical flag. The flag is never
inferred from the wording of a query, so a stale document cannot be served as
a current answer.

## Trust and freshness (Open Knowledge Format)

PremAgentic reads OKF
frontmatter: who wrote a document (`generated`), who confirmed it (`verified`),
and when it goes stale (`stale_after`). Content a machine wrote is served to
agents only once a person has reviewed it, so one agent's unreviewed output
does not become another agent's input. Content past its `stale_after` is hidden
from agents and shown, flagged, to people. Both are checked when the question
is asked, so a change of policy applies to the next search with no re-ingest.
Every result says what it is: trust tier, authorship, stale flag, and the OKF
concept id when the folder is read as a bundle (`prem ingest --okf-bundle`).
Every surface passes the caller's own policy: a person gets the people's
setting, an agent the agents' setting or its own minimum tier. A search that
names no policy gets the strict one. Ordinary files with no OKF frontmatter are
never held back.
In a folder read as a bundle, a concept whose frontmatter cannot be parsed is
treated as machine-written and unverified. The limit: frontmatter is
self-declared, so the trust gate is as strong as control over who can write
the files.

## Undeclared authorship in an OKF bundle

By default a concept that does not
say who wrote it is served like an ordinary file. A source can instead treat
it as machine-written and unverified, so agents do not see it until a person
signs it off (`--undeclared-as-machine`). It never applies outside a bundle.
Every bundle run counts the concepts that declare no author, so the exposure
is visible before the setting is switched on.

## The review queue

The portal's Review page lists the documents a person
should look at before agents are served them: machine-written documents
below human-reviewed, which agents do not see at the default trust setting,
and Open Knowledge Format concepts that nobody has verified. Each row gives
the path, the source, who the document says wrote it, its trust tier, its
stale date and the folder the file lives in, filterable by source and tier.
The page changes nothing. A person reviews the file where it lives and signs
it off there, with a `verified` entry naming a `human:` actor, and the next
ingest of its source reads the sign-off. The health page shows how many
documents are in the queue.
