# Surfaces

The projects in the repository, the three ways a caller reaches the HTTP surface, groups mapped from your own directory, agents over MCP, and your own words on the tools.

| Project | What it is |
|---|---|
| `Premagentic.Core` | Connectors, chunker, embedding providers, migrations, gates, hybrid retrieval, the in-memory vector index |
| `Premagentic.Cli` | `migrate` (alias `init-db`), `setup`, `remove`, `rebuild-index`, `ingest`, `search`, `section`, `eval`, `reminders`, and administration: `users`, `groups`, `agents`, `tokens`, `oauth`, `rules`, `settings`, `sources`, `extensions`, `profile`. `prem --help`, `prem <command> --help` and `prem --version` answer with no database; [The prem command](cli.md) is generated from that text |
| `Premagentic.Api` | Sign-in through one seam (a password and a session, or a trusted header), search and section as the caller (`POST /api/search`, `/api/section`), MCP over HTTP at `/mcp` for agent tokens, `GET /health`, and the administration portal at `/portal` with its usage page (`/portal/usage`), connect page (`/portal/connect`), and Review page with the reminders view |
| `Premagentic.McpServer` | A stdio bridge to `/mcp` for an agent that only speaks stdio, with an agent token read from a file; it holds no database credentials |
| `Premagentic.Tests` | Unit tests, plus the gates, identity, the datastore, the vector index and setup end to end on stock PostgreSQL |
| `Premagentic.Conformance` | xunit fixtures an extension author inherits to prove a reader, a chunker, a connector, an embedding provider, a sign-in adapter or a reminder sink keeps its contract |
| `samples/extensions/sentence-chunker` | A whole extension: a chunker, a `.csv` reader and a sign-in adapter that claims nothing, loaded by the tests from a folder by hash |
| `samples/extensions/paragraph-chunker` | An extension with a library of its own (`paragraph-rules`), listed in its manifest with its hash, which is how a dependency is allowed |

## Who is calling

Every request to the HTTP surface runs as a caller PremAgentic resolved itself,
with that caller's groups and rules as they are at that moment. A request with
no caller is refused with a 401. There are three ways to be one.

- **A person signs in** with a sign-in name and a password (`POST
  /api/session`) and gets a session cookie: HttpOnly, Secure, SameSite=Strict.
  A session ends after 30 minutes idle or 8 hours at most, at sign-out, or the
  moment the account is disabled or given a new password. Password sign-in is
  refused over plain HTTP. Five wrong passwords lock an account for 15
  minutes, failures are throttled by address, and every failure gets the same
  answer, so a caller cannot tell which names exist. A request that changes
  something under the cookie carries the anti-forgery token that sign-in
  returned, in `X-Prem-Antiforgery`.
- **An agent presents its token** in `Authorization: Bearer prem_agt_...`. It
  reaches what its registration gives it, is held to its requests per minute
  (counted by each API process separately), and sees machine-written content
  by the agents' trust setting or its own minimum tier. A revoked or expired
  token reaches nothing.
- **A trusted header**, for a customer whose proxy already signs people in:
  set `PREM_SIGN_IN_HEADER` to the header the proxy writes the person's
  PremAgentic sign-in name into. PremAgentic resolves that user's groups from its
  own tables; an unknown or disabled name reaches nothing. With the mode on,
  only the proxy may reach the port, because a client that can set the header
  can name any user.

PremAgentic signs people in two ways out of the box: a password, which issues
a session, and a trusted header, for a deployment where a proxy in front has
already signed the person in. Both go through one seam, so a third way can be
added as an extension without touching the core.

Whatever the way, it resolves a person to an account you already made. It
never creates an account and never grants a role. A name PremAgentic does not
know reaches nothing.

**Groups from your own directory.** If people sign in through a directory,
what its groups mean here is decided by a principal mapper, which an
extension brings (see [Extensions](extensions.md));
PremAgentic for Teams brings one, directory group mapping. A group the mapper does
not map means nothing and is ignored, and with no mapper every group means
nothing, so nobody gets access you did not write down; the start says when
no mapper is loaded, and the log names each ignored group once. The same
mapper decides what a connector's permissions come to, for a connector that
reads a source's own permissions (the filesystem connector does not, and none
ships yet): a file readable by a principal the mapper maps is readable by that
PremAgentic group, and a file readable only by principals it does not map is
readable by nobody. An ingest run says how many principals it met that mean
nothing here, so a source whose groups were never mapped does not look like an
empty folder.

Behind a reverse proxy the API sees the proxy's address, so the address
throttle covers everyone behind it at once, and where TLS ends at the proxy
password sign-in is refused. Name the proxies in `PREM_TRUSTED_PROXIES` (see
[Behind a reverse proxy](running.md#behind-a-reverse-proxy)) and the API believes their forwarding headers instead.

Agents connect over MCP at `/mcp` in the same process, with their token:
Streamable HTTP, MCP revision 2026-07-28, stateless. They get two read-only
tools, `search_knowledge` and `get_document_section`, and no tool that writes,
stores or remembers anything. For an agent that only speaks stdio,
`Premagentic.McpServer` is a bridge: it reads a token from the file named by
`PREM_AGENT_TOKEN_FILE`, forwards to `PREM_API_URL`, and holds no database
credentials.

A request to `/mcp` may carry at most `mcp.max_request_bytes` bytes: 256 KB
unless an administrator sets it (`prem settings set mcp.max_request_bytes
1048576`, read when the server starts). 256 KB is the largest legitimate
request with room to spare: an assistant's largest is a `tools/call`
carrying a query and a few arguments, a few kilobytes even for a pasted
passage. A larger request is refused with HTTP 413 and a JSON-RPC error
(code -32600) that states the limit, before anything reads past it. The
stdio bridge reaches the server through `/mcp`, so it is bound by the same
number.

A query is at most 4,000 characters, and a section's path and heading 4,000
each, on every surface: the API, MCP, the portal and `prem`. A question is a
sentence or a paragraph; the embedding model reads only the start of a longer
one, and every query is kept whole in the audit trail. A longer one is
refused with one sentence that says so, before anything is read or recorded:
HTTP 400 from the API, the sentence as the tool's answer over MCP. A request
to `/api/search` or `/api/section` may carry at most 64 KB (65536 bytes),
which holds the longest fields even with every character escaped; a larger
one is refused with HTTP 413 before anything reads past it.

**Your own words on the tools.** An assistant reads a tool's description to
decide whether to call it. Set `mcp.tool_descriptions` to name your own corpus
and the tools get called when they should be:
`prem settings set mcp.tool_descriptions '{"search_knowledge": "Search the
Contoso handbook and client files."}'`. It is read when the server starts.

Every result says what it is: trust tier, authorship, stale flag and concept
id, on the HTTP surface, over MCP and in the CLI.
