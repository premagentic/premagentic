<#
.SYNOPSIS
    Lays out the PostgreSQL that Premagentic bundles on Windows: the minimal set
    from EDB's Windows x64 binaries archive, and the license notices it needs.

.DESCRIPTION
    Downloads the pinned archive (about 380 MB; never committed) into a cache
    folder, refuses it unless its SHA-256 is the pinned one, and extracts only:

      pgsql/bin    initdb, pg_ctl, postgres, pg_dump, pg_restore, psql, and the
                   13 DLLs they load (read from their import tables)
      pgsql/lib    plpgsql.dll and dict_snowball.dll, which initdb loads
      pgsql/share  everything but message translations (locale) and docs, and
                   in share/extension only plpgsql; timezone must stay, because
                   PostgreSQL on Windows reads its own time zone data
      licenses     the PostgreSQL License and EDB's third-party notices from the
                   archive, plus the ICU and winpthreads licenses, which EDB's
                   file lacks, fetched from their own projects by pinned hash

    The Visual C++ 2015 to 2022 x64 runtime is not in the archive and is not
    copied: it is a prerequisite the Windows installer installs, and
    prem setup checks for it.

    Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
    ./installer/windows/fetch-postgresql.ps1 -Destination build/postgresql
    prem setup --bundled-postgres build/postgresql/pgsql --data-dir D:\Premagentic\data ...
#>
param(
    [string] $Destination = (Join-Path (Get-Location) "build/postgresql"),
    [string] $Cache = (Join-Path ([System.IO.Path]::GetTempPath()) "premagentic-cache")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3

# PostgreSQL 17, the major version Premagentic is tested on. The hash was taken
# when the archive was first downloaded; EDB does not publish one.
$Archive = @{
    Url    = "https://get.enterprisedb.com/postgresql/postgresql-17.11-4-windows-x64-binaries.zip"
    Name   = "postgresql-17.11-4-windows-x64-binaries.zip"
    Sha256 = "b9424ee7bc60b52450ff910a3630225df32e633f3cb29c1d126d9299d59aea28"
}
$Notices = @(
    @{ Url = "https://raw.githubusercontent.com/unicode-org/icu/release-67-1/icu4c/LICENSE"
       Name = "ICU-67-LICENSE.txt"
       Sha256 = "25e21013a7bc2fad735e28c5278a120e4c7f1c327c8c8b9b4df1751748cddbb2" },
    @{ Url = "https://raw.githubusercontent.com/mingw-w64/mingw-w64/v12.0.0/mingw-w64-libraries/winpthreads/COPYING"
       Name = "winpthreads-COPYING.txt"
       Sha256 = "63263614cdd29f2f93cba85e992f041b31f9fc7b4033692f31269489a8a1b177" }
)
$Tools = "initdb.exe", "pg_ctl.exe", "postgres.exe", "pg_dump.exe", "pg_restore.exe", "psql.exe"
$Dlls = "icudt67.dll", "icuin67.dll", "icuuc67.dll", "libcrypto-3-x64.dll", "libiconv-2.dll", "libintl-9.dll",
        "liblz4.dll", "libpq.dll", "libssl-3-x64.dll", "libwinpthread-1.dll", "libxml2.dll", "libzstd.dll", "zlib1.dll"
$Modules = "plpgsql.dll", "dict_snowball.dll"
$ArchiveNotices = "server_license.txt", "commandlinetools_3rd_party_licenses.txt"
$ExpectedFiles = 677

function Get-Pinned([hashtable] $Item, [string] $Path) {
    if (Test-Path $Path) {
        if ((Get-FileHash -Algorithm SHA256 $Path).Hash -eq $Item.Sha256) { return }
        Remove-Item $Path
    }
    Write-Host "Downloading $($Item.Url)"
    $partial = "$Path.partial"
    # TLS 1.2 for Windows PowerShell 5.1, which may default to older versions.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Item.Url -OutFile $partial -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $partial).Hash
    if ($actual -ne $Item.Sha256) {
        Remove-Item $partial
        throw "$($Item.Url) has SHA-256 $actual, not the pinned $($Item.Sha256). Nothing was extracted."
    }
    Move-Item $partial $Path -Force
}

function Test-Wanted([string] $Entry) {
    $parts = $Entry.Split("/")
    if ($parts.Length -lt 3 -or $parts[0] -ne "pgsql" -or $Entry.EndsWith("/")) { return $false }
    switch ($parts[1]) {
        "bin"   { return $parts.Length -eq 3 -and ($Tools + $Dlls) -contains $parts[2] }
        "lib"   { return $parts.Length -eq 3 -and $Modules -contains $parts[2] }
        "share" {
            if ($parts[2] -in "locale", "doc") { return $false }
            if ($parts[2] -eq "extension") { return $parts.Length -eq 4 -and $parts[3].StartsWith("plpgsql") }
            return $true
        }
        default { return $false }
    }
}

New-Item -ItemType Directory -Force -Path $Cache | Out-Null
$zipPath = Join-Path $Cache $Archive.Name
Get-Pinned $Archive $zipPath

if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
$licenses = Join-Path $Destination "licenses"
New-Item -ItemType Directory -Force -Path $licenses | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        if (Test-Wanted $name) {
            $target = Join-Path $Destination $name
        } elseif ($name.StartsWith("pgsql/") -and $name.Split("/").Length -eq 2 -and $ArchiveNotices -contains $name.Split("/")[1]) {
            $target = Join-Path $licenses ("PostgreSQL-" + $name.Split("/")[1])
        } else {
            continue
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally {
    $zip.Dispose()
}

foreach ($notice in $Notices) { Get-Pinned $notice (Join-Path $licenses $notice.Name) }

foreach ($file in $Tools + $Dlls) {
    if (-not (Test-Path (Join-Path $Destination "pgsql/bin/$file"))) { throw "The archive has no pgsql/bin/$file." }
}
$files = @(Get-ChildItem -Recurse -File $Destination)
$megabytes = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
if ($files.Count -ne $ExpectedFiles) {
    throw "Laid out $($files.Count) files, not the expected $ExpectedFiles. The archive or the selection has changed."
}
Write-Host "PostgreSQL 17.11 laid out in $Destination`: $($files.Count) files, $megabytes MB (PostgreSQL in pgsql, notices in licenses)."
