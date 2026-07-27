# EWeLink Linker

> Windows 智能设备联动控制系统 - 根据 PC 状态自动控制 eWeLink 智能设备

## 目录

- [项目概述](#项目概述)
- [架构设计](#架构设计)
- [项目结构](#项目结构)
- [构建与运行](#构建与运行)
- [配置系统](#配置系统)
- [触发器系统](#触发器系统)
- [如何添加新的触发条件](#如何添加新的触发条件)
- [通信协议](#通信协议)
- [日志系统](#日志系统)
- [常见问题排查](#常见问题排查)
- [开发注意事项](#开发注意事项)

---

## 项目概述

EWeLink Linker 是一个 Windows 平台的智能设备联动控制系统。它通过监控 PC 的状态（开关机、睡眠唤醒、CPU 温度、GPU 温度、CPU 使用率、应用启停等），自动控制 eWeLink 智能插座/开关的通断。

### 核心功能

| 功能 | 描述 |
|------|------|
| 电源事件联动 | 开机/关机/睡眠/唤醒时自动控制设备 |
| 定时触发 | 每天固定时间执行动作 |
| 间隔触发 | 每隔 N 分钟循环执行 |
| CPU/GPU 监控 | 温度/使用率超阈值时触发 |
| 应用监控 | 指定应用启动/关闭时触发 |
| 复合条件 | 支持 AND/OR 逻辑组合多个条件 |
| 本地控制 | 通过 LAN 协议直控设备，无需云端 |
| 配置热重载 | 修改配置后自动生效，无需重启服务 |

### 技术栈

- **运行时**: .NET 10.0
- **UI框架**: WPF (Windows Presentation Foundation)
- **服务**: Windows Service (ServiceBase)
- **通信**: HTTP/REST (云端 API) + TCP Socket (LAN 协议)
- **加密**: AES-128-CBC (LAN) + HMAC-SHA256 (云端签名)
- **序列化**: System.Text.Json

---

## 快速开始

### 第一步：构建项目

```bash
# 克隆项目后，在项目根目录执行：
dotnet restore
dotnet build -c Release

# 或使用批处理脚本：
build-all.bat
```

### 第二步：配置并运行 ConfigApp

1. 运行 `publish\ConfigApp\EWeLinkLinker.ConfigApp.exe`
2. 在登录界面输入 eWeLink 账号密码，选择区域（默认 cn）
3. 点击 **"登录获取设备"**，等待设备列表加载
4. 如果设备没有 IP 地址，点击 **"刷新IP"** 自动发现

### 第三步：配置联动规则

1. 点击 **"+ 新建规则"** 创建新规则
2. 设置规则名称和启用状态
3. 添加条件：
   - 选择条件类型（时间/CPU温度/GPU温度/应用启动等）
   - 选择比较运算符（≥/>/</≤/=/≠）
   - 输入参数值
   - 多个条件可选择 AND/OR 组合
4. 添加动作：
   - 选择目标设备
   - 选择通道（CH0-CH3）
   - 选择状态（开/关）
5. 点击 **"保存配置"**

### 第四步：安装服务并运行

1. 点击界面上的 **"安装"** 按钮（需要管理员权限）
2. 安装完成后点击 **"启动"** 启动服务
3. 服务状态应显示为 ●运行中

### 第五步：验证

1. 触发你设置的条件（如让 CPU 温度超过阈值）
2. 观察设备是否按预期动作
3. 查看日志：点击 **"📄"** 按钮打开日志文件夹

---

## 使用指南

### ConfigApp 界面说明

```
┌─────────────────────────────────────────────────────────────┐
│  EWeLink Linker    服务状态: ●运行中  [刷新] [安装] [停止] [卸载] │
├─────────────────────────────────────────────────────────────┤
│  账号: [________] 密码: [________] 区域: [cn▼] [登录获取设备]   │
├──────────────────────────┬──────────────────────────────────┤
│  设备列表 (2 台设备)      │  联动规则                         │
│  ┌──────────────────┐   │  ┌──────────────────────────────┐│
│  │ 插座1  ●在线      │   │  │ ☑ 开机开灯              [✕]  ││
│  │ 192.168.1.100    │   │  │  ┌──────────────────────┐   ││
│  │ [通道0]          │   │  │  │ 📋 当满足以下条件时     │   ││
│  └──────────────────┘   │  │  │ [且▼][CPU温度▼] [≥▼] [60] [°C] [▶] [✕] ││
│  ┌──────────────────┐   │  │  │ [+ 添加条件]            │   ││
│  │ 插座2  ○离线      │   │  │  └──────────────────────┘   ││
│  │ (未连接)          │   │  │  ┌──────────────────────┐   ││
│  └──────────────────┘   │  │  │ ⚡ 执行以下动作          │   ││
│                         │  │  │ [插座1▼][CH0▼][开▼] [✕] │   ││
│                         │  │  │ [+ 添加动作]            │   ││
│                         │  │  └──────────────────────┘   ││
│                         │  └──────────────────────────────┘│
│                         │  [+ 新建规则]                    │
├──────────────────────────┴──────────────────────────────────┤
│  [刷新IP] [刷新状态]           [●] 日志        [保存配置]     │
└─────────────────────────────────────────────────────────────┘
```

### 服务控制

| 按钮 | 功能 | 可用状态 |
|------|------|---------|
| 安装 | 安装 Windows 服务并启动 | 未安装时 |
| 启动/停止 | 启动或停止服务 | 已安装时 |
| 卸载 | 卸载 Windows 服务 | 已安装时 |
| 刷新 | 手动刷新服务状态 | 始终可用 |
| 日志 | 启用/禁用日志写入 | 始终可用 |
| 📄 | 打开日志文件夹 | 始终可用 |

### 设备管理

| 操作 | 说明 |
|------|------|
| 登录获取设备 | 从云端获取设备列表 |
| 刷新IP | 在局域网中发现设备 IP 地址 |
| 刷新状态 | 从云端获取最新设备状态 |
| 通道开关 | 点击通道按钮手动控制设备 |

### 规则编辑器

#### 条件类型

| 类型 | 说明 | 示例 |
|------|------|------|
| 时间 | 每天固定时间 | `08:00` |
| 间隔 | 每隔 N 分钟 | `30` (分钟) |
| CPU温度 | CPU 温度超过阈值 | `60` (°C) |
| CPU使用率 | CPU 使用率超过阈值 | `90` (%) |
| GPU温度 | GPU 温度超过阈值 | `80` (°C) |
| 应用启动 | 指定应用启动时 | `notepad` |
| 应用关闭 | 指定应用关闭时 | `chrome` |
| 开机 | 系统启动时 | - |
| 关机 | 系统关机时 | - |
| 睡眠 | 系统睡眠时 | - |
| 唤醒 | 系统唤醒时 | - |

#### 比较运算符

| 运算符 | 显示 | 适用类型 |
|--------|------|---------|
| ≥ | Gte | 数值类型 |
| > | Gt | 数值类型 |
| ≤ | Lte | 数值类型 |
| < | Lt | 数值类型 |
| = | Eq | 全部类型 |
| ≠ | Neq | 全部类型 |

#### 逻辑运算符

| 运算符 | 说明 |
|--------|------|
| And | 两个条件必须同时满足 |
| Or | 任一条件满足即可 |

### 配置文件说明

配置文件 `linker.json` 位于 `publish/config/` 目录：

```json
{
  "loggingEnabled": true,           // 是否启用日志
  "account": {
    "account": "your@email.com",    // eWeLink 账号
    "password": "your_password",    // 密码
    "countryCode": "+86",          // 国家代码
    "region": "cn"                 // 区域：cn/eu/us/as
  },
  "tokens": {
    "accessToken": "...",          // 自动获取
    "refreshToken": "...",         // 自动获取
    "userApiKey": "..."            // 自动获取
  },
  "devices": [...],                // 设备列表（自动获取）
  "rules": [...]                   // 规则列表（手动配置）
}
```

### 日志文件

| 文件 | 位置 | 说明 |
|------|------|------|
| 服务日志 | `publish/Service/logs/service-YYYY-MM-DD.log` | 服务端运行日志 |
| 配置应用日志 | `config/debug.log` | ConfigApp 调试日志 |

**日志清理策略**：

- **服务日志**：启动时自动删除 7 天前的旧日志文件
- **配置应用日志 (`debug.log`)**：每写入 200 条检查一次，超过 2MB 时移动到 `debug.log.old`

---

## 架构设计

### 三层架构

```
┌─────────────────────────────────────────────────────────┐
│                    ConfigApp (WPF)                       │
│  ┌─────────┐  ┌──────────┐  ┌───────────┐  ┌─────────┐ │
│  │ 登录界面 │  │ 设备列表  │  │ 规则编辑器 │  │ 服务控制 │ │
│  └─────────┘  └──────────┘  └───────────┘  └─────────┘ │
└────────────────────────┬────────────────────────────────┘
                         │ 共享配置文件 (linker.json)
┌────────────────────────┴────────────────────────────────┐
│                      Core (Library)                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌─────────┐ │
│  │ Cloud    │  │ Lan      │  │ Token    │  │ Config  │ │
│  │ Client   │  │ Client   │  │ Manager  │  │ System  │ │
│  └──────────┘  └──────────┘  └──────────┘  └─────────┘ │
│  ┌──────────────────────────────────────────────────┐   │
│  │              Trigger System                       │   │
│  │  ┌─────────┐ ┌──────────┐ ┌─────────┐ ┌───────┐ │   │
│  │  │ Time    │ │ Interval │ │ App     │ │ CPU   │ │   │
│  │  │ Trigger │ │ Trigger  │ │ Trigger │ │ Trigger│ │   │
│  │  └─────────┘ └──────────┘ └─────────┘ └───────┘ │   │
│  └──────────────────────────────────────────────────┘   │
└────────────────────────┬────────────────────────────────┘
                         │ 引用
┌────────────────────────┴────────────────────────────────┐
│                    Service (Windows Service)              │
│  ┌──────────────────────────────────────────────────┐   │
│  │           LinkerWindowsService                     │   │
│  │  - OnStart / OnStop / OnShutdown                   │   │
│  │  - OnPowerEvent (Suspend/Resume)                   │   │
│  │  - FileSystemWatcher (配置热重载)                   │   │
│  │  - TriggerManager (规则引擎)                        │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

### 数据流

```
用户操作 (ConfigApp)
    │
    ▼
保存配置 ──→ linker.json ◄──┐
    │                        │ FileSystemWatcher
    ▼                        │
发布到 Service 目录 ─────────►│
                             │
Service 运行时 ◄─────────────┘
    │
    ├─→ 电源事件 ──→ ExecuteEventAsync ──→ 匹配规则 ──→ LAN 控制
    │
    └─→ 触发器轮询 ──→ EvaluateAsync ──→ 条件满足 ──→ ExecuteRuleAsync ──→ LAN 控制
```

---

## 项目结构

```
EWeLinkLinker/
├── config/
│   └── linker.json              # 默认配置模板
├── docs/
│   └── DEVELOPMENT_GUIDE.md     # 开发指南
├── publish/                     # 发布输出目录
│   ├── ConfigApp/               # WPF 配置应用
│   ├── Service/                 # Windows 服务
│   └── config/                  # 共享配置目录
│       └── linker.json          # 运行时配置文件
├── src/
│   ├── EWeLinkLinker.slnx       # 解决方案文件
│   ├── EWeLinkLinker.Core/      # 核心库
│   │   ├── Cloud/               # 云端 API 通信
│   │   │   ├── AuthSigner.cs    # HMAC-SHA256 签名算法
│   │   │   └── CloudClient.cs   # 登录、Token 刷新、设备列表
│   │   ├── Config/              # 配置管理
│   │   │   └── LinkerConfig.cs  # 配置加载/保存 + 数据模型
│   │   ├── Lan/                 # 局域网通信
│   │   │   ├── AesCrypto.cs     # AES-128-CBC 加解密
│   │   │   └── LanClient.cs     # 设备发现 + 本地控制
│   │   ├── Logging/             # 日志系统
│   │   │   ├── LoggerConfig.cs  # 全局日志开关
│   │   │   ├── ServiceLogger.cs # 结构化服务日志
│   │   │   └── SimpleLogger.cs  # 简单文件日志
│   │   ├── Models/              # 数据模型
│   │   │   ├── AuthTokens.cs    # 认证令牌
│   │   │   ├── DeviceInfo.cs    # 设备信息
│   │   │   └── LinkerRule.cs    # 规则/条件/动作模型
│   │   ├── Services/            # 业务服务
│   │   │   └── LinkerService.cs # 规则执行引擎
│   │   ├── Token/               # Token 管理
│   │   │   └── TokenManager.cs  # JWT 验证与刷新
│   │   └── Triggers/            # 触发器系统
│   │       ├── ITrigger.cs      # 触发器接口定义
│   │       ├── TriggerManager.cs # 触发器管理器
│   │       ├── TriggerRegistry.cs # 触发器注册表（反射自动发现）
│   │       ├── OptimizedTriggerBase.cs # 触发器基类
│   │       ├── PollingScheduler.cs # 轮询调度器
│   │       ├── SmartPoller.cs   # 智能轮询器（未使用）
│   │       ├── RuleTrigger.cs   # 复合条件触发器
│   │       ├── TimeTrigger.cs   # 时间触发器
│   │       ├── IntervalTrigger.cs # 间隔触发器
│   │       ├── SystemTrigger.cs # CPU 温度/使用率触发器
│   │       ├── GpuTempTrigger.cs # GPU 温度触发器
│   │       ├── AppTrigger.cs    # 应用启动/关闭触发器
│   │       ├── CompositeTrigger.cs # 嵌套复合触发器（未使用）
│   │       └── ComparisonHelper.cs # 比较运算辅助类
│   ├── EWeLinkLinker.Service/   # Windows 服务
│   │   ├── Program.cs           # 入口点
│   │   ├── LinkerWindowsService.cs # 服务主体
│   │   └── Properties/
│   └── EWeLinkLinker.ConfigApp/ # WPF 配置界面
│       ├── App.xaml(.cs)        # WPF 应用入口
│       ├── MainWindow.xaml(.cs) # 主窗口（核心 UI 逻辑）
│       ├── Converters.cs        # WPF 值转换器
│       ├── CpuUsageHelper.cs    # CPU 使用率读取（Windows API）
│       ├── ActionRowControl.xaml(.cs) # 动作行控件（未使用）
│       ├── CompositeConditionEditor.xaml(.cs) # 复合条件编辑器（未使用）
│       ├── TimePicker.xaml(.cs) # 时间选择器控件
│       └── Resources/Styles.xaml # 样式资源（未使用）
├── install.ps1                  # 服务安装脚本
├── uninstall.ps1                # 服务卸载脚本
├── build-all.bat                # 构建脚本
└── README.md                    # 本文档
```

---

## 构建与运行

### 环境要求

| 工具 | 版本 | 用途 |
|------|------|------|
| .NET SDK | 10.0+ | 编译和运行 |
| Visual Studio 2022+ 或 VS Code | - | 开发 IDE |
| PowerShell 5.1+ | - | 安装/卸载服务脚本 |
| Windows 10/11 | - | 目标平台 |

### 构建

```bash
# 还原依赖
dotnet restore

# 构建全部
dotnet build

# 发布 Release 版本
dotnet build -c Release

# 或使用批处理脚本
build-all.bat
```

### 运行 ConfigApp（开发模式）

```bash
dotnet run --project src/EWeLinkLinker.ConfigApp
```

### 运行 Service（控制台模式，调试用）

```bash
dotnet run --project src/EWeLinkLinker.Service -- --console
```

### 安装为 Windows 服务（需要管理员权限）

```powershell
# 以管理员身份运行 PowerShell
.\install.ps1
```

或使用批处理：

```batch
:: 以管理员身份运行
build-and-install.bat
```

---

## 配置系统

### 配置文件位置

| 应用 | 路径 | 说明 |
|------|------|------|
| ConfigApp (开发) | `src/.../bin/Debug/../../../config/linker.json` | 指向项目根目录 |
| ConfigApp (发布) | `publish/ConfigApp/../../config/linker.json` | 指向 publish/config |
| Service (发布) | `publish/Service/../../config/linker.json` | 同上 |

### 配置文件格式

```json
{
  "loggingEnabled": true,
  "account": {
    "account": "邮箱或手机号",
    "password": "密码",
    "countryCode": "+86",
    "region": "cn"
  },
  "tokens": {
    "accessToken": "JWT token",
    "refreshToken": "refresh token",
    "userApiKey": "user api key"
  },
  "devices": [
    {
      "deviceId": "设备ID",
      "name": "设备名称",
      "ipAddress": "自动发现",
      "deviceKey": "设备密钥",
      "macAddress": "云端MAC",
      "realMacAddress": "手动输入的真实MAC",
      "channelCount": 4,
      "channelStates": ["off", "off", "off", "off"]
    }
  ],
  "rules": [
    {
      "id": "唯一ID",
      "name": "规则名称",
      "enabled": true,
      "conditions": [
        {
          "id": "条件ID",
          "type": "cpu_temp",
          "parameter": "60",
          "parameter2": "",
          "comparison": "Gte",
          "operator": "And"
        }
      ],
      "actions": [
        {
          "deviceId": "目标设备ID",
          "name": "设备名称",
          "state": "on",
          "outlet": 0
        }
      ]
    }
  ]
}
```

### 配置项说明

| 字段 | 类型 | 说明 |
|------|------|------|
| `loggingEnabled` | bool | 是否启用日志写入 |
| `account.account` | string | eWeLink 账号（邮箱或手机号） |
| `account.password` | string | 密码 |
| `account.countryCode` | string | 国家代码，如 "+86" |
| `account.region` | string | 区域：cn/eu/us/as |
| `tokens.accessToken` | string | 云端 API 访问令牌 |
| `tokens.refreshToken` | string | 云端 API 刷新令牌 |
| `tokens.userApiKey` | string | 用户 API Key |
| `devices[].deviceId` | string | 设备唯一标识 |
| `devices[].name` | string | 设备显示名称 |
| `devices[].ipAddress` | string | 局域网 IP（自动发现） |
| `devices[].deviceKey` | string | 设备加密密钥 |
| `devices[].macAddress` | string | 云端 MAC 地址 |
| `devices[].realMacAddress` | string | 真实 MAC 地址（用于设备发现） |
| `devices[].channelCount` | int | 通道数量（1-5） |
| `devices[].channelStates` | string[] | 各通道状态："on"/"off" |
| `rules[].id` | string | 规则唯一标识 |
| `rules[].name` | string | 规则显示名称 |
| `rules[].enabled` | bool | 是否启用 |
| `rules[].conditions[].type` | string | 触发器类型（见下表） |
| `rules[].conditions[].parameter` | string | 主参数值 |
| `rules[].conditions[].parameter2` | string | 第二参数值（用于范围） |
| `rules[].conditions[].comparison` | string | 比较运算符（见下表） |
| `rules[].conditions[].operator` | string | 逻辑运算符：And/Or |
| `rules[].actions[].deviceId` | string | 目标设备 ID |
| `rules[].actions[].state` | string | 动作："on"/"off" |
| `rules[].actions[].outlet` | int | 通道号（0-3） |

### 触发器类型

| type 值 | 说明 | 参数格式 | 比较运算符 |
|---------|------|---------|-----------|
| `time` | 每天固定时间 | `HH:mm` | Eq/Neq/Gte/Lt |
| `interval` | 间隔执行 | 分钟数 | - |
| `cpu_temp` | CPU 温度 | 温度(°C) | Gte/Gt/Lte/Lt/Eq/Neq |
| `cpu_usage` | CPU 使用率 | 百分比(%) | Gte/Gt/Lte/Lt/Eq/Neq |
| `gpu_temp` | GPU 温度 | 温度(°C) | Gte/Gt/Lte/Lt/Eq/Neq |
| `app_start` | 应用启动 | 进程名 | - |
| `app_close` | 应用关闭 | 进程名 | - |
| `boot` | 系统启动 | 无 | - |
| `shutdown` | 系统关机 | 无 | - |
| `sleep` | 系统睡眠 | 无 | - |
| `wake` | 系统唤醒 | 无 | - |

### 比较运算符

| 值 | 显示 | 含义 |
|----|------|------|
| `Gte` | ≥ | 大于等于 |
| `Gt` | > | 大于 |
| `Lte` | ≤ | 小于等于 |
| `Lt` | < | 小于 |
| `Eq` | = | 等于 |
| `Neq` | ≠ | 不等于 |
| `Range` | 范围 | 范围内（参数格式：min,max） |

---

## 触发器系统

### 核心接口

```csharp
public interface ITrigger : IDisposable
{
    string Id { get; }
    string Type { get; }
    string DisplayName { get; }
    TriggerState State { get; }
    Task<bool> PollAsync(CancellationToken ct = default);
    void Start();
    void Stop();
    event EventHandler<TriggerStateChangedEventArgs>? StateChanged;
    void SetLogPath(string logPath);
}
```

### 触发器状态机

```
                    Start()
    ┌──────────► Monitoring ─────────────────┐
    │                                        │
    │         条件满足                         │
    │                                        ▼
    │                                   Triggered
    │                                        │
    │         条件消失                         │
    │                                        │
    └────────────────────────────────────────┘
                Stop() / Dispose()
                     │
                     ▼
                    Idle
```

### 轮询调度器

`PollingScheduler` 统一管理所有触发器的轮询：

```csharp
// 核心机制:
// 1. 单一 Timer，5秒间隔
// 2. 并发轮询所有触发器（SemaphoreSlim 限制最大 4 并发）
// 3. 触发器触发后自动复位（由触发器内部逻辑控制）
// 4. 轮询完成后调用 IPostPollCallback.OnPollingComplete()
```

### 复合条件评估

`RuleTrigger` 处理多个条件的 AND/OR 组合：

```
条件A [And] 条件B [Or] 条件C

评估逻辑：
1. 按 OR 分组：[A And B] OR [C]
2. 组内 AND 运算
3. 组间 OR 运算

示例：
- A=true, B=false, C=true → (true AND false) OR true = true
- A=true, B=true, C=false → (true AND true) OR false = true
```

---

## 如何添加新的触发条件

### 步骤概览

1. 创建触发器类（继承 `OptimizedTriggerBase`）
2. 添加 `[Trigger]` 特性
3. 实现 `EvaluateCoreAsync` 方法
4. 在 UI 中添加选项
5. 更新日志显示（可选）

### 详细步骤

#### 步骤 1：创建触发器类

在 `src/EWeLinkLinker.Core/Triggers/` 目录下创建新文件，如 `MemoryTrigger.cs`：

```csharp
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 内存使用率触发器（边沿检测型）
/// 参数格式: 阈值（百分比），如 "80" 表示 80%
/// </summary>
[Trigger("memory_usage", "内存使用率", "内存使用率超过阈值时触发")]
public class MemoryTrigger : OptimizedTriggerBase
{
    private readonly string _parameter;
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;

    public override string Type => "memory_usage";
    public override string DisplayName => "内存使用率";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(10);

    public MemoryTrigger(TriggerConfig config) : base()
    {
        _parameter = config.Parameter;
        _comparison = config.Comparison;

        if (!float.TryParse(config.Parameter, out _))
            throw new ArgumentException("内存阈值必须为数字");
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "内存阈值不能为空";
            return false;
        }
        if (!float.TryParse(parameter, out var value) || value < 0 || value > 100)
        {
            errorMessage = "内存阈值必须为 0-100 之间的数字";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        var usage = GetMemoryUsage();
        if (float.IsNaN(usage))
        {
            return ValueTask.FromResult(false);
        }

        var isTriggered = ComparisonHelper.Evaluate(usage, _parameter, "", _comparison);

        // 边沿检测：从未满足变为满足时触发
        if (isTriggered && !_wasTriggered)
        {
            _wasTriggered = true;
            return ValueTask.FromResult(true);
        }

        // 条件不再满足，复位
        if (!isTriggered && _wasTriggered)
        {
            _wasTriggered = false;
            State = TriggerState.Monitoring;
        }

        return ValueTask.FromResult(false);
    }

    private static float GetMemoryUsage()
    {
        // 实现内存使用率读取
        // 示例：使用 PerformanceCounter 或 WMI
        try
        {
            var totalMemory = new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory;
            var freeMemory = new Microsoft.VisualBasic.Devices.ComputerInfo().AvailablePhysicalMemory;
            return (float)((totalMemory - freeMemory) * 100.0 / totalMemory);
        }
        catch
        {
            return float.NaN;
        }
    }
}
```

#### 步骤 2：添加 `[Trigger]` 特性

```csharp
[Trigger("memory_usage", "内存使用率", "内存使用率超过阈值时触发")]
```

参数说明：
- 第一个参数：`type` 标识符（在配置文件中使用）
- 第二个参数：显示名称（在 UI 中显示）
- 第三个参数：描述文本

#### 步骤 3：实现 `EvaluateCoreAsync`

这是触发器的核心方法，每次轮询时调用：

```csharp
protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
{
    // 1. 读取当前值
    var currentValue = GetCurrentValue();
    
    // 2. 使用 ComparisonHelper 比较
    var isTriggered = ComparisonHelper.Evaluate(currentValue, _parameter, _parameter2, _comparison);
    
    // 3. 边沿检测（防止重复触发）
    if (isTriggered && !_wasTriggered)
    {
        _wasTriggered = true;
        return ValueTask.FromResult(true);  // 触发！
    }
    
    // 4. 条件消失时复位
    if (!isTriggered && _wasTriggered)
    {
        _wasTriggered = false;
        State = TriggerState.Monitoring;
    }
    
    return ValueTask.FromResult(false);
}
```

#### 步骤 4：在 UI 中添加选项

**4.1 修改 `src/EWeLinkLinker.ConfigApp/MainWindow.xaml`**

在触发器类型 ComboBox 中添加新选项：

```xml
<ComboBoxItem Content="内存使用率" Tag="memory_usage"/>
```

**4.2 修改 `src/EWeLinkLinker.Core/Models/LinkerRule.cs`**

添加辅助属性：

```csharp
[System.Text.Json.Serialization.JsonIgnore]
public bool IsMemoryUsage => Type == "memory_usage";

[System.Text.Json.Serialization.JsonIgnore]
public bool IsNumeric => IsInterval || IsCpuTemp || IsCpuUsage || IsGpuTemp || IsMemoryUsage;
```

更新参数标签和占位符：

```csharp
public string ParameterLabel => Type switch
{
    // ... 其他类型 ...
    "memory_usage" => "使用率",
    _ => ""
};

public string ParameterPlaceholder => Type switch
{
    // ... 其他类型 ...
    "memory_usage" => "80",
    _ => ""
};
```

**4.3 修改 `src/EWeLinkLinker.ConfigApp/MainWindow.xaml.cs`**

在测试按钮中添加读取逻辑：

```csharp
private static string GetMemoryUsageInfo(string thresholdStr, string comparisonText)
{
    try
    {
        var usage = GetMemoryUsage();
        var threshold = float.Parse(thresholdStr);
        var status = usage >= threshold ? "✓ 超过阈值" : "✗ 未超过";
        return $"内存使用率: {usage:F1}%\n阈值: {comparisonText} {threshold}%\n状态: {status}";
    }
    catch (Exception ex)
    {
        return $"内存使用率读取失败: {ex.Message}";
    }
}
```

#### 步骤 5：更新日志显示（可选）

修改 `src/EWeLinkLinker.Core/Logging/ServiceLogger.cs`：

```csharp
private static string GetConditionDescription(ConditionInfo cond)
{
    return cond.Type switch
    {
        // ... 其他类型 ...
        "memory_usage" => $"内存使用率 {GetComparisonString(cond.Comparison)} {cond.Parameter}%",
        _ => $"{cond.Type} {GetComparisonString(cond.Comparison)} {cond.Parameter}"
    };
}
```

### 注意事项

#### 1. 边沿检测 vs 电平检测

**边沿检测**（推荐用于温度/使用率等）：
- 只在条件从"不满足"变为"满足"时触发一次
- 需要等待条件消失后才能再次触发
- 使用 `_wasTriggered` 标志

```csharp
if (isTriggered && !_wasTriggered)
{
    _wasTriggered = true;
    return ValueTask.FromResult(true);
}
if (!isTriggered && _wasTriggered)
{
    _wasTriggered = false;
    State = TriggerState.Monitoring;
}
```

**电平检测**（用于时间/间隔等）：
- 每次轮询都根据条件判断
- 不需要 `_wasTriggered` 标志

```csharp
return ValueTask.FromResult(isTriggered);
```

#### 2. 资源管理

如果触发器使用了需要释放的资源（如 `PerformanceCounter`、`Computer`），必须正确实现 `OnDispose`：

```csharp
protected override void OnDispose()
{
    _counter?.Dispose();
    _counter = null;
    base.OnDispose();  // 可选
}
```

#### 3. 线程安全

- `EvaluateCoreAsync` 可能从线程池线程调用
- 不要直接访问 UI 元素
- 使用 `lock`保护共享状态
- 使用 `CancellationToken` 支持取消

#### 4. 轮询间隔

根据触发器类型选择合适的轮询间隔：

| 触发器类型 | 推荐间隔 | 说明 |
|-----------|---------|------|
| 时间 | 30秒 | 时间触发器有 30 秒窗口 |
| 间隔 | 1分钟 | 按分钟计算 |
| CPU/GPU 温度 | 10秒 | 温度变化较快 |
| CPU/GPU 使用率 | 5秒 | 使用率变化快 |
| 应用启动/关闭 | 3秒 | 需要快速响应 |

#### 5. 参数验证

在构造函数和 `ValidateParameter` 中验证参数：

```csharp
public MemoryTrigger(TriggerConfig config) : base()
{
    // 构造函数中验证（抛出异常）
    if (!float.TryParse(config.Parameter, out _))
        throw new ArgumentException("内存阈值必须为数字");
}

public override bool ValidateParameter(string parameter, out string? errorMessage)
{
    // UI 验证（返回错误信息）
    if (string.IsNullOrEmpty(parameter))
    {
        errorMessage = "内存阈值不能为空";
        return false;
    }
    errorMessage = null;
    return true;
}
```

#### 6. 触发器注册

触发器通过反射自动注册。确保：
- 类是 `public` 的
- 有 `[Trigger]` 特性
- 继承自 `OptimizedTriggerBase`
- 有接受 `TriggerConfig` 的构造函数

`TriggerRegistry` 会在首次访问时自动扫描程序集中的所有触发器类型。

---

## 通信协议

### 云端 API (eWeLink V2)

#### 端点

| 区域 | Base URL |
|------|----------|
| 中国 (cn) | `https://cn-apia.coolkit.cn` |
| 欧洲 (eu) | `https://eu-apia.coolkit.cc` |
| 美国 (us) | `https://us-apia.coolkit.cc` |
| 亚洲 (as) | `https://as-apia.coolkit.cc` |

#### 签名算法

```
Authorization = "Sign " + Base64(HMAC-SHA256(JSON_body, appSecret))
```

- AppId: `R8Oq3y0eSZSYdKccHlrQzT1ACCOUT9Gv`
- SignKey: `1ve5Qk9GXfUhKAn1svnKwpAlxXkMarru`

#### API 列表

| 端点 | 方法 | 说明 |
|------|------|------|
| `/v2/user/login` | POST | 登录获取 Token |
| `/v2/user/refresh` | POST | 刷新 Token |
| `/v2/device/thing` | GET | 获取设备列表 |

### 局域网协议 (AES-128-CBC)

#### 设备发现流程

```
┌─────────────────────────────────────────────────────────────┐
│  Step 1: Ping Sweep                                         │
│  - 50 并发 Ping 整个子网 (192.168.x.1 ~ 192.168.x.254)       │
│  - 超时 200ms                                                │
│  - 等待 300ms 让 ARP 缓存更新                                 │
├─────────────────────────────────────────────────────────────┤
│  Step 2: ARP 表匹配                                          │
│  - 执行 arp -a 获取 ARP 缓存                                 │
│  - 将设备 MAC (云端/真实) 与 ARP 表匹配                        │
│  - 跳过已有 IP 的设备                                        │
├─────────────────────────────────────────────────────────────┤
│  Step 3: TCP 端口扫描                                         │
│  - 对未匹配设备扫描 8081 端口                                 │
│  - 50 并发，300ms 超时                                        │
│  - 对开放端口的 IP 发送加密验证请求                             │
│  - 解密成功则确认设备身份                                      │
└─────────────────────────────────────────────────────────────┘
```

#### AES-128-CBC 加密

```
Key = MD5(deviceKey)        → 16 bytes
IV  = Random 16 bytes       → 每次请求随机
Mode = CBC, Padding = PKCS7

请求格式:
{
  "sequence": "timestamp_ms",
  "deviceid": "设备ID",
  "selfApikey": "123",
  "encrypt": true,
  "data": "Base64(加密后的JSON)",
  "iv": "Base64(IV)"
}

控制命令 (加密前):
{
  "switches": [
    { "outlet": 0, "switch": "on" }
  ]
}
```

---

## 日志系统

### 日志文件位置

| 应用 | 路径 |
|------|------|
| ConfigApp | `config/debug.log` |
| Service | `publish/Service/logs/service-YYYY-MM-DD.log` |

### 日志开关

在配置文件中控制：

```json
{
  "loggingEnabled": true
}
```

或在 ConfigApp 界面中切换"日志"复选框。

### 日志清理

服务启动时自动清理 7 天前的旧日志文件。

### 日志格式

```
[HH:mm:ss.fff] [LEVEL] [ClassName] Message
```

示例：
```
[14:25:01.345] [INFO] [LinkerWindowsService] Service starting...
[14:25:01.402] [INFO] [TriggerManager] 规则 [规则 1] 初始化完成: 2 个条件监控器
[14:25:06.528] [INFO] [PollingScheduler] ✓ 条件触发: GPU温度 (a94040d0)
```

---

## 常见问题排查

### 服务无法启动

1. 检查日志文件：`publish/Service/logs/service-YYYY-MM-DD.log`
2. 确认 `config/linker.json` 存在且有效
3. 确认已通过 ConfigApp 登录
4. 尝试控制台模式运行查看详细错误

### 设备无法发现

1. 检查 `debug.log` 中的设备发现日志
2. 确认设备和 PC 在同一子网
3. 检查 ARP 表：`arp -a`
4. 确认设备支持 LAN 协议（部分 IR 桥接器不支持）
5. 尝试手动输入真实 MAC 地址

### 触发器不触发

1. 检查服务日志中的规则加载信息
2. 确认规则 `Enabled = true`
3. 确认条件类型拼写正确
4. 使用 ConfigApp 中的 "▶" 测试按钮验证条件
5. 检查 PollingScheduler 是否启动

### Token 过期

1. 检查 `refreshToken` 是否正确保存
2. 确认 `config/linker.json` 中有 `refreshToken`
3. 重新登录获取新的 Token

### 编译错误

```bash
# 清理后重新构建
dotnet clean
dotnet restore
dotnet build
```

---

## 开发注意事项

### 内存管理

1. **Dispose 模式**：所有持有非托管资源的类必须实现 `IDisposable`
2. **事件订阅**：长生命周期对象订阅短生命周期对象的事件时，必须取消订阅
3. **静态集合**：避免在静态集合中存储对象引用，防止内存泄漏
4. **Process 对象**：使用 `Process.GetProcesses()` 后必须释放每个 `Process` 对象

### 线程安全

1. **UI 线程**：WPF UI 元素只能在 UI 线程访问，使用 `Dispatcher.Invoke`
2. **并发集合**：使用 `ConcurrentDictionary` 替代 `Dictionary` + `lock`
3. **async/await**：避免 `async void`，使用 `async Task`
4. **CancellationToken**：长时间运行的操作应支持取消

### 配置热重载

1. **原子写入**：使用临时文件 + 重命名确保配置完整性
2. **防抖**：FileSystemWatcher 使用 500ms 防抖避免重复加载
3. **异常处理**：配置加载失败时保持原有配置

### 日志最佳实践

1. **级别选择**：
   - `Info`：正常操作流程
   - `Warning`：非致命错误
   - `Error`：需要关注的错误
   - `Debug`：详细调试信息

2. **性能考虑**：高频轮询的触发器应减少日志输出

### 测试建议

1. **控制台模式**：开发时使用 `--console` 模式运行服务
2. **测试按钮**：使用 ConfigApp 中的 "▶" 按钮测试单个条件
3. **日志观察**：使用 `tail -f` 或日志查看器实时观察日志

---

## 许可证

本项目仅供学习和个人使用。

## 相关资源

- [eWeLink API 文档](https://coolkit-technologies.github.io/eWeLink-API/)
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- [.NET 10 文档](https://learn.microsoft.com/dotnet/)
