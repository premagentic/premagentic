# Configuration

The environment variables every host reads, their defaults, the two that are retired, and what switching the embedding provider means for the index.

| Variable | Default | Notes |
|---|---|---|
| `PREM_CONNECTION_STRING` | unset | A PostgreSQL connection string. A deployment uses `PREM_CREDENTIALS_FILE` instead. |
| `PREM_DEV_DATABASE` | unset | `1` uses the local development database from docker-compose.yml when nothing else is configured. With nothing configured and no switch, every host refuses to start. |
| `PREM_CREDENTIALS_FILE` | unset | A credentials file written by `prem setup`. Refused together with `PREM_CONNECTION_STRING`. |
| `PREM_SETUP_ADMIN_CONNECTION` | unset | `prem setup` only: a role that can create roles and databases. |
| `PREM_SEARCH_CONNECTION_STRING` | unset | The search role's connection, for a deployment configured by connection string. An installed deployment reads `search.credentials` beside `app.credentials` instead, and refuses to start without it; setting both is refused. A deployment by connection string with neither refuses to start unless `PREM_ALLOW_NO_SEARCH_ROLE` says so. `/health` reports `rowLevelSecurity: true` when the search role is in use. |
| `PREM_ALLOW_NO_SEARCH_ROLE` | unset | `1` lets a deployment configured by `PREM_CONNECTION_STRING` run with no search role: search reads then run as the application role and only the SQL gate filters them, and the API logs a warning. It does not apply to an installed deployment. The development database runs without a search role and says so. |
| `PREM_TRUSTED_PROXIES` | unset | Reverse proxies, by address or range, whose forwarding headers the API believes. |
| `PREM_EMBEDDING_PROVIDER` | `local` | `local` (ONNX MiniLM, offline), `openai` (needs a key and breaks the no-outbound-call promise), `hash` (tests). A provider that cannot be made or reached stops the command with one line and exit code 2 |
| `ORT_DISABLE_TELEMETRY` | set to `1` by PremAgentic | The ONNX Runtime's own telemetry switch. PremAgentic sets it before the runtime loads unless you have set it |
| `PREM_ONNX_MODEL_DIR` | unset | The folder holding the local model, populated by `scripts/download-model.ps1` or `.sh`. Unset, the CLI, the API and setup look for `models/minilm` under the current folder, then beside the program and in each folder above it, so `dotnet run --project src/Premagentic.Api` finds the model the quick start downloaded at the repository root; a refusal names every folder tried. |
| `PREM_EXTENSIONS_DIR` | unset | The folder whose subfolders hold extensions, when the setting `extensions.folder` does not say. Neither one means no extensions |
| `PREM_TENANT_KEY` | `default` | One tenant per deployment |
| `PREM_TENANT_NAME` | `Premagentic deployment` | The tenant's name. Every host writes it to the tenant row when it starts, so the last host to start decides it. |
| `PREM_HEADING_PREFIX` | on | Embeds passages with a "title > heading" context prefix, in its own vector space |
| `PREM_SIGN_IN_HEADER` | unset | HTTP only. The header an authenticating proxy writes a PremAgentic sign-in name into. Unset means the header mode is off. |
| `PREM_ALLOW_HTTP_SIGN_IN` | unset | `1` allows password sign-in over plain HTTP, for development on one machine only. Passwords then cross the network unencrypted. |
| `PREM_API_URL` | unset | Stdio bridge only. The API's address, `https://`; plain `http://` only for localhost. |
| `PREM_AGENT_TOKEN_FILE` | unset | Stdio bridge only. A file holding the agent's token. `PREM_AGENT_TOKEN` holds it directly instead; setting both is refused. |

`PREM_PRINCIPAL_HEADER` and `PREM_PRINCIPALS` are retired: the API refuses
to start while the first is set.

The settings an administrator changes with `prem settings` are kept in the
database, not the environment; `prem settings list` shows each with its
default. One bounds the MCP endpoint: `mcp.max_request_bytes`, the largest
request body `/mcp` takes, 262144 bytes (256 KB) by default, from 16384 to
30000000, read when the server starts.

Switching embedding providers changes the vector space. Re-run ingest, which
re-embeds, because the provider name and dimensions are recorded per chunk.
