# Starts one command detached from the calling shell, so a terminal or session that
# ends cannot stop it: prove-windows-sandbox.sh start runs its waiter this way,
# because the proof takes minutes. Writes the command's output to <Name>.out and,
# the moment it ends, its exit code to <Name>.exit, both in -Folder, and a line to
# timeline.txt there. Refuses to start when <Name>.exit already exists.
#   powershell -File start-detached.ps1 -Name wait -Folder <folder> -Command "<cmd line>"
param(
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $Folder,
    [Parameter(Mandatory)] [string] $Command
)
$ErrorActionPreference = 'Stop'
# Never handed on: a Git Bash caller may set it for this call, and git refuses the
# paths the release scripts give it when it is set.
Remove-Item Env:MSYS_NO_PATHCONV -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$log = Join-Path $Folder "$Name.out"
$exit = Join-Path $Folder "$Name.exit"
if (Test-Path $exit) { throw "$exit exists already; clear the folder or name the step afresh." }
# cmd writes the exit code the moment the command ends; a plain %ERRORLEVEL% would be
# expanded before the command runs.
$cmd = "$Command > `"$log`" 2>&1 & call echo %^ERRORLEVEL% > `"$exit`""
$p = Start-Process -FilePath cmd.exe -ArgumentList '/d', '/s', '/c', "`"$cmd`"" -WindowStyle Hidden -PassThru
"$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')) UTC started $Name, cmd pid $($p.Id): $cmd" | Add-Content (Join-Path $Folder 'timeline.txt')
"started $Name as cmd pid $($p.Id)"
