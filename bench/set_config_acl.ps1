#Requires -Version 7.0
<#
.SYNOPSIS
    收紧 publish 目录的 ACL：配置目录只让 SYSTEM / Administrators / 配置者本人访问。

.DESCRIPTION
    为什么需要这个脚本：
      publish\config\linker.json 里的账号密码和 token 是 DPAPI（LocalMachine 范围 + 程序里写死的熵）密文，
      也就是说**任何本机用户都能解开**——只要他能读到那个文件。而它原来继承着
      "Authenticated Users:(M)"，等于任何登录用户都能读、也能改（把 devices[].ipAddress 改成自己的机器，
      服务就会以 SYSTEM 身份去连它）。程序目录以前是手工收紧的，配置目录没有，而且这一步不可复现。

    脚本做三件事：断开继承 → 写入显式 ACE → 逐项校验；改之前把原 SDDL 备份成文件，-Restore 可回滚。

    注意：ConfigApp 是用**普通用户身份**写 linker.json 的，所以配置者本人必须保留写权限，
    不能简单收紧成"Users 只读"——那会让「保存配置」直接失效。

.PARAMETER Owner
    需要写权限的交互用户，默认当前用户。install.ps1 / deploy.ps1 以管理员身份运行时请显式传。

.PARAMETER Restore
    回滚到某个备份文件（由本脚本 -Backup 产出）。

.EXAMPLE
    pwsh -File bench\set_config_acl.ps1 -DryRun
    pwsh -File bench\set_config_acl.ps1 -Owner "JIKUN\Kun"
    pwsh -File bench\set_config_acl.ps1 -Restore "$env:TEMP\ewl_acl_backup_20261106_1730.json"
#>
[CmdletBinding()]
param(
    [string]$Repo = 'E:\ClaudeCode\EWeLinkLinker',
    [string]$Owner = '',
    [string]$Restore = '',
    [switch]$DryRun,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$publishDir = Join-Path $Repo 'publish'

function Say([string]$msg, [string]$color = 'Gray') {
    if (-not $Quiet) { Write-Host $msg -ForegroundColor $color }
}

# ── 回滚 ──────────────────────────────────────────────────────────
if ($Restore) {
    if (-not (Test-Path $Restore)) { throw "备份文件不存在: $Restore" }
    $backup = Get-Content $Restore -Raw | ConvertFrom-Json
    foreach ($entry in $backup.entries) {
        if (-not (Test-Path $entry.path)) { Say "跳过（已不存在）: $($entry.path)" Yellow; continue }
        $acl = Get-Acl $entry.path
        $acl.SetSecurityDescriptorSddlForm($entry.sddl)
        Set-Acl -Path $entry.path -AclObject $acl
        Say "已回滚: $($entry.path)" Green
    }
    exit 0
}

if (-not (Test-Path $publishDir)) { throw "找不到 $publishDir（先构建一次）" }
if (-not $Owner) {
    $Owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    Say "未指定 -Owner，使用当前用户: $Owner" Yellow
}

# 目标目录 → 允许访问的主体与权限
#   config      : SYSTEM/Admins 完全控制，配置者本人可改（ConfigApp 要用）
#   Service/GUI : SYSTEM/Admins 完全控制，普通用户只读执行（保持既有形状）
$targets = @(
    @{ Path = $publishDir;                          Users = 'Read' }
    @{ Path = (Join-Path $publishDir 'config');     Users = 'None' }
    @{ Path = (Join-Path $publishDir 'Service');    Users = 'ReadAndExecute' }
    @{ Path = (Join-Path $publishDir 'ConfigApp');  Users = 'ReadAndExecute' }
)

$inherit = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
           [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
$noProp = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow

# ── 备份原 SDDL ───────────────────────────────────────────────────
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupPath = Join-Path $env:TEMP "ewl_acl_backup_$stamp.json"
$entries = @()
foreach ($t in $targets) {
    if (Test-Path $t.Path) {
        $entries += [ordered]@{ path = $t.Path; sddl = (Get-Acl $t.Path).Sddl }
    }
}
if (-not $DryRun) {
    ([ordered]@{ created = (Get-Date).ToString('s'); owner = $Owner; entries = $entries } | ConvertTo-Json -Depth 5) |
        Set-Content -Path $backupPath -Encoding utf8
    Say "原 ACL 已备份: $backupPath" DarkGray
}

# ── 应用 ──────────────────────────────────────────────────────────
foreach ($t in $targets) {
    $path = $t.Path
    if (-not (Test-Path $path)) { Say "跳过（不存在）: $path" Yellow; continue }

    if ($DryRun) {
        Say "[dry-run] 会收紧: $path  (Users=$($t.Users), Owner=$Owner)" Cyan
        continue
    }

    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($true, $false)          # 断开继承，且不保留已继承的项
    @($acl.Access) | ForEach-Object { [void]$acl.RemoveAccessRule($_) }   # 清掉显式项，保证幂等

    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        'NT AUTHORITY\SYSTEM', 'FullControl', $inherit, $noProp, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        'BUILTIN\Administrators', 'FullControl', $inherit, $noProp, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $Owner, 'Modify', $inherit, $noProp, $allow)))
    if ($t.Users -ne 'None') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            'BUILTIN\Users', $t.Users, $inherit, $noProp, $allow)))
    }

    Set-Acl -Path $path -AclObject $acl
    Say "已收紧: $path" Green
}

if ($DryRun) { exit 0 }

# ── 校验 ──────────────────────────────────────────────────────────
$problems = @()
foreach ($t in $targets) {
    if (-not (Test-Path $t.Path)) { continue }
    $acl = Get-Acl $t.Path

    $authUsers = @($acl.Access | Where-Object {
        try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-11' } catch { $false }
    })
    if ($authUsers.Count -gt 0) { $problems += "$($t.Path): 仍然存在 Authenticated Users 的 ACE" }

    $hasSystem = @($acl.Access | Where-Object {
        try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-18' -and
              $_.AccessControlType -eq 'Allow' -and $_.FileSystemRights -match 'FullControl' } catch { $false }
    }).Count -gt 0
    if (-not $hasSystem) { $problems += "$($t.Path): 缺 SYSTEM 完全控制（服务读不到配置/写不了日志）" }

    $hasOwner = @($acl.Access | Where-Object {
        try { $_.IdentityReference.Value -eq $Owner -and $_.AccessControlType -eq 'Allow' } catch { $false }
    }).Count -gt 0
    if (-not $hasOwner) { $problems += "$($t.Path): 缺 $Owner 的访问权（ConfigApp 将无法保存配置）" }

    if (-not $acl.AreAccessRulesProtected) { $problems += "$($t.Path): 继承未断开" }
    Say ("  {0,-28} protected={1} aces={2}" -f (Split-Path $t.Path -Leaf), $acl.AreAccessRulesProtected, $acl.Access.Count) DarkGray
}

# 密钥文件本身也要跟着变（它原来带着继承来的 Authenticated Users:(M)）
$configDir = Join-Path $publishDir 'config'
if (Test-Path $configDir) {
    foreach ($f in Get-ChildItem $configDir -File -Force) {
        $fa = Get-Acl $f.FullName
        if (-not $fa.AreAccessRulesProtected) {
            $fa.SetAccessRuleProtection($true, $false)
            @($fa.Access) | ForEach-Object { [void]$fa.RemoveAccessRule($_) }
            $fa.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                'NT AUTHORITY\SYSTEM', 'FullControl', 'None', 'None', $allow)))
            $fa.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                'BUILTIN\Administrators', 'FullControl', 'None', 'None', $allow)))
            $fa.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                $Owner, 'Modify', 'None', 'None', $allow)))
            Set-Acl -Path $f.FullName -AclObject $fa
            Say "  已收紧文件: $($f.Name)" Green
        }
    }
}

if ($problems.Count -gt 0) {
    Say "校验失败:" Red
    $problems | ForEach-Object { Say "  - $_" Red }
    Say "可用 -Restore `"$backupPath`" 回滚" Yellow
    exit 2
}

Say "ACL 校验通过（Authenticated Users 已移除，SYSTEM/Administrators/$Owner 在位）" Green
Say "备份: $backupPath" DarkGray
exit 0
