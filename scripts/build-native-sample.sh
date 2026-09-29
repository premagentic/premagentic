#!/usr/bin/env sh
# Builds the native library of the native-lines sample extension for win-x64
# and linux-x64 from its one C file, into the folders the sample's project
# copies from. The built files are committed, so no machine that builds the
# solution needs a C compiler; this script is how they were made and how
# anyone can make them again.
#
#   scripts/build-native-sample.sh           build both, over the committed files
#   scripts/build-native-sample.sh --check   build both into a temporary folder
#                                            and compare them byte for byte with
#                                            the committed files; exit 1 on any
#                                            difference
#
# Needs clang and lld (ld.lld and lld-link). Made with clang 22.1.8 and its
# lld, on Windows. The function uses no C library, so the build links nothing
# (-nostdlib) and the result depends on no runtime on either platform.
set -eu

here="$(cd "$(dirname "$0")/.." && pwd)"
sample="$here/samples/extensions/native-lines/native"
source="$sample/prem_sample_lines.c"

out="$sample"
if [ "${1:-}" = "--check" ]; then
    out="$(mktemp -d)"
    mkdir -p "$out/win-x64" "$out/linux-x64"
fi

# A path as the compiler reads it: Git Bash on Windows names folders
# /c/..., which a Windows clang cannot open; cygpath gives C:/... there, and
# is not there to call on Linux or macOS, where the path is already right.
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi; }

# MSYS_NO_PATHCONV keeps Git Bash from rewriting /noentry and /export into
# file paths, which is why the paths are converted above instead; it changes
# nothing elsewhere.
MSYS_NO_PATHCONV=1 clang --target=x86_64-pc-windows-msvc -O2 -shared -nostdlib -fuse-ld=lld \
    -Wl,/noentry -Wl,/export:prem_sample_line_end -Wl,/Brepro \
    -o "$(native "$out/win-x64/prem_sample_lines.dll")" "$(native "$source")"
clang --target=x86_64-linux-gnu -O2 -fPIC -shared -nostdlib -fuse-ld=lld \
    -Wl,--build-id=none \
    -o "$(native "$out/linux-x64/libprem_sample_lines.so")" "$(native "$source")"
# lld-link writes an import library and its symbols beside the DLL; only the
# DLL is part of the sample.
rm -f "$out/win-x64/prem_sample_lines.lib" "$out/win-x64/prem_sample_lines.exp" "$out/win-x64/prem_sample_lines.pdb"

if [ "$out" = "$sample" ]; then
    echo "built into $sample"
    exit 0
fi

status=0
for file in win-x64/prem_sample_lines.dll linux-x64/libprem_sample_lines.so; do
    if cmp -s "$out/$file" "$sample/$file"; then
        echo "identical: $file"
    else
        echo "DIFFERENT: $file" >&2
        status=1
    fi
done
rm -rf "$out"
exit $status
