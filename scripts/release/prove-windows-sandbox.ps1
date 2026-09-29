<#
.SYNOPSIS
    Runs inside Windows Sandbox: the clean-machine install of the Windows release
    archive, following only its INSTALL.txt, with what each step showed written to
    C:\results. prove-windows-sandbox.sh stages it and starts it on the host; that
    script's header says the whole proof.

.DESCRIPTION
    Before INSTALL.txt, three facts about the machine are checked and written down:
    no route out (the sandbox is configured with networking disabled), no .NET
    anywhere, and the archive matching SHA256SUMS. Then a PostgreSQL 17 is made
    from the pinned EDB binaries zip, because INSTALL.txt starts from "a PostgreSQL
    14 or later that you already run". Then INSTALL.txt, step by step: each step's
    own text is printed from the unzipped INSTALL.txt, and its commands are run as
    written, with only the placeholders filled in. The first administrator's
    password is a generated file given to setup with --admin-password-file rather
    than typed at setup's prompt, so the script runs unattended from the .wsb's
    logon command; it is never printed.

    Step 4 is run both ways INSTALL.txt gives: the API from a prompt, then, since
    the sandbox's account is an administrator, setup again with --windows-service
    and the service started with sc.exe. That second half is the clean-machine
    proof of the Windows service.

    The Visual C++ runtime: the machine is checked to have none in
    System32 or on PATH, and is never given one. The PostgreSQL the proof makes
    gets the four pinned files beside its own programs in C:\pg\pgsql\bin (from
    C:\input\vc-runtime, checked against its SHA256SUMS), which is on no PATH.
    The archive must ship the four files in bin\ (a release archive does: build.sh
    and verify-archive.sh refuse one without them), and they must be the only
    runtime prem can find, through the service half: the PATH prem inherits, and
    the machine's PATH the service gets, are printed and checked. If setup stops
    where it loads the model, the FAIL line carries what setup said, what the
    loader says about bin\onnxruntime.dll and which of its imports the loader
    cannot find.

    PostgreSQL's zip is checked against the pin the host read from
    installer\windows\fetch-postgresql.ps1 and staged as C:\input\postgresql.pin.
    Every FAIL line carries the exit code of the program its step ran.

    Nothing here needs a network. The sandbox is thrown away when it is closed;
    C:\results is the only thing that stays. The last thing written there is
    done.txt: PASS, or FAIL with the step, which the host watches for.

.EXAMPLE
    # In the sandbox, from an elevated PowerShell (the sandbox's account is one):
    powershell -ExecutionPolicy Bypass -File C:\input\prove-windows-sandbox.ps1
#>
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3

$results = "C:\results"
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
Start-Transcript -Path (Join-Path $results "transcript-$stamp.txt") | Out-Null
function Step([string] $what) { Write-Host "`n== $what" }
function Pass([string] $what) { Write-Host "PASS  $what" }
$done = Join-Path $results "done.txt"
# An exit code as the FAIL line shows it: a Windows status in hex beside the number
# (-1073741515 is 0xC0000135, a DLL not found), or "none" where no program ran.
function Show-ExitCode($code) {
    if ($null -eq $code) { return "none (no program ran)" }
    # As a number first: a string would be compared as text, and a leading hyphen ignored.
    $code = [int64] $code
    # The same four bytes read unsigned (0xFFFFFFFF is -1 to PowerShell 5.1, no mask).
    if ($code -lt 0) { return "{0} (0x{1:X8})" -f $code, [BitConverter]::ToUInt32([BitConverter]::GetBytes([int32] $code), 0) }
    return "$code"
}
# Every FAIL line carries the exit code of the program the step ran, or says none ran.
function Fail([string] $what, $code = $null) {
    $line = "FAIL  $what; exit code $(Show-ExitCode $code)"
    Write-Host $line; Set-Content $done $line; Stop-Transcript | Out-Null; exit 1
}
# Any error the script did not name ends the run the same way, so the host never waits on a run that stopped.
trap {
    $last = Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    $line = "FAIL  $_; the last program's exit code $(Show-ExitCode $last)"
    Write-Host $line; Set-Content $done $line; Stop-Transcript | Out-Null; exit 1
}
# The four files ONNX Runtime's Windows library imports and Windows does not carry.
$runtimeFiles = "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Loader {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
}
"@
# Loads a DLL as Windows would and says what the loader said: "loaded", or its own error text.
function Test-Load([string] $name, [uint32] $flags) {
    $module = [Loader]::LoadLibraryExW($name, [IntPtr]::Zero, $flags)
    if ($module -ne [IntPtr]::Zero) { [Loader]::FreeLibrary($module) | Out-Null; return "loaded" }
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    return "error $err, $((New-Object ComponentModel.Win32Exception $err).Message)"
}
# The DLL names a PE file imports, read from its import table.
function Get-Imports([string] $path) {
    $b = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($b, 0x3C); $opt = $pe + 24
    $dd = if ([BitConverter]::ToUInt16($b, $opt) -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $sections = [BitConverter]::ToUInt16($b, $pe + 6); $table = $opt + [BitConverter]::ToUInt16($b, $pe + 20)
    $offset = { param($rva) for ($i = 0; $i -lt $sections; $i++) { $s = $table + 40 * $i; $va = [BitConverter]::ToUInt32($b, $s + 12)
        $len = [Math]::Max([BitConverter]::ToUInt32($b, $s + 8), [BitConverter]::ToUInt32($b, $s + 16))
        if ($rva -ge $va -and $rva -lt $va + $len) { return [int]($rva - $va + [BitConverter]::ToUInt32($b, $s + 20)) } }; -1 }
    $names = @(); $at = & $offset ([BitConverter]::ToUInt32($b, $dd + 8))
    while ($at -gt 0 -and ($n = [BitConverter]::ToUInt32($b, $at + 12)) -ne 0) {
        $o = & $offset $n; $e = $o; while ($b[$e] -ne 0) { $e++ }
        $names += [Text.Encoding]::ASCII.GetString($b, $o, $e - $o); $at += 20
    }
    return $names
}
function Show-InstallStep([string] $number) {
    # The step's own words, from the INSTALL.txt in the archive, before it is run.
    $text = Get-Content (Join-Path $script:root "INSTALL.txt") -Raw
    $match = [regex]::Match($text, "(?ms)^$number\. .*?(?=^\d+\. |^The manual:)")
    if (-not $match.Success) { Fail "INSTALL.txt has no step $number" }
    Write-Host "--- INSTALL.txt step $number, as written:"
    Write-Host $match.Value.TrimEnd()
    Write-Host "---"
}

Step "The machine"
$zip = Get-ChildItem C:\input -Filter "premagentic-*-win-x64.zip" | Select-Object -First 1
if (-not $zip) { Fail "no premagentic-*-win-x64.zip in C:\input" }
$sums = Get-Content C:\input\SHA256SUMS | Where-Object { $_ -match [regex]::Escape($zip.Name) }
$actual = (Get-FileHash $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $sums -or ($sums -split '\s+')[0] -ne $actual) { Fail "$($zip.Name) does not match SHA256SUMS" }
Pass "$($zip.Name) matches SHA256SUMS ($actual)"
if (Get-Command dotnet -ErrorAction SilentlyContinue) { Fail "dotnet is on this machine; it is not clean" }
if (Test-Path "C:\Program Files\dotnet") { Fail "C:\Program Files\dotnet exists; the machine is not clean" }
Pass "no dotnet command and no .NET install"
$client = [System.Net.Sockets.TcpClient]::new()
try { $reached = $client.ConnectAsync("1.1.1.1", 443).Wait(5000) } catch { $reached = $false } finally { $client.Dispose() }
if ($reached) { Fail "this machine reached 1.1.1.1:443; the sandbox has a network" }
Pass "no route to 1.1.1.1:443"
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Fail "this run is not elevated; the service half needs an administrator" }
Pass "running elevated as $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
# No Visual C++ runtime anywhere Windows would look for one by name: not in System32,
# and not in any folder on this process's PATH, which every program started here inherits.
$pathFolders = @($env:PATH -split ';' | Where-Object { $_ })
Write-Host "PATH of this process, one folder a line:"
$pathFolders | ForEach-Object { Write-Host "  $_" }
$found = @(foreach ($folder in @("$env:WINDIR\System32") + $pathFolders) {
    foreach ($file in $runtimeFiles) { if (Test-Path -LiteralPath (Join-Path $folder $file)) { Join-Path $folder $file } }
})
if ($found.Count -gt 0) { Fail "a Visual C++ runtime is on this machine already: $($found -join ', ')" }
Pass "no $($runtimeFiles -join ', ') in System32 or on PATH"

Step "A PostgreSQL 17 that 'you already run', from the pinned EDB binaries"
# The repository's pin (installer\windows\fetch-postgresql.ps1), staged by the host as "<sha256>  <name>".
$pinned, $pgName = (Get-Content C:\input\postgresql.pin -Raw).Trim() -split '\s+', 2
$pgZip = Get-Item -LiteralPath (Join-Path C:\input $pgName) -ErrorAction SilentlyContinue
if (-not $pgZip) { Fail "no $pgName in C:\input" }
if ((Get-FileHash $pgZip.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pinned) { Fail "$pgName is not the pinned PostgreSQL zip" }
# Only what a server needs, pgsql's bin, lib and share, read straight from the zip:
# the whole zip is 21,961 files, 17,561 of them pgAdmin's, and Expand-Archive took
# 28 minutes over it in the first run.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$started = Get-Date
$extracted = 0
$pgArchive = [IO.Compression.ZipFile]::OpenRead($pgZip.FullName)
try {
    foreach ($entry in $pgArchive.Entries) {
        if ($entry.FullName -notmatch '^pgsql/(bin|lib|share)/' -or $entry.FullName.EndsWith('/')) { continue }
        $target = Join-Path C:\pg ($entry.FullName -replace '/', '\')
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        $extracted++
    }
} finally { $pgArchive.Dispose() }
if (-not (Test-Path C:\pg\pgsql\bin\initdb.exe)) { Fail "pgsql\bin\initdb.exe is not in the PostgreSQL zip's bin folder" }
Pass "$extracted files of pgsql's bin, lib and share extracted in $([int]((Get-Date) - $started).TotalSeconds) s"
# This PostgreSQL's own runtime, beside its programs and nowhere else: its binaries
# import the same four files, and nothing of it may reach the PATH prem inherits.
$sums = @{}
Get-Content C:\input\vc-runtime\SHA256SUMS | ForEach-Object { $hash, $name = $_ -split '\s+\*?', 2; $sums[$name] = $hash }
foreach ($file in $runtimeFiles) {
    $source = Join-Path C:\input\vc-runtime $file
    if (-not (Test-Path $source)) { Fail "C:\input\vc-runtime has no $file" }
    if ((Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sums[$file]) { Fail "C:\input\vc-runtime\$file does not match its SHA256SUMS line" }
    Copy-Item $source C:\pg\pgsql\bin\
}
Pass "the four pinned runtime files copied into C:\pg\pgsql\bin for this PostgreSQL only"
$pgPassword = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char] $_ })
Set-Content C:\pg\pw -Value $pgPassword -NoNewline
$adminPasswordFile = "C:\pg\first-admin.password"
Set-Content $adminPasswordFile -Value (-join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char] $_ })) -NoNewline
& C:\pg\pgsql\bin\initdb.exe -D C:\pg\data -U postgres -A scram-sha-256 --pwfile=C:\pg\pw -E UTF8 *> (Join-Path $results "initdb-$stamp.txt")
if ($LASTEXITCODE -ne 0) { Fail "initdb failed; see initdb-$stamp.txt" $LASTEXITCODE }
# Not through a pipe: pg_ctl leaves a cmd.exe running beside the server that keeps
# the standard output it inherited, so a pipe never closes and the script would
# wait forever (the first run of this proof did). Its streams go to files instead, and
# only pg_ctl itself is waited for.
$pgCtl = Start-Process -FilePath C:\pg\pgsql\bin\pg_ctl.exe -NoNewWindow -PassThru `
    -ArgumentList "-D", "C:\pg\data", "-l", (Join-Path $results "postgres-$stamp.log"), "-w", "start" `
    -RedirectStandardOutput (Join-Path $results "pg_ctl-$stamp.out") -RedirectStandardError (Join-Path $results "pg_ctl-$stamp.err")
$null = $pgCtl.Handle
$pgCtl.WaitForExit()
if ($pgCtl.ExitCode -ne 0) { Fail "PostgreSQL did not start; see pg_ctl-$stamp.out and .err" $pgCtl.ExitCode }
Pass "PostgreSQL 17.11 runs on localhost:5432, superuser postgres"

Step "INSTALL.txt step 1"
$live = "C:\Program Files\PremAgentic"
Expand-Archive $zip.FullName -DestinationPath $live
$script:root = (Get-ChildItem $live -Directory | Select-Object -First 1).FullName
Show-InstallStep 1
Get-ChildItem $script:root | Format-Table Name, Length -AutoSize | Out-String | Write-Host
Pass "unzipped into $script:root"
Set-Location $script:root
# A release archive ships the four files in bin\, the only runtime prem may find.
$shipped = @($runtimeFiles | Where-Object { Test-Path (Join-Path $script:root "bin\$_") })
if ($shipped.Count -ne $runtimeFiles.Count) { Fail "the archive ships $($shipped.Count) of the four Visual C++ runtime files in bin\: $($shipped -join ', ')" }
Pass "the archive ships $($runtimeFiles -join ', ') in bin\"
& .\bin\prem.exe --version
if ($LASTEXITCODE -ne 0) { Fail "prem --version failed" $LASTEXITCODE }
Pass "prem --version runs, which loads no model"
# The folders Windows searches last for a DLL a program imports: a PATH entry holding
# the runtime would stand in for the archive's own copies.
function Assert-PathHoldsNoRuntime([string] $whose, [string] $pathText) {
    $folders = @($pathText -split ';' | Where-Object { $_ })
    Write-Host "PATH of $whose, one folder a line:"
    $folders | ForEach-Object { Write-Host "  $_" }
    $reached = @(foreach ($folder in $folders) {
        $full = try { [IO.Path]::GetFullPath($folder).TrimEnd('\') } catch { $folder }
        if ($full -ieq 'C:\pg\pgsql\bin') { $folder; continue }
        foreach ($file in $runtimeFiles) { if (Test-Path -LiteralPath (Join-Path $folder $file)) { Join-Path $folder $file } }
    })
    if ($reached.Count -gt 0) { Fail "the PATH of $whose reaches a Visual C++ runtime: $($reached -join ', ')" }
    Pass "the PATH of $whose holds no Visual C++ runtime and not C:\pg\pgsql\bin"
}

Step "INSTALL.txt step 2"
Show-InstallStep 2
$adminFile = "C:\pg\admin.connection"
Set-Content $adminFile -Value "connection=Host=localhost;Username=postgres;Password=$pgPassword;Database=postgres" -NoNewline
Remove-Variable pgPassword
Pass "the admin connection file is $adminFile (its contents are not written here)"

Step "INSTALL.txt step 3"
Show-InstallStep 3
$hostName = $env:COMPUTERNAME
# With networking disabled the sandbox has no network adapter, so its own name does not
# resolve (curl exit 6, ping cannot find it; the API was listening). Every /health call
# pins the name to this machine, and the certificate is still verified against that name.
# A person's machine resolves its own name; this is a departure of the proof, not a step.
$pinnedName = "${hostName}:8443:127.0.0.1"
Assert-PathHoldsNoRuntime "the prem process (this script's, which prem inherits)" $env:PATH
# Setup's own output is kept whole in the results folder; its streams go to files,
# not through PowerShell, so a line on standard error cannot end the script early.
$setupOut = Join-Path $results "setup-$stamp.out"
$setupErr = Join-Path $results "setup-$stamp.err"
$setup = Start-Process -FilePath (Join-Path $script:root "bin\prem.exe") -NoNewWindow -PassThru `
    -ArgumentList "setup", "--admin-connection-file", $adminFile, "--admin-user", "first-admin", "--admin-password-file", $adminPasswordFile, "--host-name", $hostName `
    -RedirectStandardOutput $setupOut -RedirectStandardError $setupErr
$null = $setup.Handle
$setup.WaitForExit()
$setupExit = $setup.ExitCode
$said = @(Get-Content $setupOut, $setupErr)
$said | Write-Host
$modelLine = $said | Where-Object { $_ -match 'onnxruntime' } | Select-Object -First 1
if ($setupExit -ne 0 -and $modelLine) {
    # Setup stopped where it loads the model: what the loader says about ONNX
    # Runtime's library, loaded from its own folder first as .NET loads it
    # (LOAD_WITH_ALTERED_SEARCH_PATH), and which of the library's imports it
    # cannot find: neither beside it nor in System32 (LOAD_LIBRARY_SEARCH_SYSTEM32),
    # with the PATH shown above to hold none.
    $onnx = Join-Path $script:root "bin\onnxruntime.dll"
    $loaderSays = Test-Load $onnx 0x8
    $missing = @(foreach ($import in Get-Imports $onnx) {
        if (Test-Path (Join-Path (Split-Path $onnx) $import)) { continue }
        $result = Test-Load $import 0x800
        if ($result -ne "loaded") { "$import ($result)" }
    })
    Fail ("prem setup stopped at the model; setup said: $($modelLine.Trim()); the loader on bin\onnxruntime.dll: $loaderSays; " +
        "the imports the loader cannot find: $($missing -join ', ')") $setupExit
}
if ($setupExit -ne 0) { Fail "prem setup failed; its output is in setup-$stamp.out and .err" $setupExit }
$credentials = Join-Path $env:LOCALAPPDATA "Premagentic"
Pass "setup finished; its files are in $credentials"

Step "INSTALL.txt step 4, from a prompt"
Show-InstallStep 4
$env:PREM_CREDENTIALS_FILE = Join-Path $credentials "app.credentials"
$api = Start-Process -FilePath .\bin\Premagentic.Api.exe -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $results "api-$stamp.out") -RedirectStandardError (Join-Path $results "api-$stamp.err")
$ok = $false
foreach ($i in 1..180) { & curl.exe --silent --fail --cacert (Join-Path $credentials "https.crt") --resolve $pinnedName "https://${hostName}:8443/health" *> $null; if ($LASTEXITCODE -eq 0) { $ok = $true; break }; Start-Sleep 1 }
if (-not $ok) { Fail "the API did not answer /health over HTTPS within 180 s; the exit code is curl's last" $LASTEXITCODE }
& curl.exe --silent --cacert (Join-Path $credentials "https.crt") --resolve $pinnedName "https://${hostName}:8443/health" | Write-Host
Pass "the API answers /health over HTTPS, the certificate verified against https.crt"

Step "INSTALL.txt step 5"
Show-InstallStep 5
foreach ($command in @(@("profile", "apply", "samples\profiles\starter"), @("ingest", "--source", "open"), @("ingest", "--source", "hr"))) {
    & .\bin\prem.exe @command
    if ($LASTEXITCODE -ne 0) { Fail "prem $($command -join ' ') failed" $LASTEXITCODE }
}
& .\bin\prem.exe search "how long do I have to file an expense claim" --top 3
Pass "the starter profile is applied and both sources are ingested"

if (Test-Path (Join-Path $script:root "extensions")) {
    Step "INSTALL.txt step 6"
    Show-InstallStep 6
    & .\bin\prem.exe settings set extensions.folder (Join-Path $script:root "extensions")
    & .\bin\prem.exe extensions allow (Join-Path $script:root "extensions\pdf-reader")
    & .\bin\prem.exe extensions allow (Join-Path $script:root "extensions\docx-reader")
    & .\bin\prem.exe extensions allow (Join-Path $script:root "extensions\xlsx-reader")
    $listed = & .\bin\prem.exe extensions list
    if ($LASTEXITCODE -ne 0) { Fail "prem extensions list failed" $LASTEXITCODE }
    $listed | Write-Host
    foreach ($reader in "pdf-reader", "docx-reader", "xlsx-reader") {
        if (-not ($listed -match "^  $reader  [0-9a-fA-F]{64}  \(loaded\)")) { Fail "$reader is not allowed and loaded" }
    }
    Pass "the three readers allowed from the archive and loaded"
}

Step "INSTALL.txt step 4, as a Windows service"
Stop-Process -Id $api.Id -Force
$api.WaitForExit(15000) | Out-Null
# A service is started with the machine's environment, not this script's.
Assert-PathHoldsNoRuntime "the machine, which the service is started with" ([Environment]::GetEnvironmentVariable("PATH", "Machine"))
& .\bin\prem.exe setup --admin-connection-file $adminFile --admin-user first-admin --admin-password-file $adminPasswordFile --host-name $hostName --windows-service `
    --credentials-dir C:\ProgramData\Premagentic
if ($LASTEXITCODE -ne 0) { Fail "prem setup --windows-service failed" $LASTEXITCODE }
& sc.exe qc PremAgentic | Write-Host
& sc.exe start PremAgentic | Write-Host
$ok = $false
foreach ($i in 1..180) { & curl.exe --silent --fail --cacert C:\ProgramData\Premagentic\https.crt --resolve $pinnedName "https://${hostName}:8443/health" *> $null; if ($LASTEXITCODE -eq 0) { $ok = $true; break }; Start-Sleep 1 }
& sc.exe query PremAgentic | Write-Host
if (-not $ok) { Fail "the service did not answer /health over HTTPS within 180 s; the exit code is curl's last" $LASTEXITCODE }
Pass "the Windows service PremAgentic runs bin\Premagentic.Api.exe and answers /health over HTTPS"

$verdict = "PASS  the Windows archive $($zip.Name) installed on a clean machine by INSTALL.txt, from a prompt and as a service, " +
    "with the Visual C++ runtime files it ships in bin\ as the only ones prem can find"

# Only when the host staged it (stage --remove wrote C:\input\mode-remove.txt): prem remove
# for this service install, as the manual's "Removing PremAgentic"
# (docs/upgrading-and-removing.md) gives it. The archive's INSTALL.txt has no removal
# step. The same checks run before (the control: each must find what setup made) and
# after (each must find it gone).
if (Test-Path C:\input\mode-remove.txt) {
    Step "prem remove, the manual's removal of a service install, with the data (--purge --yes)"
    $env:PGPASSWORD = (Get-Content C:\pg\pw -Raw)
    function Get-Installed {
        & sc.exe query PremAgentic *> $null
        $service = $LASTEXITCODE
        $database = & C:\pg\pgsql\bin\psql.exe -h localhost -U postgres -d postgres -tAc "SELECT count(*) FROM pg_database WHERE datname = 'premagentic'"
        $roles = & C:\pg\pgsql\bin\psql.exe -h localhost -U postgres -d postgres -tAc "SELECT count(*) FROM pg_roles WHERE rolname IN ('premagentic_owner', 'premagentic_app', 'premagentic_search')"
        $files = @(Get-ChildItem C:\ProgramData\Premagentic -File -ErrorAction SilentlyContinue | ForEach-Object Name)
        [pscustomobject]@{ ServiceQueryExit = $service; Databases = [int] "$database".Trim(); Roles = [int] "$roles".Trim();
            FolderExists = (Test-Path C:\ProgramData\Premagentic); Files = $files }
    }
    $before = Get-Installed
    Write-Host "before: sc.exe query exit $($before.ServiceQueryExit); databases $($before.Databases); roles $($before.Roles); C:\ProgramData\Premagentic $(if ($before.FolderExists) { 'holds ' + ($before.Files -join ', ') } else { 'absent' })"
    if ($before.ServiceQueryExit -ne 0 -or $before.Databases -ne 1 -or $before.Roles -ne 3 -or -not $before.FolderExists -or $before.Files.Count -eq 0) {
        Fail "control: before prem remove the checks do not find what setup made (service query exit $($before.ServiceQueryExit), databases $($before.Databases), roles $($before.Roles))"
    }
    Pass "control: before prem remove, the service PremAgentic is registered, the database and its three roles exist, and C:\ProgramData\Premagentic holds $($before.Files.Count) files"
    & .\bin\prem.exe remove --plan --purge --windows-service --credentials-dir C:\ProgramData\Premagentic --admin-connection-file $adminFile | Write-Host
    if ($LASTEXITCODE -ne 0) { Fail "prem remove --plan failed" $LASTEXITCODE }
    Pass "prem remove --plan listed its steps"
    & .\bin\prem.exe remove --purge --yes --windows-service --credentials-dir C:\ProgramData\Premagentic --admin-connection-file $adminFile | Write-Host
    if ($LASTEXITCODE -ne 0) { Fail "prem remove --purge --yes failed" $LASTEXITCODE }
    Pass "prem remove --purge --yes finished"
    $after = Get-Installed
    Remove-Item Env:PGPASSWORD
    Write-Host "after: sc.exe query exit $($after.ServiceQueryExit); databases $($after.Databases); roles $($after.Roles); C:\ProgramData\Premagentic $(if ($after.FolderExists) { 'holds ' + ($after.Files -join ', ') } else { 'absent' })"
    if ($after.ServiceQueryExit -ne 1060) { Fail "the service PremAgentic is still registered after prem remove (sc.exe query exit $($after.ServiceQueryExit), not 1060)" }
    if ($after.Databases -ne 0 -or $after.Roles -ne 0) { Fail "after prem remove: $($after.Databases) database(s) and $($after.Roles) role(s) are left" }
    if ($after.Files.Count -ne 0) { Fail "after prem remove, C:\ProgramData\Premagentic still holds $($after.Files -join ', ')" }
    Pass "after prem remove: no service PremAgentic (sc.exe query exit 1060), no database premagentic and none of its three roles, and no credentials files in C:\ProgramData\Premagentic"
    $verdict = "PASS  with prem remove: the Windows archive $($zip.Name) installed on a clean machine by INSTALL.txt, from a prompt and as a service, " +
        "and prem remove --purge took the service, the database, its roles and the credentials files away"
}
Write-Host "`n$verdict"
Set-Content $done $verdict
Stop-Transcript | Out-Null
