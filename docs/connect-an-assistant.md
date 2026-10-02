# Connect an assistant

This page registers an agent, issues it a token, and points a real MCP client at PremAgentic, over HTTP and through the stdio bridge, then checks what the assistant gets and what it does not. It was written from a run against the database the [Getting started](getting-started.md) steps leave behind, with Claude Code as the client; every command and every quoted line is from that run. Where a configuration was taken from a client's own documentation and not run here, the page says so.

## What an assistant gets

- **Two tools, both read-only.** `search_knowledge` takes `query`, `includeHistorical` and `topK` and returns cited passages: path, heading, content, and each passage's lifecycle, trust tier, authorship and stale flag. `get_document_section` takes `path`, `heading` and `includeHistorical` and returns the full text of a section a search cited. There is no tool that writes, stores or remembers anything, and the stdio bridge passes through only tools the API marks read-only.
- **Its own rights, every call.** A call runs as the registered agent, with the groups it holds and the folder rules as they are at that moment. A `service` agent reads as itself and the groups it was granted; an `acts-for-user` agent reads as its owner and never more.
- **The trust setting.** Machine-written content reaches an agent by `trust.agents_minimum_tier` (`human-reviewed` by default), or by the agent's own `--min-trust` when set; stale content by `trust.stale`. People have a separate setting, so what an agent sees can be narrower than what a person sees.
- **Where the model runs, and why PremAgentic asks.** Registering an agent requires `--model local` or `--model hosted`; there is no default, because the answer decides what may be served. Everything served to a hosted-model agent leaves the network, so every such agent is a member of the reserved group `hosted-model agents`, the location is written on every audit row, a folder rule that denies that group keeps what that rule decides from every agent registered as using a hosted model, and a source's "may be served to hosted models" switch set to no keeps everything at or beneath the source's folder from them, whatever rule decides it. The group can only take access away; allowing it gives nothing to anybody.
- **A rate.** 60 requests per minute unless `--rate` says otherwise, counted by each API process.
- **What it receives at connect.** PremAgentic sends its name and version, the two read-only tools with this deployment's descriptions of them, and, when an administrator has written them, the deployment's instructions (`mcp.instructions`). A client that follows the MCP specification puts the instructions in the model's context. They are also a resource, `premagentic://instructions`, for a client that reads resources; a client that reads a file instead (`AGENTS.md`, `CLAUDE.md` and the like) can take the same text from the connect page. The stdio bridge passes all of it through.

## Connect your own, from the portal

The quickest way for a person to connect an assistant is the connect page,
`/portal/connect`, which every signed-in person can open. Give the assistant
a name, say which assistant it is (Claude Desktop, Copilot in VS Code, a
coding tool such as Claude Code, a local MCP client, or something else;
ChatGPT is listed too, but it cannot reach a server inside your network
directly, as described below) and where
its model runs, and name the vendor for a hosted model. The
page makes an agent that acts as you, reads what you may read as your access
is at each call, runs at the default rate, and can ask for no group and no
rate of its own. Its token is shown once, inside the configuration for the
assistant you chose, with the server's address filled in and a button to
copy it; the page never shows it again. Your assistants are listed on the
same page, with where each model runs, when it was made and last used, and
its buttons: Reissue key, Revoke and Remove on a live one, and Remove alone
on one that is revoked or has ended. Reissue key gives the assistant a new
key in place of its current one, which stops working at once; the new key
is shown once, inside the configuration, as when you connected it. Revoke
ends it at once and keeps it on your list. Remove ends it and takes it off
your list; what it asked stays in the audit under its name, marked removed.
Reissue key and Remove each ask first, on a page that says what will
happen. An administrator sets how many each
person may have with `agents.self_service_max` (2 by default, 0 turns the
form off) and can replace any assistant's configuration text with
`connect.snippets`, a JSON object of assistant kind to template, in which
`{address}` and `{token}` are filled in. The configurations for Claude Code
and for the stdio bridge are the ones proven below; Claude Desktop and
Copilot follow each client's own configuration format and have not been run
here yet; for ChatGPT the page says why it cannot reach a server inside your
network directly, and names a way that has not been tried with PremAgentic.
An assistant on your own computer that signs in by OAuth instead of carrying
a token uses the MCP authorization flow, which is built and off by default;
[The authorization flow](authorization-flow.md) says what turning it on
takes and what a person sees.

The rest of this page is the administrator's way, from the command line,
which is also how a service agent that acts for a group is registered.

## Register the agent

From the clone, with `PREM_DEV_DATABASE=1` set as in the quick start. An agent needs an owner, a person who answers for it:

```
dotnet run --project src/Premagentic.Cli -- users add owner --display "Owner"
dotnet run --project src/Premagentic.Cli -- agents add assistant --owner owner --mode service --model hosted --vendor "Anthropic"
dotnet run --project src/Premagentic.Cli -- agents grant assistant engineering
```

```
User 'owner' added as member, with no password.
Agent 'assistant' added, owned by 'owner', service, a hosted model from Anthropic. Issue it a token with 'prem tokens issue assistant'.
Agent 'assistant' now holds group 'engineering'.
```

Issue the token. It is printed once, on the last line, and cannot be recovered; put that line in a file only you can read and never on a command line. In PowerShell, the run here caught the output and wrote the last line to a file:

```
$t = dotnet run --project src/Premagentic.Cli -- tokens issue assistant --days 30
$t[0..1]
[IO.File]::WriteAllText('C:\<a private folder>\agent.token', ($t[-1].ToString().Trim() + "`n"))
```

```
Token 127ecb4136025a4cc663c896 for agent 'assistant', expires 2026-10-23 13:30:49Z.
It is shown this once and cannot be recovered; store it now:
```

The token begins with `prem_agt_`. `prem tokens revoke <id>` ends it, with the id from the first line. `prem tokens reissue <id>`
issues a new one for the same agent and revokes the old one in one change;
the new one lasts as long as the old one was issued for, counted from now,
unless `--days` says otherwise, and a disabled agent or person, or a
revoked, expired or ended token, is refused, so a reissue never brings back
what a disable ended. `prem agents remove <name>` revokes an agent's tokens,
ends its grant and takes it off every list; nothing is deleted: the change
record, the audit and the usage still name it, marked removed, its name is
free for a new agent, and `prem agents list --removed` lists the removed
ones with when and by whom. `prem agents list` shows the agent, its owner, its rate, its model location and its groups:

```
assistant                service        owner owner            enabled   60/min  model: hosted (Anthropic)  groups: engineering
```

## Start the API

The quick start runs the CLI; an assistant connects to the API. From the clone, with the same shell:

```
$env:PREM_ONNX_MODEL_DIR = "$PWD\models\minilm"
dotnet run --project src/Premagentic.Api
```

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
```

The first line is needed because `dotnet run` starts the API in its own project folder, so the default model path `./models/minilm` points inside `src/Premagentic.Api`; without it the API stops with `Premagentic API cannot start: The local embedding provider needs model.onnx and vocab.txt in '<clone>\src\Premagentic.Api\models\minilm', and they are missing. Run scripts/download-model.ps1 or scripts/download-model.sh first.` The API also warns that no search role is configured, which is right for the development database; an installed deployment has one from `prem setup`, listens on HTTPS at 8443, and is where an assistant belongs in use.

A call with no token gets 401 and `{"error":"Sign in, or present an agent token."}`.

## Connect over HTTP

The endpoint is the API's address plus `/mcp`: here `http://localhost:5000/mcp`, on an installed deployment `https://<host>:8443/mcp`. The transport is Streamable HTTP, stateless, and the token goes in `Authorization: Bearer prem_agt_...` on every request. Any client that takes an HTTP MCP server with a header can connect. The configuration that ran here, given to Claude Code with `--mcp-config` (the file holds the token, so keep it private):

```json
{
  "mcpServers": {
    "premagentic": {
      "type": "http",
      "url": "http://localhost:5000/mcp",
      "headers": { "Authorization": "Bearer prem_agt_..." }
    }
  }
}
```

Claude Code's own help gives the same thing as one command, `claude mcp add --transport http premagentic http://localhost:5000/mcp --header "Authorization: Bearer prem_agt_..."`; that form was not run here. Plain `http://` is for one machine only; over a network use the deployment's HTTPS address and give the client the `https.crt` that `prem setup` wrote, if it does not already trust the certificate.

## Connect a stdio-only client

For a client that only starts a local process, `Premagentic.McpServer` is a bridge: it reads the token from a file, talks to the API's `/mcp`, and holds no database credentials. Build it once:

```
dotnet build src/Premagentic.McpServer -c Release
```

It takes two variables. `PREM_API_URL` is the API's address, `https://`, with plain `http://` accepted only for localhost. `PREM_AGENT_TOKEN_FILE` names the file holding the token; `PREM_AGENT_TOKEN` holds it directly instead, and setting both is refused. The configuration that ran here:

```json
{
  "mcpServers": {
    "premagentic": {
      "command": "C:\\<clone>\\src\\Premagentic.McpServer\\bin\\Release\\net10.0\\Premagentic.McpServer.exe",
      "env": {
        "PREM_API_URL": "http://localhost:5000",
        "PREM_AGENT_TOKEN_FILE": "C:\\<a private folder>\\agent.token"
      }
    }
  }
}
```

On Linux or macOS the command is the same path without `.exe`. Using `dotnet run --project src/Premagentic.McpServer` as the command also connected here; it builds on the first connection, and the whole exchange took 14 s against 10 s with the built executable, so the executable is the better command. A bridge that cannot start says what to set and exits with code 2, on standard error, so the client shows it:

```
Premagentic MCP bridge cannot start: PREM_API_URL must be the Premagentic API's address, such as https://prem.example.org:8443.
Premagentic MCP bridge cannot start: The bridge needs the agent token of a registered agent: set PREM_AGENT_TOKEN_FILE to a file holding it (issued with 'prem tokens issue <agent>'), or PREM_AGENT_TOKEN to the token itself.
Premagentic MCP bridge cannot start: PREM_API_URL must start with https://, because the agent token goes with every request. Plain http:// is accepted only for localhost.
```

### The bridge as a container

Each release also publishes the bridge alone as an image, `ghcr.io/premagentic/premagentic-mcp:<version>`, built from that release's Linux archive (`docker/mcp-bridge.Dockerfile`), and lists it in the MCP Registry as `io.github.premagentic/premagentic`. A client that runs containers starts it with the API's address and the agent's token:

```
docker run -i --rm -e PREM_API_URL=https://prem.example.org:8443 -e PREM_AGENT_TOKEN=prem_agt_... ghcr.io/premagentic/premagentic-mcp:0.1.0
```

Inside the container, `localhost` is the container itself, so the address is the API's own name and `https://`. An API whose certificate `prem setup` signed itself is trusted by mounting that certificate and naming it in `SSL_CERT_FILE`: add `-v /etc/premagentic/https.crt:/certs/prem.crt:ro -e SSL_CERT_FILE=/certs/prem.crt`. Without it the bridge reports "The SSL connection could not be established" and lists no tools; with it, both tools came through in the run here.

## Other clients, checked against their documentation

The connect page offers three more kinds. Each configuration was checked
field by field against the client's own documentation, and the ChatGPT text
against OpenAI's, on 2026-09-24, with tests that cite the pages; none has
been run on a real client here, and the page says so.

Where a client sends its request from decides whether it can reach
PremAgentic inside your network. ChatGPT, and a custom connector added in
Claude, call a remote MCP server from their vendor's cloud, not from your
computer. Claude Code and VS Code connect from the computer they run on, and
send the token as a header.

**Claude Desktop.** Claude Desktop starts a local program, so it connects
through the stdio bridge, `Premagentic.McpServer`. The connect page gives the
block for `claude_desktop_config.json` (the Claude menu in the system menu
bar, Settings, Developer, Edit Config) with the server's address and the new
token filled in; replace the command with the full path of the bridge, then
quit Claude Desktop completely and start it again. Use this local
configuration, not a custom connector: a custom connector added in Claude
(claude.ai, Claude Desktop or the mobile apps) is called from Anthropic's
cloud and cannot reach a server inside your network.

**VS Code (Copilot).** VS Code takes PremAgentic as an HTTP server in
`.vscode/mcp.json`, or in the user configuration (MCP: Open User
Configuration), under `servers`, with `type: "http"`, the `url` and an
`Authorization` header. The connect page gives the block with the token
written in, and the way VS Code's documentation prefers: an input variable
that VS Code asks for once and stores.

**ChatGPT.** ChatGPT calls a remote MCP server from OpenAI's cloud, so it
cannot reach PremAgentic inside your network directly, and it offers no way
to send the agent's token as a header. OpenAI documents Secure MCP Tunnel, a
program you run inside your network that connects out to OpenAI and can
start a local MCP program such as PremAgentic's bridge; this has not been
tried with PremAgentic. Everything ChatGPT reads through the tunnel reaches
OpenAI, so register the assistant with a hosted model, and the folders kept
from hosted-model agents are kept from it. The connect page says this, and
gives the bridge's program and its two settings, `PREM_API_URL` and
`PREM_AGENT_TOKEN`, for a person who tries it.

## Say what the corpus is

An assistant reads a tool's description to decide whether to call it, and the shipped description says "this organization's own documents". Name your corpus in `mcp.tool_descriptions`, a JSON object of tool name to description, and restart the API; it is read once at start. The value set here:

```
dotnet run --project src/Premagentic.Cli -- settings set mcp.tool_descriptions '{"search_knowledge": "Search the Northwind Cabinets employee handbook and HR policies."}'
```

The command stores the value, and `prem settings list` shows it. The API's next start logged `The MCP tool search_knowledge is described in this deployment's own words.`, and `tools/list` over HTTP returned the search tool with the description `Search the Northwind Cabinets employee handbook and HR policies.` and `readOnlyHint: true`.

## Check it works

Ask the client the quick start's two questions as the agent. The prompt used here asked Claude Code to use the `search_knowledge` tool, answer in two sentences and cite the path and heading the tool returned, and to say so if the tool returned nothing that answered. Both transports gave the same tool results and the same kind of answer.

**The cited answer.** For "How long do I have to submit an expense claim?", the tool returned:

```
2 results (157 ms, historical=False):

--- [0.0246] open/handbook.md § Employee handbook > Expense claims
lifecycle: active | trust: unverified | authorship: unknown | stale: no | concept: -
Submit an expense claim within thirty days of the trip. Claims older than
thirty days need a director's written approval before finance will process
them.
```

and the assistant answered: "You have 30 days from the trip to submit an expense claim; claims older than that require a director's written approval before finance will process them." It cited `open/handbook.md`, "Employee handbook > Expense claims", and added on its own that the passage is marked trust unverified and authorship unknown, which is the field the result carries.

**The denied one.** For "What is the compensation review cycle for band four engineers?", the same agent, which holds `engineering`, got two hits from `open/handbook.md` and nothing from `hr/salary-bands.md`; nothing says a document was withheld, because the access gate ran before ranking and the passage was never a candidate. The assistant answered that the tool returned nothing that answers this and that it had no documented answer to give. The same call made directly with `curl` and the token, without any client, returned the same two hits and no salary bands.

**The control.** A second agent registered the same way and granted `hr` instead asked the same question through the same bridge and got:

```
--- [0.0246] hr/salary-bands.md § Salary bands > Band four
lifecycle: active | trust: unverified | authorship: unknown | stale: no | concept: -
Band four engineers sit between the senior and staff levels. Compensation
review happens in the first quarter.
```

So the denial above is the gate, not an empty index. Both agents were registered as hosted-model agents; the folder rules here do not deny the `hosted-model agents` group, so nothing was held back on that account. Deny it on a folder, and the same agents lose that folder while a local-model agent keeps it.
