# What PremAgentic is

What PremAgentic does, the claim it makes about the network and how that claim is tested, what setup needs from the internet once, where the retrieval core came from, and what the product does not do.

On-premises, for agents: the name says what it is. Governed knowledge
retrieval that runs entirely inside a customer's own network. Point it at a
folder of Markdown, text, PDF or Word files (the last two through first-party
extensions an administrator allows), write down who may read which folders,
and its people and agents get cited answers out of them, with those rules
enforced before anything is retrieved.

```
the customer's folders of documents -> connector -> chunk -> embed (locally)
-> plain PostgreSQL -> access, lifecycle, trust and freshness gates
-> lexical search in PostgreSQL + exact vector search in process, fused
-> cited answers
-> a golden set that proves it works on THIS corpus
```

At runtime, with the default local embedding provider, PremAgentic makes no
outbound call, needs no third-party API key, and sends no document anywhere on
its own. An assistant that runs on a hosted model does receive the passages
each search returns: PremAgentic asks where each agent's model runs, writes the
answer on every audit row, and can keep a folder from every agent registered as
using one (see [Connect an assistant](connect-an-assistant.md)). Connections
can still come from the runtime's TLS stack: with an HTTPS certificate issued
by a certificate authority, it may fetch the certificate's revocation status
(OCSP) and any missing intermediate certificates from the addresses the
certificate names, and may do so more than once. The self-signed certificate
setup makes by default names no such address.

That claim is tested, not assumed. `scripts/clean-install/run.sh --offline`
installs the Linux release archive and runs the whole quick start (setup, ingest, a signed-in search
over HTTPS, an agent's call over MCP) in a container whose only network
interface is loopback. It first shows that the same checks do reach out from
a container that has a network, then traces every connection an ingest and
the API open: each one goes to the database. The embedding runtime is one
dependency that can report on its own: some builds of ONNX Runtime upload
telemetry from a long-running process, and PremAgentic switches that off
itself (`ORT_DISABLE_TELEMETRY=1`, unless you have set the variable) before
the runtime loads.

Setup is different, and it is stated here so the claim above is not read as more
than it is. Installing downloads the embedding model from Hugging Face, the
PostgreSQL container image and the .NET packages, once. A site with no internet
access has to bring those in first. The optional `openai` embedding provider sends
passage text to a third party and is off unless selected.

PremAgentic is built and maintained by Agave Information Solutions. The
retrieval core was developed and measured on a single-user knowledge base of
about 1,300 Markdown documents before the authorization model and the
connector abstraction were added for multi-user organizations.

## What it does not do

- **It does not sign people in through a company directory yet.** PremAgentic
  keeps its own accounts, signs people in with a password over HTTPS, and
  checks agent tokens; a customer whose proxy already signs people in can pass
  a PremAgentic sign-in name in a trusted header. OIDC and Windows integrated
  sign-in are not built.
- **It does not write to the source.** Ingest is one directional. The
  customer's systems stay authoritative and this store is rebuildable from
  them at any time.
- **It does not generate prose.** It returns passages and citations. What
  reads them is the caller's business.
