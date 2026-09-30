# Running it

Running the API as a Windows service or a systemd unit, and what to set when it sits behind a reverse proxy.

## Running as a service

**Windows.** From an elevated prompt, `prem setup --windows-service` registers
the API as the service `Premagentic` under its own virtual account,
`NT SERVICE\Premagentic`. Setup gives that account read access to the
application's and the search role's credentials files, the HTTPS settings and
the certificate; the owner's credentials stay with administrators. The account
is not confined to those files: like any service account it can read what the
machine's Users group can read, and on a domain it reaches the network as the
computer's own account. Its files default to `C:\ProgramData\Premagentic`.
Start it with `sc.exe start PremAgentic`.

Setup makes that folder the administrators' own: owned by Administrators,
changed by SYSTEM and administrators only, and read by the service. The files
the service reads are owned by Administrators, and read by the service and by
the account that ran setup, so `prem` can still be pointed at the
application's credentials file from that account. The API checks this before
it reads anything: when the folder of its `PREM_CREDENTIALS_FILE`, that file,
or a file beside it that it reads is owned by anyone but administrators,
SYSTEM or the account the API runs as, or anyone else may change it, it
refuses to start with exit code 2 and a sentence naming the path. Running
setup again from an elevated prompt puts it right, or says what stands in the
way. Change anything in that folder from an elevated prompt. The folder's
rules then reach `https.crt` too, the certificate clients are given: it is
readable by administrators and the service only, so copy it out from an
elevated prompt. A profile's golden set is never copied there:
`prem profile apply` copies it to `C:\ProgramData\Premagentic-golden-sets`,
beside it, which the Users group, and so the service, may read.

**Linux.** `deploy/systemd/premagentic-api.service` is a unit template: a system
account, `Type=notify`, and the application's four files handed to the
service by systemd, so they stay readable by root only. Run setup as root with
`--credentials-dir /etc/premagentic`, then install and enable the unit.

Either way, the API refuses to start, with exit code 2 and a sentence saying
what to set, when it has no database configured. The systemd unit has been run on a fresh Ubuntu 24.04
with systemd, and the Windows service on a clean Windows 11 in Windows
Sandbox; the bundled database as a service has not yet been run on a clean
machine with the rights it needs; see [Known state](status.md).

## Behind a reverse proxy

Set `PREM_TRUSTED_PROXIES` to your proxies' addresses or ranges
(`10.0.0.5, 10.1.0.0/16`). The API then believes their `X-Forwarded-For` and
`X-Forwarded-Proto`, so sign-in over HTTPS that ends at the proxy works and
the sign-in throttle counts each client. Nothing else is believed. Do not set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED`; the API refuses to start with it,
because it believes anyone. The portal's origin check compares with the host
the server sees, so a proxy that rewrites the host needs this setting too.

## Fully local

A deployment that must keep everything on its own hardware, the model
included, runs PremAgentic beside a local model server and an MCP chat
client. The layout, the chat client's agent registration, the connect
snippet and the offline proof of the whole loop are in the repository under
`deploy/fully-local/` (its README). No model server or chat client is bundled. The offline proof
can run the loop a second time with a real local model server (llama.cpp's
`llama-server` with a 0.5B model), which answered from the passages,
offline, with nothing connecting out; give it the release tarball and a GGUF
model file as `PREM_LLAMA_SERVER` and `PREM_LLAMA_MODEL`, and the stand-in's
run stays the default. No chat client has been tried against it yet.
