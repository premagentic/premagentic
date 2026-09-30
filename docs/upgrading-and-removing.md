# Upgrading and removing

How to upgrade a deployment and roll it back, and how `prem remove` takes away what setup registered, with or without the data.

## Upgrading

Back up the configuration (`pg_dump --schema=prem_config -Fc`), stop the API,
replace the programs, run `prem migrate` with `owner.credentials` and then
`prem setup` with the same options, and start the API. To roll back, put
the old programs back, restore the configuration backup into a database
whose two PremAgentic schemas you have dropped, run the old `prem setup`,
and ingest again: the index is rebuilt from your documents. `prem setup
--plan` lists what an upgrade would apply, including a generated text match
function whose body is not this release's, which `prem migrate` reinstalls.

### An install made before setup checked its folder, on Windows

Setup and the API now refuse a credentials folder that other accounts may
change (see [Installing](installing.md)). A folder an earlier setup made in
`C:\ProgramData` took that folder's rules, which let every user add files, so
both refuse it after the upgrade, naming this page. From an elevated prompt,
take that access away:

```
icacls "C:\ProgramData\Premagentic" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"
```

If the refusal is about the folder's owner, check what is in the folder
first, then make Administrators its owner:

```
takeown /F "C:\ProgramData\Premagentic" /A
```

Then run `prem setup --windows-service` again from the same prompt. It hands
the folder and the files the service reads to administrators, and the service
can then start. Use your own path if you gave `--credentials-dir`. The folder may hold
the bundled server's data folder, so do not move it aside instead.

## Removing PremAgentic

`prem remove` takes away what `prem setup` registered and keeps your data.
On Windows, from an elevated prompt, it stops and removes the `Premagentic`
and `PremagenticDb` services. On Linux it prints the systemd steps for root
to run and runs nothing as root itself. The database, its three roles, the
credentials files and a bundled server's data folder all stay, and the output
names each with its path, so running `prem setup` again picks them up.

    prem remove --plan     # every step, nothing changed
    prem remove            # the services go, the data stays

To delete the data as well:

    prem remove --purge          # lists what would be deleted, changes nothing
    prem remove --purge --yes    # deletes it

A purge drops the database as its owner, drops the three roles over setup's
administrator connection (`--admin-connection-file` or
`PREM_SETUP_ADMIN_CONNECTION`; a bundled server uses its own superuser),
deletes a bundled server's data folder, and deletes the credentials files
last. A profile's golden set copies, in
`C:\ProgramData\Premagentic-golden-sets` by default, are left where they are.
Every document, user, setting and the audit trail goes, and it cannot
be undone. Anything that would stop the purge is found before anything is
changed. A purge that stops part way can be run again.

Each line of output starts with a label a program can match: `[removed]`,
`[plan]`, `[kept]`, `[manual]`, `[none]` or `[FAILED]`. Exit codes: 0 done or
a plan, 1 a purge not confirmed or a step that failed, 2 refused before
anything changed.
