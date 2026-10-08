$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$repo = 'E:\ClaudeCode\EWeLinkLinker'
$cur  = Join-Path $repo 'publish\config\linker.json'
$bak  = Join-Path $env:TEMP 'ewl_cfgbak_view1\linker.json'
$out  = Join-Path $env:TEMP 'ewl_gui\cfg_semantic_diff.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Say($m) { $lines.Add($m); [IO.File]::WriteAllLines($out, $lines, (New-Object System.Text.UTF8Encoding($true))) }
# 动态取属性时不要用 "$($x.$f)" 这种嵌套插值（这里解析器会报"缺少 )"），先取出来再转字符串
function Field($obj, $name) { if ($null -eq $obj) { return '<absent>' }; $v = $obj.$name; if ($null -eq $v) { return '' }; return [string]$v }
function Cmp-Fields($a, $b, $names) {
    $d = @()
    foreach ($n in $names) { $fa = Field $a $n; $fb = Field $b $n; if ($fa -ne $fb) { $d += ($n + '(' + $fa + '->' + $fb + ')') } }
    return $d
}

foreach ($f in @($cur, $bak)) {
    if (-not (Test-Path $f)) { Say "MISSING: $f"; exit 4 }
    $i = Get-Item $f
    Say ("file=" + (Split-Path $f -Leaf) + " dir=" + (Split-Path $f -Parent) + " size=" + $i.Length +
         " mtime=" + $i.LastWriteTime.ToString('MM-dd HH:mm:ss') + " sha=" + (Get-FileHash $f -Algorithm SHA256).Hash.Substring(0,12))
}

function Load($p) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json }
$A = Load $bak    # 10-07 02:23 那份（我开的备份）
$B = Load $cur    # 现在盘上这份

function Unp($b64) {
    # 盘上 account.password / tokens.* 是 DPAPI **LocalMachine + Entropy("EWeLinkLinker_v1")** 的 base64
    # （LinkerConfig.Protect 就这么写的）。用 CurrentUser 解会失败，而两个失败串相等会伪装成"没变"，
    # 所以解不开必须报 UNVERIFIED，不给 same=True。
    if ([string]::IsNullOrWhiteSpace($b64)) { return '<empty>' }
    try {
        $entropy = [Text.Encoding]::UTF8.GetBytes('EWeLinkLinker_v1')
        $p = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($b64), $entropy, 'LocalMachine')
        return [Text.Encoding]::UTF8.GetString($p)
    } catch { return "<DECODE-FAILED:$($_.Exception.GetType().Name)>" }
}
function Cmp-Secret($a, $b) {
    if ($a.StartsWith('<') -or $b.StartsWith('<')) {
        return 'UNVERIFIED(两边密文都解不开或只解开一边: ' + $a + ' / ' + $b + ')'
    }
    return 'same=' + ($a -eq $b) + ' liveLen=' + $b.Length
}

Say ("account.account same=" + ((Field $A.account 'account') -eq (Field $B.account 'account')) + " liveLen=" + (Field $B.account 'account').Length)
Say ("account.region same=" + ((Field $A.account 'region') -eq (Field $B.account 'region')) + " countryCode same=" + ((Field $A.account 'countryCode') -eq (Field $B.account 'countryCode')))
$pa = Unp (Field $A.account 'password'); $pb = Unp (Field $B.account 'password')
Say ("password plaintext " + (Cmp-Secret $pa $pb))
foreach ($k in 'accessToken','refreshToken','userApiKey') {
    $va = Unp (Field $A.tokens $k); $vb = Unp (Field $B.tokens $k)
    Say ("tokens.$k plaintext " + (Cmp-Secret $va $vb))
}
Say ("tokenObtainedAtUtc live=" + (Field $B.tokens 'tokenObtainedAtUtc') + " sameAsBak=" + ((Field $A.tokens 'tokenObtainedAtUtc') -eq (Field $B.tokens 'tokenObtainedAtUtc')))
Say ("loggingEnabled bak=" + (Field $A 'loggingEnabled') + " live=" + (Field $B 'loggingEnabled'))
Say ("pollingIntervalSeconds bak=" + (Field $A 'pollingIntervalSeconds') + " live=" + (Field $B 'pollingIntervalSeconds'))

$ad = @{}; foreach ($d in $A.devices) { $ad[$d.deviceId] = $d }
$bd = @{}; foreach ($d in $B.devices) { $bd[$d.deviceId] = $d }
$onlyBak = @($ad.Keys | Where-Object { -not $bd.ContainsKey($_) })
$onlyLive = @($bd.Keys | Where-Object { -not $ad.ContainsKey($_) })
Say ("devices count bak=" + $A.devices.Count + " live=" + $B.devices.Count + " onlyBak=" + $onlyBak.Count + " onlyLive=" + $onlyLive.Count)
foreach ($id in ($ad.Keys | Where-Object { $bd.ContainsKey($_) } | Sort-Object)) {
    $x = $ad[$id]; $y = $bd[$id]
    $d = Cmp-Fields $x $y @('name','ipAddress','macAddress','realMacAddress','isOnline','deviceKey','channelCount')
    if ((Field $x 'channelStates') -ne (Field $y 'channelStates')) {
        # 通道状态数组逐个比，只报哪一路变了，不报设备密钥那类字段值
        $sa = @($x.channelStates); $sb = @($y.channelStates)
        for ($i = 0; $i -lt [Math]::Max($sa.Count, $sb.Count); $i++) {
            if ("$($sa[$i])" -ne "$($sb[$i])") { $d += ("channelStates[$i](" + $sa[$i] + '->' + $sb[$i] + ')') }
        }
    }
    $dNoKey = @($d | ForEach-Object { ($_ -split '\(')[0] })
    Say ("device $id name=" + (Field $y 'name') + " changed=" + $(if ($d.Count) { ($dNoKey -join ',') } else { '(none)' }))
    if ($dNoKey -contains 'deviceKey') { Say '    [deviceKey 变了；值不打印]' }
}

# 规则按名字对齐而不是按 id：ConfigApp 存盘时会给没有 id 的老规则现编一个（两份的 id 本来就不同名）
$ar = @{}; $ai = 0; foreach ($r in $A.rules) { $ai++; $k = (Field $r 'name'); if (-not $k) { $k = "#$ai" }; if ($ar.ContainsKey($k)) { $k = $k + "#$ai" }; $ar[$k] = $r }
$br = @{}; $bi = 0; foreach ($r in $B.rules) { $bi++; $k = (Field $r 'name'); if (-not $k) { $k = "#$bi" }; if ($br.ContainsKey($k)) { $k = $k + "#$bi" }; $br[$k] = $r }
Say ("rules count bak=" + $A.rules.Count + " live=" + $B.rules.Count + " bakIds=[" + (($A.rules | ForEach-Object { Field $_ 'id' }) -join ',') + "] liveIds=[" + (($B.rules | ForEach-Object { Field $_ 'id' }) -join ',') + "]")
foreach ($k in ($br.Keys | Sort-Object)) {
    $x = $ar[$k]; $y = $br[$k]
    $id = Field $y 'id'
    if (-not $x) { Say ("rule [$k] id=$id 只在盘上新那份有"); continue }
    Say ("rule $id name=" + (Field $y 'name') + " enabled bak=" + (Field $x 'enabled') + " live=" + (Field $y 'enabled') + " enabledChanged=" + ((Field $x 'enabled') -ne (Field $y 'enabled')))
    $xc = @($x.conditions); $yc = @($y.conditions)
    Say ("  conditions count bak=" + $xc.Count + " live=" + $yc.Count)
    for ($i = 0; $i -lt [Math]::Max($xc.Count, $yc.Count); $i++) {
        $cx = $xc[$i]; $cy = $yc[$i]
        if (-not $cy) { Say ("    cond[$i] 只在旧那份有: type=" + (Field $cx 'type')); continue }
        if (-not $cx) { Say ("    cond[$i] 新增: type=" + (Field $cy 'type') + " param=" + (Field $cy 'parameter') + " cmp=" + (Field $cy 'comparison') + " band=" + (Field $cy 'releaseBand')); continue }
        $d = Cmp-Fields $cx $cy @('type','parameter','comparison','operator','releaseBand','releaseParameter')
        Say ("    cond[$i] " + $(if ($d.Count) { 'changed: ' + ($d -join ' ') } else { 'unchanged' }) + " live=type=" + (Field $cy 'type') + " param=" + (Field $cy 'parameter') + " cmp=" + (Field $cy 'comparison') + " band=" + (Field $cy 'releaseBand'))
    }
    $xa = @($x.actions); $ya = @($y.actions)
    Say ("  actions count bak=" + $xa.Count + " live=" + $ya.Count)
    for ($i = 0; $i -lt [Math]::Max($xa.Count, $ya.Count); $i++) {
        $cax = $xa[$i]; $cay = $ya[$i]
        if (-not $cay) { Say ("    action[$i] 只在旧那份有: dev=" + (Field $cax 'deviceId') + " name=" + (Field $cax 'name')); continue }
        if (-not $cax) { Say ("    action[$i] 新增: dev=" + (Field $cay 'deviceId') + " name=" + (Field $cay 'name') + " ch=" + (Field $cay 'outlet') + " state=" + (Field $cay 'state')); continue }
        $d = Cmp-Fields $cax $cay @('deviceId','name','state','outlet')
        Say ("    action[$i] " + $(if ($d.Count) { 'changed: ' + ($d -join ' ') } else { 'unchanged' }) + " live=dev=" + (Field $cay 'deviceId') + " name=" + (Field $cay 'name') + " ch=" + (Field $cay 'outlet') + " state=" + (Field $cay 'state'))
    }
}
Say 'DIFF_DONE'
Write-Output 'WROTE'
