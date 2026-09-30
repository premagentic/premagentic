# Profiles

A folder of plain files carrying a whole configuration: what it holds, what it may set, how it is validated and applied as a whole, and how a deployment is compared with it.

A profile is a folder of plain files holding a whole configuration: the
settings, the sources, and the rules that decide who may read them. It is how
a second office is set up like the first, and how a rebuilt server comes back
the way it was, without anyone remembering which settings were changed.

```
prem profile validate <folder>    check it against this deployment, change nothing
prem profile apply <folder>       apply it, or refuse the whole profile
prem profile show [<folder>]      what is applied, and how this deployment differs
```

The folder holds `profile.json` (a name, a version, and the seam versions it
expects; the name follows the rule a source's name does, up to 64 letters,
digits, dots, hyphens and underscores, starting with a letter or digit) and, as it
needs them, `settings.json`, `groups.json`,
`sources.json` and `golden-set.json`. A file a profile does not know is refused rather than
ignored: a profile that quietly skips a file is a profile that quietly does
not configure something.

A profile can set what an administrator can set, and nothing further. There is
no file in it that creates a user, grants a role, puts somebody in a group, or
writes a rule naming somebody who does not exist.

In `settings.json`, `null` unsets a setting, as `prem settings unset` does,
so its default applies; a trust setting cannot be unset. The MCP
authorization flow's settings are checked as the whole profile would leave
them, so a profile that turns the flow on and sets its address is valid
whatever the order of its lines (see [The authorization flow](authorization-flow.md)).

A profile can create the groups it names. `groups.json` lists their names:

```
[ "hr", "engineering" ]
```

The groups are applied before the sources and the rules, so a rule may name a
group the same profile creates, and a fresh server takes the starter profile
in one step:

```
prem profile apply ./samples/profiles/starter
```

A group that exists already is left as it is. A group the profile creates is
recorded in the change record like one made on the groups page. Who is in a
group is not in a profile: an administrator adds the members. The starter
profile names `hr` because its rule does, and `engineering` because its golden
set asks a question as somebody in it to prove that person is told nothing.

`prem profile validate` names every group, user or agent a rule names that
neither exists nor is in `groups.json`, so the list of what to create is the
output of a validate run rather than something to work out by reading the files.

`validate` reports every problem it finds rather than the first, and changes
nothing. `apply` validates the whole profile before it writes anything, so a
profile with one bad item changes nothing at all. The apply itself is not one
transaction, because the settings, sources and rules each carry their own;
what holds is that nothing is applied unless everything validated, and a
failure part way through names exactly what was applied and what was not.

A source's folder may be written relative to the profile, so a profile can
ship the documents it configures. A source's folder and prefix cannot be
changed by applying a profile, since that would mean removing the source and
indexing a whole corpus again.

`golden-set.json` is copied to a folder the server can read
(`--golden-set-dir`, by default `Premagentic-golden-sets` in the shared
application data folder, `C:\ProgramData\Premagentic-golden-sets` on Windows)
and the golden set path is set to the copy. The default is shared rather than
per-account on purpose: the file is read by the account the service runs as,
which is usually not the account applying the profile. Every account on the
machine may read what is copied there, the Windows service's account
included, by the rules `C:\ProgramData` gives the folders made in it. The
folder is beside setup's credentials folder (`C:\ProgramData\Premagentic` for
the Windows service) and never in it, so a profile applied before setup does
not make that folder with those rules, which setup would refuse.
