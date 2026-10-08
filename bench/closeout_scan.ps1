$ErrorActionPreference = 'Continue'
$out = Join-Path $env:TEMP 'ewl_gui\closeout_scan.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Say($m) { $lines.Add($m); [IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding($true))) }
Say ('=== 收尾扫描 at ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ===')

Say '--- 还在跑的进程（产品该留的：服务；测试该关的：ConfigApp / 台架 pwsh）---'
foreach ($n in @('EWeLinkLinker.ConfigApp','EWeLinkLinker.Service','dotnet')) {
  $p = @(Get-Process -Name $n -ErrorAction SilentlyContinue)
  Say ('  ' + $n + ' = ' + $p.Count + $(if ($p.Count) { ' pid=' + ($p.Id -join ',') } else { '' }))
}
Say ('  service state = ' + (Get-Service EWeLinkLinker).Status)

Say '--- TEMP 下所有 ewl_* 目录（大小 / 最后写入）---'
foreach ($d in (Get-ChildItem $env:TEMP -Directory -Filter 'ewl_*' | Sort-Object Name)) {
  $sz = (Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
  $n  = @(Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue).Count
  Say ('  ' + $d.Name.PadRight(22) + ' files=' + $n.ToString().PadLeft(5) + '  ' + ('{0,8:N1}' -f ($sz/1MB)) + ' MB  mtime=' + $d.LastWriteTime.ToString('MM-dd HH:mm'))
}

Say '--- 全盘 TEMP 里所有叫 linker.json 的文件（只看哈希，不看内容）---'
$live = 'E:\ClaudeCode\EWeLinkLinker\publish\config\linker.json'
$liveSha = (Get-FileHash $live -Algorithm SHA256).Hash
Say ('  真件 sha=' + $liveSha.Substring(0,12) + ' size=' + (Get-Item $live).Length)
$copies = @(Get-ChildItem $env:TEMP -Recurse -Filter 'linker.json' -File -ErrorAction SilentlyContinue)
Say ('  TEMP 里的份数 = ' + $copies.Count)
foreach ($c in $copies) {
  $h = (Get-FileHash $c.FullName -Algorithm SHA256).Hash
  Say ('    ' + $c.FullName.Replace($env:TEMP,'~') + '  size=' + $c.Length + ' sha=' + $h.Substring(0,12) + ' 等于真配置=' + ($h -eq $liveSha))
}
Say '--- TEMP 里的 debug.log / 截图 / 台架 transcript ---'
foreach ($pat in @('*.png','debug.log','*.txt')) {
  $f = @(Get-ChildItem $env:TEMP -Recurse -Filter $pat -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'ewl_' })
  $sz = ($f | Measure-Object Length -Sum).Sum
  Say ('  ' + $pat + ' = ' + $f.Count + ' 个 ' + ('{0:N1}' -f ($sz/1MB)) + ' MB')
}
Say '--- 仓库状态 ---'
Say ('  git HEAD      = ' + ((& git -C 'E:\ClaudeCode\EWeLinkLinker' rev-parse --short HEAD) 2>&1 | Out-String).Trim())
Say ('  工作区改动数  = ' + (@(& git -C 'E:\ClaudeCode\EWeLinkLinker' status --porcelain 2>&1).Count))
Say ('  未推提交数    = ' + ((& git -C 'E:\ClaudeCode\EWeLinkLinker' rev-list --count 'origin/main..HEAD') 2>&1 | Out-String).Trim())
Say ('  远端 main     = ' + ((& git -C 'E:\ClaudeCode\EWeLinkLinker' ls-remote origin refs/heads/main) 2>&1 | Out-String).Trim().Substring(0,12))
Say '--- 服务日志目录（产品自己的东西，删不删由他定）---'
foreach ($f in (Get-ChildItem 'E:\ClaudeCode\EWeLinkLinker\publish\Service\logs' -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
  Say ('  ' + $f.Name + ' ' + ('{0:N1}' -f ($f.Length/1MB)) + ' MB  mtime=' + $f.LastWriteTime.ToString('MM-dd HH:mm'))
}
Write-Output 'WROTE'
