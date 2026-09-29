# Storage

What PremAgentic needs from PostgreSQL, the two schemas and their promises, how the schema is migrated, what the audit trail records, and where the vectors live.

Plain PostgreSQL with no extension: 14 or later, the versions the PostgreSQL
project still maintains, developed and tested on 17. A stock server the
customer already runs will do. A managed one should too, since nothing needs an
extension or a superuser step, but none has been run yet.

Two schemas with different promises:

- `prem_config` holds what cannot be rebuilt from the customer's files: the
  tenant, users, groups, agents, token hashes, access lists, folder rules,
  settings and the audit trail. Back it up.
- `prem_index` holds documents, chunks and their vectors. It is disposable:
  `prem rebuild-index --confirm` empties it, and ingest rebuilds it from the
  files. `prem_config` is never touched by a rebuild.

The schema is created and changed only by numbered migrations compiled into
the build. `prem migrate` applies them under a lock so two processes starting
together cannot both apply one, one transaction per migration. Every other
command checks first: when the schema is current it only verifies it, so a
database role with no right to change the schema can start; when something is
pending it is applied if the role may, and otherwise the command stops and
says to run `prem migrate` with the owner's credentials. A migration whose text changed after it was applied
is refused, and so is a database migrated by a newer build.

The audit trail records, for every question, the text of the question, the
calling user and agent when known, the access label, and the path, heading and
content hash of each passage returned, so a
bad answer can be traced to the exact version of the file that fed it even
after a re-ingest. Because the text of every question is kept, the trail is as
sensitive as the questions people ask.

**Vectors live in memory.** Embeddings are stored normalized, as float32
bytes, and the search process holds a copy for exact cosine search, grouped by
document. Each search asks PostgreSQL, under the gates, which documents the
caller may read and at which version, and scores only those documents' chunks.
A new or rewritten document is searchable at once, and a deleted or
re-permissioned one drops out of the next search, with no restart. Memory is
about 1.6 KB per chunk at 384 dimensions (39 MB for 25,000 chunks, 150 MB for
100,000, 750 MB for 500,000), which belongs in the hardware sizing.
