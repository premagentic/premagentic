<#
.SYNOPSIS
    Proves that the Windows release archive runs on this machine with no .NET SDK
    or runtime reachable, by following its INSTALL.txt against a throwaway
    database.

.DESCRIPTION
    Unzips the archive into a folder of its own (by default archive-smoke in your
    home folder: never the temp folder, whose cleaner goes by file dates, and never
    the repository), then runs every step with dotnet taken off PATH and every
    DOTNET_ variable unset: prem --version, prem setup with the first
    administrator (into a database and three roles of its own, on the compose
    development PostgreSQL), the starter profile and the ingest of its two sources,
    the PDF, Word and Excel readers the archive ships, allowed by hash and reading
    an invented PDF and Word file and the sample equipment register workbook
    (before they are allowed, the same files are all skipped), the API, /health over HTTPS verified against the certificate setup
    made, an agent's search over MCP with its token that finds the documents and
    the PDF's second page, and the stdio MCP bridge started from the archive and
    answering initialize. The API's and the bridge's loaded modules are read to
    show that the one runtime in bin\ is theirs and none came from a .NET install.

    The control: the model folder is moved away and the same search must stop at
    once with the one line naming where the model was looked for.

    At the end the API is stopped, prem remove --purge drops the database and the
    three roles, and the folder is deleted. No password or token is printed.

    This proves the archive runs on a machine without the SDK. It is NOT the
    clean-machine proof of the Windows service, which needs a clean Windows
    machine: prove-windows-sandbox.sh is that proof.

.EXAMPLE
    docker compose up -d
    ./scripts/release/prove-windows-archive.ps1 -Archive C:\release\premagentic-0.2.0-win-x64.zip
#>
param(
    [Parameter(Mandatory)] [string] $Archive,
    [string] $Destination = (Join-Path $HOME "archive-smoke"),
    [int] $DatabasePort = 5434,
    [string] $DatabaseUser = "prem",
    [int] $HttpsPort = 8543
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3

function Pass([string] $what) { Write-Host "PASS  $what" }
function Fail([string] $what) { throw "FAIL  $what" }
function Step([string] $what) { Write-Host "`n== $what" }

# Runs a program with its output captured whole, and returns its exit code and output.
function Run([string] $program, [string[]] $arguments) {
    $output = & $program @arguments 2>&1 | Out-String
    return @{ Exit = $LASTEXITCODE; Output = $output }
}

$Archive = (Resolve-Path $Archive).Path
$name = Split-Path -Leaf $Archive
if ($name -notmatch '^premagentic-(.+)-win-x64\.zip$') { throw "$name is not a win-x64 release archive" }
$version = $Matches[1]
$repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$Destination = [System.IO.Path]::GetFullPath($Destination)
foreach ($forbidden in @([System.IO.Path]::GetTempPath(), $repo)) {
    if ($Destination.StartsWith([System.IO.Path]::GetFullPath($forbidden).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Destination is inside $forbidden; choose a folder outside the temp folder and the repository"
    }
}
if (Test-Path $Destination) { throw "$Destination already exists; this script deletes only a folder it made" }

# Git for Windows' own curl, found above whichever git.exe the PATH gives: from
# PowerShell that is Git\cmd\git.exe, from Git Bash Git\mingw64\bin\git.exe.
$git = (Get-Command git).Source
$curl = $null
for ($folder = Split-Path $git; $folder; $folder = Split-Path $folder) {
    if (Test-Path (Join-Path $folder "mingw64\bin\curl.exe")) { $curl = Join-Path $folder "mingw64\bin\curl.exe"; break }
}
if (-not $curl) { throw "Git for Windows' curl (mingw64\bin\curl.exe above $git) is needed; it checks a certificate against a file" }

$database = "prem_archive_smoke"
$roles = "prem_smoke_owner", "prem_smoke_app", "prem_smoke_search"
$api = $null
$made = $false
$root = $null
$cleanups = @()

try {
    Step "The throwaway database's server"
    $ready = Run docker @("exec", "premagentic-postgres", "pg_isready", "-U", $DatabaseUser, "-h", "localhost")
    if ($ready.Exit -ne 0) { Fail "the compose development PostgreSQL (premagentic-postgres) is not ready; run docker compose up -d" }
    $taken = Run docker @("exec", "premagentic-postgres", "psql", "-U", $DatabaseUser, "-d", "postgres", "-tAc",
        "SELECT count(*) FROM pg_database WHERE datname = '$database'")
    if ($taken.Output.Trim() -ne "0") { Fail "a database $database already exists on it; this script drops only what it made" }
    Pass "premagentic-postgres is ready and has no $database"

    Step "Unzipping into $Destination"
    New-Item -ItemType Directory -Path $Destination | Out-Null
    $made = $true
    Expand-Archive -Path $Archive -DestinationPath $Destination
    $root = Join-Path $Destination ($name -replace '\.zip$', '')
    foreach ($file in "bin\prem.exe", "bin\Premagentic.Api.exe", "bin\Premagentic.McpServer.exe", "models\minilm\model.onnx") {
        if (-not (Test-Path (Join-Path $root $file))) { Fail "the unzipped archive has no $file" }
    }
    $runtimes = @(Get-ChildItem -Path $root -Recurse -Filter coreclr.dll)
    if ($runtimes.Count -ne 1) { Fail "the unzipped archive carries $($runtimes.Count) copies of coreclr.dll; the programs share one runtime" }
    Pass "unzipped; the three programs are in bin\ over one runtime, and the model is there"

    Step "No .NET for this run"
    foreach ($variable in Get-ChildItem env: | Where-Object { $_.Name -like "DOTNET_*" -or $_.Name -like "PREM_*" -or $_.Name -eq "MSBuildSDKsPath" }) {
        Remove-Item "env:$($variable.Name)"
    }
    $env:PATH = ($env:PATH -split ';' | Where-Object { $_ -and $_ -notmatch 'dotnet' }) -join ';'
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { Fail "dotnet is still on PATH" }
    Pass "dotnet is not on PATH, and no DOTNET_ or PREM_ variable is set"

    $prem = Join-Path $root "bin\prem.exe"
    Push-Location $Destination
    $cleanups += { Pop-Location }

    $out = Run $prem @("--version")
    if ($out.Exit -ne 0 -or $out.Output -notlike "*$version*") { Write-Host $out.Output; Fail "prem --version does not print $version" }
    Pass "prem --version prints $($out.Output.Trim())"

    Step "INSTALL.txt step 3: prem setup, into $database"
    $password = & docker exec premagentic-postgres printenv POSTGRES_PASSWORD
    $adminFile = Join-Path $Destination "admin.credentials"
    Set-Content -Path $adminFile -NoNewline -Value "connection=Host=127.0.0.1;Port=$DatabasePort;Username=$DatabaseUser;Password=$password;Database=postgres`n"
    Remove-Variable password
    $adminPasswordFile = Join-Path $Destination "first-admin.password"
    $bytes = [byte[]]::new(24); [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    Set-Content -Path $adminPasswordFile -NoNewline -Value (([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', '') + "`n")
    $credentials = Join-Path $Destination "credentials"
    $setup = Run $prem @("setup", "--admin-connection-file", $adminFile, "--database", $database,
        "--owner-role", $roles[0], "--app-role", $roles[1], "--search-role", $roles[2],
        "--credentials-dir", $credentials, "--admin-user", "first-admin", "--admin-password-file", $adminPasswordFile,
        "--host-name", "localhost", "--https-port", "$HttpsPort")
    if ($setup.Exit -ne 0 -or $setup.Output -notmatch "Setup complete") { Write-Host $setup.Output; Fail "prem setup failed" }
    if ($setup.Output -notlike "*model files present in $root\models\minilm*") { Write-Host $setup.Output; Fail "setup did not find the model inside the archive" }
    if ($setup.Output.Contains((Get-Content $adminPasswordFile -First 1))) { Fail "the administrator's password appears in setup's output" }
    Pass "setup completed into $database, using the model in the archive"

    $env:PREM_CREDENTIALS_FILE = Join-Path $credentials "app.credentials"

    Step "INSTALL.txt step 5: the starter profile and its two sources"
    foreach ($arguments in @(@("profile", "apply", (Join-Path $root "samples\profiles\starter")), @("ingest", "--source", "open"), @("ingest", "--source", "hr"))) {
        $out = Run $prem $arguments
        if ($out.Exit -ne 0) { Write-Host $out.Output; Fail "prem $($arguments -join ' ') failed" }
    }
    $gate = Run $prem @("search", "band four compensation review", "--as", "group:engineering")
    if ($gate.Exit -ne 0 -or $gate.Output -notmatch "hits in" -or $gate.Output -match "hr/salary-bands.md") { Write-Host $gate.Output; Fail "the gate did not hold for engineering" }
    Pass "profile applied, both sources ingested; engineering does not reach the salary bands"

    Step "INSTALL.txt step 6: the PDF, Word and Excel readers shipped in the archive"
    # An invented PDF, Word file and macro-enabled name, generated here each run and
    # never committed, and the repository's invented equipment register workbook,
    # outside the unzipped archive.
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { Fail "python is needed for the invented reader samples (scripts/clean-install/make-reader-samples.py)" }
    $formats = Join-Path $Destination "formats"
    $out = Run $python.Source @((Join-Path $repo "scripts\clean-install\make-reader-samples.py"), $formats)
    if ($out.Exit -ne 0) { Write-Host $out.Output; Fail "the invented reader samples could not be written" }
    Copy-Item (Join-Path $repo "sample-docs\spreadsheets\equipment-register.xlsx") $formats
    $out = Run $prem @("settings", "set", "extensions.folder", (Join-Path $root "extensions"))
    if ($out.Exit -ne 0) { Write-Host $out.Output; Fail "setting extensions.folder failed" }
    # The control: before the readers are allowed, the same ingest reads none of the
    # four and counts each as skipped, so the readers are what read them below.
    $before = Run $prem @("ingest", $formats, "--public", "--prefix", "formats")
    if ($before.Exit -ne 0 -or $before.Output -notmatch "Skipped 4 file\(s\)" -or $before.Output -match "formats/loading-dock\.pdf|formats/visitor-policy\.docx|formats/equipment-register\.xlsx") {
        Write-Host $before.Output; Fail "before the readers were allowed, the four files were not all skipped"
    }
    foreach ($reader in "pdf-reader", "docx-reader", "xlsx-reader") {
        $out = Run $prem @("extensions", "allow", (Join-Path $root "extensions\$reader"))
        if ($out.Exit -ne 0) { Write-Host $out.Output; Fail "allowing $reader from the archive failed" }
    }
    $list = Run $prem @("extensions", "list")
    if ($list.Output -notmatch "(?m)^  pdf-reader  [0-9a-fA-F]{64}  \(loaded\)" -or $list.Output -notmatch "(?m)^  docx-reader  [0-9a-fA-F]{64}  \(loaded\)" -or $list.Output -notmatch "(?m)^  xlsx-reader  [0-9a-fA-F]{64}  \(loaded\)" -or $list.Output -notmatch "(?m)^Refused: none\.") {
        Write-Host $list.Output; Fail "the readers are not all three allowed and loaded from the archive, or an extension was refused"
    }
    $after = Run $prem @("ingest", $formats, "--public", "--prefix", "formats")
    if ($after.Exit -ne 0 -or $after.Output -notmatch "formats/loading-dock\.pdf" -or $after.Output -notmatch "formats/visitor-policy\.docx" -or $after.Output -notmatch "formats/equipment-register\.xlsx" -or -not $after.Output.Contains(".docm (macro-enabled) 1")) {
        Write-Host $after.Output; Fail "the readers did not index the PDF, the Word file and the workbook and skip the macro-enabled one"
    }
    Pass "before they are allowed all four are skipped; allowed by hash from the archive, the readers index the PDF, the Word file and the workbook and skip the .docm"

    Step "INSTALL.txt step 4: the API"
    $api = Start-Process -FilePath (Join-Path $root "bin\Premagentic.Api.exe") -WorkingDirectory $Destination -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $Destination "api.out") -RedirectStandardError (Join-Path $Destination "api.err")
    $listening = $false
    foreach ($i in 1..90) {
        try { $client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $HttpsPort); $client.Dispose(); $listening = $true; break } catch { Start-Sleep -Seconds 1 }
        if ($api.HasExited) { break }
    }
    if (-not $listening) { Get-Content (Join-Path $Destination "api.out"), (Join-Path $Destination "api.err") -ErrorAction SilentlyContinue; Fail "the API is not listening on $HttpsPort" }
    $modules = (Get-Process -Id $api.Id).Modules | ForEach-Object FileName
    $fromDotnet = @($modules | Where-Object { $_ -match '\\dotnet\\' })
    if ($fromDotnet.Count -gt 0) { Fail "the API loaded from a .NET install: $($fromDotnet -join ', ')" }
    $coreclr = @($modules | Where-Object { $_ -like '*\coreclr.dll' })
    if ($coreclr.Count -ne 1 -or -not $coreclr[0].StartsWith((Join-Path $root "bin\"), [StringComparison]::OrdinalIgnoreCase)) {
        Fail "the API's coreclr.dll is not the archive's: $($coreclr -join ', ')"
    }
    Pass "the API (process $($api.Id)) listens on $HttpsPort; its runtime is $($coreclr[0]) and no module comes from a .NET install"

    $crt = Join-Path $credentials "https.crt"
    $health = & $curl --fail --silent --show-error --max-time 30 --cacert $crt "https://localhost:$HttpsPort/health"
    if ($LASTEXITCODE -ne 0 -or ($health -join '') -notmatch '"status":"ok"') { Fail "/health over HTTPS, verified against https.crt, did not answer ok" }
    & $curl --fail --silent --max-time 10 "https://localhost:$HttpsPort/health" *> $null
    if ($LASTEXITCODE -eq 0) { Fail "a client not given https.crt trusted the certificate; the check above proves nothing" }
    Pass "/health answers ok over HTTPS against https.crt; a client without it refuses"

    Step "An agent searches over MCP with its token"
    $out = Run $prem @("agents", "add", "reader", "--owner", "first-admin", "--mode", "service", "--model", "local")
    if ($out.Exit -ne 0) { Write-Host $out.Output; Fail "making the agent failed" }
    $out = Run $prem @("agents", "grant", "reader", "hr")
    if ($out.Exit -ne 0) { Write-Host $out.Output; Fail "granting hr failed" }
    $header = Join-Path $Destination "agent.header"
    $tokenFile = Join-Path $Destination "agent.token"
    $issued = & $prem tokens issue reader --days 1 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "issuing the token failed" }
    Set-Content -Path $tokenFile -NoNewline -Value (($issued | Select-Object -Last 1).ToString().Trim() + "`n")
    Set-Content -Path $header -NoNewline -Value ("Authorization: Bearer " + (Get-Content $tokenFile -First 1) + "`n")
    Remove-Variable issued
    $meta = '"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}'
    foreach ($case in @(@("band four compensation review", "hr/salary-bands.md"), @("how long do I have to file an expense claim", "open/handbook.md"),
                        @("where do returns go", "formats/loading-dock.pdf", "Page 2"), @("what badge do visitors wear", "formats/visitor-policy.docx", "Visitors"),
                        @("where is the spare key to the cage kept", "formats/equipment-register.xlsx", "equipment-register.xlsx > Keys"))) {
        $body = Join-Path $Destination "mcp.json"
        Set-Content -Path $body -NoNewline -Value ('{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_knowledge","arguments":{"query":"' + $case[0] + '","topK":5},' + $meta + '}}')
        $answer = & $curl --fail --silent --show-error --max-time 30 --cacert $crt -H "@$header" -H "Content-Type: application/json" `
            -H "Accept: application/json, text/event-stream" -H "MCP-Protocol-Version: 2026-07-28" -H "Mcp-Method: tools/call" `
            -H "Mcp-Name: search_knowledge" --data-binary "@$body" "https://localhost:$HttpsPort/mcp"
        if ($LASTEXITCODE -ne 0) { Fail "over MCP, '$($case[0])' failed" }
        # The answer is JSON on the wire, where ">" arrives escaped as >.
        $text = ($answer -join '') -replace '\\u003E', '>'
        foreach ($expected in $case[1..($case.Count - 1)]) {
            if ($text -notlike "*$expected*") { Fail "over MCP, '$($case[0])' did not return $expected" }
        }
    }
    Remove-Item $header
    Pass "over MCP the agent granted hr finds the salary bands, the handbook, the PDF by its second page, the Word file and the workbook's Keys sheet"

    Step "The MCP bridge from the archive, started as an assistant starts a local program"
    # The bridge shares bin\ and its runtime with the other two programs, so it is
    # run here, not assumed: it must start, answer initialize with its name and this
    # version, and load its runtime from bin\. Its search through the API is proven
    # by the Linux proof; here Windows does not trust setup's self-signed
    # certificate for one process without a change to a certificate store.
    $start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $root "bin\Premagentic.McpServer.exe"))
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment["PREM_API_URL"] = "https://localhost:$HttpsPort"
    $start.Environment["PREM_AGENT_TOKEN_FILE"] = $tokenFile
    $bridge = [System.Diagnostics.Process]::Start($start)
    try {
        $bridgeErrors = $bridge.StandardError.ReadToEndAsync()
        $bridge.StandardInput.NewLine = "`n"
        $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"archive-proof","version":"0"}}}')
        $bridge.StandardInput.Flush()
        $line = $bridge.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(90000)) { Fail "the bridge did not answer initialize within 90 s" }
        $initialize = $line.Result
        $bridgeCore = @($bridge.Modules | ForEach-Object FileName | Where-Object { $_ -like '*\coreclr.dll' })
        $bridge.StandardInput.Close()
        if (-not $bridge.WaitForExit(30000)) { Fail "the bridge did not end when its input closed" }
        $said = $bridgeErrors.Result
    }
    finally { if (-not $bridge.HasExited) { $bridge.Kill() } }
    if ("$initialize$said".Contains("prem_agt_")) { Fail "the agent's token appears in the bridge's output" }
    if ($initialize -notmatch '"name":"premagentic"' -or -not $initialize.Contains($version)) { Write-Host $initialize; Write-Host $said; Fail "the bridge's initialize did not name premagentic and $version" }
    if ($bridgeCore.Count -ne 1 -or -not $bridgeCore[0].StartsWith((Join-Path $root "bin\"), [StringComparison]::OrdinalIgnoreCase)) { Fail "the bridge's coreclr.dll is not the archive's: $($bridgeCore -join ', ')" }
    Remove-Item $tokenFile
    Pass "the bridge started from bin\, answered initialize as premagentic $version, its runtime $($bridgeCore[0])"

    Step "The control: the same search without the model folder"
    $model = Join-Path $root "models\minilm"
    Rename-Item $model "minilm.away"
    try { $control = Run $prem @("search", "band four compensation review", "--as", "group:hr") }
    finally { Rename-Item (Join-Path $root "models\minilm.away") "minilm" }
    if ($control.Exit -eq 0) { Fail "the search succeeded without the model folder; the proof above could not have failed" }
    if ($control.Output -notmatch "needs model\.onnx and vocab\.txt in a models/minilm folder, and none was found\. Looked in: .*models\\minilm") {
        Write-Host $control.Output; Fail "the search without the model failed, but not with the line naming where it looked"
    }
    if ($control.Output -match "Unhandled exception|^   at ") { Fail "the refusal printed a stack trace" }
    Pass "without the model folder the search stops, exit $($control.Exit), naming the folders it looked in"

    Write-Host "`nPASS  $name runs with no .NET reachable"
}
finally {
    if ($api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force
        $api.WaitForExit(15000) | Out-Null
        if (Get-Process -Id $api.Id -ErrorAction SilentlyContinue) { Write-Warning "the API (process $($api.Id)) is still running; stop it by that id" }
    }
    foreach ($cleanup in $cleanups) { & $cleanup }
    if ($root -and (Test-Path (Join-Path $Destination "credentials\owner.credentials"))) {
        $removed = & (Join-Path $root "bin\prem.exe") remove --purge --yes --credentials-dir (Join-Path $Destination "credentials") `
            --admin-connection-file (Join-Path $Destination "admin.credentials") 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $removed; Write-Warning "prem remove --purge failed; drop $database and the roles $($roles -join ', ') by hand" }
    }
    $left = & docker exec premagentic-postgres psql -U $DatabaseUser -d postgres -tAc `
        "SELECT (SELECT count(*) FROM pg_database WHERE datname = '$database') + (SELECT count(*) FROM pg_roles WHERE rolname IN ('$($roles -join "','")'))"
    if ("$left".Trim() -ne "0") { Write-Warning "$database or its roles are still on premagentic-postgres ($left left)" } else { Write-Host "the database $database and its three roles are gone" }
    if ($made) { Remove-Item -Recurse -Force $Destination; Write-Host "deleted $Destination" }
}
