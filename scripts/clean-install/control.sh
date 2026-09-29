#!/usr/bin/env bash
# The offline proof's control: run.sh --offline with PREM_LOOP_CONTROL_REACH_OUT=1,
# which makes the stand-in model server try one connection out at its start. The
# run must then fail, and fail at the check that exists to catch it: the stand-in's
# connect to 1.1.1.1:443, in the strace check. A run that fails anywhere else (a
# missing model, a slow start, a broken step) proves nothing about that check, and
# this says so, naming the check it did fail at.
#
#   scripts/clean-install/control.sh [--log <file>]
#   scripts/clean-install/control.sh --verdict <log> <exit status>
#
# The first form runs the control and writes its whole output to the log (by
# default a new file in the temp folder, whose path is printed). The second reads
# a log and an exit status already in hand and gives the same verdict; the tests
# of this script use it.
#
# run.sh proves a release archive: with PREM_ARCHIVE set, which this passes on,
# the one given; otherwise one it builds from HEAD for this run.
#
# Prints CONTROL PASSED and exits 0 only for the failure the control exists for;
# otherwise prints "the control proved nothing" with the reason and exits 1.
set -uo pipefail

here=$(cd "$(dirname "$0")" && pwd)
expected="the stand-in's connect to 1.1.1.1:443"

verdict() { # <log> <exit status>
  local log=$1 status=$2 line
  line=$(grep '^CONTROL FAILED AT: ' "$log" | tail -n 1 || true)
  line=${line#CONTROL FAILED AT: }
  line=${line%$'\r'}
  if [ "$status" != 0 ] && [ "$line" = "$expected" ]; then
    printf 'CONTROL PASSED: the run failed at %s, the check the control exists for\n' "$expected"
    return 0
  fi
  if [ "$status" = 0 ]; then printf 'the control proved nothing: the run passed, so the check did not catch the connection out\n'
  elif [ -z "$line" ]; then printf 'the control proved nothing: the run failed (exit %s) without saying where\n' "$status"
  else printf 'the control proved nothing: it failed at %s, not at %s\n' "$line" "$expected"
  fi
  return 1
}

case ${1:-} in
  --verdict)
    [ $# = 3 ] || { printf 'usage: control.sh --verdict <log> <exit status>\n' >&2; exit 2; }
    verdict "$2" "$3"; exit $? ;;
  --log)
    [ $# = 2 ] || { printf 'usage: control.sh [--log <file>]\n' >&2; exit 2; }
    log=$2 ;;
  "") log=$(mktemp "${TMPDIR:-/tmp}/prem-control-XXXXXX.log") ;;
  *) printf 'usage: control.sh [--log <file>] | --verdict <log> <exit status>\n' >&2; exit 2 ;;
esac

PREM_LOOP_CONTROL_REACH_OUT=1 "$here/run.sh" --offline > "$log" 2>&1
status=$?
printf 'the control run exited %s; its output is in %s\n' "$status" "$log"
verdict "$log" "$status"
