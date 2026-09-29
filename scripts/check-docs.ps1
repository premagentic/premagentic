#!/usr/bin/env pwsh
# Checks the manual's links and its index. The same checks, output and exit
# code as scripts/check-docs.sh, for a PowerShell shell:
#
#   scripts/check-docs.ps1 [-AllowMissing slug,slug]
#
# Over README.md and docs/*.md: every relative link and image resolves to a
# file, and every #anchor to a heading in the file it names. docs/toc.json and
# docs/README.md list the same slugs in the same order, every slug has a file,
# and each page's first heading is the title toc.json gives it. Fenced code is
# not scanned. Each failure is printed with its file and line, and the exit
# code is 1 when there was any and 0 when the manual is clean.
#
# -AllowMissing names pages that are not written yet: their files may be
# absent and links to them are not followed.
param([string]$AllowMissing = "")

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
# The .NET file calls below read the process's own working folder, which
# Set-Location does not move; without this line the script only works when
# started from the repository root.
[System.Environment]::CurrentDirectory = (Get-Location).Path

$allow = @($AllowMissing -split "," | Where-Object { $_ -ne "" })
$script:failures = 0
$script:links = 0
function Fail($message) { Write-Output "FAIL $message"; $script:failures++ }

# The lines outside fenced code, each with its line number.
function Unfenced($file) {
    $inFence = $false
    $n = 0
    foreach ($text in [System.IO.File]::ReadAllLines($file)) {
        $n++
        if ($text -match '^\s*```') { $inFence = -not $inFence; continue }
        if (-not $inFence) { [pscustomobject]@{ Line = $n; Text = $text } }
    }
}

# The anchors GitHub gives a file's headings: lowercase, punctuation dropped,
# spaces to hyphens, and a repeated heading numbered -1, -2 and so on.
function Anchors($file) {
    $seen = @{}
    foreach ($entry in Unfenced $file) {
        if ($entry.Text -match '^#{1,6} (.*)$') {
            $anchor = ($Matches[1].ToLowerInvariant() -replace '[^a-z0-9 _-]', '') -replace ' ', '-'
            $count = if ($seen.ContainsKey($anchor)) { $seen[$anchor] } else { 0 }
            $seen[$anchor] = $count + 1
            if ($count) { "$anchor-$count" } else { $anchor }
        }
    }
}

# Every link and image target outside fenced code, with its line.
function Targets($file) {
    foreach ($entry in Unfenced $file) {
        foreach ($match in [regex]::Matches($entry.Text, '\]\(([^)]*)\)')) {
            [pscustomobject]@{ Line = $entry.Line; Target = $match.Groups[1].Value }
        }
    }
}

# The first heading of a file, without its marks.
function FirstHeading($file) {
    foreach ($entry in Unfenced $file) {
        if ($entry.Text -match '^# (.*)$') { return $Matches[1] }
    }
    return ""
}

function CheckFile($file) {
    $dir = if ($file.StartsWith("docs/")) { "docs" } else { "." }
    foreach ($link in Targets $file) {
        $script:links++
        $target = $link.Target
        if ($target -match '^(https?://|mailto:)') { continue }
        $path = ($target -split '#', 2)[0]
        $anchor = if ($target.Contains('#')) { ($target -split '#', 2)[1] } else { "" }
        if ($path -ne "") {
            $resolved = if ($dir -eq ".") { $path } else { "docs/$path" }
            $resolved = $resolved -replace '^\./', ''
            if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
                if ($resolved -match '^docs/(.*)\.md$' -and $allow -contains $Matches[1]) { continue }
                Fail "${file}:$($link.Line): '$target' does not resolve to a file"
                continue
            }
        } else {
            $resolved = $file
        }
        if ($anchor -ne "") {
            if (-not $resolved.EndsWith(".md")) {
                Fail "${file}:$($link.Line): '#$anchor' names a file that is not Markdown"
                continue
            }
            if (-not (@(Anchors $resolved) -contains $anchor)) {
                Fail "${file}:$($link.Line): '#$anchor' is not a heading in $resolved"
            }
        }
    }
}

$files = 0
$pages = @(Get-ChildItem -Path docs -Filter *.md | Sort-Object Name | ForEach-Object { "docs/" + $_.Name })
foreach ($file in @("README.md") + $pages) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    $files++
    CheckFile $file
}

# The index: toc.json and docs/README.md name the same pages in the same order.
# toc.json is read by its "slug" and "title" tokens in order, one page's slug
# followed by that page's title; a section's title has no slug before it.
$tocPairs = @()
$pendingSlug = ""
foreach ($match in [regex]::Matches([System.IO.File]::ReadAllText("docs/toc.json"), '"(slug|title)"\s*:\s*"([^"]*)"')) {
    if ($match.Groups[1].Value -eq "slug") { $pendingSlug = $match.Groups[2].Value }
    elseif ($pendingSlug -ne "") { $tocPairs += [pscustomobject]@{ Slug = $pendingSlug; Title = $match.Groups[2].Value }; $pendingSlug = "" }
}
$tocSlugs = @($tocPairs | ForEach-Object { $_.Slug })
$indexSlugs = @(Unfenced "docs/README.md" | ForEach-Object {
    if ($_.Text -match '^- \[[^\]]*\]\(([A-Za-z0-9_.-]*)\.md\)') { $Matches[1] }
})

if ($tocSlugs.Count -eq 0) {
    Fail "docs/toc.json: no pages found"
} elseif (($tocSlugs -join "`n") -ne ($indexSlugs -join "`n")) {
    Fail "docs/toc.json and docs/README.md do not list the same pages in the same order:"
    $max = [Math]::Max($tocSlugs.Count, $indexSlugs.Count)
    for ($i = 0; $i -lt $max; $i++) {
        $a = if ($i -lt $tocSlugs.Count) { $tocSlugs[$i] } else { "(none)" }
        $b = if ($i -lt $indexSlugs.Count) { $indexSlugs[$i] } else { "(none)" }
        if ($a -ne $b) { Write-Output "      $($i + 1): toc.json '$a', docs/README.md '$b'" }
    }
}

foreach ($pair in $tocPairs) {
    $page = "docs/$($pair.Slug).md"
    if (-not (Test-Path -LiteralPath $page -PathType Leaf)) {
        if ($allow -contains $pair.Slug) { Write-Output "note  $page is not written yet (allowed)"; continue }
        Fail "docs/toc.json: '$($pair.Slug)' has no $page"
        continue
    }
    $heading = FirstHeading $page
    if ($heading -ne $pair.Title) {
        Fail "${page}: its first heading is '$heading' but docs/toc.json says '$($pair.Title)'"
    }
}

Write-Output "checked $files file(s), $($script:links) link(s); failures: $($script:failures)"
if ($script:failures -ne 0) { exit 1 }
exit 0
