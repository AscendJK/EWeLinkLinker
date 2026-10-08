param([string]$RunId = 'anon', [string]$ExpectSha = '')
$ErrorActionPreference = 'Stop'

$repo    = 'E:\ClaudeCode\EWeLinkLinker'
$pubSvc  = Join-Path $repo 'publish\Service'
$pubCfg  = Join-Path $repo 'publish\ConfigApp'
$cfgFile = Join-Path $repo 'publish\config\linker.json'
$stage   = 'C:\Users\Kun\AppData\Local\Temp\ewl_stage_' + $RunId
$bak     = 'C:\Users\Kun\AppData\Local\Temp\ewl_backup_' + $RunId
$svcName = 'EWeLinkLinker'

$tx = 'C:\Users\Kun\AppData\Local\Temp\ewl_hold\run\deploy_' + $RunId + '.txt'
$lines = New-Object System.Collections.Generic.List[string]
New-Item -ItemType Directory -Force -Path (Split-Path $tx -Parent) | Out-Null   # transcript 目录不假设它已经存在
function Say([string]$s) { $script:lines.Add("[$RunId] $s"); [IO.File]::WriteAllLines($tx, $lines) }
function Sha256([string]$p) { if (Test-Path $p) { (Get-FileHash $p -Algorithm SHA256).Hash.Substring(0,12) } else { 'MISSING' } }
function ProdVer([string]$p) { if (Test-Path $p) { (Get-Item $p).VersionInfo.ProductVersion } else { 'MISSING' } }
function SidOf($a) { try { return $a.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { return '' } }
function Wait-State([string]$want, [double]$limitS) {
  $sw = New-Object Diagnostics.Stopwatch; $sw.Start()
  while ((Get-Service -Name $svcName -ErrorAction SilentlyContinue).Status -ne $want -and $sw.Elapsed.TotalSeconds -lt $limitS) { Start-Sleep -Milliseconds 200 }
  return $sw.Elapsed.TotalSeconds
}

$keyFiles = @(
  (Join-Path $pubSvc 'EWeLinkLinker.Service.exe'),
  (Join-Path $pubSvc 'EWeLinkLinker.Core.dll'),
  (Join-Path $pubCfg 'EWeLinkLinker.ConfigApp.exe'),
  (Join-Path $pubCfg 'EWeLinkLinker.Core.dll')
)

# ---------- 0. guards ----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say ("elevated=" + $isAdmin)
if (-not $isAdmin) { Say 'ABORT: not elevated (binary dirs now deny non-admin write)'; exit 2 }

$gui = @(Get-Process -Name 'EWeLinkLinker.ConfigApp' -ErrorAction SilentlyContinue)
if ($gui.Count -gt 0) { Say ("ABORT: ConfigApp running (pid " + ($gui.Id -join ',') + ") would lock publish\ConfigApp"); exit 2 }

$cfgBefore = @((Get-Item $cfgFile).Length, (Get-Item $cfgFile).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), (Sha256 $cfgFile))
Say ("config before   : size=" + $cfgBefore[0] + " mtime=" + $cfgBefore[1] + " sha=" + $cfgBefore[2])
foreach ($f in $keyFiles) { Say ('installed now   : ' + (Split-Path $f -Leaf) + ' [' + (Split-Path (Split-Path $f -Parent) -Leaf) + '] sha=' + (Sha256 $f) + ' ver=' + (ProdVer $f)) }
Say ('HEAD expects    : ' + $ExpectSha)

# ---------- 1. build to staging while the service keeps running ----------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
$svcStage = Join-Path $stage 'Service'
$cfgStage = Join-Path $stage 'ConfigApp'
Push-Location $repo
& dotnet publish (Join-Path $repo 'src\EWeLinkLinker.Service\EWeLinkLinker.Service.csproj') -c Release -o $svcStage --self-contained false --nologo 2>&1 | Out-Null
$e1 = $LASTEXITCODE
& dotnet publish (Join-Path $repo 'src\EWeLinkLinker.ConfigApp\EWeLinkLinker.ConfigApp.csproj') -c Release -o $cfgStage --self-contained false --nologo 2>&1 | Out-Null
$e2 = $LASTEXITCODE
Pop-Location
Say ("staging publish exit Service=" + $e1 + " ConfigApp=" + $e2)
if ($e1 -ne 0 -or $e2 -ne 0) { Say 'BUILD FAILED - service never stopped, nothing copied'; exit 3 }
foreach ($p in @((Join-Path $svcStage 'EWeLinkLinker.Service.exe'), (Join-Path $svcStage 'EWeLinkLinker.Core.dll'), (Join-Path $cfgStage 'EWeLinkLinker.ConfigApp.exe'), (Join-Path $cfgStage 'EWeLinkLinker.Core.dll'))) {
  if (-not (Test-Path $p)) { Say ('STAGING MISSING ' + $p); exit 3 }
  Say ('staged          : ' + (Split-Path $p -Leaf) + ' [' + (Split-Path $p -Parent).Substring($stage.Length) + '] sha=' + (Sha256 $p) + ' ver=' + (ProdVer $p))
}

# ---------- 2. rollback copy, verified ----------
New-Item -ItemType Directory -Path $bak -Force | Out-Null
Copy-Item $pubSvc (Join-Path $bak 'Service') -Recurse -Force
Copy-Item $pubCfg (Join-Path $bak 'ConfigApp') -Recurse -Force
$bkOk = $true
foreach ($f in $keyFiles) {
  $leaf = Split-Path $f -Leaf
  $sub  = Split-Path (Split-Path $f -Parent) -Leaf
  $rb   = Join-Path $bak ($sub + '\' + $leaf)
  $same = (Test-Path $rb) -and ((Sha256 $rb) -eq (Sha256 $f))
  Say ("backup verified   : " + $sub + '\' + $leaf + " same=" + $same)
  if (-not $same) { $bkOk = $false }
}
if (-not $bkOk) { Say 'ABORT: backup not verifiable - stopping nothing'; exit 2 }

function Restore-Backup([string]$why) {
  Say ("ROLLBACK START: " + $why)
  & robocopy (Join-Path $bak 'Service') $pubSvc /E /NFL /NDL /NJH /NJS /NP | Out-Null
  Say ("  robocopy Service restore exit=" + $LASTEXITCODE)
  & robocopy (Join-Path $bak 'ConfigApp') $pubCfg /E /NFL /NDL /NJH /NJS /NP | Out-Null
  Say ("  robocopy ConfigApp restore exit=" + $LASTEXITCODE)
  try { Start-Service -Name $svcName -ErrorAction Stop } catch { Say ('  start after rollback failed: ' + $_.Exception.Message) }
  Say ("  service after rollback=" + (Get-Service -Name $svcName -ErrorAction SilentlyContinue).Status)
  Say ("  backup KEPT at " + $bak)
  Say ("$RunId ROLLBACK DONE")
}

# ---------- 3. stop -> swap -> start ----------
$logToday = Join-Path $pubSvc ("logs\service-" + (Get-Date).ToString('yyyy-MM-dd') + ".log")
$linesBefore = 0
if (Test-Path $logToday) {
  $fs = New-Object IO.FileStream($logToday, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
  $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
  $linesBefore = @(($sr.ReadToEnd() -split "\r?\n") | Where-Object { $_.Trim() }).Count
  $fs.Dispose()
}
Say ("log anchor      : " + (Split-Path $logToday -Leaf) + " nonEmptyLinesBefore=" + $linesBefore)

$t0 = Get-Date
Stop-Service -Name $svcName -Force -ErrorAction Stop
$sec1 = Wait-State 'Stopped' 30
Say ("stopped in " + ('{0:N1}' -f $sec1) + "s ; status=" + (Get-Service -Name $svcName).Status)

& robocopy $svcStage $pubSvc /E /NFL /NDL /NJH /NJS /NP | Out-Null
$r1 = $LASTEXITCODE
& robocopy $cfgStage $pubCfg /E /NFL /NDL /NJH /NJS /NP | Out-Null
$r2 = $LASTEXITCODE
Say ("swap robocopy exit Service=" + $r1 + " ConfigApp=" + $r2 + " (0-7 means success)")
if ($r1 -gt 7 -or $r2 -gt 7) { Restore-Backup 'robocopy failed'; exit 4 }

try { Start-Service -Name $svcName -ErrorAction Stop } catch { Restore-Backup ('start failed: ' + $_.Exception.Message); exit 4 }
$sec2 = Wait-State 'Running' 40
$down = ((Get-Date) - $t0).TotalSeconds
Say ("running in " + ('{0:N1}' -f $sec2) + "s ; total downtime " + ('{0:N1}' -f $down) + "s")
if ((Get-Service -Name $svcName).Status -ne 'Running') { Restore-Backup 'service never reached Running'; exit 4 }

# ---------- 4. post checks ----------
foreach ($f in $keyFiles) { Say ('installed after : ' + (Split-Path $f -Leaf) + ' [' + (Split-Path (Split-Path $f -Parent) -Leaf) + '] sha=' + (Sha256 $f) + ' ver=' + (ProdVer $f)) }
$cfgAfter = @((Get-Item $cfgFile).Length, (Get-Item $cfgFile).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), (Sha256 $cfgFile))
Say ("config after    : size=" + $cfgAfter[0] + " mtime=" + $cfgAfter[1] + " sha=" + $cfgAfter[2] + " unchanged=" + (($cfgAfter -join '|') -eq ($cfgBefore -join '|')))

$acl = Get-Acl (Join-Path $pubSvc 'EWeLinkLinker.Service.exe')
$auN = @($acl.Access | Where-Object { (SidOf $_) -eq 'S-1-5-11' }).Count
$usrN = @($acl.Access | Where-Object { (SidOf $_) -eq 'S-1-5-32-545' -and $_.AccessControlType -eq 'Allow' })
$sysN = @($acl.Access | Where-Object { (SidOf $_) -eq 'S-1-5-18' }).Count
Say ("new exe ACL     : aceCount=" + $acl.Access.Count + " SYSTEM=" + $sysN + " AuthUsers=" + $auN + " Users=" + $(if ($usrN.Count) { $usrN[0].FileSystemRights.ToString() } else { 'NONE' }))

Say 'waiting 45s to collect post-start polling rounds'
Start-Sleep -Seconds 45

$txt = ''
if (Test-Path $logToday) {
  $fs = New-Object IO.FileStream($logToday, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
  $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $txt = $sr.ReadToEnd(); $fs.Dispose()
}
$all = @(($txt -split "\r?\n") | Where-Object { $_.Trim() })
$after = @()
if ($all.Count -gt $linesBefore) { $after = @($all | Select-Object -Skip $linesBefore) }
Say ("log lines after stop = " + $after.Count + " (file now " + $all.Count + ")")

# slice from the NEW process's own header so old-build stop lines never pollute the counts
$startIdx = -1
for ($i = $after.Count - 1; $i -ge 0; $i--) { if ($after[$i] -match 'EWeLink Linker Service 1\.0\.0\+') { $startIdx = $i; break } }
$since = if ($startIdx -ge 0) { @($after | Select-Object -Skip $startIdx) } else { $after }
Say ("new-build window starts at line " + $startIdx + " ; lines=" + $since.Count)
foreach ($l in ($since | Select-Object -First 26)) { Say ('  | ' + $l) }

$j = $since -join "`n"
$res = [ordered]@{
  versionInLog   = ($j -match ('EWeLink Linker Service 1\.0\.0\+' + [regex]::Escape($ExpectSha)))
  oldV120Present = ($j -match 'v1\.2\.0')
  baselineLines  = @([regex]::Matches($j, '按基线处理')).Count
  ruleFires      = @([regex]::Matches($j, '!! 规则触发')).Count
  seedCpuRounds  = @([regex]::Matches($j, '轮询调度器启动')).Count
  pollRounds     = @([regex]::Matches($j, '\[轮询\]')).Count
  errors         = @([regex]::Matches($j, '\[ERROR\]')).Count
  warns          = @([regex]::Matches($j, '\[WARN\]')).Count
  configUnchanged = (($cfgAfter -join '|') -eq ($cfgBefore -join '|'))
  aclTight       = ($auN -eq 0 -and $sysN -gt 0 -and ($usrN.Count -gt 0) -and $usrN[0].FileSystemRights.ToString() -match 'Read')
}
foreach ($k in $res.Keys) { Say ("CHECK {0} = {1}" -f $k, $res[$k]) }

if (-not $res.versionInLog) { Restore-Backup 'log does not show the new version stamp'; exit 5 }
Say ("$RunId DEPLOY DONE downtime_s=" + ('{0:N1}' -f $down))
exit 0
