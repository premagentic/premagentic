# mail-reminders: a sample extension that sends reminders by mail

A reminder sink. Each time `prem reminders run` delivers a run, the built-in
sink keeps it in the database for the portal first, and then this sends each
owner their summary by mail: one message per owner, and the documents nobody
owns to the administrators' address. `prem reminders run --plan` delivers to
no sink, so it sends nothing.

PremAgentic calls out to nothing on its own. This extension does, which is why
it is an extension: it runs only once an administrator installs it and allows
it with `prem extensions allow`, and the health page names it while it is
loaded.

## Settings

Copy `mail.example.json` to `mail.json` in the extension's folder, beside
`extension.json`, and fill it in:

| Key | What it is |
|---|---|
| `host`, `port` | The SMTP server (port defaults to 587). |
| `tls` | Encrypt the connection; defaults to true. |
| `from` | The sender address. |
| `credentialsFile` | Optional. A file of two lines, the user name and the password, readable only by the account the service runs as. |
| `userDomain` | Optional. An owner `user:name` with no address of their own is sent to `name@userDomain`. |
| `addresses` | An owner principal to an address. `administrators` is required. |

A password never goes in `mail.json`: a `password` key there refuses the
extension at startup. An owner with no address is reported as the run's
failure after every other owner's message is sent, and `prem reminders run`
exits 1.
