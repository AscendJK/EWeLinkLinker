$ErrorActionPreference = 'Stop'
# 权威一跑：先把"刚编出来的那份"塞进沙盒并硬断言字节一致，再跑全部臂，报告和产物指纹写进同一份 transcript
$sb     = Join-Path $env:TEMP 'ewl_gui'
$stage  = Join-Path $env:TEMP 'ewl_stage_final'
$appDir = Join-Path $sb 'app'
$rep    = Join-Path $sb 'final_run.txt'
$lines  = New-Object System.Collections.Generic.List[string]
function Say($m) { $lines.Add($m); [IO.File]::WriteAllLines($rep, $lines, (New-Object System.Text.UTF8Encoding($true))) }

Say ("now=" + (Get-Date -Format 'HH:mm:ss'))
Get-Process -Name 'EWeLinkLinker.ConfigApp' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit() }
& robocopy $stage $appDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
$rc = $LASTEXITCODE
Say ("robocopy_rc=$rc src=$stage")
if ($rc -ge 8) { Say 'ABORT'; exit 3 }
foreach ($f in 'EWeLinkLinker.ConfigApp.exe','EWeLinkLinker.ConfigApp.dll','EWeLinkLinker.Core.dll') {
    $hs = (Get-FileHash (Join-Path $stage $f) -Algorithm SHA256).Hash.Substring(0,12)
    $hd = (Get-FileHash (Join-Path $appDir $f) -Algorithm SHA256).Hash.Substring(0,12)
    Say ("{0} stage={1} sandbox={2} same={3}" -f $f, $hs, $hd, ($hs -eq $hd))
    if ($hs -ne $hd) { Say 'ABORT: 沙盒里不是刚编出来的那份'; exit 5 }
}
Say '--- 跑全部臂 ---'
$out = & pwsh -NoProfile -File (Join-Path $sb 'gui_arms.ps1') 2>&1
Say ('arms_stdout=' + ($out -join ' '))
Say ('arms_rc=' + $LASTEXITCODE)
$report = Get-Content (Join-Path $sb 'gui_arms_ALL.txt') -Raw -Encoding UTF8
$pass = @([regex]::Matches($report, 'CHECK\[PASS\]')).Count
$fail = @([regex]::Matches($report, 'CHECK\[FAIL\]')).Count
Say ("pass_checks=$pass fail_checks=$fail")
$arms = @([regex]::Matches($report, '(?m)^--- 臂 ([^\r\n]+)')) | ForEach-Object { $_.Groups[1].Value }
Say ('arms_ran=' + ($arms -join ' ; '))
Say ('tail=' + (($report -split "`n" | Select-Object -Last 2) -join ' | '))
Say ('FINAL_rc=' + $LASTEXITCODE + ' fail=' + $fail)
exit $(if ($LASTEXITCODE -eq 0 -and $fail -eq 0) { 0 } else { 1 })
