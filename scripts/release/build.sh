#!/usr/bin/env bash
# Builds the release archives: one self-contained archive per platform that
# needs no .NET install, no build and no network to install, and one SHA256SUMS.
#
#   scripts/release/build.sh --version X.Y.Z --out <folder> [--rid linux-x64] [--rid win-x64] [--model-dir <folder>]
#                            [--vc-runtime <VC_redist.x64.exe>]
#
#   --version      the version to stamp. It is NOT read from Directory.Build.props:
#                  that file stays at 0.1.0 until the first public version is chosen.
#   --out          where the archives and SHA256SUMS are written.
#   --rid          a platform to build; the default is linux-x64 and win-x64.
#   --model-dir    the embedding model's folder; the default is models/minilm, which
#                  scripts/download-model.sh fills (and this script runs it when the
#                  default folder is empty). Whatever folder it is, the model is
#                  refused unless model.onnx and vocab.txt hash to the SHA-256 values
#                  pinned in scripts/download-model.sh.
#   --vc-runtime   the Microsoft Visual C++ Redistributable the Windows archive takes
#                  its four runtime DLLs from; the default is vc-runtime/VC_redist.x64.exe,
#                  which scripts/download-vc-runtime.sh fills (and this script runs it
#                  when that file is missing and win-x64 is built). Whatever file it is,
#                  it is refused unless it is the size and SHA-256 pinned in that script,
#                  and so is each of the four files taken out of it.
#   --extensions-dir  the folder of first-party extensions, relative to the repository
#                  root (default: extensions). When it is absent the archives carry no
#                  extensions folder, and this script says so.
#   --work         a working folder (default: a folder inside --out, removed on exit).
#
# What it does: takes a snapshot of HEAD (git archive, so an uncommitted change can
# never reach an archive and the same commit builds the same bytes wherever it is
# checked out), publishes the command line, the API and the MCP bridge self-contained
# for each platform and lays the three into one bin/ folder with one runtime between
# them (ReleaseTool.cs merge says how a file they carry differently is decided and
# refuses the build otherwise), lays out the archive (the notices, a licenses/ folder with the
# text of every package and runtime pack the publish deploys, the embedding model with
# its notice, the starter profile, the sample documents, the systemd unit on Linux,
# on Windows the four Visual C++ runtime files ONNX Runtime needs, taken unmodified
# out of the pinned Microsoft redistributable (ReleaseTool.cs vcruntime) with their
# own license file, the first-party extensions, INSTALL.txt), writes it as a .tar.gz (Linux) or .zip
# (Windows) with sorted entries, fixed times and modes, and checks the finished archive
# against scripts/release/verify-archive.sh. The bundled PostgreSQL is not in these
# archives: setup uses a PostgreSQL 14 or later that already exists.
#
# The archive times are the commit's time (or SOURCE_DATE_EPOCH when it is set).
#
# The .NET SDK: exactly the version the commit's global.json pins, and no other. The
# SDK decides the .NET runtime a self-contained program carries, so the runtime in an
# archive is chosen by that file; a runtime security patch is taken by raising the
# version there, on purpose. licenses/PACKAGES.txt names the runtime it carries.
#
# Packages: every restore here is in locked mode against the commit's lock files,
# the plain ones for the extensions and the per-runtime ones for the programs, so a
# package id or version that moved without its lock file refuses the build.
#
# Needs: bash, git, the .NET SDK global.json names, GNU tar, gzip, unzip, sha256sum. Works from bash on
# Linux and from Git Bash on Windows. It publishes; it does not push, tag or release.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)

fail() { printf 'refused: %s\n' "$*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }
usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//' >&2; exit 2; }
host_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

version= out= work= model_dir= vc_runtime= extensions_dir=extensions rids=()
while [ $# -gt 0 ]; do
  case $1 in
    --version) version=${2:?--version needs a value}; shift 2 ;;
    --out) out=${2:?--out needs a value}; shift 2 ;;
    --rid) rids+=("${2:?--rid needs a value}"); shift 2 ;;
    --model-dir) model_dir=${2:?--model-dir needs a value}; shift 2 ;;
    --vc-runtime) vc_runtime=${2:?--vc-runtime needs a value}; shift 2 ;;
    --extensions-dir) extensions_dir=${2:?--extensions-dir needs a value}; shift 2 ;;
    --work) work=${2:?--work needs a value}; shift 2 ;;
    -h | --help) usage ;;
    *) printf 'unknown option: %s\n' "$1" >&2; usage ;;
  esac
done
[ -n "$version" ] || { printf 'give --version\n' >&2; usage; }
[ -n "$out" ] || { printf 'give --out\n' >&2; usage; }
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || fail "--version '$version' is not X.Y.Z (a pre-release suffix such as -rc.1 is allowed)"
[ ${#rids[@]} -gt 0 ] || rids=(linux-x64 win-x64)
for rid in "${rids[@]}"; do
  case $rid in linux-x64 | win-x64) ;; *) fail "--rid $rid: this script builds linux-x64 and win-x64" ;; esac
done

mkdir -p "$out"
out=$(cd "$out" && pwd)
[ -n "$work" ] || work="$out/.work-$$"
mkdir -p "$work"
work=$(cd "$work" && pwd)
# Out of the snapshot first: a folder that is a process's current directory cannot
# be removed on Windows.
cleanup() { cd "$out" || cd /; dotnet build-server shutdown >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

commit=$(git -C "$repo" rev-parse HEAD)
short=${commit:0:10}
epoch=${SOURCE_DATE_EPOCH:-$(git -C "$repo" log -1 --format=%ct)}
if ! git -C "$repo" diff --quiet HEAD; then
  printf 'warning: the working tree differs from HEAD. The archives are built from HEAD (%s) only.\n' "$short" >&2
fi

# ---- the snapshot --------------------------------------------------------------------
# Everything the archive carries comes from here, never from the working tree.
step "Snapshot of $short"
src=$work/src
mkdir -p "$src"
git -C "$repo" -c core.autocrlf=false archive --format=tar HEAD | tar -x -C "$src"
[ -f "$src/Premagentic.slnx" ] || fail "the snapshot of HEAD has no Premagentic.slnx"
release=$src/scripts/release
[ -f "$release/licenses/index.txt" ] || fail "the snapshot of HEAD has no scripts/release/licenses; commit it first"

# ---- the SDK: the one the snapshot's global.json pins ------------------------------
# dotnet picks its SDK from the global.json above the folder it runs in, not above the
# project it is given, so every dotnet command below runs from the snapshot.
[ -f "$src/global.json" ] || fail "the snapshot of HEAD has no global.json; a release is built with the SDK it pins"
pinned_sdk=$(tr -d '\r' < "$src/global.json" | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)
[[ $pinned_sdk =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "could not read the SDK version from global.json"
[ -z "$model_dir" ] || [ ! -d "$model_dir" ] || model_dir=$(cd "$model_dir" && pwd)
[ -z "$vc_runtime" ] || [ ! -f "$vc_runtime" ] || vc_runtime=$(cd "$(dirname "$vc_runtime")" && pwd)/$(basename "$vc_runtime")
cd "$src"
# The last line that is a version: a first dotnet command on a fresh machine may
# print a welcome text before it.
said=$(DOTNET_NOLOGO=1 dotnet --version 2>&1 | tr -d '\r') || true
sdk=$(grep -E '^[0-9]+\.[0-9]+\.[0-9]+' <<< "$said" | tail -n 1 || true)
[ -n "$sdk" ] || fail "dotnet --version gave no version; it said: $(tr '\n' ' ' <<< "$said")"
[ "$sdk" = "$pinned_sdk" ] || fail "global.json pins the .NET SDK $pinned_sdk and this machine resolved $sdk. A release is built with exactly the pinned SDK, because the SDK decides the .NET runtime the archives carry. Install $pinned_sdk, or change global.json on purpose."
printf 'sdk: %s, the version global.json pins\n' "$sdk"

# ---- the embedding model: the pins, and the refusal --------------------------------
# Read from the snapshot, with carriage returns dropped in case a file has them.
pins=$(tr -d '\r' < "$src/scripts/download-model.sh")
pin() { sed -n "s/^$1=\"\\(.*\\)\"\$/\\1/p" <<< "$pins"; }
model_sha=$(pin minilm_model_sha256)
vocab_sha=$(pin minilm_vocab_sha256)
model_rev=$(sed -n 's#^minilm_base=".*/resolve/\([0-9a-f]\{40\}\)"$#\1#p' <<< "$pins")
[[ $model_sha =~ ^[0-9a-f]{64}$ && $vocab_sha =~ ^[0-9a-f]{64}$ && $model_rev =~ ^[0-9a-f]{40}$ ]] \
  || fail "could not read the model pins from scripts/download-model.sh"
for value in "$model_sha" "$vocab_sha" "$model_rev"; do
  grep -q "$value" "$src/scripts/download-model.ps1" \
    || fail "scripts/download-model.ps1 does not pin $value; the two download scripts must pin the same files"
done

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }
check_model() { # folder
  local file expected actual
  for file in model.onnx:$model_sha vocab.txt:$vocab_sha; do
    expected=${file#*:}
    file=${file%%:*}
    [ -f "$1/$file" ] || fail "$1/$file is missing"
    actual=$(sha256_of "$1/$file")
    [ "$actual" = "$expected" ] \
      || fail "the model file $1/$file hashes to $actual and the pinned SHA-256 is $expected"
  done
}
if [ -z "$model_dir" ]; then
  model_dir=$repo/models/minilm
  if [ ! -f "$model_dir/model.onnx" ] || [ ! -f "$model_dir/vocab.txt" ]; then
    step "Fetching the pinned embedding model"
    MODELS_DIR="$repo/models" sh "$src/scripts/download-model.sh"
  fi
fi
check_model "$model_dir"
printf 'model: %s, revision %s, both files match the pins\n' "$model_dir" "${model_rev:0:12}"

# The packages folder is looked up after the first restore, which is what creates
# it on a fresh machine; packages-folder.sh says which path it tried when it refuses.
packages=

tool() { dotnet run "$(host_path "$here/ReleaseTool.cs")" -- "$@"; }
# PathMap: the compiler names a C# file-local type after a hash of its source file's
# path, so without it a build from a different folder gives a different assembly.
path_map=-p:PathMap="$(host_path "$src")=/_/"
publish_args=(-c Release --self-contained true -p:Version="$version" -p:SourceRevisionId="$short"
  -p:ContinuousIntegrationBuild=true "$path_map" -p:DebugType=none -p:DebugSymbols=false
  "-warnaserror:NU1901,NU1902,NU1903,NU1904" --nologo -v q)

# ---- the Visual C++ runtime, for the Windows archive only ---------------------------
# ONNX Runtime's Windows library imports it and Windows does not carry it. The four
# files come out of the pinned Microsoft redistributable, once, before anything is
# published; the pins are the snapshot's, and the tool refuses a package or a file
# that does not match them.
vc_runtime_files=
if [[ " ${rids[*]} " == *" win-x64 "* ]]; then
  vc_pins=$src/scripts/download-vc-runtime.sh
  [ -f "$vc_pins" ] || fail "the snapshot of HEAD has no scripts/download-vc-runtime.sh"
  vc_pin() { tr -d '\r' < "$vc_pins" | sed -n "s/^$1=\"\\(.*\\)\"\$/\\1/p"; }
  if [ -z "$vc_runtime" ]; then
    vc_runtime=$repo/vc-runtime/VC_redist.x64.exe
    if [ ! -f "$vc_runtime" ]; then
      step "Fetching the pinned Visual C++ Redistributable"
      VC_RUNTIME_DIR="$repo/vc-runtime" sh "$vc_pins"
    fi
  fi
  [ -f "$vc_runtime" ] || fail "--vc-runtime $vc_runtime: no such file"
  step "The Visual C++ runtime files"
  vc_runtime_files=$work/vc-runtime
  tool vcruntime --package "$(host_path "$vc_runtime")" --pins "$(host_path "$vc_pins")" --out "$(host_path "$vc_runtime_files")"
fi

# The first-party extensions, built once (they are managed code for every platform)
# into a work folder. The build drops files beside each one that it must not ship,
# such as a copy of Premagentic.Core; the archive takes only what its extension.json
# names, and the build folder's deps.json tells the license step what it carries.
built_extensions=()
extensions=("$src/$extensions_dir"/*/*.csproj)
if [ -f "${extensions[0]}" ]; then
  step "Building the extensions"
  for project in "${extensions[@]}"; do
    name=$(basename "$(dirname "$project")")
    # The same -p:Version as the programs. It flows into Premagentic.Core through the
    # project reference, which is the point: the reader then asks for exactly the
    # host's version of the contracts' library, and its manifest names the release.
    # Built without it, a reader asks for the version in Directory.Build.props, and a
    # release numbered below that could not load it.
    dotnet build "$(host_path "$project")" -c Release -p:Version="$version" -p:SourceRevisionId="$short" \
      -p:ContinuousIntegrationBuild=true "$path_map" -p:DebugType=none -p:DebugSymbols=false -p:RestoreLockedMode=true --nologo -v q \
      -o "$(host_path "$work/extensions/$name")" > "$work/extension-$name.log" 2>&1 \
      || { cat "$work/extension-$name.log" >&2; fail "building the extension $name failed"; }
    [ -f "$work/extensions/$name/extension.json" ] || fail "extension $name was built without an extension.json"
    built_extensions+=("$name")
    printf 'built extension %s\n' "$name"
  done
else
  printf 'extensions: none, %s/ has no extension projects on this commit; the archives have no extensions folder\n' "$extensions_dir"
fi

archives=()
for rid in "${rids[@]}"; do
  top=premagentic-$version-$rid
  stage=$work/$rid/stage
  mkdir -p "$stage"
  step "Publishing $rid"
  # Each program self-contained on its own first, then the three laid into bin/ with
  # one runtime between them: the API's set of files is the others' superset, and
  # each program keeps its own apphost, deps.json and runtimeconfig.json beside it.
  # Each restores in locked mode against its runtime's own lock file,
  # packages.<rid>.lock.json, which scripts/release/lock-files.sh writes: a publish
  # for a runtime adds the ICU package, so its graph is not the plain one.
  publishes=()
  for project in Cli Api McpServer; do
    log=$work/publish-$rid-$project.log
    dotnet publish "$(host_path "$src/src/Premagentic.$project/Premagentic.$project.csproj")" -r "$rid" "${publish_args[@]}" \
      -p:NuGetLockFilePath="packages.$rid.lock.json" -p:RestoreLockedMode=true \
      -o "$(host_path "$work/$rid/publish/$project")" > "$log" 2>&1 || { cat "$log" >&2; fail "publishing Premagentic.$project for $rid failed"; }
    publishes+=("$(host_path "$work/$rid/publish/$project")")
    printf 'published %s\n' "Premagentic.$project"
  done
  tool merge --into "$(host_path "$stage/bin")" --from "${publishes[@]}"
  rm -rf "$work/$rid/publish"

  step "Laying out $top"
  [ -n "$packages" ] || packages=$("$here/packages-folder.sh")
  cp "$src/LICENSE" "$src/NOTICE" "$src/THIRD-PARTY-NOTICES.md" "$stage/"
  deps_folders=("$(host_path "$stage/bin")")
  for name in "${built_extensions[@]}"; do deps_folders+=("$(host_path "$work/extensions/$name")"); done
  tool licenses --publish "${deps_folders[@]}" --sdk "$sdk" \
    --packages "$(host_path "$packages")" --texts "$(host_path "$release/licenses")" --out "$(host_path "$stage/licenses")"
  for name in "${built_extensions[@]}"; do
    tool extension --built "$(host_path "$work/extensions/$name")" --out "$(host_path "$stage/extensions/$name")"
  done

  mkdir -p "$stage/models/minilm"
  cp "$model_dir/model.onnx" "$model_dir/vocab.txt" "$stage/models/minilm/"
  printf '{ "name": "local:all-MiniLM-L6-v2", "pooling": "mean", "dimensions": 384, "revision": "%s" }\n' "$model_rev" \
    > "$stage/models/minilm/prem-model.json"
  sed "s/@REVISION@/$model_rev/g" "$release/model-notice.txt" > "$stage/models/minilm/NOTICE.txt"
  cp "$release/model-license-apache-2.0.txt" "$stage/models/minilm/LICENSE-Apache-2.0.txt"

  mkdir -p "$stage/samples/profiles"
  cp -r "$src/samples/profiles/starter" "$stage/samples/profiles/starter"
  cp -r "$src/sample-docs" "$stage/sample-docs"

  exec_args=()
  case $rid in
    linux-x64)
      mkdir -p "$stage/deploy/systemd"
      cp "$src/deploy/systemd/premagentic-api.service" "$stage/deploy/systemd/"
      installer=$release/install-linux.txt
      exec_args=(--exec bin/prem --exec bin/Premagentic.Api --exec bin/Premagentic.McpServer)
      ;;
    win-x64)
      installer=$release/install-windows.txt
      # Beside the programs, where Windows looks first for onnxruntime.dll's imports.
      for dll in "$vc_runtime_files"/*.dll; do
        [ ! -e "$stage/bin/$(basename "$dll")" ] \
          || fail "the publish already carries bin/$(basename "$dll"); the Visual C++ runtime comes only from the pinned package"
        cp "$dll" "$stage/bin/"
      done
      sed -e "s|@VERSION@|$(vc_pin vc_redist_version)|g" -e "s|@URL@|$(vc_pin vc_redist_url)|g" \
        -e "s|@SHA256@|$(vc_pin vc_redist_sha256)|g" "$release/vc-runtime-license.txt" \
        > "$stage/licenses/Microsoft-Visual-CPP-Runtime.txt"
      ;;
  esac
  # An archive with no extensions leaves out what INSTALL.txt says about them.
  if [ ${#built_extensions[@]} -gt 0 ]; then with=with-extensions; else with=no-extensions; fi
  "$here/install-text.sh" "$installer" "$version" "$with" > "$stage/INSTALL.txt"

  step "Archiving $top"
  case $rid in
    linux-x64)
      archive=$top.tar.gz
      tool pack --dir "$(host_path "$stage")" --top "$top" --tar "$(host_path "$work/$rid/$top.tar")" --mtime "$epoch" "${exec_args[@]}"
      gzip -n -9 -c "$work/$rid/$top.tar" > "$out/$archive"
      ;;
    win-x64)
      archive=$top.zip
      tool pack --dir "$(host_path "$stage")" --top "$top" --zip "$(host_path "$out/$archive")" --mtime "$epoch"
      ;;
  esac
  "$here/verify-archive.sh" "$out/$archive" "$rid"
  archives+=("$archive")
  # The staged tree is the largest thing here; drop it before the next platform.
  printf 'sizes before the archive is written (uncompressed): '
  for folder in bin models; do printf '%s %s KB, ' "$folder" "$(du -sk "$stage/$folder" | cut -f1)"; done
  printf 'whole tree %s KB\n' "$(du -sk "$stage" | cut -f1)"
  rm -rf "$stage" "$work/$rid/$top.tar"
done

( cd "$out" && sha256sum "${archives[@]}" | sed 's/ \*/  /' | LC_ALL=C sort -k2 > SHA256SUMS )
step "Done: commit $short, version $version"
for archive in "${archives[@]}"; do
  printf '%s  %s bytes\n' "$archive" "$(wc -c < "$out/$archive" | tr -d ' ')"
done
cat "$out/SHA256SUMS"
