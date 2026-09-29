# The authorization flow

The MCP authorization flow, built and off by default: what turning it on takes, what off means, how an assistant registers and a person approves it, how a grant ends, and the settings.

An assistant that runs on a person's own computer and signs in by OAuth,
instead of carrying a token pasted from the connect page, can use the MCP
authorization flow (OAuth 2.1 with PKCE, as MCP revision 2026-07-28 describes
it). It is built and off by default. It has been run end to end here with
a headless browser and a script standing in for the assistant, from
discovery and registration through consent, the token exchange, a search,
a refresh and a revoke; no assistant of a vendor's has been run against it
yet. It changes nothing about where an assistant's requests come from:
one called from its vendor's cloud still cannot reach a server inside the
network directly (see [Connect an assistant](connect-an-assistant.md)).

The invariants hold for it as for a pasted token. An assistant that signs in
this way reads through an agent that acts for the person who approved it,
under that person's access at each call, with the same read-only tools, the
same rate, and an audit row for every search. It never writes. The server
makes no outbound request for the flow: it fetches no client description, no
key set and no introspection answer.

## Turning it on

Two settings, then a restart:

```
prem settings set mcp.oauth.public_url https://knowledge.example.com
prem settings set mcp.oauth.enabled true
```

`mcp.oauth.public_url` is the server's address as assistants reach it, in one
spelling: lowercase `https://`, a lowercase host, no path, no port when it is
443. Any other spelling is refused with the canonical one named, so a retype
cannot change the audience the tokens are bound to. Every address the flow
publishes is made from it, never from a request's `Host` header; behind a
reverse proxy it is the proxy's public address. With the self-signed
certificate `prem setup` makes, an assistant's computer must trust that
certificate, or a proxy with a trusted one must sit in front.

`prem settings set mcp.oauth.enabled true` is refused until a usable
`mcp.oauth.public_url` is stored, and unsetting the address is refused while
the flow is enabled. Both are read when the server starts. If the flag is on
and the address is missing or unusable, or an entry of
`mcp.oauth.dynamic_redirect_uris` is bad, the server starts with the flow off
and logs one warning naming both remedies; it never refuses to start over
these settings.

## What off means

While the flow is off, none of its paths is mapped: each answers exactly what
a path that never existed answers for the same caller, and no table of the
flow is read or written by anything the server does, a person's disable
included. That is proven by a test that reads the database's own statistics
for the flow's five tables before and after a full session and finds them
unmoved, beside a control that sees them move while the flow is on. The
`prem oauth` commands work whether the flow is on or off, so an
administrator can register clients before turning it on and revoke grants
after turning it off.

Turning the flag off suspends grants without ending them; `prem settings set
mcp.oauth.enabled false` prints how many live grants there are and the
command that ends them. The stop that needs no restart is

```
prem oauth grants revoke --all
prem settings set agents.self_service_max 0
```

which ends every grant and refuses every new approval from the next call.
Closing self-registration (`mcp.oauth.dynamic_registration false`) takes a
restart.

## How an assistant registers

An assistant reaches the flow through the standard discovery documents at
`/.well-known/oauth-protected-resource/mcp` and
`/.well-known/oauth-authorization-server`. Every address in both documents
is made from the stored `mcp.oauth.public_url`, never from a request's
`Host` header. It then registers in one of the ways below.

- **It registers itself** at the registration endpoint, when
  `mcp.oauth.dynamic_registration` is on (the default). It gets an id and no
  secret: every client is public, and the code exchange is protected by
  PKCE (S256 only) and by refresh tokens that rotate on every use. A
  self-registered assistant may send its answer only to a loopback address,
  that is a program on the computer the browser runs on, unless the
  administrator lists exact `https` addresses in
  `mcp.oauth.dynamic_redirect_uris`; any other address it offers is dropped,
  and a private scheme such as `vscode:` is never accepted. That default is
  what keeps a stranger from registering a friendly name with an address of
  their own and sending a colleague a link to a genuine consent page.
- **An administrator registers it** with `prem oauth clients add`, with the
  redirect addresses typed in, and can state where its model runs so the
  consent page says so instead of asking each person.
- **An administrator stores its metadata document by hand**, for an
  assistant that names itself by an `https` address (a client ID metadata
  document). The administrator saves the document the vendor publishes and
  runs `prem oauth clients add --metadata-file <file> --id <address>`. The
  document's `client_id` must equal the address exactly; its name and
  redirect addresses are taken from it; a private-scheme redirect is
  dropped; a client secret or key of any kind is refused; every other field
  is left out; and the server keeps the file's SHA-256, so it can be
  compared with the published copy later; the portal's Clients page shows,
  under such a client's id, that SHA-256 and when the document was stored.
  The server reads nothing from that address, ever, and its metadata says so
  (`client_id_metadata_document_supported` is `false`): such an assistant
  uses the address as a client id configured ahead of time, where it lets
  one be set. The address is matched exactly, so a difference of case, a
  trailing slash or a percent-encoding is a different assistant, and one not
  stored is refused with a sentence naming the command.
  When the vendor changes the document, `prem oauth clients replace
  --metadata-file <file> --id <address>` replaces the stored copy in place:
  the name and redirect addresses come from the new copy, the stored SHA-256
  and its date move together, and the change record keeps the old hash and
  the new. Every grant people gave stands, and a code is exchanged only for
  a redirect address the new copy lists, so an answer address the vendor
  dropped stops working at once. A client registered with its addresses
  typed in has no document to replace; remove it and add it from the
  document. An administrator can do both in the portal too: Register from a
  metadata document on the Clients page takes the address and the document,
  pasted or as its file, and a client stored from one has Replace its
  document on its row. Each goes through the same check as the command and
  shows its refusal in the same words. A document is at most 64 KB; a larger one is refused on the
  Clients page with the flow's own sentence, "The metadata document is
  larger than 64 KB, which no client's document needs.", however large it
  is, and a form that carries more than 128 KB in its document or 320 KB in
  all is refused that way before the page reads it. Over HTTP/2, which a
  browser picks with HTTPS, the page arrives at any size; over HTTP/1.1 a
  browser may show a form past 320 KB as a dropped connection instead. A browser sends pasted text with its
  line breaks as CR LF, so to keep the hash comparable with the published
  file, choose the file.

Self-registration is bounded: at most `mcp.oauth.max_pending_clients`
never-approved clients in all (100 by default), at most
`mcp.oauth.pending_clients_per_address` from one address (10), at most
`mcp.oauth.registrations_per_hour` from one address in an hour (10), and a
client that is never approved is removed after 24 hours. Filling the cap
takes as many addresses as the first divided by the second, ten at the
defaults, and they must keep registering to keep it full. The hourly count
and the token endpoint's failure throttle are held in the API process's
memory. A deployment runs one API process (the systemd unit and the Windows
service each start one), so both limits hold for the whole deployment; they
start again from zero when the process restarts, and two processes would
each keep their own. Removing a self-registered
client ends that registration, not the software: it may register again,
within the same bounds.

## How a person approves

The assistant sends the person's browser to the server, which forwards it to
the consent page in the portal; the person signs in first if they are not
signed in. The page shows the assistant's registered name and whether it
registered itself or an administrator registered it, the address its answer
goes to (called a program on this computer for a loopback address), the
assistant that will be made or kept, what it may do (search and fetch the
documents the person can read, read-only, and nothing else), and the
question where its model runs, unless the administrator stated it. Approve
and Deny answer with a page holding one link back to the assistant; the
browser is never sent anywhere without that page. A request from an
unregistered assistant, or one that names an address it did not register,
gets a page and no link at all.

Approving makes an agent that acts for the person, of the kind the
assistant's name says, counted against `agents.self_service_max` together
with the agents the person made on the connect page. A person has one live
grant per assistant; approving the same assistant again replaces the grant
and keeps the agent when its model location still matches, and makes a new
agent, ending the old one, when it does not. An approval the assistant
never completes holds a slot only while its code can still be exchanged
(`mcp.oauth.code_seconds`, 60 by default).

## What the assistant holds, and how it ends

The assistant exchanges its code for an access token and a refresh token;
both are opaque and stored hashed, like agent tokens. An access token lasts
`mcp.oauth.access_token_minutes` (60); a refresh token rotates on every use
and lasts `mcp.oauth.refresh_token_days` unused (30); a used refresh token
that comes back ends the grant, since only a copy could present it. A grant
lasts `mcp.oauth.grant_days` (90) however it is used, and then the person
approves again. Every access token is bound to this server's address: after
`mcp.oauth.public_url` changes, every token issued under the old address is
refused at its next call.

A grant ends when:

- the person revokes it on the connect page, or an administrator revokes it
  on the portal's Grants page or with `prem oauth grants revoke`;
- an administrator disables or removes the client, which ends every grant of
  that client and disables their agents; enabling the client again brings
  none back;
- the person or the agent is disabled: the grant ends for good, and
  re-enabling does not bring it back. A disable is just as final for the
  tokens the person made on the connect page. After a re-enable, the person
  connects each assistant again;
- the person's password changes, which ends that person's grants and
  connect-page tokens, as it already ended their sessions; a first password
  set for a person who signed in by header or by an extension ends them
  too. Service agents, and agents an administrator made, are not touched by
  a disable or a password change;
- the server's address changes.

An administrator's revoke, or a disable of the person's agent for an
assistant, ends what the person had; it does not stop the person connecting
the same assistant again. A new approval makes a new grant, recorded like
the first. To stop an assistant for everyone, disable or remove the client.

A registration, an approval, a denial and every revocation are rows in the
change record, with the reason; a refresh writes no row, and the grant lists
show each grant's refresh count and last refresh instead. Every search and
fetch through a grant is an audit row as the agent, like any other.

After restoring `prem_config` from a backup, and before starting the API,
run `prem oauth grants revoke --all`: a grant the backup holds no longer
matches what the assistants hold.

## What the log holds

For the flow's requests, the server logs each refusal with its method, its
path and a fixed reason, and the grant it concerns when one was found. It
never logs a query, a token, a code, a verifier, a state or a challenge. The
framework's own request lines ("Request starting" and "Request finished")
carry each request's whole query, and the authorize address carries a
client's state and challenge in its query, so those lines are off by
default for every log the server writes to. A default level set for every
category, or for one provider, does not turn them back on; the lines that
name each endpoint run show its path only. An operator who wants the
request lines names their category for the log they read, for example
`Logging__Console__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information`,
and takes those queries into the log with them.

## The settings

Every setting is in the catalog `prem settings` reads and shows; the first
four are read when the server starts.

| Setting | Default | Range |
|---|---|---|
| `mcp.oauth.enabled` | false | true or false |
| `mcp.oauth.public_url` | unset | one canonical `https` address |
| `mcp.oauth.dynamic_registration` | true | true or false |
| `mcp.oauth.dynamic_redirect_uris` | empty | at most 20 exact `https` addresses, each at most 512 characters |
| `mcp.oauth.max_pending_clients` | 100 | 1 to 10,000 |
| `mcp.oauth.pending_clients_per_address` | 10 | 1 to 1,000 |
| `mcp.oauth.registrations_per_hour` | 10 | 1 to 1,000 |
| `mcp.oauth.access_token_minutes` | 60 | 5 to 1,440 |
| `mcp.oauth.refresh_token_days` | 30 | 1 to 365 |
| `mcp.oauth.grant_days` | 90 | 1 to 365 |
| `mcp.oauth.code_seconds` | 60 | 10 to 600 |

A profile may set these keys too, and is judged as a whole, as the
deployment would stand once it is applied, never line by line: a profile
that turns the flow on must leave a usable `mcp.oauth.public_url`, whether
it sets the address itself, on any line, or finds it already stored, and a
profile that unsets the address (`null`) while the flow stays on is refused.
Each refusal is the sentence `prem settings` gives. The first four still
take a restart after `prem profile apply`, which says so.

The commands are on [The prem command](cli.md) under `prem oauth`.
