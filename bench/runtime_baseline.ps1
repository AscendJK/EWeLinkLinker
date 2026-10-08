param([string]$Tag = 'now', [string]$WaitUntil = '')
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
$repo = 'E:\ClaudeCode\EWeLinkLinker'
$log  = Join-Path $repo ('publish\Service\logs\service-' + (Get-Date).ToString('yyyy-MM-dd') + '.log')
$out  = Join-Path $env:TEMP ('ewl_gui\runtime_baseline_' + $Tag + '.txt')
$lines = New-Object System.Collections.Generic.List[string]
function Say($m) { $lines.Add($m); [IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding($true))) }

if ($WaitUntil -match '^(\d{1,2}):(\d{2})$') {
  $t = (Get-Date -Hour ([int]$Matches[1]) -Minute ([int]$Matches[2]) -Second 0)
  if ($t -lt (Get-Date)) { $t = $t.AddHours(24) }
  while ((Get-Date) -lt $t) { Start-Sleep -Seconds 20 }
}
Say ('=== 运行时基线采样 tag=' + $Tag + ' at ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ===')

# ---- 1. 进程指标：非提权能不能读到，逐字段试，读不到就写 NA ----
# PID 走 CIM：`sc.exe query` 的本地化输出里 "PID" 那行取不到（实测报 NONE），别拿它当"进程没了"
$svcPid = 'NONE'
try {
  $w = Get-CimInstance Win32_Service -Filter "Name='EWeLinkLinker'" -ErrorAction Stop
  if ($w -and $w.ProcessId) { $svcPid = [string]$w.ProcessId }
  Say ('service pid (CIM) = ' + $svcPid + '  state=' + $w.State + '  startmode=' + $w.StartMode)
} catch { Say ('  CIM 取 PID 失败: ' + $_.Exception.Message.Trim()) }
if ($svcPid -ne 'NONE') {
  $pr = Get-Process -Id ([int]$svcPid) -ErrorAction SilentlyContinue
  if ($pr) {
    foreach ($f in @(@('WorkingSet MB', { $pr.WorkingSet64 / 1MB }),
                     @('PrivateMemory MB', { $pr.PrivateMemorySize64 / 1MB }),
                     @('HandleCount', { $pr.HandleCount }),
                     @('ThreadCount', { $pr.ThreadCount }),
                     @('PeakWorkingSet MB', { $pr.PeakWorkingSet64 / 1MB }))) {
      try { Say ('  ' + $f[0] + ' = ' + ('{0:N1}' -f ([double](& $f[1])))) }
      catch { Say ('  ' + $f[0] + ' = NA（' + $_.Exception.Message.Trim() + '）') }
    }
    try { Say ('  cpu_seconds = NA（非提权读不到别的用户的进程，10-07 也是读不到）') } catch {}
  } else { Say '  进程句柄拿不到' }
}

# ---- 2. 日志窗口：从最后一次启动头开始，避免把上一世的数据混进来 ----
$fs = New-Object IO.FileStream($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
$sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $txt = $sr.ReadToEnd(); $fs.Dispose()
$all = @(($txt -split "\r?\n") | Where-Object { $_.Trim() })
Say ('主日志总行数 = ' + $all.Count)
$hdr = -1
for ($i = $all.Count - 1; $i -ge 0; $i--) { if ($all[$i] -match 'EWeLink Linker Service 1\.0\.0\+') { $hdr = $i; break } }
$since = if ($hdr -ge 0) { @($all | Select-Object -Skip $hdr) } else { $all }
Say ('本次启动头 = ' + $(if ($hdr -ge 0) { $all[$hdr].Trim() } else { '没找到，用全文件' }))
Say ('本次启动以来行数 = ' + $since.Count + '  首行时间 = ' + $(if ($since.Count) { ($since[0] -replace '^\[|\].*','') } else { '-' }) + '  末行时间 = ' + $(if ($since.Count) { ($since[$since.Count-1] -replace '^\[|\].*','') } else { '-' }))

# ---- 3. 单轮轮询耗时：[轮询] 那一行 → 同轮最后一条 [RuleTrigger 判定行的时间差（主日志带毫秒）----
function Ts($l) {
  $m = [regex]::Match($l, '^\[(\d{2}):(\d{2}):(\d{2})\.(\d{3})\]')
  if (-not $m.Success) { return $null }
  return [double](([int]$m.Groups[1].Value * 3600 + [int]$m.Groups[2].Value * 60 + [int]$m.Groups[3].Value) * 1000 + [int]$m.Groups[4].Value)
}
$deltas = New-Object System.Collections.Generic.List[double]
$cur = $null; $curT = $null; $last = $null
foreach ($l in $since) {
  $t = Ts $l
  if ($null -eq $t) { continue }
  if ($l -match '\[轮询\] 触发器') {
    if ($null -ne $cur -and $null -ne $last) { $deltas.Add($last - $cur) }
    $cur = $t; $last = $null
  } elseif ($l -match '\[RuleTrigger' -and $null -ne $cur) { $last = $t }
}
if ($null -ne $cur -and $null -ne $last) { $deltas.Add($last - $cur) }
$arr = @($deltas | Sort-Object)
Say ('轮询→判定 样本数 = ' + $arr.Count + '（0 = 我的配对没命中，别当"耗时 0"）')
if ($arr.Count -gt 0) {
  $med = $arr[[int][Math]::Floor($arr.Count / 2)]
  $p95 = $arr[[Math]::Min($arr.Count - 1, [int][Math]::Ceiling($arr.Count * 0.95) - 1)]
  Say ('  中位 = ' + ('{0:N0}' -f $med) + 'ms  p95 = ' + ('{0:N0}' -f $p95) + 'ms  最大 = ' + ('{0:N0}' -f $arr[$arr.Count - 1]) + 'ms')
  Say ('  10-07 基线：中位 48ms')
}
Say ('轮询周期耗时过长(>3s) 警告条数 = ' + (@($since | Where-Object { $_ -match '轮询周期耗时过长' }).Count) + '  10-07 基线：0 条')
Say ('[ERROR] 条数 = ' + (@($since | Where-Object { $_ -match '\[ERROR\]' }).Count) + '   [WARN] 条数 = ' + (@($since | Where-Object { $_ -match '\[WARN\]' }).Count))
foreach ($l in (@($since | Where-Object { $_ -match '\[WARN\]|\[ERROR\]' }) | Select-Object -First 8)) { Say ('  | ' + $l.Trim()) }
Say ('规则触发条数 = ' + (@($since | Where-Object { $_ -match '!! 规则触发' }).Count) + '   动作执行条数 = ' + (@($since | Where-Object { $_ -match '动作执行' }).Count))
Say ('轮询轮数 = ' + (@($since | Where-Object { $_ -match '\[轮询\] 触发器' }).Count))

# ---- 4. 整机负载（判"内存平不平"时要顺手记一条，避免把机器空闲当成进程稳定）----
try {
  $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
  Say ('  系统内存: 总 ' + ('{0:N1}' -f ($os.TotalVisibleMemorySize / 1MB)) + ' GB 空闲 ' + ('{0:N1}' -f ($os.FreePhysicalMemory / 1MB)) + ' GB')
} catch { Say ('  系统内存 = NA（' + $_.Exception.Message.Trim() + '）') }
Say '=== END ==='
Write-Output ('WROTE ' + $out)
