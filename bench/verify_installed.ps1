$ErrorActionPreference = 'Continue'
$out = Join-Path $env:TEMP 'ewl_gui\verify_installed_6e20.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Say($m) { $lines.Add($m); [IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding($true))) }
$repo = 'E:\ClaudeCode\EWeLinkLinker'
$svcExe = Join-Path $repo 'publish\Service\EWeLinkLinker.Service.exe'
$cfgExe = Join-Path $repo 'publish\ConfigApp\EWeLinkLinker.ConfigApp.exe'
$cfg    = Join-Path $repo 'publish\config\linker.json'
Say ('svc_exe_ver = ' + (Get-Item $svcExe).VersionInfo.ProductVersion)
Say ('gui_exe_ver = ' + (Get-Item $cfgExe).VersionInfo.ProductVersion)
Say ('svc_exe_sha = ' + (Get-FileHash $svcExe -Algorithm SHA256).Hash.Substring(0,12))
Say ('gui_exe_sha = ' + (Get-FileHash $cfgExe -Algorithm SHA256).Hash.Substring(0,12))
Say ('svc_core    = ' + (Get-FileHash (Join-Path $repo 'publish\Service\EWeLinkLinker.Core.dll') -Algorithm SHA256).Hash.Substring(0,12))
Say ('gui_core    = ' + (Get-FileHash (Join-Path $repo 'publish\ConfigApp\EWeLinkLinker.Core.dll') -Algorithm SHA256).Hash.Substring(0,12))
$stale = @(Get-ChildItem (Join-Path $repo 'publish') -Recurse -Filter '*IdentityModel*' -File -ErrorAction SilentlyContinue)
Say ('stale_identity_left = ' + $stale.Count + ' ' + (($stale | ForEach-Object { $_.FullName }) -join ', '))
$svc = Get-Service EWeLinkLinker
Say ('service = ' + $svc.Status + ' startType=' + $svc.StartType)
$p = @(Get-Process -Name 'EWeLinkLinker.Service' -ErrorAction SilentlyContinue)
Say ('service_pid = ' + $(if ($p.Count) { $p[0].Id } else { 'NONE' }))
Say ('config = ' + (Get-Item $cfg).Length + ' bytes, mtime ' + (Get-Item $cfg).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + ', sha ' + (Get-FileHash $cfg -Algorithm SHA256).Hash.Substring(0,12))
$log = Join-Path $repo ('publish\Service\logs\service-' + (Get-Date).ToString('yyyy-MM-dd') + '.log')
$fs = New-Object IO.FileStream($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
$sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $txt = $sr.ReadToEnd(); $fs.Dispose()
$all = @(($txt -split "\r?\n") | Where-Object { $_.Trim() })
Say ('log_lines = ' + $all.Count)
Say '--- 最后 8 行 ---'
foreach ($l in ($all | Select-Object -Last 8)) { Say ('  ' + $l.Trim()) }
$since = @($all | Where-Object { $_ -match '12:39:5' })
Say ('--- 本次启动(12:39:5x)之后的 ERROR/WARN 计数 = ' + (@($since | Where-Object { $_ -match '\[ERROR\]|\[WARN\]' }).Count) + ' ---')
$fire = @($all | Where-Object { $_ -match '规则触发|条件触发|发送|已发出' })
Say ('触发/下发相关行数(全天) = ' + $fire.Count)
foreach ($l in ($fire | Select-Object -Last 6)) { Say ('  ' + $l.Trim()) }
Write-Output 'WROTE'
