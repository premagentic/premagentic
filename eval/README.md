# eval

Reports from `prem eval` go here. They are git-ignored.

The sample golden set is the starter profile's own file,
`samples/profiles/starter/golden-set.json`: one copy, which the quick start
runs and the starter profile applies. Pass a report path under this folder
when you run it, so the report does not land inside the profile folder,
where `prem profile apply` would refuse it as a file a profile does not know:

```
prem eval samples/profiles/starter/golden-set.json eval/report.md
```
