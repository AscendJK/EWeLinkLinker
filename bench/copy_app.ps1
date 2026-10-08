param([Parameter(Mandatory=$true)][string]$Src, [string]$Label = 'app')
$ErrorActionPreference = 'Stop'
$sb  = Join-Path $env:TEMP 'ewl_gui'
$app = Join-Path $sb 'app'
$out = Join-Path $sb ("copy_app_{0}.txt" -f $Label)
$lines = New-Object System.Collections.Generic.List[string]
function Say($m) { $lines.Add($m); [IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding($true))) }

if (-not (Test-Path (Join-Path $Src 'EWeLinkLinker.ConfigApp.exe'))) { Say "ABORT: 源目录里没有 ConfigApp.exe → $Src"; exit 3 }
Get-Process -Name 'EWeLinkLinker.ConfigApp' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit() }
New-Item -ItemType Directory -Force -Path $app | Out-Null
Remove-Item "$app\*" -Recurse -Force
& robocopy $Src $app /E /NFL /NDL /NJH /NJS /NP | Out-Null
$rc = $LASTEXITCODE
Say ("robocopy rc=$rc src=$Src")
if ($rc -ge 8) { Say 'ABORT: robocopy failed'; exit 3 }
foreach ($f in 'EWeLinkLinker.ConfigApp.exe','EWeLinkLinker.ConfigApp.dll','EWeLinkLinker.Core.dll') {
    $a = Join-Path $Src $f; $b = Join-Path $app $f
    if (-not (Test-Path $a)) { Say "MISSING in src: $f"; exit 4 }
    if (-not (Test-Path $b)) { Say "MISSING in sandbox: $f"; exit 4 }
    $ha = (Get-FileHash $a -Algorithm SHA256).Hash.Substring(0,12)
    $hb = (Get-FileHash $b -Algorithm SHA256).Hash.Substring(0,12)
    Say ("{0} src={1} sandbox={2} same={3}" -f $f, $ha, $hb, ($ha -eq $hb))
    if ($ha -ne $hb) { Say 'ABORT: 沙盒里跑的不是指定那份产物'; exit 5 }
}
Say ("ProductVersion=" + (Get-Item (Join-Path $app 'EWeLinkLinker.ConfigApp.dll')).VersionInfo.ProductVersion)
Say ("exe_mtime=" + (Get-Item (Join-Path $app 'EWeLinkLinker.ConfigApp.exe')).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
Say "OK"
exit 0
