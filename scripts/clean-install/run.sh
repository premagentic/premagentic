#!/usr/bin/env bash
# The clean-machine install test for Linux, on the release archive itself. Run
# it on demand; it is not part of `dotnet test`.
#
#   scripts/clean-install/run.sh             the install, the quick start and the upgrade
#   scripts/clean-install/run.sh --offline   the install and the quick start with no network at all
#
# What it installs is the Linux release archive, the file a person downloads,
# never a layout of its own: with PREM_ARCHIVE set, that archive (a
# premagentic-<version>-linux-x64.tar.gz with the SHA256SUMS it was released
# with beside it, such as the release workflow's draft); otherwise one built for
# this run from HEAD by scripts/release/build.sh, for linux-x64 only, at the
# version PREM_PROOF_VERSION (default 0.1.0-proof.g<commit>). Either way the
# archive is checked against its SHA256SUMS first, and that line is printed at
# the start and at the end, so the record can say which bytes were proven. An
# uncommitted change is not in a built archive: build.sh builds HEAD only.
#
# It builds, on this machine's Docker:
#   - a network created with --internal, which has no route out of the host;
#   - a stock postgres:17.11 container on that network;
#   - a bare ubuntu:24.04 container on that network, with no .NET and no libicu,
#     that is given the archive and, outside it in /root/proof, what a person
#     would bring: an admin connection file, the password files of the first
#     administrator and one more person, and the invented files the readers read.
# Inside the Ubuntu container it follows the archive's own INSTALL.txt step by
# step, printing each step's text as the archive has it before running it, and
# checking that the commands it runs are the ones that text shows: it unpacks
# the archive into /opt/premagentic, runs `prem setup` with the first
# administrator and the HTTPS certificate (and again, which changes nothing),
# starts the API as the application role over HTTPS (asked for /health from a
# second container, which verifies the certificate and the host name), applies
# the starter profile and ingests its two sources (searching, with the gate
# holding both ways), allows the PDF, Word and Excel readers the archive ships by
# hash and ingests an invented PDF, an invented Word file, the sample equipment
# register workbook and a macro-enabled name, which is skipped; then restarts the
# API, as INSTALL.txt says a running API needs, and runs the quick start over it:
# two people sign in over HTTPS and search, and an agent calls the search tool
# over MCP with its token, each seeing what the folder rules give them and
# nothing else, the PDF's passage cited by its page and the workbook's by its
# sheet. No step sets PREM_ONNX_MODEL_DIR: the programs find the model the
# archive carries.
#
# Then the upgrade between two releases, archive to archive, on an install that
# holds data, in a database and roles of its own on the same containers: the
# older release's archive is unpacked into its own folder, set up, given
# documents and started; its configuration schema is backed up; it is stopped;
# its folder is set aside and the new archive unpacked in its place; the schema
# is migrated as the owner and setup run again; the new API starts, finds the
# older release's data, and serves a search over HTTPS to a signed-in person.
# Then the rollback: the older folder back, the configuration restored from the
# backup, the index rebuilt, and the older API answering again.
#
# With --offline there is no network at all instead: the database runs with
# --network none, and the application container and the client join that
# network namespace, whose only interface is loopback. Before anything is
# installed the script shows that the checks can tell (a container with a
# network reaches a public address and resolves a public name) and that this
# namespace cannot (neither works there). After setup it asks the product
# itself for an outside call, a search with PREM_EMBEDDING_PROVIDER=openai,
# which must fail at once and name why, not hang. Then the whole quick start
# runs there and succeeds, with both ingests and both starts of the API under
# strace: every connect() they make goes to the database on loopback, which also
# proves strace saw the connects, so the evidence covers the embedding runtime
# and not only the missing network. Then the fully local loop: a chat client's
# agent, registered with its model location local, asks PremAgentic over MCP and
# hands the passages to a local model server's chat completions endpoint, here a
# stand-in from deploy/fully-local that answers from what it is handed and is no
# model; the client and the model server are traced too, and may connect to
# nothing but the API and the model server, and to nothing at all. Setting
# PREM_LOOP_CONTROL_REACH_OUT=1 makes the model server try one connection out,
# and the run must then fail: that is the control that the check can catch it.
# In that mode the run ends with one line, CONTROL FAILED AT: <check>, naming the
# one check it failed at; scripts/clean-install/control.sh runs the control and
# counts it only when that check is the stand-in's connect to 1.1.1.1:443.
# The application image is ubuntu:24.04 with strace, python3 and libgomp1
# added, built before anything goes offline. There is no upgrade in this mode.
#
# It prints PASS or FAIL for each check and exits non-zero on the first
# failure. Everything it creates is removed on exit, the archives it built with
# it (a given PREM_ARCHIVE is never touched). No password, session or token is
# ever printed or put on a command line: they reach curl on its standard input.
#
# What a run costs: one archive build for linux-x64 (a few minutes, and about
# 1 GB of disk while it runs) unless PREM_ARCHIVE is given, and for the upgrade
# a second one, of the older release. To prove one archive offline, online and
# with the control, build it once and give it to all three as PREM_ARCHIVE.
#
# Needs: Docker, git, Python 3 (for the invented PDF and Word files, written
# each run), bash (Linux, macOS, or Git Bash on Windows), and to build an archive
# what scripts/release/build.sh needs (the pinned .NET SDK) and the model files
# (scripts/download-model.sh, or PREM_ONNX_MODEL_DIR pointing at a folder with
# model.onnx and vocab.txt).
#
# PREM_UPGRADE_FROM names the older release: an older release archive (with its
# SHA256SUMS beside it), or a git commit of this repository that has
# scripts/release/build.sh, whose archive is built from a clone checked out at
# that commit and verified to be it. Default: the previous release, the nearest
# tag v<version> in HEAD's history that is not on HEAD itself, when its commit
# has scripts/release/build.sh; a pre-release tag (one with a hyphen, such as
# v1.2.0-rc.1) counts only when the version proven is a pre-release too. A
# shallow clone is refused, since it cannot tell. With none, as in a repository
# whose first release is HEAD, the upgrade and the rollback do not run: the run
# says so in one line beginning UPGRADE NOT RUN, and its last line says it
# passed without the upgrade. The upgrade is never counted as passed.
# PREM_BUILD_DIR is where archives are built, outside the temp folder (default:
# artifacts/ in this repository, which git ignores); each run uses a folder of
# its own there and removes it.
set -euo pipefail

mode=online
case "${1:-}" in
  "") ;;
  --offline) mode=offline ;;
  *) printf 'Usage: %s [--offline]\n' "$0" >&2; exit 2 ;;
esac

repo=$(cd "$(dirname "$0")/../.." && pwd)
model_dir=${PREM_ONNX_MODEL_DIR:-$repo/models/minilm}
run_id="prem-clean-$(date +%s)-$RANDOM"
network="$run_id-net"
db="$run_id-db"
app="$run_id-app"
curl_image=curlimages/curl:8.22.0@sha256:58adaa4e8dca9c988bae2aba4ab3434a0bb2da16bbe3f92dec39ec7785166777
# The proof's images, pinned by digest (docker buildx imagetools inspect).
pg_image=postgres:17.11@sha256:d74eeac9a635390a49bc21bd49fccd973de707e2a53a76ac49b552b8712ec46f
ubuntu_image=ubuntu:24.04@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3
work=$(mktemp -d)
build_dir=${PREM_BUILD_DIR:-$repo/artifacts}/$run_id

# Git Bash rewrites arguments that look like POSIX paths for a Windows program.
# The container-side paths below must reach docker unchanged, so docker alone runs
# with that off; git and build.sh need it on (git refuses a /c/... path it is
# handed unrewritten), so it is never exported. Host paths are converted here.
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }
host_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

app_image=$ubuntu_image
# With PREM_LOOP_CONTROL_REACH_OUT=1 this run is the control, and it says on exit,
# in one fixed line, the one check it failed at, so a failure at the wrong step
# can be told from the one the control exists for (control.sh reads the line).
failed_at= current_step=
cleanup() {
  local status=$?
  docker rm -f "$app" "$db" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  [ "$app_image" = "$ubuntu_image" ] || docker rmi -f "$app_image" >/dev/null 2>&1 || true
  rm -rf "$work" || true
  rm -rf "$build_dir" || true
  rmdir "$(dirname "$build_dir")" 2>/dev/null || true
  if [ "${PREM_LOOP_CONTROL_REACH_OUT:-}" = 1 ]; then
    if [ "$status" = 0 ]; then printf 'CONTROL FAILED AT: nothing, the run passed\n'
    elif [ -n "$failed_at" ]; then printf 'CONTROL FAILED AT: %s\n' "$failed_at"
    else printf 'CONTROL FAILED AT: a command that stopped the step "%s" with exit %s\n' "${current_step:-before the first step}" "$status"
    fi
  fi
}
trap cleanup EXIT

step() { current_step="$*"; printf '\n== %s\n' "$*"; }
pass() { printf 'PASS  %s\n' "$*"; }
fail() { failed_at="$*"; printf 'FAIL  %s\n' "$*" >&2; exit 1; }
# wait_for_api <container> <log> <port>: waits for the API's own listening line,
# which Kestrel writes once the vector index is loaded and the port is bound, up to
# PREM_API_WAIT_SECONDS (180). Sets api_waited to the seconds it took; on running
# out it prints the log's last 40 lines and fails. A busy machine only makes it
# wait longer, so a red here is about the API, not the clock.
# dump_stalled_api <container>: what a stalled API was doing, read while its
# container still stands (the exit trap removes it). For the API program and
# every strace, found by /proc/<pid>/exe as stop_api finds them: each thread's
# State line and wchan, and how many threads are in "t (tracing stop)", the stop
# strace --seccomp-bpf holds a thread in at connect(). For the API also its
# open descriptors by type with the names of its open files, and whether
# anything listens on 8443 and whether the API holds it. Then the newest
# trace's last 20 lines, which hold connect() calls only. No environment, no
# command line and no file's contents are read, so nothing secret can reach
# the log.
dump_stalled_api() {
  printf -- '--- the stalled API and its strace, thread by thread\n'
  docker exec "$1" bash -c '
    found=0
    for p in /proc/[0-9]*; do
      exe=$(readlink "$p/exe" 2>/dev/null) || continue
      case $exe in */Premagentic.Api|*/strace) ;; *) continue ;; esac
      found=1
      threads=$(ls "$p/task" | wc -l)
      stopped=$(grep -l "^State:[[:space:]]*t (tracing stop)" "$p"/task/*/status 2>/dev/null | wc -l)
      printf "%s, pid %s: %s threads, %s in t (tracing stop)\n" "$exe" "${p#/proc/}" "$threads" "$stopped"
      for t in "$p"/task/*; do
        printf "    thread %s: %s; wchan %s\n" "${t##*/}" "$(grep "^State:" "$t/status" 2>/dev/null | cut -f2)" "$(cat "$t/wchan" 2>/dev/null || echo unreadable)"
      done
      [ "${exe##*/}" = Premagentic.Api ] || continue
      # Its open descriptors by type, and the names of the files among them.
      printf "    open descriptors: %s\n" "$(for f in "$p"/fd/*; do l=$(readlink "$f" 2>/dev/null) || continue
        case $l in socket:*) echo socket ;; pipe:*) echo pipe ;; anon_inode:*) echo "${l#anon_inode:}" ;; /*) echo file ;; *) echo other ;; esac
        done | sort | uniq -c | awk "{printf \"%s %s; \", \$2, \$1}")"
      for f in "$p"/fd/*; do l=$(readlink "$f" 2>/dev/null) || continue; case $l in /*) echo "    open file: $l" ;; esac; done | sort -u
      # Whether anything listens on 8443 (hex 20FB), from the kernel tables of
      # its network namespace, and whether this process holds that socket: a
      # bind that never came and a listening line that never came apart.
      listen=$(awk "\$4 == \"0A\" && \$2 ~ /:20FB\$/ {print \$10}" "$p/net/tcp" "$p/net/tcp6" 2>/dev/null)
      if [ -z "$listen" ]; then echo "    8443: nothing listens (the kernel tcp and tcp6 tables)"
      else
        for inode in $listen; do
          held=no
          for f in "$p"/fd/*; do [ "$(readlink "$f" 2>/dev/null)" = "socket:[$inode]" ] && held=yes; done
          echo "    8443: listening, socket inode $inode, held by this process: $held"
        done
      fi
    done
    [ "$found" = 1 ] || echo "no API program and no strace is running"
    trace=$(ls -t /root/trace/*.trace 2>/dev/null | head -n 1)
    if [ -n "$trace" ]; then echo "--- the last 20 lines of $trace"; tail -n 20 "$trace"; fi
  ' 2>&1
}
wait_for_api() {
  local container=$1 log=$2 port=$3 ceiling=${PREM_API_WAIT_SECONDS:-180} started=$SECONDS
  until docker exec "$container" grep -q 'Now listening on: ' "$log" 2>/dev/null; do
    if [ $((SECONDS - started)) -ge "$ceiling" ]; then
      printf -- '--- the last 40 lines of %s\n' "$log"
      docker exec "$container" tail -n 40 "$log" 2>&1 || true
      dump_stalled_api "$container" || true
      fail "the API wrote no listening line to $log within $ceiling s (port $port)"
    fi
    sleep 1
  done
  api_waited=$((SECONDS - started))
}
# stop_api <program> <port> <what>: stops every process running that program file,
# found by /proc/<pid>/exe (the bare image has no ps or pkill), and waits for the
# port to close. Under strace the process started is strace, which would leave
# the program running if it were the one stopped, so the program itself is.
stop_api() {
  docker exec "$app" bash -c "for p in /proc/[0-9]*; do [ \"\$(readlink \$p/exe 2>/dev/null)\" = '$1' ] && kill \${p#/proc/}; done; \
    for i in \$(seq 1 30); do (exec 3<>/dev/tcp/127.0.0.1/$2) 2>/dev/null || exit 0; sleep 1; done; exit 1" \
    || fail "the $3 API did not stop"
}
# Nothing about globalization or the model's folder is set here: the archive's
# programs must run on the bare system as they are.
in_app() { docker exec "$app" bash -c "$1"; }
# The Kerberos library error the database driver prints on every connection
# when GSS encryption is left on and the library is missing.
no_gss_noise() { if grep -q "libgssapi" "$1"; then cat "$1"; fail "$2 printed the Kerberos library error"; fi; }
# Makes a password file of 24 letters and digits, which never needs quoting.
new_password_file() { ( umask 077; head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24 > "$1"; printf '\n' >> "$1" ); }
free_mb() { df -Pk "$1" | awk 'NR == 2 { printf "%d", $4 / 1024 }'; }

# check_sums <archive>: the archive's SHA-256 must be the one the SHA256SUMS beside
# it gives; prints that line.
check_sums() {
  local archive=$1 name sums line actual
  name=$(basename "$archive")
  sums=$(dirname "$archive")/SHA256SUMS
  [ -f "$sums" ] || fail "$name has no SHA256SUMS beside it, so the bytes proven could not be named"
  line=$(tr -d '\r' < "$sums" | awk -v n="$name" 'length($1) == 64 && ($2 == n || $2 == "*" n)' | head -n 1)
  [ -n "$line" ] || fail "the SHA256SUMS beside $name has no line for it"
  actual=$(sha256sum "$archive" | cut -d' ' -f1)
  [ "${line%% *}" = "$actual" ] || fail "$name hashes to $actual and its SHA256SUMS says ${line%% *}"
  printf '%s' "$line"
}
# build_archive <source tree> <version> <out folder> <what>: scripts/release/build.sh
# of that tree, for linux-x64 only; its output goes to a log, whose end is shown if
# it fails. Prints the archive's path.
build_archive() {
  local tree=$1 version=$2 out=$3 what=$4 log before
  log="$work/build-$(basename "$out").log"
  before=$(free_mb "$(dirname "$out")")
  bash "$tree/scripts/release/build.sh" --version "$version" --out "$out" --rid linux-x64 --model-dir "$model_dir" > "$log" 2>&1 \
    || { tail -n 60 "$log" >&2; fail "building the archive of $what failed"; }
  printf '      free disk where it was built: %s MB before, %s MB after\n' "$before" "$(free_mb "$(dirname "$out")")" >&2
  printf '%s' "$out/premagentic-$version-linux-x64.tar.gz"
}
# The archive's own INSTALL.txt, read on the host before it is unpacked.
# install_step <n>: that step's text, from "<n>. " to the next step or the line
# naming the manual, with its trailing blank lines dropped.
install_step() {
  awk -v n="$1" '
    index($0, n ". ") == 1 { on = 1 }
    on && ((/^[0-9]+\. / && index($0, n ". ") != 1) || /^The manual:/) { exit }
    on { lines[++count] = $0 }
    END { while (count > 0 && lines[count] ~ /^[[:space:]]*$/) count--; for (i = 1; i <= count; i++) print lines[i] }
  ' "$work/INSTALL.txt"
}
# show_step <n> <command text it must show>...: prints step n as the archive has it,
# then checks that the step shows each command this script runs for it, so the
# proof follows the text and a change to either is caught here.
show_step() {
  local n=$1 text expected; shift
  text=$(install_step "$n")
  [ -n "$text" ] || fail "INSTALL.txt has no step $n"
  printf -- '--- INSTALL.txt step %s, as the archive has it:\n%s\n---\n' "$n" "$text"
  for expected in "$@"; do
    grep -qF -- "$expected" <<< "$text" || fail "INSTALL.txt step $n does not show '$expected', which this proof runs"
  done
}

[ -f "$model_dir/model.onnx" ] && [ -f "$model_dir/vocab.txt" ] \
  || fail "model files not found in $model_dir; run scripts/download-model.sh first"

step "The archive under test"
mkdir -p "$build_dir"
head_sha=$(git -C "$repo" rev-parse HEAD)
if [ -n "${PREM_ARCHIVE:-}" ]; then
  [ -f "$PREM_ARCHIVE" ] || fail "PREM_ARCHIVE names $PREM_ARCHIVE, which is not a file"
  archive=$(cd "$(dirname "$PREM_ARCHIVE")" && pwd)/$(basename "$PREM_ARCHIVE")
  printf '      given as PREM_ARCHIVE; this tree is at %s\n' "${head_sha:0:10}"
else
  git -C "$repo" diff --quiet HEAD || printf '      warning: the working tree differs from HEAD; the archive is built from HEAD (%s) only\n' "${head_sha:0:10}"
  archive=$(build_archive "$repo" "${PREM_PROOF_VERSION:-0.1.0-proof.g${head_sha:0:10}}" "$build_dir/new" "HEAD, ${head_sha:0:10}")
fi
name=$(basename "$archive")
[[ $name =~ ^(premagentic-(.+)-linux-x64)\.tar\.gz$ ]] || fail "$name is not a linux-x64 release archive"
top=${BASH_REMATCH[1]}
version=${BASH_REMATCH[2]}
sums_line=$(check_sums "$archive")
tar -xzOf "$archive" "$top/INSTALL.txt" | tr -d '\r' > "$work/INSTALL.txt" || fail "$name has no $top/INSTALL.txt"
pass "$name, version $version, $(wc -c < "$archive" | tr -d ' ') bytes; SHA256SUMS: $sums_line"

# Set when there is no earlier release to upgrade from; then the upgrade does not run.
no_upgrade=
if [ "$mode" = online ]; then
  upgrade_from=${PREM_UPGRADE_FROM:-}
  if [ -z "$upgrade_from" ]; then
    # The previous release: the nearest v<version> tag in HEAD's history, leaving
    # out a tag on HEAD itself, which is the release being proven, and, unless this
    # version is itself a pre-release, every pre-release tag (one with a hyphen).
    # A shallow clone does not hold the history that answers this, so it refuses.
    [ "$(git -C "$repo" rev-parse --is-shallow-repository)" != true ]       || fail "this clone is shallow, so it cannot tell whether an earlier release exists; fetch the whole history (fetch-depth: 0), or name the older release in PREM_UPGRADE_FROM"
    excludes=()
    at_head=$(git -C "$repo" tag --points-at HEAD --list 'v[0-9]*')
    for tag in $at_head; do excludes+=(--exclude "$tag"); done
    [[ $version == *-* ]] || excludes+=(--exclude 'v*-*')
    previous=$(git -C "$repo" describe --tags --abbrev=0 --match 'v[0-9]*' ${excludes[@]+"${excludes[@]}"} HEAD 2>/dev/null) || previous=
    if [ -z "$previous" ]; then
      no_upgrade="UPGRADE NOT RUN: there is no earlier release to upgrade from, so the upgrade and the rollback did not run"
    elif ! git -C "$repo" cat-file -e "$previous^{commit}:scripts/release/build.sh" 2>/dev/null; then
      no_upgrade="UPGRADE NOT RUN: the nearest earlier tag, $previous, predates the release archives, so there is no earlier release to upgrade from and the upgrade and the rollback did not run"
    else
      upgrade_from=$previous
    fi
  fi
  if [ -n "$no_upgrade" ]; then
    printf '%s\n' "$no_upgrade"
  elif [ -f "$upgrade_from" ]; then
    step "The older release for the upgrade: the archive $(basename "$upgrade_from")"
    older_archive=$(cd "$(dirname "$upgrade_from")" && pwd)/$(basename "$upgrade_from")
  else
    older_sha=$(git -C "$repo" rev-parse --verify "$upgrade_from^{commit}" 2>/dev/null) \
      || fail "PREM_UPGRADE_FROM names $upgrade_from, which is neither an archive file nor a commit of this repository"
    step "The older release for the upgrade: the archive of ${older_sha:0:10}, built from a clone checked out at it"
    # A clone of its own, not a worktree, so nothing of this repository's own
    # checkouts is touched; its line endings as committed, since build.sh runs from it.
    older_tree=$build_dir/older-src
    git clone --quiet --shared --no-checkout -c core.autocrlf=false "$repo" "$older_tree" || fail "cloning this repository for ${older_sha:0:10} failed"
    git -C "$older_tree" checkout --quiet --detach "$older_sha" || fail "checking out ${older_sha:0:10} failed"
    [ "$(git -C "$older_tree" rev-parse HEAD)" = "$older_sha" ] || fail "the clone is not at ${older_sha:0:10}; the older archive would be of another commit"
    [ -f "$older_tree/scripts/release/build.sh" ] || fail "${older_sha:0:10} has no scripts/release/build.sh; name a commit that has it, or an older archive"
    older_archive=$(build_archive "$older_tree" "0.1.0-older.g${older_sha:0:10}" "$build_dir/older" "the older release, ${older_sha:0:10}")
    rm -rf "$older_tree"
  fi
  if [ -z "$no_upgrade" ]; then
    older_name=$(basename "$older_archive")
    [[ $older_name =~ ^premagentic-(.+)-linux-x64\.tar\.gz$ ]] || fail "$older_name is not a linux-x64 release archive"
    older_version=${BASH_REMATCH[1]}
    [ "$older_version" != "$version" ] || fail "the older release has the version $version too; the upgrade could not show which one answers"
    older_sums_line=$(check_sums "$older_archive")
    pass "$older_name, version $older_version; SHA256SUMS: $older_sums_line"
  fi
fi

step "What a person brings besides the archive, in /root/proof"
proof=$work/proof
mkdir -p "$proof/fully-local"
python3 "$(host_path "$repo/scripts/clean-install/make-reader-samples.py")" "$(host_path "$proof/formats")" >/dev/null \
  || fail "the invented PDF and Word files were not written"
cp "$repo/sample-docs/spreadsheets/equipment-register.xlsx" "$proof/formats/"
cp "$repo/deploy/fully-local/stub-model-server.py" "$repo/deploy/fully-local/loop-client.py" "$proof/fully-local/"
pass "an invented PDF, Word file and macro-enabled name, the sample workbook, and the fully local loop's stand-ins"

# The client image is fetched now, by the host: in offline mode nothing inside
# the namespace could fetch it. For the same reason strace is added to the
# offline application image here.
docker pull -q "$curl_image" >/dev/null
if [ "$mode" = offline ]; then
  app_image="$run_id-image"
  docker build -q -t "$app_image" - >/dev/null <<IMAGE
FROM $ubuntu_image
RUN apt-get update && apt-get install -y --no-install-recommends strace python3 libgomp1 && rm -rf /var/lib/apt/lists/*
IMAGE
  # traced <name>: strace every connect() of the command and its threads into /root/trace/<name>.trace.
  # --seccomp-bpf stops a traced thread only at connect(): under runtime 10.0.12, strace's default stop at
  # every system call deadlocked the API after "Vector index loaded", before it listened (seen on 2026-09-25).
  traced() { printf 'strace -f -qq --seccomp-bpf -e trace=connect -o /root/trace/%s.trace' "$1"; }
else
  traced() { :; }
fi

admin_password=$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 32)
if [ "$mode" = online ]; then
  step "Creating a Docker network with no route out, and a stock PostgreSQL on it"
  docker network create --internal "$network" >/dev/null
  docker run -d --name "$db" --network "$network" -e POSTGRES_PASSWORD="$admin_password" "$pg_image" >/dev/null
  db_host=$db
  api_host=$app
  api_net=(--network "$network")
else
  step "Starting a stock PostgreSQL with no network, only loopback"
  docker run -d --name "$db" --network none -e POSTGRES_PASSWORD="$admin_password" "$pg_image" >/dev/null
  db_host=localhost
  api_host=localhost
  api_net=(--network "container:$db")
fi
for _ in $(seq 1 60); do docker exec "$db" pg_isready -U postgres -h localhost >/dev/null 2>&1 && break; sleep 1; done
docker exec "$db" pg_isready -U postgres -h localhost >/dev/null 2>&1 || fail "postgres:17.11 did not become ready"
if [ "$mode" = online ]; then
  pass "postgres:17.11 is up on an --internal network"
else
  [ "$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$db")" = "none" ] || fail "the database container is not on --network none"
  pass "postgres:17.11 is up with --network none"
fi

# INSTALL.txt step 2's file: one line naming a role that can create roles and
# databases, never on a command line. It says nothing about GSS encryption: setup
# turns it off by default, and the checks below prove no connection prints the
# Kerberos error.
( umask 077; printf 'connection=Host=%s;Username=postgres;Password=%s;Database=postgres\n' "$db_host" "$admin_password" > "$proof/admin.credentials" )
unset admin_password
# The first administrator's password, and one more person's, also files: setup
# and prem users add read the first line.
new_password_file "$proof/first-admin.password"
new_password_file "$proof/hr-person.password"
first_admin_password=$(head -n 1 "$proof/first-admin.password")

step "Starting a bare $app_image given nothing but the archive and /root/proof"
# The fully local loop's real model server and model, when given, mounted
# read-only: nothing is copied into the container for them.
model_mounts=()
if [ "$mode" = offline ] && [ -n "${PREM_LLAMA_SERVER:-}${PREM_LLAMA_MODEL:-}" ] && [ "${PREM_LOOP_CONTROL_REACH_OUT:-}" != 1 ]; then
  [ -f "${PREM_LLAMA_SERVER:-}" ] && [ -f "${PREM_LLAMA_MODEL:-}" ] \
    || fail "PREM_LLAMA_SERVER must name a llama.cpp release tarball and PREM_LLAMA_MODEL a GGUF model file, both files"
  model_mounts=(-v "$(host_path "$PREM_LLAMA_SERVER"):/root/model-server/server.tar.gz:ro"
                -v "$(host_path "$PREM_LLAMA_MODEL"):/root/model-server/model.gguf:ro")
fi
docker create --name "$app" "${api_net[@]}" "${model_mounts[@]}" "$app_image" sleep infinity >/dev/null
docker cp "$(host_path "$archive")" "$app:/root/$name"
docker cp "$(host_path "$proof")" "$app:/root/proof"
[ "$mode" = online ] && [ -z "$no_upgrade" ] && docker cp "$(host_path "$older_archive")" "$app:/root/$older_name"
docker start "$app" >/dev/null
docker exec "$app" bash -c 'mkdir -p /root/trace && chmod 600 /root/proof/*.credentials /root/proof/*.password'
if in_app 'command -v dotnet || ls -d /usr/share/dotnet /usr/lib/dotnet /opt/dotnet 2>/dev/null' | grep -q .; then
  fail "the ubuntu container has a dotnet; the proof would not show the archive needs none"
fi
if in_app 'ls /usr/lib/x86_64-linux-gnu/libicu* /lib/x86_64-linux-gnu/libicu* 2>/dev/null' | grep -q .; then
  fail "the ubuntu container has a system libicu; the test would not prove the archive carries its own"
fi
pass "$app_image started with the archive in /root, nothing installed$([ "$mode" = offline ] && printf ' but strace, python3 and libgomp1'), no dotnet and no libicu on the system"

reach_out() { docker exec "$1" bash -c 'timeout 5 bash -c "exec 3<>/dev/tcp/1.1.1.1/443"' >/dev/null 2>&1; }
resolve_out() { docker exec "$1" bash -c 'timeout 5 getent hosts example.com' >/dev/null 2>&1; }
if [ "$mode" = offline ]; then
  step "Controls: the same checks, from a container with a network, do reach out"
  control="$run_id-control"
  docker run -d --name "$control" "$ubuntu_image" sleep 60 >/dev/null
  reach_out "$control" && resolve_out "$control"; control_ok=$?
  docker rm -f "$control" >/dev/null
  [ "$control_ok" = 0 ] || fail "a container with a network could not reach 1.1.1.1:443 or resolve example.com; this host has no route out, so the checks below would prove nothing"
  pass "with a network, a container reaches 1.1.1.1:443 and resolves example.com: the checks can tell"

  step "No network in the application's namespace"
  interfaces=$(docker exec "$app" ls /sys/class/net | tr '\n' ' ')
  [ "$interfaces" = "lo " ] || fail "the application container has interfaces '$interfaces', not only lo"
  pass "the application container's only network interface is lo"
fi

step "Proving there is no route out"
if reach_out "$app"; then fail "the application container reached 1.1.1.1:443; the network has a route out"; fi
if resolve_out "$app"; then fail "the application container resolved example.com; names outside the network resolve"; fi
pass "no TCP route to 1.1.1.1:443 and no outside name resolution"

prem=/opt/premagentic/bin/prem
step "INSTALL.txt step 1: unpack it where it will live"
show_step 1 "sudo mkdir -p /opt/premagentic" "sudo tar -xzf $name -C /opt/premagentic --strip-components=1"
in_app "cd /root && mkdir -p /opt/premagentic && tar -xzf $name -C /opt/premagentic --strip-components=1" || fail "unpacking $name failed"
in_app 'test -x /opt/premagentic/bin/prem && test -x /opt/premagentic/bin/Premagentic.Api && test -x /opt/premagentic/bin/Premagentic.McpServer \
  && test -f /opt/premagentic/models/minilm/model.onnx && test -f /opt/premagentic/bin/libicuuc.so.72.1.0.3' \
  || fail "the unpacked archive lacks an executable program, the model or ICU"
[ "$(in_app 'find /opt/premagentic -name libcoreclr.so | wc -l')" = 1 ] || fail "the unpacked archive does not carry exactly one runtime"
in_app "$prem --version" > "$work/version.out" 2>&1 || { cat "$work/version.out"; fail "prem --version failed"; }
grep -qF -- "$version" "$work/version.out" || { cat "$work/version.out"; fail "prem --version does not print $version"; }
pass "unpacked into /opt/premagentic as the archive's modes left it: three executable programs over one runtime in bin/, ICU and the model; prem --version prints $(head -n 1 "$work/version.out" | tr -d '\r')"

step "INSTALL.txt step 2: a way into PostgreSQL"
show_step 2 "connection=Host=localhost;Username=postgres;Password=...;Database=postgres"
[ "$(docker exec "$app" stat -c %a /root/proof/admin.credentials)" = 600 ] || fail "the admin connection file is not readable by its owner only"
pass "/root/proof/admin.credentials, one line, mode 600 (Host=$db_host; its contents are not printed)"

setup_command="$prem setup --admin-connection-file /root/proof/admin.credentials \
  --credentials-dir /etc/premagentic --admin-user first-admin \
  --admin-password-file /root/proof/first-admin.password --host-name $api_host"

step "INSTALL.txt step 3: prem setup, with the first administrator and the HTTPS certificate"
show_step 3 "/opt/premagentic/bin/prem setup --admin-connection-file" "--credentials-dir /etc/premagentic --admin-user" "--host-name"
printf '      (the password comes from --admin-password-file rather than setup'"'"'s prompt: this run has no one to type it)\n'
in_app "cd /root && $setup_command" > "$work/setup.out" 2>&1 || { cat "$work/setup.out"; fail "prem setup failed"; }
cat "$work/setup.out"
grep -q "Setup complete" "$work/setup.out" || fail "prem setup did not report completion"
grep -q "\[checked\] preflight.text .*ICU 72.1.0.3, shipped with Premagentic" "$work/setup.out" \
  || fail "setup did not report the ICU it shipped with"
grep -q "\[applied\] administrator .*made 'first-admin' the first administrator" "$work/setup.out" || fail "the first administrator was not made"
grep -q "\[applied\] https .*self-signed certificate for $api_host" "$work/setup.out" || fail "the HTTPS certificate was not made"
grep -q "model files present in /opt/premagentic/models/minilm" "$work/setup.out" || fail "setup did not find the model the archive carries"
no_gss_noise "$work/setup.out" "prem setup"
pass "setup completed: ICU shipped, the archive's model, first administrator, certificate, health check"

for file in app.credentials owner.credentials kestrel.json https.pfx; do
  [ "$(docker exec "$app" stat -c %a "/etc/premagentic/$file")" = "600" ] || fail "$file is not mode 600"
done
[ "$(docker exec "$app" stat -c %a /etc/premagentic)" = "700" ] || fail "the credentials folder is not mode 700"
pass "credentials, HTTPS settings and certificate are mode 600, in /etc/premagentic, mode 700"

# No generated or given secret may appear in anything setup printed.
for secret in \
  "$(docker exec "$app" bash -c "sed -n 's/.*Password=\([^;]*\).*/\1/p' /etc/premagentic/app.credentials")" \
  "$(docker exec "$app" bash -c "sed -n 's/.*Password=\([^;]*\).*/\1/p' /etc/premagentic/owner.credentials")" \
  "$(docker exec "$app" bash -c "sed -n 's/.*\"Password\": \"\([^\"]*\)\".*/\1/p' /etc/premagentic/kestrel.json")" \
  "$first_admin_password"; do
  [ -n "$secret" ] || fail "a secret the check needs was not found"
  # A generated secret may begin with a dash, so it goes to grep with -e; and
  # the same grep must first find it where it certainly is, so a check that
  # cannot run fails here instead of passing below.
  grep -qF -e "$secret" <<< "$secret" || fail "the secret check cannot match a secret it was given"
  if grep -qF -e "$secret" "$work/setup.out"; then fail "a secret appears in setup's output"
  elif [ $? -ne 1 ]; then fail "the secret check could not read setup's output"; fi
done
unset secret first_admin_password
pass "no database password, certificate password or administrator password appears in setup's output"

step "INSTALL.txt step 3, again: 'Run it again at any time'"
in_app "cd /root && $setup_command" > "$work/setup2.out" 2>&1 || { cat "$work/setup2.out"; fail "the second prem setup failed"; }
grep -q "Nothing changed" "$work/setup2.out" || { cat "$work/setup2.out"; fail "the second run changed something"; }
grep -q "\[ok\]      administrator .*none was made" "$work/setup2.out" || { cat "$work/setup2.out"; fail "the second run did not find the administrator"; }
pass "a second run changed nothing and said so"

as_app='export PREM_CREDENTIALS_FILE=/etc/premagentic/app.credentials; cd /root;'

if [ "$mode" = offline ]; then
  step "The product asked for an outside call: a search with PREM_EMBEDDING_PROVIDER=openai"
  # Without a key it refuses; with one (not a real key) it tries to reach the
  # provider, which must be refused at once: exit code 2 and one line naming
  # the provider, not a hang and not a stack trace. 60 seconds is the limit for
  # "at once"; timeout exits 124 when it is reached. The search is public-only:
  # no group exists yet, and a named one would be refused before the provider
  # is ever asked.
  set +e
  in_app "$as_app timeout 60 env PREM_EMBEDDING_PROVIDER=openai $prem search 'how long do I have to file an expense claim'" \
    > "$work/openai-nokey.out" 2>&1
  nokey_exit=$?
  in_app "$as_app timeout 60 env PREM_EMBEDDING_PROVIDER=openai OPENAI_API_KEY=not-a-real-key $prem search 'how long do I have to file an expense claim'" \
    > "$work/openai.out" 2>&1
  openai_exit=$?
  set -e
  printf '      without a key, exit %s: %s\n' "$nokey_exit" "$(grep -m 1 'stopped' "$work/openai-nokey.out" | tr -d '\r' || true)"
  printf '      with a key, exit %s: %s\n' "$openai_exit" "$(grep -m 1 'stopped' "$work/openai.out" | tr -d '\r' || true)"
  for out in openai-nokey openai; do
    if grep -qE 'Unhandled exception|^   at ' "$work/$out.out"; then cat "$work/$out.out"; fail "the openai provider's failure printed a stack trace"; fi
  done
  [ "$nokey_exit" = 2 ] && grep -q "needs OPENAI_API_KEY, which is not set." "$work/openai-nokey.out" \
    || { cat "$work/openai-nokey.out"; fail "without a key, the openai provider was not refused with exit code 2"; }
  [ "$openai_exit" != 124 ] || { cat "$work/openai.out"; fail "the openai provider hung instead of failing"; }
  [ "$openai_exit" = 2 ] && grep -q "could not be reached at https://api.openai.com/" "$work/openai.out" \
    || { cat "$work/openai.out"; fail "the openai provider was not refused with exit code 2 for being unable to reach api.openai.com"; }
  pass "the openai provider is refused, exit code 2 and one line, without a key and when api.openai.com cannot be reached"
fi

api_command="PREM_CREDENTIALS_FILE=/etc/premagentic/app.credentials /opt/premagentic/bin/Premagentic.Api"
# start_api <trace name>: INSTALL.txt step 4's command, from a working folder
# outside the archive, its output in /root/api.log; then the wait.
start_api() {
  docker exec "$app" rm -f /root/api.log
  docker exec -d "$app" bash -c "cd /root && exec $(traced "$1") env $api_command > /root/api.log 2>&1"
  wait_for_api "$app" /root/api.log 8443
  docker exec "$app" cat /root/api.log > "$work/api.log"
  docker exec "$app" bash -c 'exec 3<>/dev/tcp/127.0.0.1/8443' >/dev/null 2>&1 || { cat "$work/api.log"; fail "the API is not listening on 8443"; }
  grep -q "Vector index loaded" "$work/api.log" || { cat "$work/api.log"; fail "the API did not load the vector index"; }
  no_gss_noise "$work/api.log" "the API"
}

step "INSTALL.txt step 4: the server, as the application role, over HTTPS"
show_step 4 "$api_command"
# First the refusal: with nothing configured, the API stops at once, says what
# to set, and exits 2, the code the systemd unit does not restart on.
set +e
docker exec "$app" bash -c 'cd /root && timeout 30 /opt/premagentic/bin/Premagentic.Api' > "$work/api-unconfigured.out" 2>&1
unconfigured_exit=$?
set -e
[ "$unconfigured_exit" = "2" ] || { cat "$work/api-unconfigured.out"; fail "the API with nothing configured exited $unconfigured_exit, not 2"; }
grep -q "Premagentic API cannot start: No database is configured. Set PREM_CREDENTIALS_FILE" "$work/api-unconfigured.out" \
  || { cat "$work/api-unconfigured.out"; fail "the API did not say what to set"; }
pass "with nothing configured the API refuses to start, says what to set, and exits 2"

start_api api-first
pass "the API started from bin/ with its credentials file and listens on 8443, after $api_waited s"

# The API connects as the application role, and as the search role for the
# search and section reads; never as the owner or the administrator. The search
# role's pool may have no open connection at this moment.
connected_as=$(docker exec "$db" psql -U postgres -tAc "SELECT string_agg(DISTINCT usename::text, ',' ORDER BY usename::text) FROM pg_stat_activity WHERE datname = 'premagentic'")
case "$connected_as" in
  premagentic_app | premagentic_app,premagentic_search) ;;
  *) fail "the database sees '$connected_as' connected; the API may connect only as premagentic_app and premagentic_search" ;;
esac
pass "the database sees the API connected as $connected_as and nothing else"

# The public certificate is not secret; clients are given it to trust.
# curl_to <port> <crt> [curl arguments]: a client in the API's network, which
# trusts that certificate and reads any secret header or body on its standard
# input.
curl_to() {
  local crt=$2; shift 2
  docker run -i --rm "${api_net[@]}" -v "$(host_path "$crt"):/ca.crt:ro" "$curl_image" \
    --fail --silent --show-error --max-time 30 --cacert /ca.crt "$@"
}
docker cp "$app:/etc/premagentic/https.crt" "$(host_path "$work/https.crt")"
curl_to 8443 "$work/https.crt" "https://$api_host:8443/health" < /dev/null > "$work/health.out" \
  || fail "/health over HTTPS, verified against the setup certificate, failed"
grep -q '"status":"ok"' "$work/health.out" || { cat "$work/health.out"; fail "/health did not answer ok"; }
pass "/health answers over HTTPS, with the certificate and the host name verified"

# The controls: without the certificate the client refuses the connection, so
# the verification above could fail; and plain HTTP gets no answer.
if docker run --rm "${api_net[@]}" "$curl_image" --fail --silent --max-time 10 "https://$api_host:8443/health" >/dev/null 2>&1; then
  fail "a client that was not given the certificate trusted it anyway; the check above proves nothing"
fi
if docker run --rm "${api_net[@]}" "$curl_image" --fail --silent --max-time 10 "http://$api_host:8443/health" >/dev/null 2>&1; then
  fail "the API answered plain HTTP"
fi
pass "controls: an untrusting client refuses the certificate, and plain HTTP gets no answer"

step "INSTALL.txt step 5: the starter profile and its two sources, as the application role"
show_step 5 "/opt/premagentic/bin/prem profile apply /opt/premagentic/samples/profiles/starter" \
  "/opt/premagentic/bin/prem ingest --source open" "/opt/premagentic/bin/prem ingest --source hr"
in_app "$as_app $prem profile apply /opt/premagentic/samples/profiles/starter" > "$work/ingest.out" 2>&1 \
  || { cat "$work/ingest.out"; fail "applying the starter profile failed"; }
in_app "$as_app $(traced ingest) $prem ingest --source open" >> "$work/ingest.out" 2>&1 || { cat "$work/ingest.out"; fail "ingest of the source open failed"; }
in_app "$as_app $prem ingest --source hr" >> "$work/ingest.out" 2>&1 || { cat "$work/ingest.out"; fail "ingest of the source hr failed"; }
grep -q "open/handbook.md" "$work/ingest.out" && grep -q "hr/salary-bands.md" "$work/ingest.out" \
  || { cat "$work/ingest.out"; fail "the ingest did not index the sample documents"; }
no_gss_noise "$work/ingest.out" "ingest"
pass "the starter profile applied; its two sources ingested from the archive's sample-docs/"

in_app "$as_app $prem search 'how long do I have to file an expense claim' --as group:engineering" > "$work/search1.out"
grep -q "open/handbook.md" "$work/search1.out" || { cat "$work/search1.out"; fail "the expense claim question did not find open/handbook.md"; }
pass "search found open/handbook.md"

in_app "$as_app $prem search 'band four compensation review' --as group:engineering" > "$work/search2.out"
if grep -q "hr/salary-bands.md" "$work/search2.out"; then cat "$work/search2.out"; fail "engineering reached hr/salary-bands.md"; fi
grep -q "hits in" "$work/search2.out" || { cat "$work/search2.out"; fail "the search as engineering returned no result line"; }
in_app "$as_app $prem search 'band four compensation review' --as group:hr" > "$work/search3.out"
grep -q "hr/salary-bands.md" "$work/search3.out" || { cat "$work/search3.out"; fail "hr did not reach hr/salary-bands.md"; }
pass "the gate holds both ways: engineering does not reach the salary bands, hr does"

if [ "$mode" = offline ]; then
  step "A damaged install says what is missing: prem and the API from a copy of the install without bin/libonnxruntime.so"
  # The copy is made of hard links, so it costs no disk and the install
  # stays whole; only the copy loses the library. The same search as above,
  # and the API as INSTALL.txt step 4 starts it, each from the copy's own
  # programs, must refuse in one sentence that carries the loader's own words
  # for the missing library (the sentence's remedy names it too, so that
  # alone would prove nothing), with the refusal's exit code and no stack
  # trace. The copy's API loads the model at its warm-up, before Kestrel binds
  # (Program.cs), so it is refused before it could reach for the running
  # API's port; a start that got as far would fail on that port, or run into
  # the time limit, and either way not give exit 2.
  in_app 'rm -rf /opt/premagentic-damaged && cp -al /opt/premagentic /opt/premagentic-damaged \
    && rm /opt/premagentic-damaged/bin/libonnxruntime.so && test -f /opt/premagentic/bin/libonnxruntime.so' \
    || fail "the damaged copy of the install could not be made"
  set +e
  in_app "$as_app /opt/premagentic-damaged/bin/prem search 'how long do I have to file an expense claim' --as group:engineering" \
    > "$work/damaged.out" 2>&1
  damaged_exit=$?
  in_app "cd /root && timeout 90 env PREM_CREDENTIALS_FILE=/etc/premagentic/app.credentials /opt/premagentic-damaged/bin/Premagentic.Api" \
    > "$work/damaged-api.out" 2>&1
  damaged_api_exit=$?
  set -e
  in_app 'rm -rf /opt/premagentic-damaged && test -f /opt/premagentic/bin/libonnxruntime.so' || fail "the install lost its library with the copy"
  # damaged_check <what> <file> <exit> <the refusal's opening words>
  damaged_check() {
    [ "$3" = 2 ] || { cat "$2"; fail "$1 from the damaged copy exited $3, not 2"; }
    grep -qF "$4 The local embedding model could not be loaded, because ONNX Runtime's native library could not be" "$2" \
      && grep -qF "libonnxruntime.so: cannot open shared object file" "$2" \
      || { cat "$2"; fail "$1 from the damaged copy did not say which library could not be loaded"; }
    if grep -qE 'Unhandled exception|^ +at ' "$2"; then cat "$2"; fail "$1 from the damaged copy printed a stack trace"; fi
    # The line that holds the refusal: the API writes its start's log lines first.
    printf '      %s said: %s\n' "$1" "$(tr -d '\r' < "$2" | grep -F -m 1 "$4" | cut -c1-400)"
  }
  damaged_check prem "$work/damaged.out" "$damaged_exit" "prem search stopped before it searched:"
  damaged_check "the API" "$work/damaged-api.out" "$damaged_api_exit" "Premagentic API cannot start:"
  pass "prem and the API from a copy of the install without bin/libonnxruntime.so each stopped with exit 2 and one sentence naming the library and the reinstall, with no stack trace; the install itself kept its library"
fi

step "INSTALL.txt step 6: the PDF, Word and Excel readers the archive ships, allowed by hash"
show_step 6 "/opt/premagentic/bin/prem settings set extensions.folder /opt/premagentic/extensions" \
  "/opt/premagentic/bin/prem extensions allow /opt/premagentic/extensions/pdf-reader" \
  "/opt/premagentic/bin/prem extensions allow /opt/premagentic/extensions/docx-reader" \
  "/opt/premagentic/bin/prem extensions allow /opt/premagentic/extensions/xlsx-reader"
in_app "$as_app $prem settings set extensions.folder /opt/premagentic/extensions" > "$work/folder.out" 2>&1 \
  || { cat "$work/folder.out"; fail "setting extensions.folder failed"; }
# Before they are allowed the same folder reads nothing but counts the files,
# which is the control that the readers are what reads them below.
in_app "$as_app $prem ingest /root/proof/formats --public --prefix formats" > "$work/formats-before.out" 2>&1 \
  || { cat "$work/formats-before.out"; fail "ingest of the invented files before the readers were allowed failed"; }
grep -q "Skipped 4 file(s)" "$work/formats-before.out" \
  || { cat "$work/formats-before.out"; fail "before the readers were allowed, the four files were not all counted as skipped"; }
if grep -q -e "formats/loading-dock.pdf" -e "formats/visitor-policy.docx" -e "formats/equipment-register.xlsx" "$work/formats-before.out"; then
  cat "$work/formats-before.out"; fail "a file was indexed before any reader was allowed"
fi
in_app "$as_app $prem extensions allow /opt/premagentic/extensions/pdf-reader && $prem extensions allow /opt/premagentic/extensions/docx-reader \
  && $prem extensions allow /opt/premagentic/extensions/xlsx-reader && $prem extensions list" \
  > "$work/extensions.out" 2>&1 || { cat "$work/extensions.out"; fail "allowing the readers failed"; }
grep -qE "^  pdf-reader  [0-9a-fA-F]{64}  \(loaded\)" "$work/extensions.out" \
  && grep -qE "^  docx-reader  [0-9a-fA-F]{64}  \(loaded\)" "$work/extensions.out" \
  && grep -qE "^  xlsx-reader  [0-9a-fA-F]{64}  \(loaded\)" "$work/extensions.out" \
  && grep -q "^Refused: none\." "$work/extensions.out" \
  || { cat "$work/extensions.out"; fail "the readers are not all three allowed and loaded from the archive, or an extension was refused"; }
in_app "$as_app $(traced readers) $prem ingest /root/proof/formats --public --prefix formats" > "$work/formats.out" 2>&1 \
  || { cat "$work/formats.out"; fail "ingest of the invented files through the readers failed"; }
grep -q "formats/loading-dock.pdf" "$work/formats.out" && grep -q "formats/visitor-policy.docx" "$work/formats.out" \
  && grep -q "formats/equipment-register.xlsx" "$work/formats.out" \
  || { cat "$work/formats.out"; fail "the PDF, the Word file and the Excel file were not all indexed"; }
grep -qF ".docm (macro-enabled) 1" "$work/formats.out" \
  || { cat "$work/formats.out"; fail "the macro-enabled file was not counted as skipped for that reason"; }
no_gss_noise "$work/formats.out" "the readers' ingest"
pass "allowed by hash from the archive, the readers indexed the PDF, the Word file and the workbook and skipped the macro-enabled one, with its reason"

step "INSTALL.txt step 6: 'A running API uses them after it is restarted'"
install_step 6 | tr '\n' ' ' | tr -s ' ' | grep -qF "A running API uses them after it is restarted" || fail "INSTALL.txt no longer says a running API needs a restart for the readers"
stop_api /opt/premagentic/bin/Premagentic.Api 8443 first
start_api api
pass "the API stopped and started again with step 4's command, and listens on 8443, after $api_waited s"

# http_search <port> <crt> <sign-in name> <password file> <query> <path> <present|absent> <what>
# Signs in over HTTPS with a password, then searches as that session. The
# password reaches curl on standard input, and the session cookie and its
# anti-forgery token come back into a private file that is fed to the next
# curl the same way; none of them is printed.
http_search() {
  local port=$1 crt=$2 name=$3 password_file=$4 query=$5 path=$6 expect=$7 what=$8
  ( umask 077
    printf '{"signInName":"%s","password":"%s"}' "$name" "$(head -n 1 "$password_file")" > "$work/signin.json"
    curl_to "$port" "$crt" --include -H 'Content-Type: application/json' --data-binary @- \
      "https://$api_host:$port/api/session" < "$work/signin.json" > "$work/session.out" ) \
    || { rm -f "$work/signin.json" "$work/session.out"; fail "$what: signing in as $name over HTTPS failed"; }
  rm -f "$work/signin.json"
  ( umask 077
    printf 'Cookie: __Host-prem-session=%s\nX-Prem-Antiforgery: %s\n' \
      "$(tr -d '\r' < "$work/session.out" | sed -n 's/^[Ss]et-[Cc]ookie: __Host-prem-session=\([^;]*\).*/\1/p')" \
      "$(sed -n 's/.*"antiForgeryToken":"\([^"]*\)".*/\1/p' "$work/session.out")" > "$work/session.header" )
  rm -f "$work/session.out"
  curl_to "$port" "$crt" -H @- -H 'Content-Type: application/json' \
    --data-binary "{\"query\":\"$query\",\"topK\":5}" "https://$api_host:$port/api/search" \
    < "$work/session.header" > "$work/http-search.out" \
    || { rm -f "$work/session.header"; fail "$what: the search over HTTPS as $name failed"; }
  rm -f "$work/session.header"
  if [ "$expect" = present ]; then
    grep -q "\"$path\"" "$work/http-search.out" || { cat "$work/http-search.out"; fail "$what: $name did not get $path over HTTPS"; }
  else
    grep -q '"hits":\[' "$work/http-search.out" || { cat "$work/http-search.out"; fail "$what: the search over HTTPS as $name returned no hit list"; }
    if grep -q "\"$path\"" "$work/http-search.out"; then cat "$work/http-search.out"; fail "$what: $name got $path over HTTPS"; fi
  fi
}

step "The quick start over the API: people sign in over HTTPS and search"
in_app "$as_app $prem users add hr-person --role member --password < /root/proof/hr-person.password && $prem groups members hr --add hr-person" \
  > "$work/users.out" 2>&1 || { cat "$work/users.out"; fail "adding hr-person to hr failed"; }
http_search 8443 "$work/https.crt" first-admin "$proof/first-admin.password" \
  "how long do I have to file an expense claim" open/handbook.md present "quick start"
http_search 8443 "$work/https.crt" first-admin "$proof/first-admin.password" \
  "band four compensation review" hr/salary-bands.md absent "quick start"
http_search 8443 "$work/https.crt" hr-person "$proof/hr-person.password" \
  "band four compensation review" hr/salary-bands.md present "quick start"
pass "over HTTPS, signed in: first-admin finds the handbook and not the salary bands; hr-person, in hr, finds them"

step "The quick start over the API: an agent calls the search tool over MCP"
# The token is written to a private file inside the application container and
# piped from there into curl; it never reaches this host's disk or a command line.
in_app "$as_app $prem agents add reader --owner first-admin --mode service --model local && $prem agents grant reader hr \
  && (umask 077; $prem tokens issue reader --days 1 > /root/proof/token.out && printf 'Authorization: Bearer %s\n' \"\$(tail -n 1 /root/proof/token.out)\" > /root/proof/agent.header; rm -f /root/proof/token.out)" \
  > "$work/agent.out" 2>&1 || { cat "$work/agent.out"; fail "making the agent and its token failed"; }
# Revision 2026-07-28 has no initialize: every request carries the method and
# the tool's name in headers, and its protocol version and client capabilities
# in _meta. A request without any of them is a 400.
mcp_headers=(-H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream'
  -H 'MCP-Protocol-Version: 2026-07-28' -H 'Mcp-Method: tools/call' -H 'Mcp-Name: search_knowledge')
mcp_body() {
  printf '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_knowledge","arguments":{"query":"%s","topK":5},%s}}' \
    "$1" '"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}'
}
mcp_call() {
  docker exec "$app" cat /root/proof/agent.header | curl_to 8443 "$work/https.crt" -H @- "${mcp_headers[@]}" \
    --data-binary "$(mcp_body "$1")" "https://$api_host:8443/mcp" > "$work/mcp.out" \
    || { cat "$work/mcp.out"; fail "the MCP call for '$1' failed"; }
}
mcp_call "band four compensation review"
grep -q "hr/salary-bands.md" "$work/mcp.out" || { cat "$work/mcp.out"; fail "the agent granted hr did not get hr/salary-bands.md over MCP"; }
mcp_call "how long do I have to file an expense claim"
grep -q "open/handbook.md" "$work/mcp.out" || { cat "$work/mcp.out"; fail "the agent did not get open/handbook.md over MCP"; }
# The control: the same call without the token is refused as a call with no
# caller, a 401, and not for some other fault in the request.
status=$(docker run --rm "${api_net[@]}" -v "$(host_path "$work/https.crt"):/ca.crt:ro" "$curl_image" \
  --silent --max-time 30 --cacert /ca.crt -o /dev/null -w '%{http_code}' "${mcp_headers[@]}" \
  --data-binary "$(mcp_body "band four compensation review")" "https://$api_host:8443/mcp" < /dev/null) || true
[ "$status" = 401 ] || fail "the MCP call without a token got HTTP $status, not the 401 for no caller"
pass "over MCP with its token, the agent granted hr finds the salary bands and the handbook; without a token the same call is a 401"

mcp_call "where do returns go"
grep -q "formats/loading-dock.pdf" "$work/mcp.out" && grep -q "Page 2" "$work/mcp.out" \
  || { cat "$work/mcp.out"; fail "over MCP, the returns question did not find the PDF's second page"; }
mcp_call "what badge do visitors wear"
grep -q "formats/visitor-policy.docx" "$work/mcp.out" && grep -q "Visitors" "$work/mcp.out" \
  || { cat "$work/mcp.out"; fail "over MCP, the badge question did not find the Word file under its heading"; }
mcp_call "where is the spare key to the cage kept"
# The answer is JSON on the wire, where ">" arrives escaped as >.
grep -q "formats/equipment-register.xlsx" "$work/mcp.out" && grep -qE 'equipment-register\.xlsx (>|\\u003E) Keys' "$work/mcp.out" \
  || { cat "$work/mcp.out"; fail "over MCP, the key question did not find the Excel file under its Keys sheet"; }
docker exec "$app" rm -f /root/proof/agent.header
pass "over MCP, the restarted API serves the readers' passages: the PDF cited by its page, the Word file by its heading, the Excel file by its sheet"

if [ "$mode" = offline ]; then
  step "The fully local loop: a client asks PremAgentic over MCP, then a local model server"
  # The chat client's agent, whose model runs here, and its token in a file
  # only the client reads.
  in_app "$as_app $prem agents add chat-client --owner first-admin --mode acts-for-user --model local \
    && (umask 077; $prem tokens issue chat-client --days 1 > /root/proof/token.out && tail -n 1 /root/proof/token.out > /root/proof/agent-local.token; rm -f /root/proof/token.out)" \
    > "$work/chat-agent.out" 2>&1 || { cat "$work/chat-agent.out"; fail "making the chat client's agent and its token failed"; }
  # The stand-in for a local model server, which is not a model: it answers
  # from the passages it is handed. PREM_LOOP_CONTROL_REACH_OUT=1 on this
  # script makes it try one connection out, and the run must then fail below.
  reach_out=
  [ "${PREM_LOOP_CONTROL_REACH_OUT:-}" = 1 ] && reach_out=PREM_STUB_REACH_OUT=1
  docker exec -d "$app" bash -c "cd /root/proof && exec $(traced stub) env $reach_out python3 fully-local/stub-model-server.py 11434 > stub.log 2>&1"
  in_app 'timeout 20 bash -c "until (exec 3<>/dev/tcp/127.0.0.1/11434) 2>/dev/null; do sleep 0.2; done"' \
    || fail "the stub model server did not start"
  in_app "cd /root/proof && $(traced loop) python3 fully-local/loop-client.py 'how long do I have to file an expense claim' \
    --mcp https://localhost:8443/mcp --ca /etc/premagentic/https.crt --token-file agent-local.token --model http://127.0.0.1:11434" \
    > "$work/loop.out" 2>&1 || { cat "$work/loop.out"; fail "the loop client failed"; }
  grep -q '^From open/handbook.md' "$work/loop.out" \
    || { cat "$work/loop.out"; fail "the answer does not come from the handbook passage PremAgentic returned"; }
  pass "the chat client's agent (model location local) searched over MCP, and the stand-in model server answered from what it was handed"

  # The same loop with a real model server, when one is given:
  # PREM_LLAMA_SERVER names a llama.cpp release tarball for Linux and
  # PREM_LLAMA_MODEL a GGUF model file, both fetched beforehand, since nothing
  # here can fetch, and mounted read-only at /root/model-server. The stand-in
  # above is still the default and still running; the real server listens
  # beside it on 11435. The reach-out control skips it.
  real_server=
  if [ ${#model_mounts[@]} -gt 0 ]; then
    step "The fully local loop again, with a real model server: llama.cpp's llama-server on loopback"
    server_sum=$(sha256sum "$PREM_LLAMA_SERVER" | cut -c1-64)
    model_sum=$(sha256sum "$PREM_LLAMA_MODEL" | cut -c1-64)
    model_file=$(basename "$PREM_LLAMA_MODEL")
    in_app 'mkdir -p /opt/model-server && tar -xzf /root/model-server/server.tar.gz -C /opt/model-server --strip-components=1 \
      && test -x /opt/model-server/llama-server' \
      || fail "the tarball does not hold llama-server at its top folder"
    docker exec -d "$app" bash -c "cd /root && exec $(traced server) /opt/model-server/llama-server \
      --host 127.0.0.1 --port 11435 -m /root/model-server/model.gguf -c 4096 > /root/server.log 2>&1"
    # The server's own signal that the model is loaded: /health answers 200.
    started=$SECONDS
    in_app "timeout ${PREM_MODEL_WAIT_SECONDS:-180} python3 -c '
import time, urllib.request
while True:
    try:
        if urllib.request.urlopen(\"http://127.0.0.1:11435/health\", timeout=5).status == 200: break
    except Exception: pass
    time.sleep(0.5)
'" || { docker exec "$app" tail -n 40 /root/server.log; fail "llama-server did not load the model within ${PREM_MODEL_WAIT_SECONDS:-180} s"; }
    loaded=$((SECONDS - started)); started=$SECONDS
    in_app "cd /root/proof && $(traced loop-real) python3 fully-local/loop-client.py 'how long do I have to file an expense claim' \
      --mcp https://localhost:8443/mcp --ca /etc/premagentic/https.crt --token-file agent-local.token --model http://127.0.0.1:11435 --any-answer" \
      > "$work/loop-real.out" 2>&1 || { cat "$work/loop-real.out"; fail "the loop client with the real model server failed"; }
    real_server="llama.cpp's llama-server (tarball SHA-256 $server_sum) with $model_file (SHA-256 $model_sum)"
    pass "the same agent searched over MCP, and $real_server answered from the passages: loaded in $loaded s, answered in $((SECONDS - started)) s"
    printf '      its answer, as it began: %s\n' "$(tr '\n' ' ' < "$work/loop-real.out" | cut -c1-300)"
  fi
  docker exec "$app" rm -f /root/proof/agent-local.token

  step "Every connect() of both ingests, both starts of the API, the model server and the client, under strace"
  # Allowed for each: the ports named for it on loopback (127.0.0.1 or ::1);
  # local sockets, which the C library tries for name lookups; and the C
  # library's address-selection probes when it resolves localhost, a UDP
  # socket pointed at each loopback address on port 0 that sends nothing (a
  # TCP connect to port 0 cannot succeed, so one that returns 0 is that probe),
  # with the AF_UNSPEC connect that dissolves it. Anything else fails, and so
  # does a trace without the connects named as required, which would mean
  # strace saw nothing. The model server may connect to nothing at all.
  loopback='("127\.0\.0\.1"|"::1")'
  # check_trace <name> <ports it may connect to, as a regex alternation> <ports it must be seen connecting to>
  check_trace() {
    local name=$1 allowed=$2 required=$3 port seen local_sockets probes other counts=
    docker exec "$app" cat "/root/trace/$name.trace" > "$work/$name.trace" || fail "the $name trace was not written"
    for port in $required; do
      seen=$(grep -cE "connect\(.*htons\($port\).*$loopback" "$work/$name.trace" || true)
      [ "$seen" -gt 0 ] || { head -20 "$work/$name.trace"; fail "the $name trace shows no connect to port $port: strace saw nothing, so it proves nothing"; }
      counts="$counts$seen to port $port, "
    done
    local_sockets=$(grep -c 'connect(.*AF_UNIX' "$work/$name.trace" || true)
    probes=$(grep -E "connect\(.*htons\(0\).*$loopback.*\) = 0$" "$work/$name.trace" | grep -c . || true)
    other=$(grep 'connect(' "$work/$name.trace" | grep -v 'AF_UNIX' | grep -v 'sa_family=AF_UNSPEC' \
      | grep -vE "htons\(($allowed)\).*$loopback" | grep -vE "connect\(.*htons\(0\).*$loopback.*\) = 0$" || true)
    # The one failure the reach-out control exists for, named as the control expects it.
    if [ "$name" = stub ] && grep -E 'htons\(443\)' <<< "$other" | grep -qF '"1.1.1.1"'; then
      printf '%s\n' "$other" | head -20; fail "the stand-in's connect to 1.1.1.1:443"
    fi
    [ -z "$other" ] || { printf '%s\n' "$other" | head -20; fail "the $name process connected to something it may not"; }
    printf '      %s: %s%s to local sockets, %s address-selection probes on loopback port 0, 0 to anything else\n' \
      "$name" "$counts" "$local_sockets" "$probes"
  }
  check_trace ingest 5432 5432
  check_trace readers 5432 5432
  check_trace api-first 5432 5432
  check_trace api 5432 5432
  check_trace loop '8443|11434' '8443 11434'
  check_trace stub none ''
  if [ -n "$real_server" ]; then
    check_trace loop-real '8443|11435' '8443 11435'
    check_trace server none ''
  fi
  pass "under strace, both ingests (the second through the PDF, Word and Excel readers) and both starts of the API connected to nothing but the database, the client to nothing but the API and the model server, and the model server to nothing$([ -n "$real_server" ] && printf ', the stand-in and llama-server alike')"
  printf '\nOFFLINE PROOF PASSED on %s\nSHA256SUMS: %s\n' "$name" "$sums_line"
  exit 0
fi

if [ -n "$no_upgrade" ]; then
  printf '\nCLEAN INSTALL PASSED on %s, WITHOUT THE UPGRADE: there is no earlier release to upgrade from\nSHA256SUMS: %s\n' "$name" "$sums_line"
  exit 0
fi

step "Upgrade from $older_name to $name, archive to archive, on an install that holds data"
# Its own folder, database, roles, credentials, first administrator and HTTPS
# port, so it shares nothing with the install above but the two containers. Its
# credentials live outside the folder the upgrade swaps, as INSTALL.txt's do.
up=/opt/upgrade
up_credentials=/etc/premagentic-up
up_setup="$up/bin/prem setup --admin-connection-file /root/proof/admin.credentials --credentials-dir $up_credentials \
  --database premagentic_up --owner-role up_owner --app-role up_app --admin-user up-admin \
  --admin-password-file /root/proof/first-admin.password --host-name $app --https-port 8444"
as_up="export PREM_CREDENTIALS_FILE=$up_credentials/app.credentials; cd /root;"
# The API program of whichever release the folder holds: bin/ since the three
# programs share one runtime, api/ in a release from before.
up_api() { docker exec "$app" bash -c "if [ -x $up/bin/Premagentic.Api ]; then echo $up/bin/Premagentic.Api; else echo $up/api/Premagentic.Api; fi"; }
up_start() {
  local api
  api=$(up_api)
  # The log of the start before goes first, so the wait below reads this start's line.
  docker exec "$app" rm -f /root/up-api.log
  docker exec -d "$app" bash -c "cd /root && exec env PREM_CREDENTIALS_FILE=$up_credentials/app.credentials $api > /root/up-api.log 2>&1"
  wait_for_api "$app" /root/up-api.log 8444
  docker exec "$app" bash -c 'exec 3<>/dev/tcp/127.0.0.1/8444' >/dev/null 2>&1 \
    || { docker exec "$app" cat /root/up-api.log; fail "the $1 API wrote its listening line but does not answer on 8444"; }
  printf '      the %s API, %s, listens on 8444 after %s s\n' "$1" "$api" "$api_waited"
}
up_stop() { stop_api "$(up_api)" 8444 "$1"; }
up_health() {
  docker cp "$app:$up_credentials/https.crt" "$(host_path "$work/up-https.crt")"
  curl_to 8444 "$work/up-https.crt" "https://$app:8444/health" < /dev/null > "$work/up-health.out" \
    || fail "/health of the $1 API over HTTPS failed"
  grep -q '"status":"ok"' "$work/up-health.out" || { cat "$work/up-health.out"; fail "/health of the $1 API did not answer ok"; }
}
up_search() {
  in_app "$as_up $up/bin/prem search 'band four compensation review' --as group:hr" > "$work/up-hr.out" 2>&1
  grep -q "hr/salary-bands.md" "$work/up-hr.out" || { cat "$work/up-hr.out"; fail "$1: hr did not reach hr/salary-bands.md"; }
  in_app "$as_up $up/bin/prem search 'band four compensation review' --as group:engineering" > "$work/up-eng.out" 2>&1
  if grep -q "hr/salary-bands.md" "$work/up-eng.out"; then cat "$work/up-eng.out"; fail "$1: engineering reached hr/salary-bands.md"; fi
}
# up_version <expected> <what>: the folder's prem names the release expected.
up_version() {
  in_app "$up/bin/prem --version" > "$work/up-version.out" 2>&1 || { cat "$work/up-version.out"; fail "$2: prem --version failed"; }
  grep -qF -- "$1" "$work/up-version.out" || { cat "$work/up-version.out"; fail "$2: $up/bin/prem is not version $1"; }
}

in_app "mkdir -p $up && tar -xzf /root/$older_name -C $up --strip-components=1" || fail "unpacking the older archive failed"
up_version "$older_version" "the older release"
in_app "$up_setup" > "$work/up-setup-old.out" 2>&1 || { cat "$work/up-setup-old.out"; fail "setup of the older release failed"; }
in_app "$as_up $up/bin/prem groups add hr && $up/bin/prem groups add engineering \
  && $up/bin/prem ingest $up/sample-docs/open --public --prefix open && $up/bin/prem ingest $up/sample-docs/hr --principals group:hr --prefix hr" \
  > "$work/up-ingest-old.out" 2>&1 || { cat "$work/up-ingest-old.out"; fail "ingest into the older release failed"; }
up_search "older release"
up_start "older"
up_health "older"
pass "the older release, $older_version, is unpacked from its archive, holds documents, searches with the gate, and answers over HTTPS"
printf '      migrations the older release applied: %s\n' \
  "$(docker exec "$db" psql -U postgres -d premagentic_up -tAc "SELECT string_agg(to_char(version, 'FM0000'), ' ' ORDER BY version) FROM prem_config.schema_migration")"

# The upgrade: back up the configuration, stop, set the older folder aside and
# unpack the new archive in its place, migrate as the owner, run setup again for
# anything the new release adds, start, check.
docker exec "$db" pg_dump -U postgres -d premagentic_up --schema=prem_config -Fc -f /tmp/up-config.dump \
  || fail "the configuration backup failed"
up_stop "older"
in_app "mv $up $up.old && mkdir -p $up && tar -xzf /root/$name -C $up --strip-components=1" || fail "unpacking the new archive in place of the older release failed"
up_version "$version" "after the upgrade"
in_app "PREM_CREDENTIALS_FILE=$up_credentials/owner.credentials $up/bin/prem migrate" > "$work/up-migrate.out" 2>&1 \
  || { cat "$work/up-migrate.out"; fail "migrating as the owner failed"; }
# An older release from before the search role made none, and the new setup
# makes one of this install's own; one that made it already keeps it, since
# setup refuses to point its credentials file at another role.
up_search_role="--search-role up_search"
docker exec "$app" test -f "$up_credentials/search.credentials" && up_search_role=
in_app "$up_setup $up_search_role" > "$work/up-setup-new.out" 2>&1 || { cat "$work/up-setup-new.out"; fail "setup of the new release after the upgrade failed"; }
grep -q "\[ok\]      administrator .*none was made" "$work/up-setup-new.out" || { cat "$work/up-setup-new.out"; fail "the first administrator did not survive the upgrade"; }
no_gss_noise "$work/up-setup-new.out" "setup after the upgrade"
up_search "after the upgrade"
up_start "new"
up_health "new"
http_search 8444 "$work/up-https.crt" up-admin "$proof/first-admin.password" \
  "how long do I have to file an expense claim" open/handbook.md present "after the upgrade"
http_search 8444 "$work/up-https.crt" up-admin "$proof/first-admin.password" \
  "band four compensation review" hr/salary-bands.md absent "after the upgrade"
pass "upgraded to $version from its archive: migrated as the owner, kept the administrator and the documents, and serves them over HTTPS to a signed-in person, with the gate"
printf '      migrate as the owner: %s\n' "$(tr '\n' ' ' < "$work/up-migrate.out")"

# The rollback: the older folder back, and the configuration from the backup.
# The index is disposable: the older release recreates it and ingest refills it.
up_stop "new"
in_app "rm -rf $up && mv $up.old $up" || fail "putting the older folder back failed"
up_version "$older_version" "after the rollback"
docker exec "$db" psql -U postgres -d premagentic_up -v ON_ERROR_STOP=1 -q -c "DROP SCHEMA prem_index CASCADE; DROP SCHEMA prem_config CASCADE;" \
  || fail "dropping the new release's schemas failed"
docker exec "$db" pg_restore -U postgres -d premagentic_up --exit-on-error /tmp/up-config.dump || fail "restoring the configuration backup failed"
in_app "$up_setup" > "$work/up-setup-rollback.out" 2>&1 || { cat "$work/up-setup-rollback.out"; fail "setup of the older release after the rollback failed"; }
grep -q "\[ok\]      administrator .*none was made" "$work/up-setup-rollback.out" || { cat "$work/up-setup-rollback.out"; fail "the first administrator did not survive the rollback"; }
in_app "$as_up $up/bin/prem ingest $up/sample-docs/open --prefix open && $up/bin/prem ingest $up/sample-docs/hr --prefix hr" \
  > "$work/up-ingest-rollback.out" 2>&1 || { cat "$work/up-ingest-rollback.out"; fail "rebuilding the index after the rollback failed"; }
up_search "after the rollback"
up_start "older"
up_health "older"
up_stop "older"
pass "rolled back: the older release's folder, the configuration from the backup and the index rebuilt; it searches and answers over HTTPS"

printf '\nCLEAN INSTALL PASSED on %s (upgrade from %s)\nSHA256SUMS: %s\nSHA256SUMS of the older release: %s\n' "$name" "$older_name" "$sums_line" "$older_sums_line"
