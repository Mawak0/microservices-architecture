param([switch]$KeepStorage)
$ErrorActionPreference = 'Stop'
$demoRoot = Split-Path $PSScriptRoot -Parent
$pidFile = Join-Path $demoRoot '.demo-data/processes.json'
if (!(Test-Path $pidFile)) { Write-Host 'Stand is not running'; exit 0 }
$demoProcesses = Get-Content $pidFile -Raw | ConvertFrom-Json
foreach ($demoProcessId in @($demoProcesses.services) + @($demoProcesses.launcher)) {
    $demoProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$demoProcessId"
    if (!$demoProcess) { continue }
    $knownService = $demoProcess.Name -eq 'dotnet.exe' -and $demoProcess.CommandLine -match 'src[/\\]Olympiad\.(Materials|Intake|Review)\.Api[/\\]bin[/\\]Release[/\\]net8.0[/\\]Olympiad\.(Materials|Intake|Review)\.Api.dll'
    $knownLauncher = $demoProcess.Name -eq 'node.exe' -and $demoProcess.CommandLine -match 'demo[/\\]stand\.mjs'
    if (!$knownService -and !$knownLauncher) { throw "PID $demoProcessId belongs to another application; stopping refused" }
    Stop-Process -Id $demoProcessId
}
if (!$KeepStorage) {
    $storageFile=Join-Path $demoRoot '.demo-data/storage-processes.json'
    if (Test-Path -LiteralPath $storageFile) {
        $storage=Get-Content -LiteralPath $storageFile -Raw | ConvertFrom-Json
        $s3Process=Get-CimInstance Win32_Process -Filter "ProcessId=$($storage.s3)"
        if ($s3Process) {
            $expectedData=Join-Path $demoRoot '.demo-data/s3-data'
            if ($s3Process.Name -ne 'versitygw.exe' -or !$s3Process.CommandLine.Contains($expectedData)) { throw 'Storage PID belongs to another process; stopping refused.' }
            Stop-Process -Id $storage.s3
        }
        & "$($storage.postgresBin)/pg_ctl.exe" stop -D (Join-Path $demoRoot '.demo-data/pgdata') -m fast -w
    }
}
Write-Host 'Stand stopped. PostgreSQL and S3 data are preserved.' 
