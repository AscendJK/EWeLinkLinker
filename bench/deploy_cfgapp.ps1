param([string]$RunId = 'anon', [string]$ExpectSha = '')
$ErrorActionPreference = 'Stop'

# ConfigApp-only swap. The Windows service is NOT stopped and MUST end up byte-identical.
$repo    = 'E:\ClaudeCode\EWeLinkLinker'
$pubSvc  = Join-Path $repo 'publish\Service'
$pubCfg  = Join-Path $repo 'publish\ConfigApp'
$cfgFile = Join-Path $repo 'publish\config\linker.json'
$stage   = 'C:\Users\Kun\AppData\Local\Temp\ewl_stage_' + $RunId
$bak     = 'C:\Users\Kun\AppData\Local\Temp\ewl_backup_' + $RunId
$svcName = 'EWeLinkLinker'

$tx = 'C:\Users\Kun\AppData\Local\Temp\ewl_hold\run\deploy_cfgapp_' + $RunId + '.txt'
$lines = New-Object System.Collections.Generic.List[string]
New-Item -ItemType Directory -Force -Path (Split-Path $tx -Parent) | Out-Null   # transcript 目录不假设它已经存在
function Say([string]$s) { $script:lines.Add("[$RunId] $s"); [IO.File]::WriteAllLines($tx, $lines) }
function Sha256([string]$p) { if (Test-Path $p) { (Get-FileHash $p -Algorithm SHA256).Hash } else { 'MISSING' } }
function Sha12([string]$p) { $h = Sha256 $p; if ($h.Length -ge 12) { $h.Substring(0,12) } else { $h } }
function ProdVer([string]$p) { if (Test-Path $p) { (Get-Item $p).VersionInfo.ProductVersion } else { 'MISSING' } }
function SidOf($a) { try { return $a.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { return '' } }

$cfgExe   = Join-Path $pubCfg 'EWeLinkLinker.ConfigApp.exe'
$cfgCore  = Join-Path $pubCfg 'EWeLinkLinker.Core.dll'
$svcExe   = Join-Path $pubSvc 'EWeLinkLinker.Service.exe'
$svcCore  = Join-Path $pubSvc 'EWeLinkLinker.Core.dll'

# ---------- 0. guards ----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say ("elevated=" + $isAdmin)
if (-not $isAdmin) { Say 'ABORT: not elevated (publish dirs deny non-admin write)'; exit 2 }

$gui = @(Get-Process -Name 'EWeLinkLinker.ConfigApp' -ErrorAction SilentlyContinue)
if ($gui.Count -gt 0) { Say ("ABORT: ConfigApp running (pid " + ($gui.Id -join ',') + ") would lock publish\ConfigApp"); exit 2 }

$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
Say ("service before  : status=" + $svc.Status)
if ($svc.Status -ne 'Running') { Say 'ABORT: service not Running - investigate before swapping'; exit 2 }

$procBefore = @(Get-Process -Name 'EWeLinkLinker.Service' -ErrorAction SilentlyContinue)
$pidBefore  = if ($procBefore.Count) { $procBefore[0].Id } else { 'NONE' }
$StartTime  = if ($procBefore.Count) { $procBefore[0].StartTime.ToString('yyyy-MM-dd HH:mm:ss') } else { 'NONE' }
Say ("service pid     : pid=" + $pidBefore + " started=" + $StartTime)

$svcExeBefore  = Sha256 $svcExe
$svcCoreBefore = Sha256 $svcCore
$cfgExeBefore  = Sha256 $cfgExe
$cfgCoreBefore = Sha256 $cfgCore
$cfgFileBefore = @((Get-Item $cfgFile).Length, (Get-Item $cfgFile).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), (Sha12 $cfgFile))
Say ("service files   : Service.exe=" + $svcExeBefore.Substring(0,12) + " Core.dll=" + $svcCoreBefore.Substring(0,12) + "  (must not change)")
Say ("configapp files : ConfigApp.exe=" + $cfgExeBefore.Substring(0,12) + " ver=" + (ProdVer $cfgExe))
Say ("configapp Core  : Core.dll=" + $cfgCoreBefore.Substring(0,12) + "  same_as_service_core=" + ($cfgCoreBefore -eq $svcCoreBefore))
Say ("config before   : size=" + $cfgFileBefore[0] + " mtime=" + $cfgFileBefore[1] + " sha=" + $cfgFileBefore[2])
Say ("HEAD expects    : " + $ExpectSha)

# ---------- 1. publish ConfigApp to staging while everything keeps running ----------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
$cfgStage = Join-Path $stage 'ConfigApp'
Push-Location $repo
& dotnet publish -c Release -p:NoIncremental=true (Join-Path $repo 'src\EWeLinkLinker.ConfigApp\EWeLinkLinker.ConfigApp.csproj') -o $cfgStage --self-contained false --nologo 2>&1 | Out-Null
$e1 = $LASTEXITCODE
Pop-Location
Say ("staging publish exit ConfigApp=" + $e1)
if ($e1 -ne 0) { Say 'BUILD FAILED - nothing was copied, nothing was stopped'; exit 3 }

$stExe  = Join-Path $cfgStage 'EWeLinkLinker.ConfigApp.exe'
$stCore = Join-Path $cfgStage 'EWeLinkLinker.Core.dll'
foreach ($p in @($stExe, $stCore)) {
  if (-not (Test-Path $p)) { Say ('STAGING MISSING ' + $p); exit 3 }
  Say ('staged          : ' + (Split-Path $p -Leaf) + ' sha=' + (Sha12 $p) + ' ver=' + (ProdVer $p))
}

# THE gate: is the running service's CODE the same as what this GUI ships with?
# Byte equality of Core.dll is the wrong question - any rebuild changes PE timestamp / MVID / metadata hash,
# and the embedded commit id appears twice (UTF-8 metadata + UTF-16 version resource). Measured on this box:
# deployed vs fresh build differed in 145 bytes, all inside those identity fields, with sources unchanged.
# So the authoritative check is git: no Core/Service source changed between the deployed commit and HEAD,
# and nothing uncommitted in those projects.
$deployedVer = ProdVer $cfgExe
$deployedShaFull = ''
if ($deployedVer -match '\+([0-9a-f]{40})') { $deployedShaFull = $Matches[1] }
$headFull = ''
$gitErr = ''
try { $headFull = ((& git -C $repo rev-parse HEAD) 2>&1 | Out-String).Trim() } catch { $gitErr = $_.Exception.Message }
Say ("git available   : head=" + $headFull + " err=" + $gitErr)
Say ("deployed commit : " + $deployedShaFull + " (read from installed ConfigApp version stamp)")

$coreDirty = @(& git -C $repo status --porcelain -- 'src/EWeLinkLinker.Core' 'src/EWeLinkLinker.Service' 2>&1)
$coreDiff  = @(& git -C $repo diff --name-only ($deployedShaFull + '..HEAD') -- 'src/EWeLinkLinker.Core' 'src/EWeLinkLinker.Service' 2>&1 | Where-Object { $_ -and ($_ -notmatch '^warning:') })
$coreSourcesUnchanged = ($deployedShaFull -ne '') -and ($headFull -like ($ExpectSha + '*')) -and ($coreDirty.Count -eq 0) -and ($coreDiff.Count -eq 0)
Say ("core_svc_dirty  : " + $coreDirty.Count + " " + ($coreDirty -join ' | '))
Say ("core_svc_diff   : " + $coreDiff.Count + " " + ($coreDiff -join ' | '))
Say ("core_sources_unchanged : " + $coreSourcesUnchanged)
if (-not $coreSourcesUnchanged) {
  Say 'ABORT: Core/Service code differs from what the service is running - that needs a service restart, which was not authorized. Nothing copied.'
  exit 6
}

$verOk = ((ProdVer $stExe) -like ('1.0.0+' + $ExpectSha + '*'))
$coreVerOk = ((ProdVer $stCore) -like ('1.0.0+' + $ExpectSha + '*'))
Say ("staged version  : ConfigApp.exe=" + (ProdVer $stExe) + " matches_expected=" + $verOk)
Say ("staged version  : Core.dll=" + (ProdVer $stCore) + " matches_expected=" + $coreVerOk + " (forces a real rebuild, not a stale obj)")
if (-not ($verOk -and $coreVerOk)) { Say 'ABORT: staged binaries are not built from the expected commit - nothing copied'; exit 3 }

# ---------- 2. rollback copy, verified ----------
New-Item -ItemType Directory -Path $bak -Force | Out-Null
Copy-Item $pubCfg (Join-Path $bak 'ConfigApp') -Recurse -Force
$bkOk = $true
foreach ($f in @($cfgExe, $cfgCore)) {
  $rb = Join-Path $bak ('ConfigApp\' + (Split-Path $f -Leaf))
  $same = (Test-Path $rb) -and ((Sha256 $rb) -eq (Sha256 $f))
  Say ("backup verified   : ConfigApp\" + (Split-Path $f -Leaf) + " same=" + $same)
  if (-not $same) { $bkOk = $false }
}
if (-not $bkOk) { Say 'ABORT: backup not verifiable - touching nothing'; exit 2 }

function Restore-Backup([string]$why) {
  Say ("ROLLBACK START: " + $why)
  & robocopy (Join-Path $bak 'ConfigApp') $pubCfg /E /NFL /NDL /NJH /NJS /NP | Out-Null
  Say ("  robocopy ConfigApp restore exit=" + $LASTEXITCODE)
  Say ("  ConfigApp.exe now=" + (Sha12 $cfgExe) + " expected_backup=" + (Sha12 (Join-Path $bak 'ConfigApp\EWeLinkLinker.ConfigApp.exe')))
  Say ("  service still=" + (Get-Service -Name $svcName -ErrorAction SilentlyContinue).Status)
  Say ("  backup KEPT at " + $bak)
  Say ("$RunId ROLLBACK DONE")
}

# ---------- 3. swap (no stop, no start) ----------
$t0 = Get-Date
& robocopy $cfgStage $pubCfg /E /NFL /NDL /NJH /NJS /NP | Out-Null
$r1 = $LASTEXITCODE
$elapsed = ((Get-Date) - $t0).TotalSeconds
Say ("swap robocopy exit ConfigApp=" + $r1 + " (0-7 means success) took=" + ('{0:N1}' -f $elapsed) + "s")
if ($r1 -gt 7) { Restore-Backup 'robocopy failed'; exit 4 }

# ---------- 4. post checks ----------
Say ('installed after : EWeLinkLinker.ConfigApp.exe sha=' + (Sha12 $cfgExe) + ' ver=' + (ProdVer $cfgExe))
Say ('installed after : EWeLinkLinker.Core.dll sha=' + (Sha12 $cfgCore))
Say ('service after   : EWeLinkLinker.Service.exe sha=' + (Sha12 $svcExe))
Say ('service after   : EWeLinkLinker.Core.dll sha=' + (Sha12 $svcCore))
Say ("stamp note      : service Core ver=" + (ProdVer $svcCore) + " | gui Core ver=" + (ProdVer $cfgCore) + " -> stamps differ by design, sources verified identical above")

$procAfter = @(Get-Process -Name 'EWeLinkLinker.Service' -ErrorAction SilentlyContinue)
$pidAfter  = if ($procAfter.Count) { $procAfter[0].Id } else { 'NONE' }
$StartAfter = if ($procAfter.Count) { $procAfter[0].StartTime.ToString('yyyy-MM-dd HH:mm:ss') } else { 'NONE' }
Say ("service pid     : pid=" + $pidAfter + " started=" + $StartAfter + "  (same_process=" + ($pidAfter -eq $pidBefore) + ")")

$cfgFileAfter = @((Get-Item $cfgFile).Length, (Get-Item $cfgFile).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), (Sha12 $cfgFile))
Say ("config after    : size=" + $cfgFileAfter[0] + " mtime=" + $cfgFileAfter[1] + " sha=" + $cfgFileAfter[2] + " unchanged=" + (($cfgFileAfter -join '|') -eq ($cfgFileBefore -join '|')))

$acl = Get-Acl $cfgExe
$auN = @($acl.Access | Where-Object { (SidOf $_) -eq 'S-1-5-11' }).Count
$usrN = @($acl.Access | Where-Object { (SidOf $_) -eq 'S-1-5-32-545' -and $_.AccessControlType -eq 'Allow' })
$sysN = @($acl.Access | Where-Object { (SidOf $_) -eq 'S-1-5-18' }).Count
Say ("new exe ACL     : aceCount=" + $acl.Access.Count + " SYSTEM=" + $sysN + " AuthUsers=" + $auN + " Users=" + $(if ($usrN.Count) { $usrN[0].FileSystemRights.ToString() } else { 'NONE' }))

$svcStatus = (Get-Service -Name $svcName -ErrorAction SilentlyContinue).Status

$res = [ordered]@{
  versionInstalled = ((ProdVer $cfgExe) -like ('1.0.0+' + $ExpectSha + '*'))
  coreSourcesUnchanged = $coreSourcesUnchanged
  stagedPairStamped  = ($verOk -and $coreVerOk)
  serviceUnchanged = (((Sha256 $svcExe) -eq $svcExeBefore) -and ((Sha256 $svcCore) -eq $svcCoreBefore))
  serviceRunning   = ($svcStatus -eq 'Running')
  serviceSameProc  = ($pidAfter -eq $pidBefore)
  configUnchanged  = (($cfgFileAfter -join '|') -eq ($cfgFileBefore -join '|'))
  aclTight         = ($auN -eq 0 -and $sysN -gt 0 -and ($usrN.Count -gt 0) -and $usrN[0].FileSystemRights.ToString() -match 'Read')
}
foreach ($k in $res.Keys) { Say ("CHECK {0} = {1}" -f $k, $res[$k]) }

$bad = @($res.Keys | Where-Object { -not $res[$_] })
if ($bad.Count -gt 0) { Restore-Backup ('post-check failed: ' + ($bad -join ',')); exit 5 }

# ---------- 5. 清掉不再被引用的旧包 dll（放在所有部署判据之后：这一步失败不许触发回滚）----------
# deps.json 里 0 引用（2026-10-08 实测两份都是 0），纯粹是 robocopy /E 不清_extra_文件留下的旧物。
# 服务正在跑，逐个 try/catch：万一某个还被握着，只记不抛，不能让整趟部署断在这里。
$stale = @('Microsoft.IdentityModel.Abstractions.dll','Microsoft.IdentityModel.JsonWebTokens.dll',
           'Microsoft.IdentityModel.Logging.dll','Microsoft.IdentityModel.Tokens.dll',
           'System.IdentityModel.Tokens.Jwt.dll')
foreach ($dir in @($pubSvc, $pubCfg)) {
  $leaf = Split-Path $dir -Leaf
  $found = @(Get-ChildItem $dir -Filter '*IdentityModel*' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
  Say ("stale found     : " + $leaf + " count=" + $found.Count + " " + ($found -join ','))
  foreach ($n in $found) {
    try {
      Remove-Item (Join-Path $dir $n) -Force -ErrorAction Stop
      Say ("stale removed   : " + $leaf + "\" + $n)
    } catch {
      Say ("stale LEFT      : " + $leaf + "\" + $n + " :: " + $_.Exception.Message)
    }
  }
  $left = @(Get-ChildItem $dir -Filter '*IdentityModel*' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
  Say ("stale after     : " + $leaf + " count=" + $left.Count + " " + ($left -join ','))
}
$svcStatus2 = (Get-Service -Name $svcName -ErrorAction SilentlyContinue).Status
Say ("service after cleanup : status=" + $svcStatus2 + " exe=" + (Sha12 $svcExe) + " core=" + (Sha12 $svcCore))
Say ("CLEANUP verdict   : stale_identity_left=" + (@(foreach ($dir in @($pubSvc,$pubCfg)) { Get-ChildItem $dir -Filter '*IdentityModel*' -File -ErrorAction SilentlyContinue }).Count) +
     " service_running=" + ($svcStatus2 -eq 'Running') +
     " service_bytes_unchanged=" + (((Sha256 $svcExe) -eq $svcExeBefore) -and ((Sha256 $svcCore) -eq $svcCoreBefore)))

Say ("$RunId DEPLOY DONE swap_s=" + ('{0:N1}' -f $elapsed))
exit 0
