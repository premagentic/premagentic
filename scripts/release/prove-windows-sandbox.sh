#!/usr/bin/env bash
# The clean-machine proof of a Windows release archive, in Windows Sandbox: a
# Windows that has never had .NET, a build, a network or a Visual C++ runtime,
# where the archive is installed by its INSTALL.txt from a prompt and then as the
# Windows service, with the four runtime files it ships in bin\ as the only ones
# its programs can find. This is the release checklist's Windows line (README.md);
# a hosted runner has no Windows Sandbox, so a person runs it, from Git Bash on a
# Windows 10 or 11 Pro or Enterprise with the feature on (once, elevated, then a
# restart: Enable-WindowsOptionalFeature -FeatureName Containers-DisposableClientVM -All -Online).
# prove-windows-sandbox.ps1 is what runs inside; this script stages it and watches it.
#
#   prove-windows-sandbox.sh check
#       read-only: WindowsSandbox.exe and wsb.exe, a sandbox already running, the
#       work folder, free disk
#   prove-windows-sandbox.sh stage --archive <premagentic-*-win-x64.zip> --sums <SHA256SUMS>
#           --postgresql-zip <zip> --vc-runtime <VC_redist.x64.exe> [--remove]
#       refuses, in this order: an archive that does not match its line in SHA256SUMS
#       (take both from the release run: gh run download <id> --name release-archives);
#       a PostgreSQL zip that is not the one installer/windows/fetch-postgresql.ps1
#       pins; a redistributable that is not there (ReleaseTool.cs then refuses any
#       but the pinned one). Then fills the work folder's input: the archive, its
#       SHA256SUMS, the PostgreSQL zip and its pin, the four runtime files for the
#       proof's own PostgreSQL (never on a PATH), prove-windows-sandbox.ps1, and
#       with --remove the manual's prem remove --purge after the service half; writes
#       premagentic.wsb; empties the results and the waiter's folders
#   prove-windows-sandbox.sh start [minutes]
#       opens the sandbox (the proof runs at logon by itself) and starts the waiter
#       detached, so a session that ends cannot stop it; returns at once. Refuses
#       while a sandbox or a sandbox client window is left from an earlier start
#   prove-windows-sandbox.sh status
#       the waiter's output and exit code, and the proof's step headings, PASS and
#       FAIL lines so far
#   prove-windows-sandbox.sh stop
#       closes the one running sandbox with wsb stop (no dialog), waits until it is
#       gone, then lets the machine settle; refuses unless exactly one is running
#   prove-windows-sandbox.sh clean
#       removes the staged input (the results stay)
#   prove-windows-sandbox.sh wait [minutes]
#       the waiter (what start runs detached): waits for done.txt, 45 minutes by
#       default, counting only the sandbox's VM as running; exit 0 on PASS
#   prove-windows-sandbox.sh pin
#       prints the PostgreSQL pin read from installer/windows/fetch-postgresql.ps1
#   prove-windows-sandbox.sh self-test
#       shows each refusal above that needs no sandbox fire, beside a control that
#       passes, with stand-ins for the sandbox's programs; runs on Linux too
#
# The work folder is PREM_SANDBOX_WORK, by default premagentic-sandbox in your home
# folder: never the temp folder, whose cleaner goes by file dates, and never the
# repository. The proof's record is results/: done.txt (written last, PASS or FAIL
# with the step and the exit code of the program it ran), the transcript, and the
# output of setup, the API and PostgreSQL. Record the archive's SHA256SUMS line
# beside done.txt, so the proven bytes are the released bytes.
#
# Two departures of the proof from a person's install, both stated in
# prove-windows-sandbox.ps1: every /health call pins the sandbox's own name to
# 127.0.0.1 (--resolve), because a sandbox with networking disabled has no adapter
# and cannot resolve its own name (the certificate is still verified against that
# name); and the first administrator's password is a generated file, not typed.
# Nothing here elevates; a closed sandbox is thrown away. Never export
# MSYS_NO_PATHCONV in the shell that runs this.
set -uo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
work=${PREM_SANDBOX_WORK:-$HOME/premagentic-sandbox}
input=$work/input
results=$work/results
waiter=$work/waiter
wsbfile=$work/premagentic.wsb
# PROVE_SANDBOX_FAKE is for self-test only: a folder of stand-ins for the sandbox's
# programs (tasklist, wsb, the launch), with no waiting.
fake=${PROVE_SANDBOX_FAKE:-}
sandbox_exe=${fake:+$fake/WindowsSandbox.exe}
sandbox_exe=${sandbox_exe:-/c/Windows/System32/WindowsSandbox.exe}
wsb_exe=${LOCALAPPDATA:-/c/Users/${USERNAME:-${USER:-}}/AppData/Local}/Microsoft/WindowsApps/wsb.exe
postgresql_pins=${fake:+$fake/fetch-postgresql.ps1}
postgresql_pins=${postgresql_pins:-$root/installer/windows/fetch-postgresql.ps1}
if [ -n "$fake" ]; then poll=0 vm_grace=0 settle=0; else poll=5 vm_grace=180 settle=30; fi

fail() { printf 'refused: %s\n' "$*" >&2; exit 1; }
now() { date -u '+%Y-%m-%d %H:%M:%S UTC'; }
free_mb() { df -Pk "$1" | awk 'NR == 2 { printf "%d", $4 / 1024 }'; }
winpath() { if command -v cygpath > /dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
hash_of() { sha256sum "$1" | cut -d' ' -f1; }
tasks() { if [ -n "$fake" ]; then sh "$fake/tasklist"; else tasklist 2> /dev/null; fi; }
# WindowsSandbox.exe is only the launcher on Windows 11; the VM runs as
# vmmemWindowsSandbox and the client as WindowsSandboxRemoteSession. The whole list
# is read, never cut by head.
sandbox_running() { tasks | grep -qiE '^(vmmemWindowsSandbox|WindowsSandboxRemoteSess)'; }
# Only the VM counts as a running proof: a client window can stay open over a
# sandbox that failed to initialize (a start 9 s after a stop did once).
vm_running() { tasks | grep -qiE '^vmmemWindowsSandbox'; }
wsb_run() {
  if [ -n "$fake" ]; then sh "$fake/wsb" "$@"
  else MSYS_NO_PATHCONV=1 powershell.exe -NoProfile -Command "& '$(winpath "$wsb_exe")' $*" | tr -d '\r'; fi
}
launch() {
  if [ -n "$fake" ]; then printf 'sandbox opened with %s\n' "$wsbfile" >> "$fake/launched"
  else MSYS_NO_PATHCONV=1 cmd.exe /c start "" "$(winpath "$sandbox_exe")" "$(winpath "$wsbfile")"; fi
}
start_waiter() { # <minutes>
  if [ -n "$fake" ]; then printf 'waiter started for %s minutes\n' "$1" >> "$fake/launched"; return; fi
  local bash_exe
  bash_exe=$(winpath /)'bin\bash.exe'
  [ -f "$(cygpath -u "$bash_exe")" ] || bash_exe=$(winpath "$(command -v bash)")
  MSYS_NO_PATHCONV=1 powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(winpath "$here/start-detached.ps1")" \
    -Name wait -Folder "$(winpath "$waiter")" -Command "\"$bash_exe\" \"$(winpath "$here/prove-windows-sandbox.sh")\" wait $1" | tr -d '\r'
}
# The zip's name and SHA-256 as fetch-postgresql.ps1 pins them: the Name line that
# names the binaries zip and the Sha256 line after it.
pin() {
  [ -f "$postgresql_pins" ] || fail "no $postgresql_pins"
  tr -d '\r' < "$postgresql_pins" | awk '
    /Name *= *"postgresql-[^"]*\.zip"/ { match($0, /"[^"]*"/); name = substr($0, RSTART + 1, RLENGTH - 2); next }
    name != "" && /Sha256 *= *"[0-9a-f]+"/ { match($0, /"[0-9a-f]+"/); print substr($0, RSTART + 1, RLENGTH - 2) "  " name; exit }'
}

stage() {
  local archive= sums= postgresql= vc= remove=
  while [ $# -gt 0 ]; do
    case $1 in
      --archive) archive=${2:?}; shift 2 ;;
      --sums) sums=${2:?}; shift 2 ;;
      --postgresql-zip) postgresql=${2:?}; shift 2 ;;
      --vc-runtime) vc=${2:?}; shift 2 ;;
      --remove) remove=1; shift ;;
      *) fail "stage takes --archive, --sums, --postgresql-zip, --vc-runtime and --remove, not $1" ;;
    esac
  done
  [ -n "$archive" ] && [ -n "$sums" ] && [ -n "$postgresql" ] && [ -n "$vc" ] || fail "stage needs --archive, --sums, --postgresql-zip and --vc-runtime"
  [ -z "${MSYS_NO_PATHCONV:-}" ] || fail "MSYS_NO_PATHCONV is set in this shell"
  local name line pinned pinned_name
  # 1. The archive, against its line in SHA256SUMS.
  [ -f "$archive" ] || fail "no archive at $archive"
  name=$(basename "$archive")
  [[ $name == premagentic-*-win-x64.zip ]] || fail "$name is not a Windows release archive (premagentic-<version>-win-x64.zip)"
  [ -f "$sums" ] || fail "no SHA256SUMS at $sums"
  line=$(tr -d '\r' < "$sums" | grep -E "^[0-9a-f]{64}  \*?$name\$" || true)
  [ -n "$line" ] || fail "$sums has no line for $name"
  [ "${line%% *}" = "$(hash_of "$archive")" ] || fail "$name does not match its line in $sums: it hashes to $(hash_of "$archive")"
  # 2. The PostgreSQL zip, against the repository's pin.
  read -r pinned pinned_name <<< "$(pin)"
  [[ $pinned =~ ^[0-9a-f]{64}$ ]] || fail "no PostgreSQL pin could be read from $postgresql_pins"
  [ -f "$postgresql" ] || fail "no PostgreSQL zip at $postgresql"
  [ "$(basename "$postgresql")" = "$pinned_name" ] && [ "$(hash_of "$postgresql")" = "$pinned" ] \
    || fail "$(basename "$postgresql") is not the pinned PostgreSQL zip ($pinned_name, $pinned)"
  # 3. The redistributable the proof's PostgreSQL takes its runtime files from.
  [ -f "$vc" ] || fail "no redistributable at $vc; take the pinned one (scripts/download-vc-runtime.sh)"

  rm -rf "$input" "$results" "$waiter"
  mkdir -p "$input" "$results"
  # Hard links where the file system allows, so a 380 MB zip is not copied.
  ln "$archive" "$input/$name" 2> /dev/null || cp "$archive" "$input/$name"
  ln "$postgresql" "$input/$pinned_name" 2> /dev/null || cp "$postgresql" "$input/$pinned_name"
  printf '%s\n' "$line" > "$input/SHA256SUMS"
  printf '%s  %s\n' "$pinned" "$pinned_name" > "$input/postgresql.pin"
  dotnet run "$(winpath "$here/ReleaseTool.cs")" -- vcruntime --package "$(winpath "$vc")" \
    --pins "$(winpath "$root/scripts/download-vc-runtime.sh")" --out "$(winpath "$input/vc-runtime")" \
    || fail "the runtime files could not be taken out of $vc"
  dotnet build-server shutdown > /dev/null 2>&1 || true
  ( cd "$input/vc-runtime" && sha256sum *.dll | sed 's/ \*/  /' > SHA256SUMS )
  cp "$here/prove-windows-sandbox.ps1" "$input/"
  [ -z "$remove" ] || printf 'prem remove --purge --yes after the service half\n' > "$input/mode-remove.txt"
  cat > "$wsbfile" << EOF
<Configuration>
  <!-- Written by prove-windows-sandbox.sh stage. A clean Windows with no network:
       the input is mapped in read-only, and the results folder is the only one the
       sandbox can write back to. -->
  <Networking>Disable</Networking>
  <vGPU>Disable</vGPU>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <ProtectedClient>Enable</ProtectedClient>
  <MemoryInMB>6144</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$(winpath "$input")</HostFolder>
      <SandboxFolder>C:\input</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$(winpath "$results")</HostFolder>
      <SandboxFolder>C:\results</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <!-- The proof runs by itself at logon, as the sandbox's account (an administrator),
       and writes C:\results\done.txt last. -->
  <LogonCommand>
    <Command>powershell.exe -ExecutionPolicy Bypass -File C:\input\prove-windows-sandbox.ps1</Command>
  </LogonCommand>
</Configuration>
EOF
  printf '%s  staged %s (%s)%s; input: %s\n' "$(now)" "$name" "${line%% *}" "${remove:+, with prem remove after the service half}" \
    "$(cd "$input" && find . -type f | sed 's#^\./##' | LC_ALL=C sort | tr '\n' ' ')"
}

start() { # [minutes]
  local minutes=${1:-45}
  [ -e "$sandbox_exe" ] || fail "WindowsSandbox.exe is not on this machine; turn the feature on (this script's header) and restart"
  [ -f "$input/prove-windows-sandbox.ps1" ] && [ -f "$input/vc-runtime/SHA256SUMS" ] && [ -f "$wsbfile" ] \
    && ls "$input"/premagentic-*-win-x64.zip > /dev/null 2>&1 || fail "the work folder is not staged; stage first"
  [ -z "$(ls -A "$results" 2> /dev/null)" ] || fail "the results folder is not empty; stage again to reset it"
  [ ! -e "$waiter" ] || fail "the waiter's folder is there from an earlier start; stage again to reset it"
  ! sandbox_running || fail "a sandbox or a sandbox client window is still there; stop it (or close the client) and let it settle first"
  printf '%s  opening the sandbox; the proof runs at logon\n' "$(now)"
  launch
  start_waiter "$minutes"
  printf 'watch it with: bash %s status\n' "$here/prove-windows-sandbox.sh"
}

wait_for_done() { # [minutes]
  local minutes=${1:-45} started=$SECONDS
  printf '%s  waiting for done.txt, up to %s minutes\n' "$(now)" "$minutes"
  # The VM takes a moment to appear; its absence counts only after a grace.
  until vm_running || [ -f "$results/done.txt" ] || [ $((SECONDS - started)) -ge "$vm_grace" ]; do sleep "$poll"; done
  until [ -f "$results/done.txt" ]; do
    [ $((SECONDS - started)) -lt $((minutes * 60)) ] || { printf '%s  no done.txt after %s minutes\n' "$(now)" "$minutes"; ls -la "$results"; exit 2; }
    if ! vm_running; then
      sleep "$poll"
      [ -f "$results/done.txt" ] && break
      printf '%s  the sandbox is not running and wrote no done.txt\n' "$(now)"; ls -la "$results"; exit 3
    fi
    sleep $((poll * 2))
  done
  printf '%s  done after %s s: %s\n' "$(now)" "$((SECONDS - started))" "$(tr -d '\r' < "$results/done.txt")"
  cat "$results"/transcript-*.txt 2> /dev/null | tr -d '\r' | grep -aE '^(PASS|FAIL)|^== ' || true
  grep -q '^PASS' "$results/done.txt"
}

stop() {
  local ids count i
  ids=$(wsb_run list | tr -d '\r' | grep -E '^[0-9a-fA-F-]{36}$' || true)
  count=$(grep -c . <<< "$ids" || true)
  [ "$count" = 1 ] || fail "wsb list shows $count running sandboxes; this stops exactly one"
  wsb_run stop --id "$ids"
  # Until the VM and the client are gone, then a settle: a start 9 s after a stop
  # failed to initialize once.
  for i in $(seq 1 18); do sandbox_running || break; sleep "$poll"; done
  ! sandbox_running || fail "the sandbox is still there 90 s after wsb stop"
  sleep "$settle"
  printf '%s  stopped sandbox %s and let it settle %s s\n' "$(now)" "$ids" "$settle"
}

self_test() {
  local failures=0 out status
  # Not local: the EXIT trap reads it after this function has returned.
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  ok() { printf 'PASS  %s\n' "$*"; }
  bad() { printf 'FAIL  %s\n' "$*"; failures=$((failures + 1)); }
  # run <expected: refused|ok> <text the output must contain> <what> <subcommand and arguments...>
  run() {
    local expected=$1 text=$2 what=$3; shift 3
    status=0
    out=$(PROVE_SANDBOX_FAKE=$tmp/fake PREM_SANDBOX_WORK=$tmp/work bash "$here/prove-windows-sandbox.sh" "$@" 2>&1) || status=$?
    if [ "$expected" = refused ] && [ "$status" = 0 ]; then bad "$what: it was accepted"; printf '%s\n' "$out"
    elif [ "$expected" = ok ] && [ "$status" != 0 ]; then bad "$what: it failed ($status)"; printf '%s\n' "$out"
    elif ! grep -qF -- "$text" <<< "$out"; then bad "$what: no '$text' in its output"; printf '%s\n' "$out"
    else ok "$what: $(grep -m 1 -F -- "$text" <<< "$out")"
    fi
  }
  mkdir -p "$tmp/fake" "$tmp/in"
  : > "$tmp/fake/WindowsSandbox.exe"
  printf 'an invented archive' > "$tmp/in/premagentic-0.0.0-test-win-x64.zip"
  printf 'an invented database zip' > "$tmp/in/postgresql-0.0-test-windows-x64-binaries.zip"
  local zip=$tmp/in/premagentic-0.0.0-test-win-x64.zip pg=$tmp/in/postgresql-0.0-test-windows-x64-binaries.zip
  printf '%s  premagentic-0.0.0-test-win-x64.zip\n' "$(hash_of "$zip")" > "$tmp/in/SHA256SUMS"
  printf '%s  premagentic-0.0.0-test-win-x64.zip\n' "$(printf '0%.0s' $(seq 1 64))" > "$tmp/in/SHA256SUMS.wrong"
  pins() { printf '$PostgreSql = @{\n    Url    = "https://example.invalid/x.zip"\n    Name   = "%s"\n    Sha256 = "%s"\n}\n' "$(basename "$pg")" "$1" > "$tmp/fake/fetch-postgresql.ps1"; }
  local stage_args=(stage --archive "$zip" --postgresql-zip "$pg" --vc-runtime "$tmp/in/no-such-redistributable.exe")

  # The pin is read from the repository's own fetch-postgresql.ps1.
  status=0; out=$(PREM_SANDBOX_WORK=$tmp/work bash "$here/prove-windows-sandbox.sh" pin 2>&1) || status=$?
  if [ "$status" = 0 ] && grep -qE '^[0-9a-f]{64}  postgresql-[^ ]+-windows-x64-binaries\.zip$' <<< "$out"; then ok "the PostgreSQL pin is read from installer/windows/fetch-postgresql.ps1: $out"
  else bad "the PostgreSQL pin was not read from installer/windows/fetch-postgresql.ps1: $out"; fi

  # 1. The archive against its SHA256SUMS line; the control is refused only at the next check.
  pins "$(printf 'f%.0s' $(seq 1 64))"
  run refused "does not match its line in" "an archive that does not match its SHA256SUMS line" "${stage_args[@]}" --sums "$tmp/in/SHA256SUMS.wrong"
  run refused "is not the pinned PostgreSQL zip" "control: the matching archive passes, and a PostgreSQL zip that is not the pinned one" "${stage_args[@]}" --sums "$tmp/in/SHA256SUMS"
  # 2. The PostgreSQL zip against the pin; the control is refused only at the redistributable.
  pins "$(hash_of "$pg")"
  run refused "no redistributable at" "control: the pinned PostgreSQL zip passes, and a missing redistributable" "${stage_args[@]}" --sums "$tmp/in/SHA256SUMS"
  [ ! -e "$tmp/work/input" ] && ok "nothing was staged by a refused stage" || bad "a refused stage left $tmp/work/input"

  # 3. start refuses while a sandbox client is left, and opens one when none is.
  mkdir -p "$tmp/work/input/vc-runtime" "$tmp/work/results"
  : > "$tmp/work/input/prove-windows-sandbox.ps1"; : > "$tmp/work/input/vc-runtime/SHA256SUMS"
  cp "$zip" "$tmp/work/input/"; : > "$tmp/work/premagentic.wsb"
  printf 'echo "WindowsSandboxRemoteSession.exe   4242 Console   1   80,000 K"\n' > "$tmp/fake/tasklist"
  run refused "a sandbox or a sandbox client window is still there" "start while a sandbox client window is left" start
  [ ! -e "$tmp/fake/launched" ] && ok "nothing was opened while the client was left" || bad "a sandbox was opened while the client was left"
  printf 'echo "explorer.exe   1111 Console   1   90,000 K"\n' > "$tmp/fake/tasklist"
  run ok "opening the sandbox" "control: start with no sandbox left" start 5
  grep -q 'sandbox opened' "$tmp/fake/launched" 2> /dev/null && grep -q 'waiter started for 5 minutes' "$tmp/fake/launched" \
    && ok "control: the sandbox was opened and the waiter started" || bad "control: start did not open the sandbox and start the waiter"

  # 4. stop: exactly one sandbox, and not done until it is gone.
  printf 'if [ "$1" = list ]; then :; fi\n' > "$tmp/fake/wsb"
  run refused "shows 0 running sandboxes" "stop with no sandbox running" stop
  printf 'if [ "$1" = list ]; then echo 11111111-1111-1111-1111-111111111111; echo 22222222-2222-2222-2222-222222222222; fi\n' > "$tmp/fake/wsb"
  run refused "shows 2 running sandboxes" "stop with two sandboxes running" stop
  printf 'if [ "$1" = list ]; then echo 11111111-1111-1111-1111-111111111111; else echo "$@" >> "%s/wsb.log"; fi\n' "$tmp/fake" > "$tmp/fake/wsb"
  # The VM is listed for three more looks after the stop, then gone.
  printf 'n=$(cat "%s/looks" 2>/dev/null || echo 0); echo $((n + 1)) > "%s/looks"\n[ "$n" -lt 3 ] && echo "vmmemWindowsSandbox   5151 Services   0   2,000 K"\nexit 0\n' "$tmp/fake" "$tmp/fake" > "$tmp/fake/tasklist"
  run ok "stopped sandbox 11111111-1111-1111-1111-111111111111" "control: stop with one sandbox" stop
  grep -q 'stop --id 11111111-1111-1111-1111-111111111111' "$tmp/fake/wsb.log" 2> /dev/null && [ "$(cat "$tmp/fake/looks")" -ge 4 ] \
    && ok "stop asked wsb to stop it, and returned only after the VM was gone (looked $(cat "$tmp/fake/looks") times)" \
    || bad "stop returned before the VM was gone (looked $(cat "$tmp/fake/looks" 2> /dev/null || echo 0) times)"
  printf 'echo "vmmemWindowsSandbox   5151 Services   0   2,000 K"\n' > "$tmp/fake/tasklist"
  run refused "the sandbox is still there" "stop when the VM never goes" stop

  # 5. wait counts only the VM: a client window over a failed sandbox is not a running proof.
  rm -f "$tmp/work/results/done.txt"
  printf 'echo "WindowsSandboxRemoteSession.exe   4242 Console   1   80,000 K"\n' > "$tmp/fake/tasklist"
  run refused "the sandbox is not running and wrote no done.txt" "wait with only a client window and no VM" wait 1
  printf 'echo "vmmemWindowsSandbox   5151 Services   0   2,000 K"\n' > "$tmp/fake/tasklist"
  printf 'PASS  an invented proof\n' > "$tmp/work/results/done.txt"
  run ok "done after" "control: wait with the VM running and a PASS written" wait 1
  printf 'FAIL  an invented step; exit code 1\n' > "$tmp/work/results/done.txt"
  run refused "FAIL  an invented step" "wait on a FAIL written" wait 1

  if [ "$failures" -gt 0 ]; then printf '\n%s self-test check(s) FAILED\n' "$failures"; return 1; fi
  printf '\nSELF-TEST PASSED\n'
}

case ${1:-} in
  check)
    [ -e "$sandbox_exe" ] && printf 'WindowsSandbox.exe: present (%s)\n' "$sandbox_exe" || printf 'WindowsSandbox.exe: ABSENT\n'
    [ -e "$wsb_exe" ] && printf 'wsb.exe: present\n' || printf 'wsb.exe: ABSENT (stop needs it)\n'
    sandbox_running && printf 'a sandbox is RUNNING\n' || printf 'no sandbox running\n'
    printf 'work folder: %s\n' "$work"
    [ -f "$input/SHA256SUMS" ] && printf 'staged: %s\n' "$(tr -d '\r' < "$input/SHA256SUMS")" || printf 'staged: nothing\n'
    mkdir -p "$work" && printf 'free: %s MB\n' "$(free_mb "$work")"
    ;;
  stage) shift; stage "$@" ;;
  start) start "${2:-45}" ;;
  wait) wait_for_done "${2:-45}" ;;
  status)
    [ -f "$waiter/timeline.txt" ] && tr -d '\r' < "$waiter/timeline.txt" || printf 'no waiter started since the last stage\n'
    [ -f "$waiter/wait.out" ] && { printf -- '--- wait.out\n'; tr -d '\r' < "$waiter/wait.out"; }
    [ -f "$waiter/wait.exit" ] && printf 'wait.exit: %s\n' "$(tr -d '\r ' < "$waiter/wait.exit")" || printf 'wait.exit: not written yet (the waiter runs, or never started)\n'
    sandbox_running && printf 'a sandbox is running\n' || printf 'no sandbox running\n'
    if ls "$results"/transcript-*.txt > /dev/null 2>&1; then
      printf -- '--- the transcript so far\n'
      cat "$results"/transcript-*.txt | tr -d '\r' | grep -aE '^(PASS|FAIL)|^== ' || true
    fi
    [ -f "$results/done.txt" ] && printf 'done.txt: %s\n' "$(tr -d '\r' < "$results/done.txt")"
    ;;
  stop) stop ;;
  clean) rm -rf "$input" "$waiter" "$wsbfile"; printf 'removed the staged input; the results stay in %s\n' "$results" ;;
  pin) pin ;;
  self-test) self_test ;;
  *) sed -n '2,/^set -uo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//' >&2; exit 2 ;;
esac
