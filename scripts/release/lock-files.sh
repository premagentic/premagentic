#!/usr/bin/env bash
# The NuGet lock files, made or checked in one place.
#
#   scripts/release/lock-files.sh            write every lock file again from the
#                                             project files, after a package change
#   scripts/release/lock-files.sh --check    restore in locked mode against every lock
#                                             file; a package id or version that moved
#                                             without its lock file fails here
#
# Every project restores from its own packages.lock.json (Directory.Build.props turns
# lock files on). The three programs are also published for a runtime, and the
# command line, the API and the bridge add the ICU package only then, so a publish
# has a different graph than a plain restore: the projects a publish builds each
# carry one lock file per runtime too, packages.<rid>.lock.json, which build.sh
# publishes against in locked mode (-p:NuGetLockFilePath=packages.<rid>.lock.json).
#
# Run it from anywhere; it restores the tree it sits in, with the SDK global.json pins.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
host_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
cd "$repo"

mode=write
case ${1:-} in
  "") ;;
  --check) mode=check ;;
  *) printf 'usage: lock-files.sh [--check]\n' >&2; exit 2 ;;
esac
locked=()
[ "$mode" = check ] && locked=(-p:RestoreLockedMode=true)

programs=(src/Premagentic.Cli/Premagentic.Cli.csproj src/Premagentic.Api/Premagentic.Api.csproj src/Premagentic.McpServer/Premagentic.McpServer.csproj)
rids=(linux-x64 win-x64)

run() { # <what> <dotnet restore arguments>...
  local what=$1; shift
  local log
  log=$(mktemp)
  if dotnet restore "$@" --nologo -v q > "$log" 2>&1; then
    printf 'ok      %s\n' "$what"
  else
    cat "$log" >&2; rm -f "$log"
    printf 'refused %s\n' "$what" >&2
    exit 1
  fi
  rm -f "$log"
}

run "the solution, packages.lock.json" "$(host_path "$repo/Premagentic.slnx")" "${locked[@]}"
for rid in "${rids[@]}"; do
  for program in "${programs[@]}"; do
    # RuntimeIdentifier, not -r: the ICU reference is conditioned on the property a
    # publish for a runtime sets, and restore -r does not set it.
    run "$(basename "$program" .csproj) for $rid, packages.$rid.lock.json" "$(host_path "$repo/$program")" \
      -p:RuntimeIdentifier="$rid" -p:NuGetLockFilePath="packages.$rid.lock.json" "${locked[@]}"
  done
done
[ "$mode" = check ] && printf 'every lock file holds\n' || printf 'lock files written; commit them with the package change\n'
