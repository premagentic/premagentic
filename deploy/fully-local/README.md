# The fully local stack

PremAgentic, a model server and a chat client, all on the office's own
hardware, with nothing leaving the network: the documents stay where they are,
the embeddings are made on the server, and the model that writes the answers
runs on a machine the office owns.

```
chat client (MCP) --search_knowledge--> PremAgentic API --> PostgreSQL
      |
      +--chat completions, with the cited passages--> local model server
```

PremAgentic decides what the model is allowed to know. It generates no prose;
the model does, from the passages PremAgentic returns to the client, which
are only the ones that client's person may read.

## The layout

| Machine or service | What runs there |
|---|---|
| The PremAgentic server | `prem setup` with its PostgreSQL, the API on HTTPS (8443 by default), the local embedding model. See [Installing](../../docs/installing.md). |
| The model server | Any server that offers an OpenAI-style chat completions endpoint on the office network, running a model the office has chosen and downloaded, for example llama.cpp's `llama-server`, whose Linux build needs the OpenMP runtime (`apt install libgomp1`). None is bundled with PremAgentic. |
| Each person's computer | An MCP-capable chat client configured with PremAgentic as an MCP server and the model server as its model. |

## Registering the chat client

Each person's client acts as that person and nothing more, and its model runs
inside the network, so its agent is registered with model location `local`.
From the server, as an administrator:

```
prem agents add alice-chat --owner alice --mode acts-for-user --model local
prem tokens issue alice-chat --days 90
```

The token is printed once. A person can also connect their own assistant from
the portal's connect page, where the same rules apply.

## The connect snippet

The MCP server entry most clients take, with the token in place of `<token>`:

```json
{
  "mcpServers": {
    "premagentic": {
      "url": "https://premagentic.example.internal:8443/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

The client's model setting points at the model server's address, for example
`http://models.example.internal:11434/v1`.

## The proof

`scripts/clean-install/run.sh --offline` runs this loop where there is no
network at all: PremAgentic, the database, a chat client
(`loop-client.py`) and a stand-in for the model server
(`stub-model-server.py`) share one network namespace whose only interface is
loopback. The stand-in is not a model. It answers the chat completions shape by
naming the passages it was handed, so the proof can show the passages reached
it. Every connect() of the API, the client and the stand-in is traced: the API
connects only to the database, the client only to the API and the model
server, and the model server to nothing. A real model server and client are the
office's to install.

The same proof can run the loop a second time with a real model server. Set
`PREM_LLAMA_SERVER` to a llama.cpp release tarball for Linux and
`PREM_LLAMA_MODEL` to a GGUF model file, both fetched beforehand, since nothing
inside the namespace can fetch. They are mounted read-only; llama.cpp's
`llama-server` is started on loopback beside the stand-in, the client asks it
with `loop-client.py --any-answer` (a real model's answer need not name a
passage, so the check is that the request carried the passages and the answer
is not empty), and `llama-server` is traced like the stand-in and may connect
to nothing. The run prints the SHA-256 of both files and the start of the
answer. The stand-in's run is still the default, and still runs first.
