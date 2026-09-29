# Downloads the ONNX export of the default local embedding model into
# models/minilm (gitignored). Pass -All to also fetch bge-small-en-v1.5, as
# scripts/download-model.sh does with --all.
# - models/minilm/    all-MiniLM-L6-v2 (mean pooling; no config file = legacy default)
# - models/bge-small/ bge-small-en-v1.5 (CLS pooling; prem-model.json written), with -All
#
# Every file comes from a named revision of its repository and must hash to
# the SHA-256 recorded beside it here; a file that does not is deleted and
# the script stops with both hashes printed. A file already in place is
# checked the same way. scripts/download-model.sh pins the same revisions and
# hashes.
param([switch]$All)
$ErrorActionPreference = "Stop"

# sentence-transformers/all-MiniLM-L6-v2 at revision 1110a243fdf4706b3f48f1d95db1a4f5529b4d41
$minilmBase = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
$minilmModelSha256 = "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
$minilmVocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
# Xenova/bge-small-en-v1.5 at revision ea104dacec62c0de699686887e3f920caeb4f3e3 (the ONNX export)
$bgeModelUrl = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/ea104dacec62c0de699686887e3f920caeb4f3e3/onnx/model.onnx"
$bgeModelSha256 = "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35"
# BAAI/bge-small-en-v1.5 at revision 5c38ec7c405ec4b44b94cc5a9bb96e735b38267a (the vocabulary)
$bgeVocabUrl = "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/5c38ec7c405ec4b44b94cc5a9bb96e735b38267a/vocab.txt"
$bgeVocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"

function Test-Pinned($file, $expected) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Write-Host "refused: $file hashes to $actual and the pinned SHA-256 is $expected"
        return $false
    }
    return $true
}

function Get-ModelFile($uri, $target, $sha256) {
    if (Test-Path $target) {
        if (-not (Test-Pinned $target $sha256)) {
            Write-Host "Delete $target and run this again to download the pinned file."
            exit 1
        }
        Write-Host "exists and verified: $target"
        return
    }
    Write-Host "downloading $uri..."
    $part = "$target.part"
    Invoke-WebRequest -Uri $uri -OutFile $part
    if (-not (Test-Pinned $part $sha256)) {
        Remove-Item -Force $part
        exit 1
    }
    Move-Item -Force $part $target
    Write-Host "verified: $target"
}

# --- all-MiniLM-L6-v2 ---
$minilm = Join-Path $PSScriptRoot "..\models\minilm"
New-Item -ItemType Directory -Force -Path $minilm | Out-Null
Get-ModelFile "$minilmBase/onnx/model.onnx" (Join-Path $minilm "model.onnx") $minilmModelSha256
Get-ModelFile "$minilmBase/vocab.txt"       (Join-Path $minilm "vocab.txt")  $minilmVocabSha256

# --- bge-small-en-v1.5 (Xenova ONNX export; vocab from the BAAI source repo), only with -All ---
if ($All) {
    $bge = Join-Path $PSScriptRoot "..\models\bge-small"
    New-Item -ItemType Directory -Force -Path $bge | Out-Null
    Get-ModelFile $bgeModelUrl (Join-Path $bge "model.onnx") $bgeModelSha256
    Get-ModelFile $bgeVocabUrl (Join-Path $bge "vocab.txt")  $bgeVocabSha256
    $config = Join-Path $bge "prem-model.json"
    if (-not (Test-Path $config)) {
        '{ "name": "local:bge-small-en-v1.5", "pooling": "cls", "dimensions": 384 }' | Set-Content -Path $config -NoNewline
        Write-Host "wrote $config"
    }
}

Write-Host "Models ready."
