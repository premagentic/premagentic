#!/usr/bin/env bash
# Checks a finished release archive: that it holds every file an install needs,
# with the three programs in one bin/ folder over one runtime, that its embedding
# model is the pinned one, that its LICENSE is the GNU Affero General Public License
# 3.0 and the model's license file the Apache License 2.0, each byte for byte, that
# the Windows archive carries exactly the four pinned Visual C++ runtime files
# (and the Linux one none), that its licenses/PACKAGES.txt names
# the .NET runtime version it carries, that nothing is in it that must not be, and
# on Linux that the three programs are executable. build.sh runs it on
# every archive it writes; it can be run on a downloaded archive too.
#
#   scripts/release/verify-archive.sh <archive> <linux-x64|win-x64>
#
# It reads the archive without unpacking it to disk, and the pinned SHA-256
# values from scripts/download-model.sh and scripts/download-vc-runtime.sh. It prints one line per check
# and exits non-zero on the first that fails.
set -euo pipefail

archive=${1:?usage: verify-archive.sh <archive> <linux-x64|win-x64>}
rid=${2:?usage: verify-archive.sh <archive> <linux-x64|win-x64>}
here=$(cd "$(dirname "$0")" && pwd)

fail() { printf 'refused: %s: %s\n' "$(basename "$archive")" "$*" >&2; exit 1; }
ok() { printf 'ok     %s\n' "$*"; }

[ -f "$archive" ] || fail "no such file"
name=$(basename "$archive")
case $rid in
  linux-x64) [[ $name =~ ^(premagentic-[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?-linux-x64)\.tar\.gz$ ]] || fail "not named premagentic-<version>-linux-x64.tar.gz"
             exe= ; icu=(libicuuc.so.72.1.0.3 libicui18n.so.72.1.0.3 libicudata.so.72.1.0.3); onnx=libonnxruntime.so; coreclr=libcoreclr.so ;;
  win-x64)   [[ $name =~ ^(premagentic-[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?-win-x64)\.zip$ ]] || fail "not named premagentic-<version>-win-x64.zip"
             exe=.exe; icu=(icuuc72.dll icuin72.dll icudt72.dll); onnx=onnxruntime.dll; coreclr=coreclr.dll ;;
  *) fail "the platform is linux-x64 or win-x64, not $rid" ;;
esac
top=${BASH_REMATCH[1]}
version=${top#premagentic-}
version=${version%-"$rid"}

# The listing, one path per line, and a reader for one entry's bytes.
case $rid in
  linux-x64)
    listing=$(tar -tzf "$archive") || fail "not a readable .tar.gz"
    read_entry() { tar -xzOf "$archive" "$1"; }
    ;;
  win-x64)
    listing=$(unzip -Z1 "$archive") || fail "not a readable .zip"
    read_entry() { unzip -p "$archive" "$1"; }
    ;;
esac
has() { grep -qxF -- "$top/$1" <<< "$listing"; }

# Every entry sits under the one top folder.
outside=$(grep -v -e "^$top/" -e "^$top\$" <<< "$listing" | head -n 1 || true)
[ -z "$outside" ] || fail "an entry is outside $top/: $outside"
ok "every entry is under $top/"

required=(
  LICENSE NOTICE THIRD-PARTY-NOTICES.md INSTALL.txt
  "bin/prem$exe" "bin/Premagentic.Api$exe" "bin/Premagentic.McpServer$exe" "bin/$onnx" "bin/$coreclr"
  bin/prem.deps.json bin/prem.runtimeconfig.json
  bin/Premagentic.Api.deps.json bin/Premagentic.Api.runtimeconfig.json
  bin/Premagentic.McpServer.deps.json bin/Premagentic.McpServer.runtimeconfig.json
  models/minilm/model.onnx models/minilm/vocab.txt models/minilm/prem-model.json
  models/minilm/NOTICE.txt models/minilm/LICENSE-Apache-2.0.txt
  licenses/PACKAGES.txt
  samples/profiles/starter/profile.json samples/profiles/starter/sources.json
  sample-docs/open/handbook.md sample-docs/hr/salary-bands.md
)
[ "$rid" = linux-x64 ] && required+=(deploy/systemd/premagentic-api.service)
vc_runtime=(msvcp140 msvcp140_1 vcruntime140 vcruntime140_1)
if [ "$rid" = win-x64 ]; then
  for dll in "${vc_runtime[@]}"; do required+=("bin/$dll.dll"); done
  required+=(licenses/Microsoft-Visual-CPP-Runtime.txt)
fi
for lib in "${icu[@]}"; do required+=("bin/$lib"); done
for file in "${required[@]}"; do has "$file" || fail "it has no $file"; done
ok "all ${#required[@]} required files are present"

# The two licenses, each byte for byte as its publisher gives it: the program's is the
# GNU Affero General Public License 3.0 (gnu.org), the model's the Apache License 2.0
# (apache.org). The model's file was once a copy of the program's LICENSE.
license_is() { # <entry> <sha256> <the text it must be>
  local actual
  actual=$(read_entry "$top/$1" | sha256sum | cut -d' ' -f1)
  [ "$actual" = "$2" ] || fail "$1 is not $3: it hashes to $actual, and that text to $2"
}
license_is LICENSE 0d96a4ff68ad6d4b6f1f30f713b18d5184912ba8dd389f86aa7710db079abcb0 \
  "the GNU Affero General Public License 3.0 as gnu.org publishes it"
license_is models/minilm/LICENSE-Apache-2.0.txt cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30 \
  "the Apache License 2.0 as apache.org publishes it"
ok "LICENSE is the GNU Affero General Public License 3.0 and the model's license file the Apache License 2.0"

# One runtime for the three programs: they share bin/, and the archive carries the
# runtime's core library once. The folders each program once had to itself are gone.
runtimes=$(grep -c "/$coreclr\$" <<< "$listing" || true)
[ "$runtimes" = 1 ] || fail "it carries $runtimes copies of $coreclr; the three programs share one runtime in bin/"
old=$(grep -E "^$top/(api|mcp)/" <<< "$listing" | head -n 1 || true)
[ -z "$old" ] || fail "it has a program folder besides bin/: $old"
ok "the three programs share bin/ and one runtime"

# The Visual C++ runtime: on Windows exactly the four files scripts/download-vc-runtime.sh
# pins, in bin/, each matching its pin, and no other file of that runtime anywhere; on
# Linux none at all.
vc_found=$(grep -Ei '(^|/)(msvcp|vcruntime|concrt|vccorlib|vcamp|vcomp)[0-9][0-9a-z_]*\.dll$' <<< "$listing" | LC_ALL=C sort || true)
if [ "$rid" = win-x64 ]; then
  vc_expected=$(for dll in "${vc_runtime[@]}"; do printf '%s/bin/%s.dll\n' "$top" "$dll"; done | LC_ALL=C sort)
  [ "$vc_found" = "$vc_expected" ] \
    || fail "it carries Visual C++ runtime files other than the four pinned ones in bin/: $(tr '\n' ' ' <<< "$vc_found")"
  vc_pins=$(tr -d '\r' < "$here/../download-vc-runtime.sh")
  for dll in "${vc_runtime[@]}"; do
    expected=$(sed -n "s/^${dll}_sha256=\"\\(.*\\)\"\$/\\1/p" <<< "$vc_pins")
    [[ $expected =~ ^[0-9a-f]{64}$ ]] || fail "could not read the pin for $dll.dll from scripts/download-vc-runtime.sh"
    actual=$(read_entry "$top/bin/$dll.dll" | sha256sum | cut -d' ' -f1)
    [ "$actual" = "$expected" ] || fail "bin/$dll.dll hashes to $actual and the pinned SHA-256 is $expected"
  done
  ok "bin/ carries the four Visual C++ runtime files, each matching its pin, and no other"
else
  [ -z "$vc_found" ] || fail "it carries Visual C++ runtime files, which only the Windows archive may: $(tr '\n' ' ' <<< "$vc_found")"
fi

grep -q "^$top/licenses/runtime/.*Microsoft\.NETCore\.App\.Runtime\.$rid-.*LICENSE" <<< "$listing" \
  || fail "licenses/ has no license text for the .NET runtime"
grep -q "^$top/licenses/runtime/.*Microsoft\.AspNetCore\.App\.Runtime\.$rid-.*LICENSE" <<< "$listing" \
  || fail "licenses/ has no license text for the ASP.NET Core runtime"
grep -q "^$top/licenses/texts/.*" <<< "$listing" || fail "licenses/ has no package license texts"
ok "licenses/ carries the runtime and package texts"

# The runtime version, which the pinned SDK chose, is named where a reader looks.
runtime_line=$(read_entry "$top/licenses/PACKAGES.txt" | tr -d '\r' | grep -m 1 '^The \.NET runtime this build carries: ' || true)
grep -qE "Microsoft\.NETCore\.App\.Runtime\.$rid [0-9]+\.[0-9]+\.[0-9]+" <<< "$runtime_line" \
  || fail "licenses/PACKAGES.txt does not name the .NET runtime version it carries"
ok "${runtime_line#The }"

# Each extension carries its manifest, and nothing a build drops beside it: the
# host already has Premagentic.Core, and a deps.json is not something it reads.
for extension in $(sed -n "s#^$top/extensions/\([^/]*\)/.*#\1#p" <<< "$listing" | sort -u); do
  has "extensions/$extension/extension.json" || fail "extensions/$extension has no extension.json"
done
stray=$(grep -E "^$top/extensions/.*(/Premagentic\.Core\.dll|\.deps\.json|\.runtimeconfig\.json)\$" <<< "$listing" | head -n 3 || true)
[ -z "$stray" ] || fail "an extension carries a file its manifest does not name: $(tr '\n' ' ' <<< "$stray")"
if grep -q "^$top/extensions/" <<< "$listing"; then
  ok "extensions: $(sed -n "s#^$top/extensions/\([^/]*\)/.*#\1#p" <<< "$listing" | sort -u | tr '\n' ' ')each with its manifest and no stray build files"
fi

# Nothing that must not ship. An import library (.lib) is for linking C code
# against a native library; no program here reads one.
forbidden=$(grep -E -i '\.pdb$|/\.git/|credentials|\.pfx$|/models/bge-small/|/obj/|\.lock\.json$|\.lib$' <<< "$listing" | head -n 3 || true)
[ -z "$forbidden" ] || fail "it carries what must not ship: $(tr '\n' ' ' <<< "$forbidden")"
ok "no symbols, git data, credentials, certificates, NuGet lock files, import libraries or build folders"

# The model is the pinned one.
pins=$(tr -d '\r' < "$here/../download-model.sh")
pin() { sed -n "s/^$1=\"\\(.*\\)\"\$/\\1/p" <<< "$pins"; }
for entry in model.onnx:$(pin minilm_model_sha256) vocab.txt:$(pin minilm_vocab_sha256); do
  expected=${entry#*:}
  file=${entry%%:*}
  [[ $expected =~ ^[0-9a-f]{64}$ ]] || fail "could not read the pin for $file from scripts/download-model.sh"
  actual=$(read_entry "$top/models/minilm/$file" | sha256sum | cut -d' ' -f1)
  [ "$actual" = "$expected" ] || fail "models/minilm/$file hashes to $actual and the pinned SHA-256 is $expected"
done
ok "the model files match the pinned SHA-256 values"

install=$(read_entry "$top/INSTALL.txt")
grep -q "PremAgentic $version for" <<< "$install" || fail "INSTALL.txt does not name version $version"
! grep -q '@[A-Z-]*@' <<< "$install" || fail "INSTALL.txt has a placeholder left in it"
# The readers' step is there exactly when the readers are.
if grep -q "^$top/extensions/" <<< "$listing"; then
  grep -q 'prem.* extensions allow' <<< "$install" || fail "the archive carries extensions and INSTALL.txt does not say how to allow them"
else
  ! grep -q 'extensions' <<< "$install" || fail "the archive carries no extensions and INSTALL.txt speaks of them"
fi
ok "INSTALL.txt names $version"

if [ "$rid" = linux-x64 ]; then
  modes=$(tar -tvzf "$archive")
  for program in bin/prem bin/Premagentic.Api bin/Premagentic.McpServer; do
    grep -qE "^-rwxr-xr-x .* $top/$program\$" <<< "$modes" || fail "$program is not executable (rwxr-xr-x)"
  done
  ok "the three programs are executable"
fi
printf 'PASS   %s\n' "$name"
