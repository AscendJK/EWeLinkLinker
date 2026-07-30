# EWeLink Linker

> Windows 智能设备联动控制系统 - 根据 PC 状态自动控制 eWeLink 智能设备

## 目录

- [项目概述](#项目概述)
- [快速开始](#快速开始)
- [配置系统](#配置系统)
- [触发器系统](#触发器系统)
- [添加新的触发条件](#添加新的触发条件)
- [架构设计](#架构设计)
- [日志系统](#日志系统)
- [常见问题排查](#常见问题排查)

---

## 项目概述

EWeLink Linker 是一个 Windows 平台的智能设备联动控制系统。它通过监控 PC 的状态（开关机、睡眠唤醒、CPU 温度、GPU 温度、CPU 使用率、应用启停等），自动控制 eWeLink 智能插座/开关的通断。

### 核心功能

| 功能 | 描述 |
| --- | --- |
| 电源事件联动 | 开机/关机/睡眠/唤醒时自动控制设备 |
| 定时触发 | 每天固定时间执行动作 |
| 间隔触发 | 每隔 N 分钟循环执行 |
| CPU/GPU 监控 | 温度/使用率超阈值时触发 |
| 应用监控 | 指定应用启动/关闭时触发 |
| 复合条件 | 支持 AND/OR 逻辑组合多个条件 |
| 本地控制 | 通过 LAN 协议直控设备，无需云端 |
| 配置热重载 | 修改配置后自动生效，无需重启服务 |
| 可调轮询间隔 | 1-30 秒可配置，默认 5 秒 |

### 技术栈

- **运行时**: .NET 10.0
- **UI框架**: WPF (Windows Presentation Foundation)
- **服务**: Windows Service (ServiceBase)
- **通信**: HTTP/REST (云端 API) + TCP Socket (LAN 协议)
- **加密**: AES-128-CBC (LAN) + DPAPI (配置文件) + HMAC-SHA256 (云端签名)
- **序列化**: System.Text.Json

---

## 快速开始

### 1. 构建项目

```bash
# 双击执行
build-all.bat
```

或手动构建：

```bash
dotnet restore
dotnet build -c Release
```

### 2. 配置并运行

1. 运行 `publish\ConfigApp\EWeLinkLinker.ConfigApp.exe`
2. 输入 eWeLink 账号密码，选择区域（默认 cn）
3. 点击 **"登录获取设备"**
4. 如果设备没有 IP 地址，点击 **"刷新IP"** 自动发现

### 3. 配置联动规则

1. 点击 **"+ 新建规则"**
2. 添加条件（类型、比较符、参数值）
3. 添加动作（设备、通道、状态）
4. 点击 **"保存配置"**

### 4. 安装服务

1. 点击 **"安装"**（需要管理员权限）
2. 点击 **"启动"** 启动服务

---

## 配置系统

### 配置文件

`publish/config/linker.json`

```json
{
  "loggingEnabled": true,
  "pollingIntervalSeconds": 5,
  "account": {
    "account": "your@email.com",
    "password": "加密存储",
    "countryCode": "+86",
    "region": "cn"
  },
  "tokens": {
    "accessToken": "DPAPI加密",
    "refreshToken": "DPAPI加密",
    "userApiKey": "DPAPI加密"
  },
  "devices": [...],
  "rules": [...]
}
```

### 配置项说明

| 配置项 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `loggingEnabled` | bool | true | 是否启用日志 |
| `pollingIntervalSeconds` | int | 5 | 轮询间隔（1-30 秒） |
| `account.password` | string | - | DPAPI 加密存储 |
| `tokens.*` | string | - | DPAPI 加密存储 |

### 安全特性

- **密码加密**: 使用 Windows DPAPI (LocalMachine 范围)
- **Token 加密**: 所有 Token 使用 DPAPI 加密
- **旧版兼容**: 自动兼容未加密的旧配置文件

---

## 触发器系统

### 触发器类型

| 类型 | 参数格式 | 说明 |
| --- | --- | --- |
| `time` | `HH:mm` | 每天固定时间（如 `08:00`） |
| `interval` | 分钟数 | 每隔 N 分钟（如 `30`） |
| `cpu_temp` | 摄氏度 | CPU 温度阈值（如 `75`） |
| `cpu_usage` | 百分比 | CPU 使用率阈值（如 `90`） |
| `gpu_temp` | 摄氏度 | GPU 温度阈值（如 `80`） |
| `app_start` | 进程名 | 应用启动时（如 `notepad`） |
| `app_close` | 进程名 | 应用关闭时（如 `chrome`） |
| `boot` | - | 系统启动 |
| `shutdown` | - | 系统关机 |
| `sleep` | - | 系统睡眠 |
| `wake` | - | 系统唤醒 |

### 比较运算符

| 运算符 | 键值 | 适用类型 |
| --- | --- | --- |
| ≥ | `Gte` | 数值 |
| > | `Gt` | 数值 |
| ≤ | `Lte` | 数值 |
| < | `Lt` | 数值 |
| = | `Eq` | 全部 |
| ≠ | `Neq` | 全部 |
| 范围 | `Range` | 数值（格式：`min,max`） |

### 逻辑组合

- **AND**: 所有条件必须同时满足
- **OR**: 任一条件满足即可
- **优先级**: AND > OR（标准布尔优先级）

### 示例规则

```json
{
  "name": "高温开水冷",
  "conditions": [
    { "type": "cpu_temp", "parameter": "75", "comparison": "Gte", "operator": "And" },
    { "type": "gpu_temp", "parameter": "75", "comparison": "Gte", "operator": "Or" }
  ],
  "actions": [
    { "deviceId": "xxx", "state": "on", "outlet": 0 }
  ]
}
```

规则解释：CPU温度 ≥ 75°C **或** GPU温度 ≥ 75°C 时，打开设备通道0。

---

## 添加新的触发条件

### 步骤

#### 1. 创建触发器类

在 `src/EWeLinkLinker.Core/Triggers/` 目录下创建新文件：

```csharp
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

[Trigger("memory_usage", "内存使用率", "内存使用率超过阈值时触发")]
public class MemoryUsageTrigger : OptimizedTriggerBase
{
    private readonly string _parameter;
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;

    public override string Type => "memory_usage";
    public override string DisplayName => "内存使用率";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(5);

    public MemoryUsageTrigger(TriggerConfig config) : base()
    {
        _parameter = config.Parameter;
        _comparison = config.Comparison;

        if (!float.TryParse(config.Parameter, out _))
            throw new ArgumentException("内存使用率阈值必须为数字");
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "阈值不能为空";
            return false;
        }
        if (!float.TryParse(parameter, out var value) || value < 0 || value > 100)
        {
            errorMessage = "阈值必须为 0-100 之间的数字";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        var usage = GetMemoryUsage();
        var isTriggered = ComparisonHelper.Evaluate(usage, _parameter, null, _comparison);

        // 边沿检测
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
        return ValueTask.FromResult(false);
    }

    private static float GetMemoryUsage()
    {
        // 实现内存使用率读取
        // 可使用 PerformanceCounter 或 Microsoft.Diagnostics.Runtime
        return 0f;
    }
}
```

#### 2. 添加 `[Trigger]` 特性

```csharp
[Trigger("memory_usage", "内存使用率", "内存使用率超过阈值时触发")]
```

参数说明：

- `TypeKey`: 类型标识符（唯一）
- `DisplayName`: 显示名称
- `Description`: 描述

#### 3. 在 UI 中添加选项

在 `MainWindow.xaml` 中添加：

```xml
<ComboBoxItem Tag="memory_usage" Content="内存使用率"/>
```

在 `ComparisonComboBox` 中添加适用的比较符。

#### 4. 自动注册

触发器通过反射自动注册到 `TriggerRegistry`，无需手动添加代码。

### 注意事项

1. **边沿检测**: 使用 `_wasTriggered` 防止重复触发
2. **传感器缓存**: 如需频繁读取传感器，使用 `SensorCache.GetOrCreate()`
3. **资源管理**: 如有非托管资源，重写 `OnDispose()` 释放
4. **错误处理**: 读取失败时返回 `false`（安全失败）
5. **日志输出**: 使用 `Log(TraceLevel.Info, message)` 记录关键信息

---

## 架构设计

### 整体架构

```text
┌─────────────────────────────────────────────────────────────┐
│                    ConfigApp (WPF)                           │
│  ┌─────────┐ ┌──────────┐ ┌──────────┐ ┌─────────────────┐ │
│  │ 登录    │ │ 设备发现 │ │ 规则编辑 │ │ 服务控制        │ │
│  └─────────┘ └──────────┘ └──────────┘ └─────────────────┘ │
└─────────────────────────────────────────────────────────────┘
                           │ 共享配置文件 (linker.json)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                  LinkerWindowsService                        │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ TriggerManager                                        │  │
│  │  ┌─────────────────────────────────────────────────┐ │  │
│  │  │ PollingScheduler (Timer: 5s)                     │ │  │
│  │  │  ┌──────────┐ ┌──────────┐ ┌──────────┐       │ │  │
│  │  │  │ CpuTemp  │ │ GpuTemp  │ │ AppStart │ ...   │ │  │
│  │  │  └──────────┘ └──────────┘ └──────────┘       │ │  │
│  │  └─────────────────────────────────────────────────┘ │  │
│  └──────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### 工作流程

```text
服务启动
  │
  ├─ 加载配置 (linker.json)
  ├─ 初始化日志 (ServiceLogger)
  ├─ 创建 TriggerManager
  │    ├─ 创建 PollingScheduler
  │    ├─ 遍历规则 → 创建 RuleTrigger
  │    │    └─ 为每个条件创建 Trigger 实例
  │    └─ 注册到 PollingScheduler
  ├─ 启动 Timer (每 5 秒)
  └─ 启动 FileSystemWatcher (监控配置变化)

轮询周期 (每 5 秒)
  │
  ├─ 清空 SensorCache
  ├─ 并发轮询所有触发器 (最多 4 并发)
  │    └─ Trigger.EvaluateCoreAsync()
  │         ├─ 读取传感器 (SensorCache 共享)
  │         ├─ 比较条件
  │         └─ 边沿检测
  ├─ 等待所有触发器完成
  └─ 调用 OnPollingComplete 回调
       └─ RuleTrigger.EvaluateCompositeCondition()
            └─ 条件满足 → 执行动作 (控制设备)
```

### 关键组件

| 组件 | 职责 |
| --- | --- |
| `ServiceLogger` | 异步日志写入 (BlockingCollection + 后台线程) |
| `SensorCache` | 传感器缓存 (每轮只读一次) |
| `PollingScheduler` | 定时轮询调度器 |
| `TriggerManager` | 触发器管理 (加载、启动、重载) |
| `RuleTrigger` | 复合条件评估 (AND/OR) |
| `ComparisonHelper` | 条件比较逻辑 |

### 传感器缓存机制

```text
第 1 秒: 轮询开始
  ├─ CpuTempTrigger1 → SensorCache.GetOrCreate("cpu_temp", ReadCpuTemperature)
  │    └─ 首次调用 → 读取 WMI → 缓存值 → 返回
  ├─ CpuTempTrigger2 → SensorCache.GetOrCreate("cpu_temp", ...)
  │    └─ 命中缓存 → 直接返回 (不重新读取)
  └─ GpuTempTrigger1 → SensorCache.GetOrCreate("gpu_temp", ReadGpuTemperature)
       └─ 首次调用 → 读取 LibreHardwareMonitor → 缓存值 → 返回

第 6 秒: 下一轮轮询
  └─ SensorCache.Clear() → 释放旧缓存 → 重新读取
```

---

## 日志系统

### 日志文件

| 文件 | 位置 | 用途 |
| --- | --- | --- |
| `service-YYYY-MM-DD.log` | `publish/Service/logs/` | 服务端日志 |
| `debug.log` | `publish/config/` | 调试日志 (ConfigApp) |

### 日志格式

```text
[HH:mm:ss.fff] [LEVEL] 消息内容
```

### 服务端日志示例

```text
[10:45:04.403] [INFO] Logging enabled: True
[10:45:04.475] [INFO] Trigger manager started with 4 triggers, polling interval: 5s
[10:45:09.123] [INFO] [轮询] 触发器: 4, 传感器: [CPU温度, GPU温度]
[10:45:14.456] [INFO] [轮询] 触发器: 4, 传感器: [CPU温度, GPU温度]
[10:45:19.789] [INFO] ✓ 条件触发: CPU温度 (a1b2c3d4)
[10:45:19.790] [INFO] [RuleTrigger:规则 2] cpu_temp=Triggered, gpu_temp=Monitoring => 满足
[10:45:19.791] [INFO] !! 规则触发 [规则 2] 原因: cpu_temp=75(满足)
[10:45:19.800] [INFO] 动作执行 水冷 通道0 -> on [成功]
```

### 日志配置

在 ConfigApp 界面中：

- **日志 CheckBox**: 启用/禁用日志写入
- **📄 按钮**: 打开日志文件夹

---

## 常见问题排查

### 服务无法启动

| 原因 | 解决方案 |
| --- | --- |
| .NET 10 未安装 | 安装 .NET 10 Runtime |
| 配置文件损坏 | 删除 `linker.json` 重新登录 |
| 端口被占用 | 更换端口或关闭占用程序 |

### 设备无法控制

| 原因 | 解决方案 |
| --- | --- |
| 设备 IP 未知 | 点击"刷新IP"自动发现 |
| 设备离线 | 检查设备网络连接 |
| LAN 协议失败 | 服务自动切换到云端 API |

### 触发器不触发

| 原因 | 解决方案 |
| --- | --- |
| 条件参数错误 | 检查阈值和比较符 |
| 传感器读取失败 | 检查日志中的警告信息 |
| 规则未启用 | 检查规则 CheckBox |

### 日志不输出

| 原因 | 解决方案 |
| --- | --- |
| 日志未启用 | 检查 ConfigApp 中的日志 CheckBox |
| 服务未重启 | 修改配置后重启服务 |
| 文件权限问题 | 检查 logs 目录写入权限 |

---

## 开发注意事项

### 构建与发布

```bash
# 开发构建 (Debug)
dotnet build

# 发布构建 (Release)
build-all.bat
```

### 项目结构

```text
EWeLinkLinker/
├── src/
│   ├── EWeLinkLinker.Core/          # 核心库
│   │   ├── Config/                   # 配置模型
│   │   ├── Cloud/                    # 云端 API 客户端
│   │   ├── Lan/                      # LAN 协议客户端
│   │   ├── Logging/                  # 日志系统
│   │   ├── Models/                   # 数据模型
│   │   ├── Services/                 # 业务服务
│   │   ├── Token/                    # Token 管理
│   │   └── Triggers/                 # 触发器系统
│   ├── EWeLinkLinker.Service/        # Windows 服务
│   └── EWeLinkLinker.ConfigApp/      # WPF 配置工具
├── publish/                          # 发布目录
│   ├── ConfigApp/                    # ConfigApp 发布
│   ├── Service/                      # 服务发布
│   └── config/                       # 共享配置文件
└── build-all.bat                     # 构建脚本
```

### 代码规范

- 所有触发器继承 `OptimizedTriggerBase`
- 使用 `[Trigger]` 特性自动注册
- 非托管资源在 `OnDispose()` 中释放
- 日志使用 `Log(TraceLevel, message)` 方法

---

## 许可证

本项目仅供学习和个人使用。
