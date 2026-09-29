#!/usr/bin/env sh
# Downloads the ONNX export of the default local embedding model into
# models/minilm (gitignored). Pass --all to also fetch bge-small-en-v1.5.
# An optional MODELS_DIR environment variable overrides the target directory.
#
# Every file comes from a named revision of its repository and must hash to
# the SHA-256 recorded beside it here; a file that does not is deleted and
# the script stops with both hashes printed. A file already in place is
# checked the same way. scripts/download-model.ps1 pins the same revisions
# and hashes.
set -eu

models_dir="${MODELS_DIR:-$(cd "$(dirname "$0")/.." && pwd)/models}"

# sentence-transformers/all-MiniLM-L6-v2 at revision 1110a243fdf4706b3f48f1d95db1a4f5529b4d41
minilm_base="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
minilm_model_sha256="6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
minilm_vocab_sha256="07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
# Xenova/bge-small-en-v1.5 at revision ea104dacec62c0de699686887e3f920caeb4f3e3 (the ONNX export)
bge_model_url="https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/ea104dacec62c0de699686887e3f920caeb4f3e3/onnx/model.onnx"
bge_model_sha256="828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35"
# BAAI/bge-small-en-v1.5 at revision 5c38ec7c405ec4b44b94cc5a9bb96e735b38267a (the vocabulary)
bge_vocab_url="https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/5c38ec7c405ec4b44b94cc5a9bb96e735b38267a/vocab.txt"
bge_vocab_sha256="07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"

sha256_of() { # file
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
    else shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

check() { # file expected
    actual="$(sha256_of "$1")"
    if [ "$actual" != "$2" ]; then
        echo "refused: $1 hashes to $actual and the pinned SHA-256 is $2" >&2
        return 1
    fi
}

fetch() { # url target sha256
    if [ -f "$2" ]; then
        if ! check "$2" "$3"; then
            echo "Delete $2 and run this again to download the pinned file." >&2
            exit 1
        fi
        echo "exists and verified: $2"
        return
    fi
    echo "downloading $1..."
    curl -fSL --retry 3 -o "$2.part" "$1"
    if ! check "$2.part" "$3"; then
        rm -f "$2.part"
        exit 1
    fi
    mv "$2.part" "$2"
    echo "verified: $2"
}

minilm="$models_dir/minilm"
mkdir -p "$minilm"
fetch "$minilm_base/onnx/model.onnx" "$minilm/model.onnx" "$minilm_model_sha256"
fetch "$minilm_base/vocab.txt"       "$minilm/vocab.txt"  "$minilm_vocab_sha256"

if [ "${1:-}" = "--all" ]; then
    bge="$models_dir/bge-small"
    mkdir -p "$bge"
    fetch "$bge_model_url" "$bge/model.onnx" "$bge_model_sha256"
    fetch "$bge_vocab_url" "$bge/vocab.txt"  "$bge_vocab_sha256"
    if [ ! -f "$bge/prem-model.json" ]; then
        printf '%s' '{ "name": "local:bge-small-en-v1.5", "pooling": "cls", "dimensions": 384 }' > "$bge/prem-model.json"
        echo "wrote $bge/prem-model.json"
    fi
fi

echo "Models ready in $models_dir"
