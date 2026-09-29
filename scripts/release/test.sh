#!/usr/bin/env bash
# The release build's refusals, each shown to fire, each beside a control that
# passes, so a check that can no longer fail is caught here:
#
#   1. a model file that does not match the pinned SHA-256 is refused by build.sh
#      before anything is published;
#   2. a package with no license text, in the package or in licenses/, is refused
#      by the license step, and the same package is accepted with its text;
#   3. an archive that lacks a required file, whose LICENSE or model license file is
#      not the text it must be, whose licenses/PACKAGES.txt does not
#      name the .NET runtime it carries, that carries a second runtime, a program
#      folder besides bin/ or an import library, or whose INSTALL.txt does not say how to allow the
#      readers it ships, is refused by verify-archive.sh, and the untouched archive
#      passes;
#   4. a packages folder that does not exist is refused by packages-folder.sh,
#      naming the path it tried, and the real one is found;
#   5. an extension is shipped as its manifest lists it: a listed file that is
#      missing or changed is refused, and a complete one ships only what it lists;
#   6. a reader's package without a license text is refused, and with its
#      committed text it is covered;
#   7. an SDK other than the one global.json pins is refused by build.sh before
#      anything is published, and the pinned one is accepted;
#   8. the license step's PACKAGES.txt begins with the .NET runtime version a
#      publish deploys and the SDK it came from;
#   9. the three programs' publishes are laid into one folder: a file they carry
#      alike is kept once, the higher recorded file version wins where copies
#      differ and the runtime pack's copy where the versions tie, and two package
#      copies of one version, a copy with no version, or a file a program needs
#      and none carries are refused;
#  10. INSTALL.txt says how to allow the readers exactly when the archive has them;
#  11. the Visual C++ runtime files come out of the pinned redistributable as exactly
#      the four pinned files, checked here with sha256sum: a package with one byte
#      changed or one byte short, a file whose pin is off by one digit, pins that
#      name a fifth file, and an output folder that is not empty are each refused.
#      With --archives, a Windows archive missing one of the four, carrying another
#      runtime file or a changed one, and a Linux archive carrying one, are refused;
#  12. a zip packed with a time a zip cannot hold (before 1980) is refused by name,
#      on any host's clock, and one at 1980-01-01 is packed;
#  13. a runtime pack's license keeps the name it has on disk, whatever the host's
#      file system; and (in 9) two differing copies, one recorded as 0.0.0.0, are
#      refused rather than decided on that version;
#  14. the Windows Sandbox proof's own refusals (prove-windows-sandbox.sh self-test),
#      with stand-ins for the sandbox's programs: an archive that does not match its
#      SHA256SUMS line, a PostgreSQL zip that is not the pinned one, a start while a
#      sandbox client window is left, a stop that does not find exactly one sandbox,
#      returns before its VM is gone or passes one that never goes, and a waiter
#      that takes a client window for a running proof, each beside a control.
#
#   scripts/release/test.sh --model-dir <folder> [--archives <folder>] [--vc-runtime <VC_redist.x64.exe>]
#
# --model-dir holds the pinned model.onnx and vocab.txt. --archives is the --out
# folder of a finished build.sh run; without it the third test is skipped and
# says so. --vc-runtime is the pinned redistributable (the file build.sh used);
# without it the eleventh test is skipped and says so. Needs what build.sh needs. It changes nothing outside its own temporary
# folder, which it makes under --archives when given (a temp cleaner that goes by
# file dates would otherwise remove an unpacked archive part way through).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
host_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

model_dir= archives= vc_runtime=
while [ $# -gt 0 ]; do
  case $1 in
    --model-dir) model_dir=${2:?}; shift 2 ;;
    --archives) archives=${2:?}; shift 2 ;;
    --vc-runtime) vc_runtime=${2:?}; shift 2 ;;
    *) printf 'usage: test.sh --model-dir <folder> [--archives <folder>] [--vc-runtime <file>]\n' >&2; exit 2 ;;
  esac
done
[ -n "$model_dir" ] || { printf 'give --model-dir\n' >&2; exit 2; }

base=${archives:-$repo}
tmp=$(mktemp -d "$base/.release-test-XXXXXX")
trap 'rm -rf "$tmp"' EXIT
failures=0
pass() { printf 'PASS  %s\n' "$*"; }
fail() { printf 'FAIL  %s\n' "$*" >&2; failures=$((failures + 1)); }

# expect_refused <what> <text the refusal must contain> <command...>
expect_refused() {
  local what=$1 text=$2; shift 2
  local status=0
  "$@" > "$tmp/out" 2>&1 || status=$?
  if [ "$status" = 0 ]; then fail "$what: it was accepted"; cat "$tmp/out" >&2
  elif ! grep -qF -- "$text" "$tmp/out"; then fail "$what: refused, but not with '$text'"; cat "$tmp/out" >&2
  else pass "$what: refused with '$(grep -m 1 -F -- "$text" "$tmp/out")'"
  fi
}

# ---- 1. the model hash -----------------------------------------------------------
mkdir -p "$tmp/model"
cp "$model_dir/model.onnx" "$model_dir/vocab.txt" "$tmp/model/"
printf 'x' >> "$tmp/model/vocab.txt"
expect_refused "a vocabulary one byte longer than the pinned one" "vocab.txt hashes to" \
  "$here/build.sh" --version 0.0.1 --out "$tmp/out-model" --rid linux-x64 --model-dir "$tmp/model"
# Refused by build.sh's own check, before anything is published: verify-archive.sh
# also checks the model, and must not be what catches it here.
if grep -q '^== Publishing' "$tmp/out"; then fail "the model was not refused before publishing"; fi
[ ! -e "$tmp/out-model/premagentic-0.0.1-linux-x64.tar.gz" ] || fail "an archive was written although the model was refused"
# The same run is check 7's control: this machine's SDK is the pinned one, and the
# SDK check let it through to the model.
pinned_sdk=$(tr -d '\r' < "$repo/global.json" | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)
if grep -qx "sdk: $pinned_sdk, the version global.json pins" "$tmp/out"; then pass "control: the pinned SDK $pinned_sdk is accepted"
else fail "control: the pinned SDK $pinned_sdk was not accepted"; cat "$tmp/out" >&2; fi

# ---- 7. an SDK other than the pinned one ---------------------------------------------
# A stand-in dotnet ahead of the real one on PATH that names another version: the
# check that fires is build.sh's own, before anything is published.
mkdir -p "$tmp/other-sdk"
printf '#!/bin/sh\nif [ "$1" = --version ]; then echo 10.0.999; fi\nexit 0\n' > "$tmp/other-sdk/dotnet"
chmod +x "$tmp/other-sdk/dotnet"
expect_refused "an SDK other than the one global.json pins" "and this machine resolved 10.0.999" \
  env PATH="$tmp/other-sdk:$PATH" "$here/build.sh" --version 0.0.1 --out "$tmp/out-sdk" --rid linux-x64 --model-dir "$model_dir"
if grep -q '^== Publishing' "$tmp/out"; then fail "the SDK was not refused before publishing"; fi

# ---- 2. a missing license text ---------------------------------------------------
# A publish folder that deploys one package, Npgsql, which ships no license file of
# its own: the committed text covers it, and without that text it is refused.
packages=$("$here/packages-folder.sh")
version=$(sed -n 's/.*PackageReference Include="Npgsql" Version="\([^"]*\)".*/\1/p' "$repo/src/Premagentic.Core/Premagentic.Core.csproj")
[ -f "$packages/npgsql/$version/npgsql.nuspec" ] || { printf 'Npgsql %s is not restored; run dotnet restore first\n' "$version" >&2; exit 1; }
mkdir -p "$tmp/publish"
printf '{"libraries":{"Npgsql/%s":{"type":"package"}}}\n' "$version" > "$tmp/publish/fake.deps.json"
cp -r "$here/licenses" "$tmp/texts"
tool() { dotnet run "$(host_path "$here/ReleaseTool.cs")" -- licenses --publish "$(host_path "$tmp/publish")" \
  --packages "$(host_path "$packages")" --texts "$(host_path "$1")" --out "$(host_path "$tmp/licenses-out")"; }
if tool "$tmp/texts" > "$tmp/out" 2>&1 && grep -q "Npgsql $version | PostgreSQL | texts/" "$tmp/out"; then
  pass "control: Npgsql $version is covered by its committed text"
else
  fail "control: Npgsql $version was not covered by its committed text"; cat "$tmp/out" >&2
fi
grep -v '^npgsql ' "$here/licenses/index.txt" > "$tmp/texts/index.txt"
expect_refused "Npgsql $version with its text taken out of index.txt" "Npgsql $version (PostgreSQL" tool "$tmp/texts"

# ---- 8. PACKAGES.txt names the runtime -----------------------------------------------
# A publish folder that deploys the .NET runtime pack at a version a restore put in
# the packages folder: the first line names it, and the SDK given.
runtime_version=$(ls "$packages/microsoft.netcore.app.runtime.linux-x64" 2>/dev/null | LC_ALL=C sort | tail -n 1 || true)
if [ -z "$runtime_version" ]; then
  printf 'SKIP  PACKAGES.txt names the runtime: no linux-x64 runtime pack in %s; publish once first\n' "$packages"
else
  mkdir -p "$tmp/runtime-publish"
  printf '{"libraries":{"runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/%s":{"type":"runtimepack"}}}\n' "$runtime_version" \
    > "$tmp/runtime-publish/fake.deps.json"
  expected="The .NET runtime this build carries: Microsoft.NETCore.App.Runtime.linux-x64 $runtime_version, from the .NET SDK 9.9.999, which global.json pins."
  if dotnet run "$(host_path "$here/ReleaseTool.cs")" -- licenses --publish "$(host_path "$tmp/runtime-publish")" --sdk 9.9.999 \
       --packages "$(host_path "$packages")" --texts "$(host_path "$here/licenses")" --out "$(host_path "$tmp/runtime-licenses")" > "$tmp/out" 2>&1 \
     && [ "$(head -n 1 "$tmp/runtime-licenses/PACKAGES.txt")" = "$expected" ]; then
    pass "PACKAGES.txt begins with the runtime $runtime_version and the SDK it came from"
  else
    fail "PACKAGES.txt does not begin with '$expected'"; cat "$tmp/out" >&2; head -n 2 "$tmp/runtime-licenses/PACKAGES.txt" >&2 || true
  fi
fi

# ---- 4. the packages folder --------------------------------------------------------
if [ -d "$packages" ] && [ -f "$packages/npgsql/$version/npgsql.nuspec" ]; then pass "control: the packages folder is found, $packages"
else fail "control: packages-folder.sh printed '$packages', which does not hold the restored packages"; fi
missing="$tmp/no-such-packages-folder"
expect_refused "NUGET_PACKAGES naming a folder that does not exist" "tried '$missing'" \
  env NUGET_PACKAGES="$missing" "$here/packages-folder.sh"

# ---- 5. an extension ships only what its manifest lists, as listed ----------------
# An invented extension build folder: the assembly and the one library its manifest
# names, and two files a build drops beside them that must not ship.
ext=$tmp/ext-built
mkdir -p "$ext"
printf 'invented assembly' > "$ext/Invented.Reader.dll"
printf 'invented library' > "$ext/Invented.Library.dll"
printf 'a copy of the core' > "$ext/Premagentic.Core.dll"
printf '{}' > "$ext/Invented.Reader.deps.json"
hash_of() { sha256sum "$1" | cut -d' ' -f1; }
printf '{ "name": "invented-reader", "version": "0.1.0", "assemblyFile": "Invented.Reader.dll", "sha256": "%s",\n  "files": [ { "file": "Invented.Library.dll", "sha256": "%s" } ], "seams": { "reader": 2 } }\n' \
  "$(hash_of "$ext/Invented.Reader.dll")" "$(hash_of "$ext/Invented.Library.dll")" > "$ext/extension.json"
ext_tool() { dotnet run "$(host_path "$here/ReleaseTool.cs")" -- extension --built "$(host_path "$ext")" --out "$(host_path "$tmp/ext-out")"; }
if ext_tool > "$tmp/out" 2>&1 \
  && [ "$(cd "$tmp/ext-out" && find . -type f | LC_ALL=C sort | tr '\n' ' ')" = "./Invented.Library.dll ./Invented.Reader.dll ./extension.json " ]; then
  pass "control: the invented extension ships its manifest, its assembly and its library, and not the two stray files"
else
  fail "control: the invented extension did not ship exactly its three files"; cat "$tmp/out" >&2; ls -R "$tmp/ext-out" >&2 || true
fi
printf 'changed' >> "$ext/Invented.Library.dll"
expect_refused "an extension whose listed library changed after its manifest was written" "Invented.Library.dll hashes to" ext_tool
rm "$ext/Invented.Library.dll"
expect_refused "an extension whose manifest lists a file that is not there" "Invented.Library.dll is listed in extension.json and is not in" ext_tool

# ---- 6. a reader's package needs its license text too ------------------------------
# The PDF reader's PdfPig ships no license file; its committed text covers it, and
# without that text it is refused, as a program's package would be.
pdfpig=$(sed -n 's/.*PackageReference Include="PdfPig" Version="\([^"]*\)".*/\1/p' "$repo/extensions/pdf-reader/PdfReader.csproj")
if [ -z "$pdfpig" ]; then
  printf 'SKIP  a reader package without its license text: no PdfPig reference on this commit\n'
elif [ ! -f "$packages/pdfpig/$pdfpig/pdfpig.nuspec" ]; then
  fail "PdfPig $pdfpig is not restored in $packages; build the PDF reader first"
else
  mkdir -p "$tmp/reader-publish"
  printf '{"libraries":{"PdfPig/%s":{"type":"package"}}}\n' "$pdfpig" > "$tmp/reader-publish/Premagentic.PdfReader.deps.json"
  reader_tool() { dotnet run "$(host_path "$here/ReleaseTool.cs")" -- licenses --publish "$(host_path "$tmp/reader-publish")" \
    --packages "$(host_path "$packages")" --texts "$(host_path "$1")" --out "$(host_path "$tmp/reader-licenses")"; }
  if reader_tool "$here/licenses" > "$tmp/out" 2>&1 && grep -q "PdfPig $pdfpig | Apache-2.0 | texts/" "$tmp/out"; then
    pass "control: PdfPig $pdfpig, which the PDF reader carries, is covered by its committed text"
  else
    fail "control: PdfPig $pdfpig was not covered by its committed text"; cat "$tmp/out" >&2
  fi
  rm -rf "$tmp/reader-texts"; cp -r "$here/licenses" "$tmp/reader-texts"
  grep -v '^pdfpig ' "$here/licenses/index.txt" > "$tmp/reader-texts/index.txt"
  expect_refused "PdfPig $pdfpig with its text taken out of index.txt" "PdfPig $pdfpig (Apache-2.0" reader_tool "$tmp/reader-texts"
fi

# ---- 9. three publishes in one folder ------------------------------------------------
# Invented publish folders, each with its own deps.json. A file two carry alike is kept
# once; where their copies differ, the copy whose deps.json records the higher file
# version is kept, whichever folder comes first; two different copies of one version,
# a differing copy with no recorded version, and a file a deps.json names that no
# publish carries are each refused.
publish_folder() { # <folder> <libraries JSON> <file>=<content>...
  local folder=$tmp/merge/$1 libraries=$2; shift 2
  mkdir -p "$folder"
  printf '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/linux-x64"},"targets":{".NETCoreApp,Version=v10.0/linux-x64":{%s}}}\n' \
    "$libraries" > "$folder/$(basename "$folder").deps.json"
  for pair in "$@"; do printf '%s' "${pair#*=}" > "$folder/${pair%%=*}"; done
}
publish_folder pub-a '"runtimepack.Invented.Runtime.linux-x64/10.0.11":{"runtime":{"Shared.dll":{"fileVersion":"10.0.1126.1"}}},"a/1.0.0":{"runtime":{"a.dll":{}}}' \
  Shared.dll='the runtime pack copy' libsame.so='alike in both' a.dll='program a'
publish_folder pub-b '"Invented.Shared/10.0.12":{"runtime":{"lib/net10.0/Shared.dll":{"fileVersion":"10.0.1226.1"}}},"b/1.0.0":{"runtime":{"b.dll":{}}}' \
  Shared.dll='the package copy' libsame.so='alike in both' b.dll='program b'
publish_folder pub-c '"Invented.Shared/10.0.11":{"runtime":{"lib/net10.0/Shared.dll":{"fileVersion":"10.0.1126.1"}}}' \
  Shared.dll='another copy of the same version'
publish_folder pub-d '"Invented.Shared/10.0.13":{"runtime":{"lib/net10.0/Shared.dll":{}}}' \
  Shared.dll='a copy with no file version'
publish_folder pub-e '"e/1.0.0":{"runtime":{"e.dll":{},"missing.dll":{}}}' e.dll='program e'
publish_folder pub-g '"Invented.Shared/10.0.11":{"runtime":{"lib/net10.0/Shared.dll":{"fileVersion":"10.0.1126.1"}}}' \
  Shared.dll='a third package copy of that version'
merge_tool() { # <into> <from>...
  local into=$1; shift
  local from=()
  for f in "$@"; do from+=("$(host_path "$tmp/merge/$f")"); done
  dotnet run "$(host_path "$here/ReleaseTool.cs")" -- merge --into "$(host_path "$tmp/merge/$into")" --from "${from[@]}"
}
for order in "pub-a pub-b" "pub-b pub-a"; do
  into=out-${order// /-}
  # shellcheck disable=SC2086
  if merge_tool "$into" $order > "$tmp/out" 2>&1 \
     && [ "$(cat "$tmp/merge/$into/Shared.dll")" = 'the package copy' ] \
     && [ "$(cd "$tmp/merge/$into" && find . -type f | LC_ALL=C sort | tr '\n' ' ')" = "./Shared.dll ./a.dll ./b.dll ./libsame.so ./pub-a.deps.json ./pub-b.deps.json " ] \
     && grep -q "Shared.dll: pub-b's 10.0.1226.1" "$tmp/out"; then
    pass "control: merged $order, the higher file version's copy of Shared.dll is kept, the alike file once, each program's own files beside"
  else
    fail "control: merging $order did not keep the higher version's copy and every file once"; cat "$tmp/out" >&2
  fi
done
# One file version from a runtime pack and from a package, as .NET's own libraries
# are when the runtime and the packages are the same patch: the runtime pack's
# precompiled copy is kept, whichever folder comes first, as the SDK keeps it.
for order in "pub-a pub-c" "pub-c pub-a"; do
  into=out-${order// /-}
  # shellcheck disable=SC2086
  if merge_tool "$into" $order > "$tmp/out" 2>&1 && [ "$(cat "$tmp/merge/$into/Shared.dll")" = 'the runtime pack copy' ]; then
    pass "control: merged $order, one file version from a runtime pack and a package, the runtime pack's copy is kept"
  else
    fail "merging $order did not keep the runtime pack's copy of one file version"; cat "$tmp/out" >&2
  fi
done
expect_refused "two different package copies of one file version" "carry different copies of the same version" merge_tool out-cg pub-c pub-g
expect_refused "a differing copy whose deps.json gives no file version" "pub-d's deps.json gives it no file version" merge_tool out-ad pub-a pub-d
expect_refused "a file a deps.json names that no publish carries" "pub-e needs missing.dll" merge_tool out-ae pub-a pub-e
# A build on a Linux host records every Windows native file as 0.0.0.0, so a copy
# recorded so cannot win or lose: two differing native copies, one of them 0.0.0.0,
# are refused rather than decided on it.
publish_folder pub-n0 '"runtimepack.Invented.Runtime.linux-x64/10.0.11":{"native":{"libinvented.so":{"fileVersion":"0.0.0.0"}}}' \
  libinvented.so='a native copy recorded as 0.0.0.0'
publish_folder pub-n1 '"Invented.Native/1.0.0":{"native":{"runtimes/linux-x64/native/libinvented.so":{"fileVersion":"1.2.3.4"}}}' \
  libinvented.so='another native copy, recorded as 1.2.3.4'
expect_refused "two differing native copies, one recorded as 0.0.0.0" "records its file version as 0.0.0.0, which orders nothing" \
  merge_tool out-n pub-n0 pub-n1

# ---- 10. INSTALL.txt with and without the readers --------------------------------------
for template in install-linux.txt install-windows.txt; do
  with=$("$here/install-text.sh" "$here/$template" 9.9.9 with-extensions)
  without=$("$here/install-text.sh" "$here/$template" 9.9.9 no-extensions)
  if grep -q 'prem.* extensions allow' <<< "$with" && grep -q "PremAgentic 9.9.9 for" <<< "$with" && ! grep -q '@[A-Z-]*@' <<< "$with"; then
    pass "control: $template for an archive with readers says how to allow them and has no placeholder left"
  else fail "$template for an archive with readers lacks the readers step or keeps a placeholder"; fi
  if ! grep -q 'extensions' <<< "$without" && ! grep -q '@[A-Z-]*@' <<< "$without" && grep -q "PremAgentic 9.9.9 for" <<< "$without"; then
    pass "$template for an archive with no readers says nothing of extensions and has no placeholder left"
  else fail "$template for an archive with no readers still speaks of extensions or keeps a placeholder"; fi
done

# ---- 11. the Visual C++ runtime files out of the pinned package ---------------------
vc_pins=$repo/scripts/download-vc-runtime.sh
vc_files=(msvcp140 msvcp140_1 vcruntime140 vcruntime140_1)
vc_pin() { tr -d '\r' < "$vc_pins" | sed -n "s/^$1=\"\\(.*\\)\"\$/\\1/p"; }
if [ -z "$vc_runtime" ]; then
  printf 'SKIP  the Visual C++ runtime files: no --vc-runtime package given\n'
else
  vc_tool() { # <package> <pins> <out>
    dotnet run "$(host_path "$here/ReleaseTool.cs")" -- vcruntime --package "$(host_path "$1")" --pins "$(host_path "$2")" --out "$(host_path "$3")"
  }
  # The control: exactly the four files, each matching its pin, as sha256sum reads it.
  if vc_tool "$vc_runtime" "$vc_pins" "$tmp/vc-out" > "$tmp/out" 2>&1; then
    names=$(cd "$tmp/vc-out" && ls | LC_ALL=C sort | tr '\n' ' ')
    unmatched=
    for dll in "${vc_files[@]}"; do
      [ -f "$tmp/vc-out/$dll.dll" ] && [ "$(sha256sum "$tmp/vc-out/$dll.dll" | cut -d' ' -f1)" = "$(vc_pin "${dll}_sha256")" ] \
        || unmatched="$unmatched $dll.dll"
    done
    if [ "$names" != "msvcp140.dll msvcp140_1.dll vcruntime140.dll vcruntime140_1.dll " ]; then
      fail "control: the pinned package gave $names, not exactly the four runtime files"
    elif [ -n "$unmatched" ]; then fail "control: files that do not match their pins:$unmatched"
    else pass "control: the pinned package gives exactly the four runtime files, each matching its pin"
    fi
  else fail "control: the pinned package was refused"; cat "$tmp/out" >&2; fi
  expect_refused "the runtime files into a folder that already holds them" "is not empty" \
    vc_tool "$vc_runtime" "$vc_pins" "$tmp/vc-out"
  # The package's last byte changed (the size stays), then the package one byte short.
  cp "$vc_runtime" "$tmp/vc-changed.exe"
  printf 'x' | dd of="$tmp/vc-changed.exe" bs=1 seek=$(( $(wc -c < "$vc_runtime") - 1 )) conv=notrunc 2>/dev/null
  expect_refused "a package with its last byte changed" "and the pinned SHA-256 is" \
    vc_tool "$tmp/vc-changed.exe" "$vc_pins" "$tmp/vc-out-changed"
  head -c $(( $(wc -c < "$vc_runtime") - 1 )) "$vc_runtime" > "$tmp/vc-short.exe"
  expect_refused "a package one byte short" "bytes and the pinned size is" \
    vc_tool "$tmp/vc-short.exe" "$vc_pins" "$tmp/vc-out-short"
  rm -f "$tmp/vc-changed.exe" "$tmp/vc-short.exe"
  # The package matches its pins, and one file taken out of it does not match its own.
  sed 's/^vcruntime140_1_sha256="1/vcruntime140_1_sha256="2/' "$vc_pins" > "$tmp/vc-pins-off.sh"
  expect_refused "a runtime file whose pin is off by one digit" "vcruntime140_1.dll in the package hashes to" \
    vc_tool "$vc_runtime" "$tmp/vc-pins-off.sh" "$tmp/vc-out-off"
  # The pins and the files the tool ships are one set: a fifth pinned file is refused.
  { tr -d '\r' < "$vc_pins"; printf 'concrt140_sha256="%s"\n' "$(printf '0%.0s' $(seq 64))"; } > "$tmp/vc-pins-five.sh"
  expect_refused "pins that name a fifth runtime file" "the two must name the same files" \
    vc_tool "$vc_runtime" "$tmp/vc-pins-five.sh" "$tmp/vc-out-five"
fi

# ---- 13. a runtime pack's license, by the name it has on disk ---------------------------
# An invented runtime pack whose license file is LICENSE.txt: the member keeps that name
# on every host. A case-blind file system (Windows) used to find it under the name asked
# for first, LICENSE.TXT, so the archive named it differently there than on Linux.
mkdir -p "$tmp/case/packages/invented.app.runtime.linux-x64/1.0.0" "$tmp/case/publish"
printf 'an invented license\n' > "$tmp/case/packages/invented.app.runtime.linux-x64/1.0.0/LICENSE.txt"
printf 'invented notices\n' > "$tmp/case/packages/invented.app.runtime.linux-x64/1.0.0/THIRD-PARTY-NOTICES.TXT"
printf '{"libraries":{"runtimepack.Invented.App.Runtime.linux-x64/1.0.0":{"type":"runtimepack"}}}\n' > "$tmp/case/publish/fake.deps.json"
if dotnet run "$(host_path "$here/ReleaseTool.cs")" -- licenses --publish "$(host_path "$tmp/case/publish")" --sdk 9.9.999 \
     --packages "$(host_path "$tmp/case/packages")" --texts "$(host_path "$here/licenses")" --out "$(host_path "$tmp/case/out")" > "$tmp/out" 2>&1; then
  members=$(cd "$tmp/case/out" && find . -type f -name '*LICENSE*' | sed 's#^\./##')
  if grep -q -- '-LICENSE\.txt$' <<< "$members" && ! grep -q -- '-LICENSE\.TXT$' <<< "$members" \
     && grep -q -- '-LICENSE\.txt' "$tmp/case/out/PACKAGES.txt"; then
    pass "a runtime pack's LICENSE.txt keeps the name it has on disk, in licenses/ and in PACKAGES.txt"
  else fail "a runtime pack's LICENSE.txt was named otherwise: $(tr '\n' ' ' <<< "$members")"; fi
else fail "the license step refused an invented runtime pack with its license and notices"; cat "$tmp/out" >&2; fi

# ---- 12. a zip's times -------------------------------------------------------------
# A zip entry holds an MS-DOS date, 1980 to 2107. pack refuses any other --mtime for a
# zip by name; before, the archive API threw from deep inside it (a dry run of the
# release stopped there once, on a variant stamped 0).
mkdir -p "$tmp/zip-times/tree/bin"
printf 'an invented file\n' > "$tmp/zip-times/tree/bin/invented.txt"
zip_pack() { # <mtime> <zip>
  dotnet run "$(host_path "$here/ReleaseTool.cs")" -- pack --dir "$(host_path "$tmp/zip-times/tree")" --top invented \
    --zip "$(host_path "$1")" --mtime "$2"
}
if zip_pack "$tmp/zip-times/floor.zip" 315532800 > "$tmp/out" 2>&1; then pass "control: a zip stamped 1980-01-01 00:00 UTC, the zip's floor, is packed"
else fail "control: a zip stamped 1980-01-01 00:00 UTC was refused"; cat "$tmp/out" >&2; fi
expect_refused "a zip packed with a time before 1980" "a zip entry holds times from 1980 to 2107 only" \
  zip_pack "$tmp/zip-times/before.zip" 0

# ---- 3. an archive that lacks a file ---------------------------------------------
if [ -z "$archives" ]; then
  printf 'SKIP  an archive that lacks a file: no --archives folder given\n'
else
  archive=$(find "$archives" -maxdepth 1 -name 'premagentic-*-linux-x64.tar.gz' | head -n 1)
  [ -n "$archive" ] || { fail "no linux-x64 archive in $archives"; exit 1; }
  if "$here/verify-archive.sh" "$archive" linux-x64 > "$tmp/out" 2>&1; then pass "control: $(basename "$archive") as built passes"
  else fail "control: $(basename "$archive") as built was refused"; cat "$tmp/out" >&2; fi
  top=$(basename "$archive" .tar.gz)
  mkdir -p "$tmp/unpacked" "$tmp/broken"
  tar -xzf "$archive" -C "$tmp/unpacked"
  # The unpacked tree, packed again as build.sh packs it, into broken/; each variant
  # below changes one thing, is checked, and is undone before the next.
  repack() {
    rm -f "$tmp/broken/$top.tar.gz"
    dotnet run "$(host_path "$here/ReleaseTool.cs")" -- pack --dir "$(host_path "$tmp/unpacked/$top")" --top "$top" \
      --tar "$(host_path "$tmp/broken/$top.tar")" --mtime 0 \
      --exec bin/prem --exec bin/Premagentic.Api --exec bin/Premagentic.McpServer > /dev/null
    gzip -n -1 "$tmp/broken/$top.tar"
  }
  refused_as_repacked() { # <what> <text the refusal must contain>
    repack
    expect_refused "the same archive $1" "$2" "$here/verify-archive.sh" "$tmp/broken/$top.tar.gz" linux-x64
  }
  put_back() { tar -xzOf "$archive" "$top/$1" > "$tmp/unpacked/$top/$1"; }

  # The two licenses, byte for byte: the program's LICENSE where the model's Apache
  # text belongs, and the Apache text as the program's LICENSE, are each refused.
  cp "$tmp/unpacked/$top/LICENSE" "$tmp/unpacked/$top/models/minilm/LICENSE-Apache-2.0.txt"
  refused_as_repacked "with the program's LICENSE as the model's license file" \
    "models/minilm/LICENSE-Apache-2.0.txt is not the Apache License 2.0"
  put_back models/minilm/LICENSE-Apache-2.0.txt
  cp "$tmp/unpacked/$top/models/minilm/LICENSE-Apache-2.0.txt" "$tmp/unpacked/$top/LICENSE"
  refused_as_repacked "with the Apache License 2.0 as its LICENSE" \
    "LICENSE is not the GNU Affero General Public License 3.0"
  put_back LICENSE

  rm "$tmp/unpacked/$top/licenses/PACKAGES.txt"
  refused_as_repacked "without licenses/PACKAGES.txt" "it has no licenses/PACKAGES.txt"
  tar -xzOf "$archive" "$top/licenses/PACKAGES.txt" | grep -v '^The \.NET runtime this build carries: ' \
    > "$tmp/unpacked/$top/licenses/PACKAGES.txt"
  refused_as_repacked "with a PACKAGES.txt that does not name the .NET runtime" "does not name the .NET runtime version it carries"
  put_back licenses/PACKAGES.txt

  # The three programs share bin/ and one runtime: a second copy of the runtime's
  # core, or a program folder of the old layout, is refused.
  mkdir "$tmp/unpacked/$top/api"
  cp "$tmp/unpacked/$top/bin/libcoreclr.so" "$tmp/unpacked/$top/api/libcoreclr.so"
  refused_as_repacked "with a second copy of the runtime in api/" "carries 2 copies of libcoreclr.so"
  rm -rf "$tmp/unpacked/$top/api"
  mkdir "$tmp/unpacked/$top/mcp"
  cp "$tmp/unpacked/$top/bin/Premagentic.McpServer" "$tmp/unpacked/$top/mcp/Premagentic.McpServer"
  refused_as_repacked "with the bridge in a folder of its own again" "it has a program folder besides bin/"
  rm -rf "$tmp/unpacked/$top/mcp"

  # ONNX Runtime's import library, which the Windows publish once carried in bin/.
  printf 'an invented import library' > "$tmp/unpacked/$top/bin/onnxruntime.lib"
  refused_as_repacked "with an import library in bin/" "it carries what must not ship: $top/bin/onnxruntime.lib"
  rm "$tmp/unpacked/$top/bin/onnxruntime.lib"

  # A Visual C++ runtime file belongs in the Windows archive only.
  printf 'an invented runtime file' > "$tmp/unpacked/$top/bin/vcruntime140.dll"
  refused_as_repacked "with a Visual C++ runtime file in bin/" "which only the Windows archive may"
  rm "$tmp/unpacked/$top/bin/vcruntime140.dll"

  # An archive from a commit with no extensions has no such folder, and find then
  # fails; under pipefail that would end the script here without a word.
  first_extension=$(find "$tmp/unpacked/$top/extensions" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | head -n 1 || true)
  if [ -z "$first_extension" ]; then
    printf 'SKIP  an INSTALL.txt without its readers step, and a stray file beside an extension: the archive has no extensions\n'
  else
    # INSTALL.txt as an archive with no extensions has it, in one that has them.
    version_of_archive=${top#premagentic-}
    "$here/install-text.sh" "$here/install-linux.txt" "${version_of_archive%-linux-x64}" no-extensions > "$tmp/unpacked/$top/INSTALL.txt"
    refused_as_repacked "with an INSTALL.txt that does not say how to allow its readers" "does not say how to allow them"
    put_back INSTALL.txt
    # A copy of the core beside an extension, as a build folder has it: refused by name.
    cp "$tmp/unpacked/$top/bin/Premagentic.Core.dll" "$first_extension/Premagentic.Core.dll"
    refused_as_repacked "with a copy of Premagentic.Core beside $(basename "$first_extension")" \
      "an extension carries a file its manifest does not name"
  fi
fi

# ---- 11, in the Windows archive: exactly the four pinned runtime files ----------------
if [ -n "$archives" ]; then
  win_archive=$(find "$archives" -maxdepth 1 -name 'premagentic-*-win-x64.zip' | head -n 1)
  if [ -z "$win_archive" ]; then
    printf 'SKIP  the Windows archive and its runtime files: no win-x64 archive in %s\n' "$archives"
  else
    if "$here/verify-archive.sh" "$win_archive" win-x64 > "$tmp/out" 2>&1; then pass "control: $(basename "$win_archive") as built passes"
    else fail "control: $(basename "$win_archive") as built was refused"; cat "$tmp/out" >&2; fi
    win_top=$(basename "$win_archive" .zip)
    mkdir -p "$tmp/win-unpacked" "$tmp/win-broken"
    unzip -q "$win_archive" -d "$tmp/win-unpacked"
    win_bin=$tmp/win-unpacked/$win_top/bin
    # The unpacked tree packed again as build.sh packs it; each variant changes one
    # thing, is checked, and is undone before the next. A zip holds no time before
    # 1980, so the variants are stamped 2020-01-01, not 0 as the tar variants are.
    win_refused_as_repacked() { # <what> <text the refusal must contain>
      rm -f "$tmp/win-broken/$win_top.zip"
      dotnet run "$(host_path "$here/ReleaseTool.cs")" -- pack --dir "$(host_path "$tmp/win-unpacked/$win_top")" --top "$win_top" \
        --zip "$(host_path "$tmp/win-broken/$win_top.zip")" --mtime 1577836800 > /dev/null
      expect_refused "the same Windows archive $1" "$2" "$here/verify-archive.sh" "$tmp/win-broken/$win_top.zip" win-x64
    }
    mv "$win_bin/vcruntime140_1.dll" "$tmp/vcruntime140_1.dll"
    win_refused_as_repacked "without bin/vcruntime140_1.dll" "it has no bin/vcruntime140_1.dll"
    mv "$tmp/vcruntime140_1.dll" "$win_bin/vcruntime140_1.dll"
    printf 'an invented runtime file' > "$win_bin/msvcp140_2.dll"
    win_refused_as_repacked "with a fifth Visual C++ runtime file in bin/" "other than the four pinned ones"
    rm "$win_bin/msvcp140_2.dll"
    cp "$win_bin/vcruntime140.dll" "$tmp/vcruntime140.dll"
    printf 'x' >> "$win_bin/vcruntime140.dll"
    win_refused_as_repacked "with bin/vcruntime140.dll one byte longer" "bin/vcruntime140.dll hashes to"
    mv "$tmp/vcruntime140.dll" "$win_bin/vcruntime140.dll"
  fi
fi

# ---- 14. the Windows Sandbox proof's own refusals --------------------------------
# The proof itself needs Windows Sandbox, which no hosted runner has; its refusals do
# not, and run here with stand-ins for the sandbox's programs.
sandbox_status=0
bash "$here/prove-windows-sandbox.sh" self-test > "$tmp/sandbox-self-test" 2>&1 || sandbox_status=$?
grep -E '^(PASS|FAIL)' "$tmp/sandbox-self-test" | sed 's/^/  sandbox self-test: /' || true
if [ "$sandbox_status" = 0 ] && grep -qx 'SELF-TEST PASSED' "$tmp/sandbox-self-test"; then
  pass "the Windows Sandbox proof's refusals each fire beside a control ($(grep -c '^PASS' "$tmp/sandbox-self-test") checks)"
else fail "the Windows Sandbox proof's self-test (exit $sandbox_status)"; cat "$tmp/sandbox-self-test" >&2; fi

if [ "$failures" -gt 0 ]; then printf '\n%s check(s) FAILED\n' "$failures" >&2; exit 1; fi
printf '\nall checks passed\n'
