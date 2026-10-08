# 第一包 GUI 沙盒验收（分臂版）：每条臂一个独立进程 + 独立种子配置，结论从程序自己写的 debug.log 里取
param([string]$Only = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$sig = @'
[DllImport("user32.dll", SetLastError=true)]
public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

[DllImport("user32.dll", SetLastError=true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
'@
Add-Type -MemberDefinition $sig -Name U32 -Namespace Win
$MOVEDOWN = 0x0002; $MOVEUP = 0x0004

$AE   = [System.Windows.Automation.AutomationElement]
$TS   = [System.Windows.Automation.TreeScope]
$CT   = [System.Windows.Automation.ControlType]
$PC   = [System.Windows.Automation.PropertyCondition]
$root = $AE::RootElement

$sb    = Join-Path $env:TEMP 'ewl_gui'
$exe   = Join-Path $sb 'app\EWeLinkLinker.ConfigApp.exe'
$cfg   = Join-Path $sb 'config\linker.json'
$debug = Join-Path $sb 'config\debug.log'
$shots = Join-Path $sb 'shots'
$rep   = Join-Path $sb ("gui_arms_{0}.txt" -f $(if ($Only) { $Only } else { 'ALL' }))
# 沙盒的 config/shots 目录会被"清临时文件"删掉，这里自己补回来，否则 Seed 写 linker.json 直接抛
New-Item -ItemType Directory -Force -Path (Split-Path $cfg -Parent) | Out-Null
New-Item -ItemType Directory -Force -Path $shots | Out-Null
$lines = New-Object System.Collections.Generic.List[string]
$script:failed = 0
function Say($m) { $script:lines.Add($m) }
function Flush { $script:lines | Set-Content -Path $rep -Encoding UTF8 }
function Check($ok, $m) { if (-not $ok) { $script:failed++ }; Say "CHECK[$(if($ok){'PASS'}else{'FAIL'})] $m" }
function Hash-Cfg { (Get-FileHash -Path $cfg -Algorithm SHA256).Hash }
function Shot($tag, $rect) {
    $x = [int][Math]::Floor($rect.X) - 6; $y = [int][Math]::Floor($rect.Y) - 6
    $w = [int][Math]::Ceiling($rect.Width) + 12; $h = [int][Math]::Ceiling($rect.Height) + 12
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $png = Join-Path $shots "$tag.png"
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
    Say "  [截图] $tag.png (x=$x y=$y w=$w h=$h)"
}

function Click-At($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$x, [int]$y)
    Start-Sleep -Milliseconds 120
    [Win.U32]::mouse_event($MOVEDOWN, 0, 0, 0, 0); Start-Sleep -Milliseconds 60
    [Win.U32]::mouse_event($MOVEUP, 0, 0, 0, 0); Start-Sleep -Milliseconds 450
}
# 只认被测进程的 ListItem：从桌面根枚举会混进别的窗口（实测抓到过 README 正文的
# 列表项，rect 在 y=-2305），于是"点完弹窗还在（N 项残留）"是假的，补的那次 ESC 反而改掉了选中项。
function List-Items {
    @( $root.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ListItem))) |
       ForEach-Object { [pscustomobject]@{ El = $_; R = $_.Current.BoundingRectangle; Raw = $_.Current.Name } } |
       Where-Object { -not $script:appPid -or $_.El.Current.ProcessId -eq $script:appPid } )
}
# 下拉当前真的选中了什么：B1/C1 原来只证明"列表里有这一项"，整序跑时它绿着而模型里已经是别的设备。
# WPF 这里 ComboBox 自己的 Name 是空的（实测），所以要往下找带 SelectionItem 且 IsSelected 的孩子。
function Selected-Text($cb) {
    if (-not $cb) { return '' }
    try { if ($cb.Current.Name) { return $cb.Current.Name } } catch { }
    try {
        $vp = $cb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($vp.Current.Value) { return $vp.Current.Value }
    } catch { }
    $kids = @($cb.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
    foreach ($k in $kids) {
        try {
            $sp = $k.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($sp.Current.IsSelected -and $k.Current.Name) { return $k.Current.Name }
        } catch { }
    }
    foreach ($k in $kids) { if ($k.Current.Name) { return $k.Current.Name } }
    return ''
}
# 读不到显示文字时，"空白"和"我的探针不行"长得一模一样——所以把整棵子树打出来分清楚是哪一种
function Dump-Combo($cb, $tag) {
    if (-not $cb) { Say "  dump[$tag] 没有控件引用"; return }
    $kids = @($cb.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
    Say ("  dump[$tag] 控件 Name='" + $cb.Current.Name + "' 后代=" + $kids.Count)
    foreach ($k in ($kids | Select-Object -First 12)) {
        $sel = ''
        try { $sel = ' isSel=' + ($k.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected } catch { }
        Say ("    - " + $k.Current.ControlType.ProgrammaticName.Replace('ControlType.','') + " '" + $k.Current.Name + "'" + $sel)
    }
}
function Item-Text($it) {
    if ($it.Raw -and $it.Raw -notlike 'EWeLinkLinker.*') { return $it.Raw }
    try {
        $t = $it.El.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::Text)))
        if ($t.Count -gt 0) { return $t[0].Current.Name }
    } catch {}
    return $it.Raw
}
function Close-Dropdown { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 350 }
# 用 UIA 的 ExpandCollapse 展开，不靠合成鼠标点击：
# 这台机器桌面上有一个 Pane 名为 FLUTTERVIEW 的窗口盖住屏幕右半（实测 FromPoint 在 x>=1281 全部命中它），
# 落在被盖住区域的点击根本到不了被测程序，A 臂因此假红（4 条通道下拉全 0 项）。
function Expand-Items($el, $tag) {
    $pat = $null
    try { $pat = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) }
    catch { Say "  dropdown[$tag] 拿不到 ExpandCollapse 模式: " + $_.Exception.Message; return ,@{ Names = @() } }
    $pat.Expand(); Start-Sleep -Milliseconds 800
    $items = @($el.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ListItem))))
    $named = @($items | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    Say "  dropdown[$tag] -> $($named.Count) 项: $($named -join ',')"
    $pat.Collapse(); Start-Sleep -Milliseconds 250
    return ,@{ Names = $named }
}
function Open-Dropdown($x, $y, $tag) {
    Close-Dropdown
    $before = @(List-Items | ForEach-Object { "$($_.Raw)|$([int]$_.R.Y)" })
    Click-At $x $y
    Start-Sleep -Milliseconds 250
    $new = @(List-Items | Where-Object { $before -notcontains "$($_.Raw)|$([int]$_.R.Y)" })
    if ($new.Count -eq 0) { Start-Sleep -Milliseconds 600; $new = @(List-Items | Where-Object { $before -notcontains "$($_.Raw)|$([int]$_.R.Y)" }) }
    $named = @($new | ForEach-Object { Item-Text $_ } | Select-Object -Unique)
    Say "  dropdown[$tag] -> $($named.Count) 项: $($named -join ',')"
    return ,@{ Names = $named; Raw = $new }
}
# 点开某个下拉、按显示文字挑一条、真实点击它（条目矩形全部打出来，不然点错了查都不知道怎么点错的）
function Pick-By-Text($x, $y, $want, $tag, $cb) {
    Close-Dropdown
    $before = @(List-Items | ForEach-Object { "$($_.Raw)|$([int]$_.R.Y)" })
    Click-At $x $y
    Start-Sleep -Milliseconds 400
    $items = @(List-Items | Where-Object { $before -notcontains "$($_.Raw)|$([int]$_.R.Y)" })
    if ($items.Count -eq 0) { Say "  !! [$tag] 点了 $x,$y 没弹出任何东西"; return $false }
    foreach ($it in $items) {
        $r = $it.R
        Say ("    item '{0}' rect=({1},{2}) {3}x{4}" -f (Item-Text $it), [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    }
    $hit = $items | Where-Object { (Item-Text $_) -eq $want } | Select-Object -First 1
    if (-not $hit) { Say "  !! [$tag] 下拉里没有「$want」"; return $false }
    Click-At ([int]($hit.R.X + $hit.R.Width / 2)) ([int]($hit.R.Y + $hit.R.Height / 2))
    Start-Sleep -Milliseconds 800
    $left = @(List-Items | Where-Object { $before -notcontains "$($_.Raw)|$([int]$_.R.Y)" })
    if ($left.Count -gt 0) { Say "  !! [$tag] 点完弹窗还在（$($left.Count) 项残留），补一次 ESC"; Close-Dropdown }
    $got = Selected-Text $cb
    # 收起状态的下拉在 UIA 树里是"0 个后代 + 空 Name"（实测），显示文字根本读不出来，
    # 所以这里只打一行参考。真能翻红的判据是落盘（B4/C3）和像素（J 臂）。
    if ($cb) { Say "  picked[$tag] UIA 读到「$got」（收起的下拉读不到，不据此判）" }
    return $true
}
# 一格里"有没有字"用像素判：WPF 收起的 ComboBox 对 UIA 是空的，但屏幕上的字是实的。
# 只取左侧文字区（右边留给下拉箭头），数深色像素。
function Ink-In($rect, $tag) {
    $x = [int][Math]::Floor($rect.X) + 7
    $w = [int][Math]::Max(10, [Math]::Floor($rect.Width * 0.55))
    $y = [int][Math]::Floor($rect.Y) + 7
    $h = [int][Math]::Max(6, [int]$rect.Height - 14)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $dark = 0
    for ($py = 0; $py -lt $h; $py++) {
        for ($px = 0; $px -lt $w; $px++) {
            $c = $bmp.GetPixel($px, $py)
            if ($c.R -lt 110 -and $c.G -lt 110 -and $c.B -lt 135) { $dark++ }
        }
    }
    $g.Dispose(); $bmp.Dispose()
    Say ("  ink[$tag] 文字区 $($w)x$($h) @(x=$x,y=$y) 深色像素=$dark")
    return $dark
}
# MessageBox 是 #32770，但它不在桌面根的直接子节点里（实测挂在更深层），只能按名字往下找
function Find-DialogEl($name) {
    if ($script:win) {
        $d = $script:win.FindFirst($TS::Descendants, (New-Object $PC($AE::NameProperty, $name)))
        if ($d) { return $d }
    }
    return $root.FindFirst($TS::Descendants, (New-Object $PC($AE::NameProperty, $name)))
}
function Find-Dialog($name, $seconds) {
    $d = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $d) {
        $w = Find-DialogEl $name
        if ($w) { return $w }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Dismiss-Dialog() {
    $d = Find-DialogEl '无法保存'
    if (-not $d) { return $true }
    $btns = @($d.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::Button))))
    $invoked = $false
    if ($btns.Count -gt 0) {
        try { ($btns[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); $invoked = $true } catch { }
    }
    if (-not $invoked) { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
    Start-Sleep -Milliseconds 700
    return (-not (Find-DialogEl '无法保存'))
}
function Click-Save($win) {
    $b = @($win.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::Button)))) |
        Where-Object { $_.Current.Name -eq '保存配置' } | Select-Object -First 1
    if (-not $b) { return $false }
    ($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    return $true
}
# 弹窗正文在 class='Static' 的 Pane 节点上（不是 Text 节点），所以按名字收所有子节点
function Dialog-Body($d) {
    (@($d.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
       ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' ')
}
function Log-Text() { if (Test-Path $debug) { Get-Content $debug -Raw -Encoding UTF8 } else { '' } }

function Click-Button($win, $name) {
    $b = @($win.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::Button)))) |
        Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $b) { Say "  !! 找不到按钮「$name」"; return $false }
    ($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    return $true
}
# 一次点击可能弹「错误」也可能弹「刷新完成」，只能轮"哪个先出现"，不能按名字各等一遍（否则失败臂要空等满超时）
function Wait-AnyDialog($names, $seconds) {
    $d = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $d) {
        foreach ($n in $names) {
            $el = Find-DialogEl $n
            if ($el) { return @{ Name = $n; El = $el } }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Dismiss-Any($dlg) {
    $btns = @($dlg.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::Button))))
    $ok = $false
    if ($btns.Count -gt 0) {
        try { ($btns[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); $ok = $true } catch {}
    }
    if (-not $ok) { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
    Start-Sleep -Milliseconds 800
    return $ok
}
function SaveCfg-H($app) {
    # 崩完之后他确实又存了一次（09:13:36 那次「刷新状态」末尾的 SaveConfig）；
    # 这条臂也要走完这一步，才看得到空 deviceId 到底落不落得了盘。
    [void](Click-Save $app.Win)
    Start-Sleep -Milliseconds 1800
    $bad = Find-DialogEl '无法保存'
    if ($bad) { Say ('  保存被拦住: ' + (Dialog-Body $bad)); Dismiss-Any $bad | Out-Null }
}
function Actions-From-Cfg() {
    $j = Get-Content $cfg -Raw -Encoding UTF8 | ConvertFrom-Json
    @($j.rules | ForEach-Object { $_.actions } | ForEach-Object {
        [pscustomobject]@{ deviceId = $_.deviceId; name = $_.name; state = $_.state; outlet = $_.outlet } })
}

# 种子设备：1 / 4 / 8 路各一台
function Device-List {
    @(
        [ordered]@{ deviceId='sandbox-1ch'; name='沙盒一路插座'; ipAddress='192.0.2.11'; deviceKey=''; macAddress='aa:bb:cc:00:00:01'; realMacAddress='aa:bb:cc:00:00:01'; isOnline=$true; channelCount=1; channelStates=@('off') },
        [ordered]@{ deviceId='sandbox-4ch'; name='沙盒四路插排'; ipAddress='192.0.2.12'; deviceKey=''; macAddress='aa:bb:cc:00:00:02'; realMacAddress='aa:bb:cc:00:00:02'; isOnline=$true; channelCount=4; channelStates=@('off','off','off','off') },
        [ordered]@{ deviceId='sandbox-4chB'; name='沙盒四路插排乙'; ipAddress='192.0.2.14'; deviceKey=''; macAddress='aa:bb:cc:00:00:04'; realMacAddress='aa:bb:cc:00:00:04'; isOnline=$true; channelCount=4; channelStates=@('off','off','off','off') },
        [ordered]@{ deviceId='sandbox-8ch'; name='沙盒八路PDU';  ipAddress='192.0.2.13'; deviceKey=''; macAddress='aa:bb:cc:00:00:03'; realMacAddress='aa:bb:cc:00:00:03'; isOnline=$true; channelCount=8; channelStates=@('off','off','off','off','off','off','off','off') }
    )
}

# 通道那一格：展开后直接对目标项 Select（按坐标点会被别的窗口的弹层骗到，见 List-Items 那条注释）
function Pick-Channel($cb, $want, $tag) {
    $pat = $null
    try { $pat = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) }
    catch { Say "  !! [$tag] 拿不到 ExpandCollapse: " + $_.Exception.Message; return $false }
    $pat.Expand(); Start-Sleep -Milliseconds 700
    $items = @($cb.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ListItem))))
    $hit = $null
    foreach ($it in $items) { if ($it.Current.Name -eq $want) { $hit = $it; break } }
    if (-not $hit) {
        Say ("  !! [$tag] 通道下拉里没有「$want」，实到 [" + (($items | ForEach-Object { $_.Current.Name }) -join ',') + ']')
        try { $pat.Collapse() } catch { }
        return $false
    }
    try { ($hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
    catch { Say ("  !! [$tag] Select 失败: " + $_.Exception.Message); try { $pat.Collapse() } catch { }; return $false }
    Start-Sleep -Milliseconds 600
    try { $pat.Collapse() } catch { }
    return $true
}

# 种子配置：3 台设备（1/4/8 路）+ 一条规则（一个条件 + 指定动作）
function Seed($actions, $condType = 'time', $condParam = '08:00', $condCmp = 'Eq', $enabled = $true) {
    $obj = [ordered]@{
        account = [ordered]@{ account = ''; region = 'cn' }
        tokens  = [ordered]@{ accessToken = ''; refreshToken = ''; userApiKey = '' }
        devices = Device-List
        rules = @(
            [ordered]@{
                id = 'sbx00001'; name = '沙盒臂'; enabled = $enabled
                conditions = @([ordered]@{ type=$condType; parameter=$condParam; comparison=$condCmp; operator='And' })
                actions = @($actions | ForEach-Object {
                    [ordered]@{ deviceId=$_.dev; name=$_.name; state=$_.state; outlet=$_.outlet }
                })
            }
        )
        loggingEnabled = $true
        pollingIntervalSeconds = 5
    }
    $json = ConvertTo-Json $obj -Depth 8
    [System.IO.File]::WriteAllText($cfg, $json, (New-Object System.Text.UTF8Encoding($false)))
    return Hash-Cfg
}

# 多条规则的种子配置（电源事件那一臂要用：四类都得能存）
function Seed-Rules($rules) {
    $obj = [ordered]@{
        account = [ordered]@{ account = ''; region = 'cn' }
        tokens  = [ordered]@{ accessToken = ''; refreshToken = ''; userApiKey = '' }
        devices = Device-List
        rules = @($rules | ForEach-Object {
            [ordered]@{
                id = $_.id; name = $_.name; enabled = $true
                conditions = @([ordered]@{ type=$_.condType; parameter=$_.condParam; comparison=$_.cmp; operator='And' })
                actions = @([ordered]@{ deviceId=$_.dev; name=$_.devName; state='on'; outlet=0 })
            }
        })
        loggingEnabled = $true
        pollingIntervalSeconds = 5
    }
    [System.IO.File]::WriteAllText($cfg, (ConvertTo-Json $obj -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
    return Hash-Cfg
}

function Start-App() {
    Get-Process -Name 'EWeLinkLinker.ConfigApp' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit() }
    if (Test-Path $debug) { Remove-Item $debug -Force }
    $p = Start-Process -FilePath $exe -PassThru
    $deadline = (Get-Date).AddSeconds(30); $win = $null
    while ((Get-Date) -lt $deadline -and -not $win) {
        # 必须按进程号认领窗口：只按标题找会抓到上一臂那个正在退出的窗口，
        # 于是后面 FindAll 拿到空集合、报"对 null 调用方法"，看起来像产品崩了。
        $cand = @($root.FindAll($TS::Children, (New-Object $PC($AE::NameProperty, 'EWeLink Linker'))))
        $win = $cand | Where-Object { $_.Current.ProcessId -eq $p.Id } | Select-Object -First 1
        if (-not $win) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $win) { throw ('主窗口没出现 (pid=' + $p.Id + ')') }
    $script:win = $win
    $script:appPid = $p.Id
    # 先把窗口挪到左上角再动手：这台机器桌面上偶发有应用盖住右半屏（实测 x>=1281 那一带被一个
    # Pane 名为 FLUTTERVIEW 的窗口挡住过），合成鼠标点击会被它吃掉，于是"点了没弹出任何东西"
    # 和"像素判据读到别人家的字"两件事同时发生——看起来像产品坏了，其实是我的手没伸进去。
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) {
        [void][Win.U32]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 30, 30, 0, 0, 0x0001 -bor 0x0004 -bor 0x0040)
        Start-Sleep -Milliseconds 500
        Say '  [窗口] 已挪到 (30,30)，避开右半屏可能被别的应用盖住的区域'
    } else {
        Say '  !! [窗口] 拿不到 MainWindowHandle，没挪窗口——落在右半屏的点击可能被别的窗口吃掉'
    }
    # 等它把配置加载完（日志出现"最终规则数"）再动手，否则读到的是半成品界面
    $d2 = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $d2 -and (Log-Text) -notmatch '最终规则数') { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Milliseconds 2000
    # 界面真的建出来了才返回：至少有一个 List（条件区/动作区）
    $d3 = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $d3) {
        # 每轮都从桌面根重新认领窗口：握着早期那个元素引用不放，偶发会一直读到
        # 一棵还没长出来的树（后代=0），报出来像产品没画界面。
        $again = @($root.FindAll($TS::Children, (New-Object $PC($AE::NameProperty, 'EWeLink Linker')))) |
                 Where-Object { $_.Current.ProcessId -eq $p.Id } | Select-Object -First 1
        if ($again) { $win = $again; $script:win = $again }
        $n = @($win.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::List)))).Count
        if ($n -ge 2) { break }
        Start-Sleep -Milliseconds 300
    }
    if ($n -lt 2) {
        # 抛之前把控件类型分布打出来：光说"0 个 List"分不出"界面没画"还是"UIA 没暴露"
        $all = @($win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
        $hist = ($all | Group-Object { $_.Current.ControlType.ProgrammaticName } | Sort-Object Count -Descending |
                Select-Object -First 8 | ForEach-Object { $_.Name.Replace('ControlType.','') + '=' + $_.Count }) -join ' '
        throw ('界面没建出规则列表（List=' + $n + ' pid=' + $p.Id + ' 窗口数=' + $cand.Count +
               ' 后代总数=' + $all.Count + ' 直查根=' + @($root.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::List)))).Count +
               ' 类型分布[' + $hist + ']），不能继续点')
    }
    return ,@{ P = $p; Win = $win }
}
function Stop-App($app) { if (-not $app.P.HasExited) { $app.P.Kill(); $app.P.WaitForExit() } }

# 第一行动作的两个下拉的位置（第一行一定有 UIA 节点，用它标定横向坐标）
function Row-Anchors($win, $rowIndex) {
    $lists = $win.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::List)))
    $actList = $lists[$lists.Count - 1]
    $rows = @($actList.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) |
              Sort-Object { $_.Current.BoundingRectangle.Y })
    $r = $rows[$rowIndex]
    $c0 = @($rows[0].FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ComboBox))))
    $y = [int]($r.Current.BoundingRectangle.Y + 22)
    # 版式重排之后动作行改成按内容收宽，行高/控件位置都可能和老坐标对不上：
    # 把这一行的矩形和每个下拉的矩形打出来，"没弹出东西"才知道是点歪了还是产品没弹。
    foreach ($cb in $c0) {
        $cr = $cb.Current.BoundingRectangle
        Say ("    row[$rowIndex] 里 ComboBox rect=($([int]$cr.X),$([int]$cr.Y),$([int]$cr.Width)x$([int]$cr.Height)) 中心=($([int]($cr.X+$cr.Width/2)),$([int]($cr.Y+$cr.Height/2)))")
    }
    $rr = $r.Current.BoundingRectangle
    Say ("    row[$rowIndex] 矩形=($([int]$rr.X),$([int]$rr.Y),$([int]$rr.Width)x$([int]$rr.Height)) 用的 Y=$y")
    return ,@{
        Rows    = $rows
        # 点元素自己矩形的中心，不要写死 X+偏移：界面把下拉从"撑满整行"改成
        # "靠左 180-300"之后，X+200 就点到框外了（B/C 两条臂因此假红）。
        ChanX   = [int]($c0[1].Current.BoundingRectangle.X + $c0[1].Current.BoundingRectangle.Width / 2)
        DevX    = [int]($c0[0].Current.BoundingRectangle.X + $c0[0].Current.BoundingRectangle.Width / 2)
        DevCb   = $c0[0]
        ChanCb  = $c0[1]
        Y       = $y
        Rect    = $r.Current.BoundingRectangle
        CondRow = @($lists[1].FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))[0]
    }
}

# 任何一条臂抛异常都要先把界面进程收掉：留着它，下一臂就会抓到旧窗口，
# 报出来的错看着像产品崩了（这次 A 臂"List 节点数=0"就是这么来的）。
trap { Say "TRAP: $($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"; Flush;
       Get-Process -Name 'EWeLinkLinker.ConfigApp' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); $_.WaitForExit() } catch {} }
       break }

Say "=== GUI 分臂验收 start $(Get-Date -Format 'HH:mm:ss') ==="

# ---------------- 臂 A：通道候选按设备通道数生成 ----------------
if ($Only -eq '' -or $Only -eq 'A') {
    Say '--- 臂 A：通道候选按 ChannelCount 生成 ---'
    $h0 = Seed @(
        @{ dev='sandbox-1ch'; name='沙盒一路插座'; state='on';  outlet=0 },
        @{ dev='sandbox-4ch'; name='沙盒四路插排'; state='off'; outlet=2 },
        @{ dev='sandbox-8ch'; name='沙盒八路PDU';  state='on';  outlet=5 },
        @{ dev='sandbox-1ch'; name='沙盒一路插座'; state='off'; outlet=5 }
    )
    $app = Start-App
    $loadedPath = [regex]::Match((Log-Text), '路径: (.+)').Groups[1].Value.Trim()
    Check (([System.IO.Path]::GetFullPath($loadedPath)) -eq $cfg) "A0 程序读的是沙盒配置 → $loadedPath"
    $a = Row-Anchors $app.Win 0
    Check ($a.Rows.Count -eq 4) "A1 四条动作行都渲染出来（模型里 4 个动作）→ 实到 $($a.Rows.Count)"
    $chans = @($a.Rows | ForEach-Object { $_.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ComboBox)))[1] })
    $c0 = Expand-Items $chans[0] 'A-1路'
    $c1 = Expand-Items $chans[1] 'A-4路'
    $c2 = Expand-Items $chans[2] 'A-8路'
    $c3 = Expand-Items $chans[3] 'A-越界'
    Check ($c0.Names.Count -eq 1 -and $c0.Names[0] -eq 'CH0') "A2 一路设备只有 CH0 → [$($c0.Names -join ',')]"
    Check ($c1.Names.Count -eq 4) "A3 四路设备 = CH0-CH3 → [$($c1.Names -join ',')]"
    Check ($c2.Names.Count -eq 8) "A4 八路设备 = CH0-CH7（老界面最多 CH3）→ [$($c2.Names -join ',')]"
    Check ($c3.Names.Count -eq 6) "A5 配置里越界的 CH5 仍列得出来（不凭空变空白）→ [$($c3.Names -join ',')]"
    Stop-App $app
}

# ---------------- 臂 B：换到通道更少的设备，通道号显式收回并写日志 ----------------
if ($Only -eq '' -or $Only -eq 'B') {
    Say '--- 臂 B：8 路(CH5) 换成 4 路设备 ---'
    $h0 = Seed @(@{ dev='sandbox-8ch'; name='沙盒八路PDU'; state='on'; outlet=5 })
    $app = Start-App
    $a = Row-Anchors $app.Win 0
    Shot 'B_before' $a.Rect
    Check (Pick-By-Text $a.DevX $a.Y '沙盒四路插排' 'B-device' $a.DevCb) 'B1 把这一行的设备换成四路插排（选中项真的换过去了）'
    Shot 'B_after_pick' $a.Rect
    Check (Click-Save $app.Win) 'B2 点了保存配置'
    Start-Sleep -Milliseconds 1500
    $t = Log-Text
    $clamp = ($t -split "`n" | Where-Object { $_ -match '已收回 CH0' } | Select-Object -Last 1)
    Check ($null -ne $clamp) "B3 通道被收回写了日志（不是悄悄改）：$clamp"
    $act = ($t -split "`n" | Where-Object { $_ -match '\[保存\].*动作: Device=' } | Select-Object -Last 1)
    Check ($act -match 'Device=sandbox-4ch' -and $act -match 'Name=沙盒四路插排' -and $act -match 'Outlet=0') "B4 落盘的就是新设备+新名字+CH0：$($act.Trim())"
    Check ($t -match '配置已保存到') 'B5 这条臂本身保存成功（说明收回后的值合法）'
    Stop-App $app
}

# ---------------- 臂 C：换到通道更多的设备，原通道号必须保留 ----------------
if ($Only -eq '' -or $Only -eq 'C') {
    Say '--- 臂 C：4 路(CH2) 换成 8 路设备 ---'
    $h0 = Seed @(@{ dev='sandbox-4ch'; name='沙盒四路插排'; state='on'; outlet=2 })
    $app = Start-App
    $a = Row-Anchors $app.Win 0
    Shot 'C_before' $a.Rect
    Check (Pick-By-Text $a.DevX $a.Y '沙盒八路PDU' 'C-device' $a.DevCb) 'C1 把这一行的设备换成八路 PDU（选中项真的换过去了）'
    Shot 'C_after_pick' $a.Rect
    Check (Click-Save $app.Win) 'C2 点了保存配置'
    Start-Sleep -Milliseconds 1500
    $t = Log-Text
    $act = ($t -split "`n" | Where-Object { $_ -match '\[保存\].*动作: Device=' } | Select-Object -Last 1)
    Check ($act -match 'Device=sandbox-8ch' -and $act -match 'Name=沙盒八路PDU' -and $act -match 'Outlet=2') "C3 原来选的 CH2 保住了：$($act.Trim())"
    Check ($t -notmatch '已收回 CH0') 'C4 没有多余的收回（CH2 在新设备上合法）'
    Stop-App $app
}

# ---------------- 臂 J：收回之后，通道那一格屏幕上必须还写着东西 ----------------
# B/C 两臂只看落盘的 Outlet，落盘对不等于显示对。实测（2026-10-08）换到通道更少的设备后
# 模型和盘上都是 CH0，而屏幕上那一格是空白的——只有看显示才抓得到。
if ($Only -eq '' -or $Only -eq 'J') {
    Say '--- 臂 J：通道收回后界面不许留空白（模型 CH0，屏幕也得写 CH0）---'
    $h0 = Seed @(@{ dev='sandbox-8ch'; name='沙盒八路PDU'; state='on'; outlet=5 })
    $app = Start-App
    $a = Row-Anchors $app.Win 0
    $rectCh = $a.ChanCb.Current.BoundingRectangle
    $ink0 = Ink-In $rectCh 'J-收回前(应写 CH5)'
    Check ($ink0 -ge 25) "J1 动手之前那一格有字（CH5）→ 深色像素 $ink0"
    Pick-By-Text $a.DevX $a.Y '沙盒四路插排' 'J-device' | Out-Null
    Start-Sleep -Milliseconds 800
    $ink1 = Ink-In $rectCh 'J-收回后(应写 CH0)'
    Check ((Log-Text) -match '已收回 CH0') 'J2 模型侧确实收回了'
    # 空白有两种可能：选中项被夹成 -1，或者候选列表整个空了。展开一下才知道是哪一种
    # （收起的 ComboBox 在 UIA 里 0 个后代，读不到 ItemsSource）。
    $ex = Expand-Items $a.ChanCb 'J-收回后候选'
    Check ($ex.Names.Count -eq 4) "J4 收回后候选是 4 路该有的 CH0–CH3 → [$($ex.Names -join ',')]"
    Start-Sleep -Milliseconds 500
    $ink2 = Ink-In $rectCh 'J-展开收起之后'
    Check ($ink1 -ge [int]($ink0 * 0.5)) "J3 收回后那一格还有字，不许空白 → $ink1 vs 基线 $ink0（展开后再测=$ink2）"
    Shot 'J_after_clamp' $a.Rect
    # 收回之后他当然还会自己挑通道：这一步同时管"落盘跟着变"和"显示跟着变"
    Check (Pick-Channel $a.ChanCb 'CH3' 'J-chan') 'J5 收回之后再手动挑 CH3'
    Start-Sleep -Milliseconds 600
    $ink3 = Ink-In $rectCh 'J-手动挑 CH3 之后'
    Check ($ink3 -ge 25) "J6 手动挑完那一格有字 → 深色像素 $ink3"
    Check (Click-Save $app.Win) 'J7 点了保存配置'
    Start-Sleep -Milliseconds 1500
    $actJ = ((Log-Text) -split "`n" | Where-Object { $_ -match '\[保存\].*动作: Device=' } | Select-Object -Last 1)
    Check ($actJ -match 'Device=sandbox-4ch' -and $actJ -match 'Outlet=3') "J8 手动挑的 CH3 真的落盘了：$($actJ.Trim())"
    Shot 'J_after_manual' $a.Rect
    Stop-App $app
}

# ---------------- 臂 K：换成通道数相同的设备，通道号既不该改也不该变空白 ----------------
if ($Only -eq '' -or $Only -eq 'K') {
    Say '--- 臂 K：4 路 CH2 换成另一台 4 路设备 ---'
    $h0 = Seed @(@{ dev='sandbox-4ch'; name='沙盒四路插排'; state='on'; outlet=2 })
    $app = Start-App
    $a = Row-Anchors $app.Win 0
    $rectCh = $a.ChanCb.Current.BoundingRectangle
    $k0 = Ink-In $rectCh 'K-换之前(CH2)'
    Check ($k0 -ge 25) "K1 换之前那一格有字（CH2）→ 深色像素 $k0"
    Check (Pick-By-Text $a.DevX $a.Y '沙盒四路插排乙' 'K-device' $a.DevCb) 'K2 换成另一台 4 路设备'
    Start-Sleep -Milliseconds 800
    $k1 = Ink-In $rectCh 'K-换之后(应还是 CH2)'
    Check ($k1 -ge 25) "K3 换完那一格还有字，不许空白 → 深色像素 $k1"
    Check ((Log-Text) -notmatch '已收回 CH0') 'K4 同路数互换不该触发收回'
    Check (Click-Save $app.Win) 'K5 点了保存配置'
    Start-Sleep -Milliseconds 1500
    $actK = ((Log-Text) -split "`n" | Where-Object { $_ -match '\[保存\].*动作: Device=' } | Select-Object -Last 1)
    Check ($actK -match 'Device=sandbox-4chB' -and $actK -match 'Name=沙盒四路插排乙' -and $actK -match 'Outlet=2') "K6 落盘：新设备 + 新名字 + 通道仍是 2 → $($actK.Trim())"
    Shot 'K_after_swap' $a.Rect
    Stop-App $app
}

# ---------------- 臂 D：越界通道必须拦住保存 ----------------
if ($Only -eq '' -or $Only -eq 'D') {
    Say '--- 臂 D：一路设备存着 CH5（旧配置/手改）---'
    $h0 = Seed @(@{ dev='sandbox-1ch'; name='沙盒一路插座'; state='on'; outlet=5 })
    $app = Start-App
    # 越界的 CH5 也得在屏幕上看得见（候选列表会把它补进去），否则他看不到自己存的是什么就被拦住
    $aD = Row-Anchors $app.Win 0
    $inkD = Ink-In $aD.ChanCb.Current.BoundingRectangle 'D-越界值 CH5 在屏幕上'
    Check ($inkD -ge 25) "D0 越界的 CH5 也照样显示出来，不许变空白 → 深色像素 $inkD"
    Check (Click-Save $app.Win) 'D1 点了保存配置'
    $dlg = Find-Dialog '无法保存' 8
    if (-not $dlg) { Check $false 'D2 没弹「无法保存」' }
    else {
        $body = Dialog-Body $dlg
        Check ($body -match '通道 5' -and $body -match '只有 1 路') "D2 弹窗点名越界通道 → $body"
        Shot 'armD_dialog' $dlg.Current.BoundingRectangle
        Check (Dismiss-Dialog) 'D3 弹窗确实关掉了'
    }
    Check ((Hash-Cfg) -eq $h0) "D4 拦住之后磁盘一字未改 hash=$(Hash-Cfg)"
    Stop-App $app
}

# ---------------- 臂 E：类型切换重置残留参数 + 空进程名拦住保存 ----------------
if ($Only -eq '' -or $Only -eq 'E') {
    Say '--- 臂 E：时间条件(08:00) 切成「应用启动」---'
    $h0 = Seed @(@{ dev='sandbox-1ch'; name='沙盒一路插座'; state='on'; outlet=0 })
    $app = Start-App
    $lists = $app.Win.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::List)))
    $condRow = @($lists[1].FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))[0]
    $condCombos = @($condRow.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ComboBox))))
    Check ($condCombos.Count -eq 5) "E1 时间条件行有 5 个下拉（且/类型/比较符/时/分）→ 实到 $($condCombos.Count)"
    $typeCombo = $condCombos | Where-Object { $_.Current.BoundingRectangle.Width -gt 70 } | Select-Object -First 1
    $ec = $typeCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 400
    $li = @($typeCombo.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ListItem))))
    $appItem = $li | Where-Object { $_.Current.Name -eq '应用启动' } | Select-Object -First 1
    if (-not $appItem) { Check $false "E2 类型下拉里没有「应用启动」→ [$(($li | ForEach-Object { $_.Current.Name }) -join ',')]" }
    else {
        ($appItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        Start-Sleep -Milliseconds 1000
        Check $true 'E2 把条件类型从「时间」切成「应用启动」'
    }
    $ec.Collapse(); Start-Sleep -Milliseconds 300
    $condCombos2 = @($condRow.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ComboBox))))
    Check ($condCombos2.Count -eq 2) "E3 比较符和时间选择控件都不再出现（5 → $($condCombos2.Count)）"
    $t = Log-Text
    $reset = ($t -split "`n" | Where-Object { $_ -match '\[条件\] 类型 time → app_start' } | Select-Object -Last 1)
    Check ($null -ne $reset) "E4 残留的 08:00 被重置并留了日志：$($reset.Trim())"
    Check (Click-Save $app.Win) 'E5 点了保存配置'
    $dlg = Find-Dialog '无法保存' 8
    if (-not $dlg) { Check $false 'E6 空进程名点保存没弹「无法保存」' }
    else {
        $body = Dialog-Body $dlg
        Check ($body -match '进程名') "E6 弹窗点名建不出触发器的条件 → $body"
        Shot 'armE_dialog' $dlg.Current.BoundingRectangle
        Check (Dismiss-Dialog) 'E7 弹窗确实关掉了'
    }
    Check ((Hash-Cfg) -eq $h0) "E8 拦住之后磁盘一字未改 hash=$(Hash-Cfg)"
    Stop-App $app
}

# ---------------- 臂 F：比较符候选按类型收窄（界面上就只给真实现的） ----------------
if ($Only -eq '' -or $Only -eq 'F') {
    Say '--- 臂 F：时间条件 4 个比较符 / CPU 温度 8 个 ---'
    $h0 = Seed @(@{ dev='sandbox-1ch'; name='沙盒一路插座'; state='on'; outlet=0 }) 'time' '08:00' 'Eq'
    $app = Start-App
    $lists = $app.Win.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::List)))
    $condRow = @($lists[1].FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))[0]
    $enumNames = @('Gte','Gt','Lte','Lt','Eq','Neq','Range','OutsideRange')
    function Cmp-Items($condRow, $enumNames) {
        # 行里有好几个窄下拉（且/或、比较符、时、分），只有比较符那个的条目全落在枚举名里
        foreach ($c in @($condRow.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ComboBox))))) {
            $e = $c.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $e.Expand(); Start-Sleep -Milliseconds 350
            $names = @($c.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ListItem))) | ForEach-Object { $_.Current.Name } | Select-Object -Unique)
            $e.Collapse(); Start-Sleep -Milliseconds 200
            if ($names.Count -ge 4 -and @($names | Where-Object { $enumNames -notcontains $_ }).Count -eq 0) { return ,@($names) }
        }
        return ,@()
    }
    $f1 = Cmp-Items $condRow $enumNames
    Check ($f1.Count -eq 4 -and $f1 -notcontains 'Range') "F1 时间条件的比较符下拉只有 4 项（老界面给全 8 项）→ [$($f1 -join ',')]"
    # 切成 CPU 温度：八个比较符都真实现了，必须全给
    $combos = @($condRow.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ComboBox))))
    $typeCombo = $combos | Where-Object { $_.Current.BoundingRectangle.Width -gt 70 } | Select-Object -First 1
    $ec = $typeCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 400
    $li = @($typeCombo.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ListItem))))
    $cpu = $li | Where-Object { $_.Current.Name -eq 'CPU温度' } | Select-Object -First 1
    ($cpu.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 900
    $ec.Collapse(); Start-Sleep -Milliseconds 300
    $f2 = Cmp-Items $condRow $enumNames
    Check ($f2.Count -eq 8) "F2 换成 CPU 温度后比较符给满 8 项 → [$($f2 -join ',')]"
    $t = Log-Text
    Check ($t -match '类型 time → cpu_temp') "F3 参数跟着类型走（08:00 对温度不可用）：$(($t -split "`n" | Where-Object { $_ -match '类型 time → cpu_temp' } | Select-Object -Last 1).Trim())"
    Check (Click-Save $app.Win) 'F4 点了保存配置'
    Start-Sleep -Milliseconds 1200
    $t2 = Log-Text
    $cond = ($t2 -split "`n" | Where-Object { $_ -match '\[保存\].*条件: Type=' } | Select-Object -Last 1)
    Check ($cond -match 'Type=cpu_temp' -and $cond -match 'Param=80') "F5 落盘的条件是 cpu_temp/80：$($cond.Trim())"
    Stop-App $app
}

# ---------------- 臂 G：电源事件类条件必须能存（上一版被"注册表里没有"误拦） ----------------
if ($Only -eq '' -or $Only -eq 'G') {
    Say '--- 臂 G1：开机/关机/睡眠/唤醒 四条规则一起保存 ---'
    $h0 = Seed-Rules @(
        @{ id='g1'; name='沙盒-关机'; condType='shutdown'; condParam=''; cmp='Eq'; dev='sandbox-1ch'; devName='沙盒一路插座' },
        @{ id='g2'; name='沙盒-开机'; condType='boot';     condParam=''; cmp='Eq'; dev='sandbox-1ch'; devName='沙盒一路插座' },
        @{ id='g3'; name='沙盒-睡眠'; condType='sleep';    condParam=''; cmp='Eq'; dev='sandbox-4ch'; devName='沙盒四路插排' },
        @{ id='g4'; name='沙盒-唤醒'; condType='wake';     condParam=''; cmp='Eq'; dev='sandbox-4ch'; devName='沙盒四路插排' }
    )
    $app = Start-App
    Check (Click-Save $app.Win) 'G1-1 点了保存配置'
    Start-Sleep -Milliseconds 1500
    $dlg = Find-DialogEl '无法保存'
    Check ($null -eq $dlg) "G1-2 电源事件类不再被误拦（没弹「无法保存」）$(if($dlg){'→ 正文: ' + (Dialog-Body $dlg)})"
    if ($dlg) { Shot 'armG_dialog' $dlg.Current.BoundingRectangle; Dismiss-Dialog | Out-Null }
    $t = Log-Text
    Check ($t -match '配置已保存到') 'G1-3 日志说明确实写盘了'
    Check ((Hash-Cfg) -ne $h0) 'G1-4 磁盘配置确实变了（这条臂要的是"存得下去"）'
    $dumped = @('shutdown','boot','sleep','wake') | Where-Object { $t -match "条件: Type=$_" }
    Check ($dumped.Count -eq 4) "G1-5 四类都进了保存清单 → [$($dumped -join ',')]"
    Stop-App $app

    Say '--- 臂 G2：界面上把「时间」切成「关机」，残留的 08:00 要清掉并且还能存 ---'
    $h1 = Seed @(@{ dev='sandbox-1ch'; name='沙盒一路插座'; state='on'; outlet=0 }) 'time' '08:00' 'Eq'
    $app = Start-App
    $lists = $app.Win.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::List)))
    $condRow = @($lists[1].FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition))[0]
    $combos = @($condRow.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ComboBox))))
    $typeCombo = $combos | Where-Object { $_.Current.BoundingRectangle.Width -gt 70 } | Select-Object -First 1
    $ec = $typeCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 400
    $li = @($typeCombo.FindAll($TS::Descendants, (New-Object $PC($AE::ControlTypeProperty, $CT::ListItem))))
    $hit = $li | Where-Object { $_.Current.Name -eq '关机' } | Select-Object -First 1
    if (-not $hit) { Check $false "类型下拉里没有「关机」→ [$(($li | ForEach-Object { $_.Current.Name }) -join ',')]" }
    else {
        ($hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        Start-Sleep -Milliseconds 900
        Check $true 'G2-1 条件类型从「时间」切成「关机」'
    }
    $ec.Collapse(); Start-Sleep -Milliseconds 300
    $t2 = Log-Text
    $clearLine = ($t2 -split "`n" | Where-Object { $_ -match '这类条件不用参数' } | Select-Object -Last 1)
    Check ($null -ne $clearLine) "G2-2 残留的 08:00 被清空并留日志：$(if($clearLine){$clearLine.Trim()}else{'(没有这行)'})"
    Check (Click-Save $app.Win) 'G2-3 点了保存配置'
    Start-Sleep -Milliseconds 1500
    $dlg2 = Find-DialogEl '无法保存'
    Check ($null -eq $dlg2) "G2-4 切成关机后照样能存$(if($dlg2){'→ 正文: ' + (Dialog-Body $dlg2)})"
    $t3 = Log-Text
    Check ($t3 -match '条件: Type=shutdown, Param=,') 'G2-5 落盘的条件是 shutdown 且参数为空'
    Stop-App $app
}

# ---------------- 臂 H：刷新IP 不得在设备表换掉的一瞬间崩 ----------------
# 他 09:12:59 就是踩在这上面：[Discovery] 跑完之后 Devices.Clear() 抛出
# "Index was outside the bounds of the array."（ActionDeviceCombo_SelectionChanged 取 e.AddedItems[0]，
# 而清空那一瞬间 AddedItems 是空的），异常吞掉后面那半次刷新，动作的 DeviceId 留在内存里是空值，
# 下一次「刷新状态」末尾的 SaveConfig 就把空值写上了盘（他盘上规则 1 动作 1 现在就是这样）。
if ($Only -eq '' -or $Only -eq 'H') {
    Say '--- 臂 H：点「刷新IP」（设备表整个换掉的那一瞬间）---'
    $h0 = Seed @(
        @{ dev='sandbox-4ch'; name='沙盒四路插排'; state='off'; outlet=0 },
        @{ dev='sandbox-8ch'; name='沙盒八路PDU';  state='on';  outlet=7 }
    )
    $app = Start-App
    $loadedPath = [regex]::Match((Log-Text), '路径: (.+)').Groups[1].Value.Trim()
    Check (([System.IO.Path]::GetFullPath($loadedPath)) -eq $cfg) "H0 程序读的是沙盒配置 → $loadedPath"
    Check (Click-Button $app.Win '刷新IP') 'H1 点了「刷新IP」'
    $d = Wait-AnyDialog @('错误', '刷新完成', '部分完成') 45
    if (-not $d) { Check $false 'H2 45 秒内没有任何弹窗（既不是成功也不是失败，无法判定）' }
    else {
        $body = Dialog-Body $d.el
        Say "  dialog[$($d.name)] 正文: $body"
        Shot "armH_$($d.name)" $d.el.Current.BoundingRectangle
        Check ($d.name -ne '错误') "H2 没有弹「错误」（他踩的那一下就是这个框）$(if($d.name -eq '错误'){' → 正文: ' + $body})"
        Check ($body -notmatch 'Index was outside') 'H3 正文里没有 Index was outside the bounds of the array'
        Dismiss-Any $d.el | Out-Null
    }
    $t = Log-Text
    Check ($t -notmatch '\[警告\] 恢复 DeviceId 失败') 'H4 没有设备被判定"不再存在"（沙盒设备本来就在表里）'
    $disc = ($t -split "`n" | Where-Object { $_ -match '\[Discovery\] Result' } | Select-Object -Last 1)
    Check ($null -ne $disc) "H5 这次刷新真跑完了局域网发现：$(if($disc){$disc.Trim()}else{'(没有)'})"
    SaveCfg-H $app
    $actions = Actions-From-Cfg
    $desc = (($actions | ForEach-Object { "$($_.deviceId)/$($_.name)/CH$($_.outlet)" }) -join ' | ')
    Say "  盘上动作: $desc"
    Check (@($actions | Where-Object { [string]::IsNullOrWhiteSpace($_.deviceId) }).Count -eq 0) 'H6 落盘的每个动作都还带着 deviceId（没有一个被清成空）'
    Check (@($actions | Where-Object { $_.deviceId -eq 'sandbox-4ch' }).Count -eq 1) 'H7 四路插排那条还在'
    Check (@($actions | Where-Object { $_.deviceId -eq 'sandbox-8ch' -and $_.outlet -eq 7 }).Count -eq 1) 'H8 八路 PDU 那条还在，而且通道仍是 CH7'
    Stop-App $app
}

# ---------------- 臂 I：设备格空着的动作不许存成"永远不下发" ----------------
# 服务端 ExecuteActionAsync 拿 deviceId 找不到设备就 return（只留一行 Device not found），
# 规则看着在跑、那一路其实永不下发。他盘上现在就是这一格空着（被上面那个崩溃抹掉的）。
if ($Only -eq '' -or $Only -eq 'I') {
    Say '--- 臂 I1：启用的规则里有个动作没选设备 ---'
    $h0 = Seed @(@{ dev=''; name='水冷'; state='off'; outlet=0 }) 'time' '08:00' 'Eq' $true
    $app = Start-App
    Check (Click-Save $app.Win) 'I1-1 点了保存配置'
    $dlg = Find-Dialog '无法保存' 8
    if (-not $dlg) { Check $false 'I1-2 没弹「无法保存」（空设备被放过去了）' }
    else {
        $body = Dialog-Body $dlg
        Say "  dialog 正文: $body"
        Shot 'armI1_dialog' $dlg.Current.BoundingRectangle
        Check ($body -match '没有选中设备') ('I1-2 弹窗点名"设备那一格是空的" → ' + $body)
        Check ($body -match '重新选一次设备') ('I1-3 弹窗给了可执行的下一步 → ' + $body)
        Dismiss-Any $dlg | Out-Null
    }
    Check ((Hash-Cfg) -eq $h0) "I1-4 拦住之后磁盘一字未改 hash=$(Hash-Cfg)"

    Say '--- 臂 I2：同一格空着但规则已停用 → 只提醒，不拦（让他能把别的改动存下去）---'
    Stop-App $app
    $h1 = Seed @(@{ dev=''; name='水冷'; state='off'; outlet=0 }) 'time' '08:00' 'Eq' $false
    $app = Start-App
    Check (Click-Save $app.Win) 'I2-1 点了保存配置'
    Start-Sleep -Milliseconds 1500
    $dlg2 = Find-DialogEl '无法保存'
    Check ($null -eq $dlg2) "I2-2 停用的规则没被拦死$(if($dlg2){' → 正文: ' + (Dialog-Body $dlg2)})"
    if ($dlg2) { Dismiss-Any $dlg2 | Out-Null }
    $t = Log-Text
    $remind = ($t -split "`n" | Where-Object { $_ -match '\[保存\] 提醒' -and $_ -match '没有指向任何设备' } | Select-Object -Last 1)
    Check ($null -ne $remind) "I2-3 提醒写进了日志：$(if($remind){$remind.Trim()}else{'(没有这行)'})"
    Check ($t -match '配置已保存到') 'I2-4 这条臂本身存下去了'

    Say '--- 臂 I3：同一份坏配置，直接关窗口（关窗自动保存是 interactive:false，不许弹框）---'
    $h2 = Seed @(@{ dev=''; name='水冷'; state='off'; outlet=0 }) 'time' '08:00' 'Eq' $true
    Stop-App $app
    $app = Start-App
    $p = $app.P
    $closedOk = $p.CloseMainWindow()
    Check $closedOk 'I3-1 用 CloseMainWindow 关掉窗口（走到 OnClosed→SaveConfig(interactive:false)）'
    $exited = $p.WaitForExit(20000)
    # 弹了框就退不掉：这一条是这条臂唯一能翻红的地方（"静默跳过"写成了弹框就会卡在这）
    Check $exited $(if ($exited) { 'I3-2 窗口 20 秒内真的退了（没被模态框挂住）' } else { 'I3-2 20 秒没退——关窗自动保存弹了框' })
    if (-not $exited) { try { $p.Kill(); $p.WaitForExit() } catch {} }
    $t3 = Log-Text
    $abort = ($t3 -split "`n" | Where-Object { $_ -match '\[保存\] 中止' -and $_ -match '没有指向任何设备' } | Select-Object -Last 1)
    Check ($null -ne $abort) "I3-3 跳过写盘这件事留了日志：$(if($abort){$abort.Trim()}else{'(没有这行)'})"
    Check ((Hash-Cfg) -eq $h2) "I3-4 关窗口没有把坏配置写下去 hash=$(Hash-Cfg)"
}

Say "=== FAILED_CHECKS=$($script:failed) end $(Get-Date -Format 'HH:mm:ss') ==="
Get-Process -Name 'EWeLinkLinker.ConfigApp' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Flush
Write-Output "ARMS_DONE failed=$($script:failed)"
# 没有这条 exit，外层 `echo $?` 量到的永远是 0（上一次 4 条 FAIL 照样 rc=0 就是这么漏过去的）
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
