#!/usr/bin/env sh
# Downloads the Microsoft Visual C++ Redistributable the Windows release archive
# takes its four runtime DLLs from, into vc-runtime/ (gitignored). An optional
# VC_RUNTIME_DIR environment variable overrides the target directory.
#
# ONNX Runtime's Windows library imports the Visual C++ runtime, which Windows
# does not carry, so the Windows archive ships msvcp140.dll, msvcp140_1.dll,
# vcruntime140.dll and vcruntime140_1.dll in bin/, unmodified, under the Visual
# Studio license's Distributable Code terms
# (scripts/release/vc-runtime-license.txt). scripts/release/ReleaseTool.cs
# vcruntime takes exactly those four out of this package and checks each against
# the SHA-256 recorded here; nothing else of the package is shipped.
#
# The package comes from a versioned URL, never an alias that moves, and must be
# the size and hash recorded here; a file that does not match is deleted and the
# script stops with both values printed. A file already in place is checked the
# same way. The Linux archive needs none of this.
set -eu

vc_runtime_dir="${VC_RUNTIME_DIR:-$(cd "$(dirname "$0")/.." && pwd)/vc-runtime}"

# Microsoft Visual C++ 2015-2022 Redistributable (x64) 14.44.35211.0, from the Visual Studio 2022 channel
vc_redist_url="https://download.visualstudio.microsoft.com/download/pr/bd1c8d9d-ba95-4eee-bc6e-df1fcc876373/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe"
vc_redist_version="14.44.35211.0"
vc_redist_size="25635768"
vc_redist_sha256="cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b"
# The four files the Windows archive ships, from the package's x64 Minimum runtime
msvcp140_sha256="0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9"
msvcp140_1_sha256="bfad5aef4c63a669e3c140655cdfdf395b6c979b400a447bd5dcb65ed8826c3d"
vcruntime140_sha256="d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066"
vcruntime140_1_sha256="1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f"

sha256_of() { # file
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
    else shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

check() { # file
    size="$(wc -c < "$1" | tr -d ' ')"
    if [ "$size" != "$vc_redist_size" ]; then
        echo "refused: $1 is $size bytes and the pinned size is $vc_redist_size" >&2
        return 1
    fi
    actual="$(sha256_of "$1")"
    if [ "$actual" != "$vc_redist_sha256" ]; then
        echo "refused: $1 hashes to $actual and the pinned SHA-256 is $vc_redist_sha256" >&2
        return 1
    fi
}

package="$vc_runtime_dir/VC_redist.x64.exe"
mkdir -p "$vc_runtime_dir"
if [ -f "$package" ]; then
    if ! check "$package"; then
        echo "Delete $package and run this again to download the pinned file." >&2
        exit 1
    fi
    echo "exists and verified: $package ($vc_redist_version)"
    exit 0
fi
echo "downloading $vc_redist_url..."
curl -fSL --retry 3 -o "$package.part" "$vc_redist_url"
if ! check "$package.part"; then
    rm -f "$package.part"
    exit 1
fi
mv "$package.part" "$package"
echo "verified: $package ($vc_redist_version)"
