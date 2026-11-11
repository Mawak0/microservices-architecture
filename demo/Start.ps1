param([switch]$NoBrowser, [string]$PostgresBin='C:/Program Files/PostgreSQL/17/bin', [string]$VersityExe)
$ErrorActionPreference='Stop'
$demoRoot=Split-Path $PSScriptRoot -Parent
$demoData=Join-Path $demoRoot '.demo-data'
$cluster=Join-Path $demoData 'pgdata'
$demoUrl='http://127.0.0.1:19180'
if (!$VersityExe) { $VersityExe=Join-Path $demoRoot '.local/versitygw/versitygw_v1.11.0_Windows_x86_64/versitygw.exe' }
if (!(Test-Path -LiteralPath "$PostgresBin/initdb.exe")) { throw 'Specify -PostgresBin for PostgreSQL 17.' }
if (!(Test-Path -LiteralPath $VersityExe)) { throw 'Specify -VersityExe for official VersityGW 1.11.0. See demo/README.md.' }
try {
    $existing=Invoke-RestMethod "$demoUrl/demo/state" -TimeoutSec 2
    if (@($existing.health | Where-Object healthy).Count -eq 3) { Write-Host "Ready: $demoUrl"; exit 0 }
} catch {}
$portsInUse=Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 19180,19181,19182,19183 }
if ($portsInUse) { throw 'Demo ports 19180-19183 are occupied. Stop the previous stand first.' }
New-Item -ItemType Directory -Force $demoData | Out-Null
$settingsFile=Join-Path $demoData 'settings.json'
function New-Secret { $bytes=New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); return ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant() }
if (!(Test-Path -LiteralPath $settingsFile)) {
    @{adminPassword=New-Secret;s3Access=New-Secret;s3Secret=New-Secret} | ConvertTo-Json | Set-Content -LiteralPath $settingsFile
}
$settings=Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
if (!$settings.databasePasswords) {
    $settings | Add-Member -NotePropertyName databasePasswords -NotePropertyValue @{materials=New-Secret;intake=New-Secret;review=New-Secret;demo=New-Secret}
    $settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsFile
}
$env:PGHOST='127.0.0.1'; $env:PGPORT='55433'; $env:PGDATABASE='postgres'; $env:PGUSER='platform_admin'; $env:PGPASSWORD=$settings.adminPassword
foreach ($service in @('materials','intake','review','demo')) { [Environment]::SetEnvironmentVariable($service.ToUpper()+'_DATABASE_PASSWORD',$settings.databasePasswords.$service,'Process') }
if (!(Test-Path -LiteralPath (Join-Path $cluster 'PG_VERSION'))) {
    $passwordFile=Join-Path $demoData 'initial-password.txt'
    try {
        [IO.File]::WriteAllText($passwordFile,$settings.adminPassword,(New-Object Text.UTF8Encoding $false))
        & "$PostgresBin/initdb.exe" -D $cluster -U platform_admin --auth=scram-sha-256 "--pwfile=$passwordFile" > (Join-Path $demoData 'initdb.log') 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL initialization failed; see .demo-data/initdb.log.' }
    } finally { Remove-Item -LiteralPath $passwordFile -ErrorAction SilentlyContinue }
}
& "$PostgresBin/pg_ctl.exe" status -D $cluster > $null 2>&1
if ($LASTEXITCODE -ne 0) {
    & "$PostgresBin/pg_ctl.exe" start -D $cluster -l (Join-Path $demoData 'postgres.log') -o '-h 127.0.0.1 -p 55433' -w
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL startup failed.' }
}
Push-Location (Join-Path $demoRoot 'demo')
try { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } } finally { Pop-Location }
Push-Location $demoRoot
try {
    node demo/initialize-db.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Database and role initialization failed.' }
    $s3Data=Join-Path $demoData 's3-data'; $s3Meta=Join-Path $demoData 's3-meta'
    New-Item -ItemType Directory -Force $s3Data,$s3Meta | Out-Null
    $storageFile=Join-Path $demoData 'storage-processes.json'
    $listening=Get-NetTCPConnection -LocalPort 17071 -State Listen -ErrorAction SilentlyContinue
    if ($listening) {
        if (!(Test-Path -LiteralPath $storageFile)) { throw 'Port 17071 belongs to another process.' }
        $prior=Get-Content -LiteralPath $storageFile -Raw | ConvertFrom-Json
        if ($listening.OwningProcess -ne $prior.s3) { throw 'Port 17071 belongs to another process.' }
        $s3Process=Get-Process -Id $prior.s3
    } else {
        $env:ROOT_ACCESS_KEY_ID=$settings.s3Access; $env:ROOT_SECRET_ACCESS_KEY=$settings.s3Secret
        $s3Process=Start-Process -FilePath $VersityExe -ArgumentList @('--port','127.0.0.1:17071','posix','--sidecar',('"'+$s3Meta+'"'),'--object-lock-mode','local','--fsync',('"'+$s3Data+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $demoData 's3.log') -RedirectStandardError (Join-Path $demoData 's3-errors.log')
    }
    @{s3=$s3Process.Id;postgresBin=$PostgresBin} | ConvertTo-Json | Set-Content -LiteralPath $storageFile
    $env:S3_ENDPOINT='http://127.0.0.1:17071'; $env:S3_ACCESS_KEY=$settings.s3Access; $env:S3_SECRET_KEY=$settings.s3Secret
    foreach ($serviceName in @('materials','intake','review')) {
        $title=(Get-Culture).TextInfo.ToTitleCase($serviceName)
        dotnet build "services/$serviceName/Olympiad.$title.sln" -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $serviceName" }
    }
    $env:PGDATABASE='demo'; $env:PGUSER='demo'; $env:PGPASSWORD=$settings.databasePasswords.demo
    Start-Process -FilePath (Get-Command node).Source -ArgumentList 'demo/stand.mjs' -WorkingDirectory $demoRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $demoData 'stand.log') -RedirectStandardError (Join-Path $demoData 'stand-errors.log')
    $deadline=(Get-Date).AddSeconds(50)
    do {
        Start-Sleep -Milliseconds 300
        try {
            $ready=Invoke-RestMethod "$demoUrl/demo/state" -TimeoutSec 2
            if (@($ready.health | Where-Object healthy).Count -eq 3) {
                if (!$NoBrowser) { Start-Process -FilePath 'C:/Program Files/Google/Chrome/Application/chrome.exe' -ArgumentList $demoUrl }
                Write-Host "Ready: $demoUrl"; exit 0
            }
        } catch {}
    } while ((Get-Date) -lt $deadline)
    throw 'Startup failed. Inspect .demo-data/*.log.'
} finally { Pop-Location }
