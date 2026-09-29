# Access

How PremAgentic decides who may read a document: the four gates every read passes, the ordered access list and the folder rules, agents and where their models run, the reserved hosted-model agents group, the section fetch, and the second line of access control in the database. The other three gates are on [Lifecycle, trust and freshness](lifecycle-trust-freshness.md).

Four hard SQL conditions applied at every read, so they define the candidate
set and no relevance signal can outweigh them. Behind them, the database
enforces access a second time: search and section reads connect as a role
that PostgreSQL row-level security limits to what the caller may see, so a
bug in application code, or a hand-written query with that role's
credentials, still returns nothing the caller was not permitted (see [Two lines of access control](#two-lines-of-access-control)
below). Each is written over the
document alone. The text leg runs under them; the vector leg scores only the
chunks of documents the database returns under them; and the passages that
leave are read back under them once more.

**Access.** Who may read a document is an ordered access list, evaluated in
the order written: the first entry that names the caller decides, and no entry
means no. It is not "deny wins": an allow placed ahead of a deny grants.
Lists come from folder rules an administrator
sets (`prem rules set --prefix hr --entry "deny group:Contractors" --entry
"allow group:Staff"`); a folder no rule covers is readable by nobody. The
filesystem connector reads no NTFS or POSIX permissions. A connector that can
read a source's own permissions could supply the lists instead, and none ships
yet. Each search
computes, from the caller's groups as they are at that moment, which lists the
caller may read, and the database returns only documents under those lists, so
a removed membership, a disabled account, a revoked token or a changed rule
applies to the very next query. Changing a rule moves the documents under it at
once, with no re-embedding. Rules name groups by id, so renaming a group never
changes a rule, and a group that a live rule names cannot be removed without
`--force`, because removing it would disarm a deny. A connector that cannot
read the source's permissions returns `DocumentAccess.NoOne`, and the document
indexes but stays unreachable, so the gap is countable instead of invisible.
`AccessScope.UnrestrictedAudited(reason)` bypasses this gate for operator
tooling and the evaluation harness, and stamps its reason on every retrieval
event it produces.

PremAgentic keeps its own users, groups and agents (`prem users`, `groups`,
`agents`, `tokens`). An agent either acts for one user, holding exactly that
user's access and never more (an entry naming the agent can hold it back
further), or is a service agent holding only the groups granted to it, and
either kind stops when its owner is disabled. Agent tokens are shown once and
stored only as a hash.

Every agent says where its model runs: `local` for a model inside your
network, `hosted` for one outside it, with the vendor named. Register it with
`prem agents add <name> --owner <user> --mode <mode> --model local|hosted
[--vendor "Name"]`, and move it later with `prem agents set <name> --model
...`. There is no default; a registration that does not say is refused,
because the answer decides what the agent may be served.

PremAgentic keeps one group of its own, `hosted-model agents`, holding every
agent registered as using a hosted model. You never edit it: it follows
what the agents are set to. A folder rule that DENIES that group keeps what
that rule decides from every agent registered as using a hosted model; a
longer rule beneath it decides for itself. A source's "may be served to hosted
models" switch set to no holds everything at or beneath its folder back from
them, whatever rule decides it (see
[Sources, readers and chunkers](sources-readers-chunkers.md)). You cannot use
the group the other way round: allowing it gives nobody anything.

Every answer is recorded with where its model ran, so the audit trail is also
the record of what was served to hosted-model agents.

`SectionFetcher` applies all four. A caller who cannot find a document in
search cannot fetch it by citing its path, and a path the gates hold back is
reported as absent rather than forbidden, so the tool cannot be used to
enumerate what exists.

## Two lines of access control

PremAgentic enforces access twice: in each
query, and again in the database, which refuses every row a caller may not
read. Search and section reads connect as a third role, `premagentic_search`,
bound for the length of one read to a caller session that holds the caller's
permitted access lists, recomputed for every read, so a revoked membership
applies to the next query. The permitted lists never travel on the
connection; the policy looks them up through a function owned by the schema's
owner. The application role, which ingests, reads the whole index by design
and is the one role listed as a writer; the API refuses to start with a search
role the policy would not bind. The server stops any statement the search
role runs after 15 seconds, a `statement_timeout` setup sets on the role in
the deployment's database, so a read nobody is waiting for any more ends
there whatever the client does. With the application's own gate removed on
purpose in a test, the database still returns nothing unauthorized. The
full-text index stays in use under the policy because the text match runs
inside a database function generated from the same gate code the application
uses; a change to a gate's SQL shows up as a diff in a checked-in rendering.
Measured on 2026-09-22 at 100,000 chunks, before the benchmark in the
repository existed, the text match through the search role took as long as
through an exempt role; it had taken 2.68 times as long until the function
applied the caller's and the session's access lists as one condition, which
kept PostgreSQL's parallel plan. The benchmark times a whole search both ways
([Measuring it yourself](benchmark.md)).
