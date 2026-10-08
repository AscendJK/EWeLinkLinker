# EWeLink Linker

> Windows 智能设备联动控制系统 - 根据 PC 状态自动控制 eWeLink 智能设备

本项目采用 **MIT 许可证**（见 [LICENSE](LICENSE)）；用到的第三方组件各自的许可见[许可与第三方组件](#许可与第三方组件)。

## 目录

- [快速开始](#快速开始)
- [规则里的抖动带宽与滞回](#规则里的抖动带宽与滞回)
- [保存会被拦下的情况](#保存会被拦下的情况)
- [状态和凭据从哪来](#状态和凭据从哪来)
- [配置文件与加密](#配置文件与加密)
- [触发器系统](#触发器系统)
- [添加新的触发条件](#添加新的触发条件)
- [日志系统](#日志系统)
- [开发注意事项](#开发注意事项)
- [许可与第三方组件](#许可与第三方组件)
- [致谢](#致谢)

---
---

## 快速开始

### 1. 构建项目

**环境要求：**
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本（推荐 .NET 10）
- Windows 10/11（Windows Service + WPF 需要）

```bash
# 双击执行
build-all.bat
```

或手动构建（`build-all.bat` 做的就是这两条 `publish`，`build` 只产出 `bin\` 下的中间产物，
不会填 `publish\ConfigApp` 和 `publish\Service`，而服务和 ConfigApp 跑的就是那两个目录）：

```bash
dotnet restore
dotnet publish src\EWeLinkLinker.ConfigApp\EWeLinkLinker.ConfigApp.csproj -c Release -o publish\ConfigApp --self-contained false
dotnet publish src\EWeLinkLinker.Service\EWeLinkLinker.Service.csproj -c Release -o publish\Service --self-contained false
```

> 发布前先停服务：`publish\Service` 里的 exe/dll 在服务运行期间被锁住，直接发布会得到
> "构建失败"或一个半成品目录。`build-all.bat` 会先 `sc query` 检查并在服务在跑时拒绝构建；
> 手工执行这两条命令时没有这道保护，需要自己先停（ConfigApp 点「停止」，或 `net stop EWeLinkLinker`）。

> **只改到界面（ConfigApp）时可以不停服务**：只发 `publish\ConfigApp` 那一条命令就够，服务进程不动。
> 判据要看**源码差**而不是二进制差——`git diff --name-only <装着的commit>..HEAD -- src/EWeLinkLinker.Core src/EWeLinkLinker.Service`
> 为空、且这两个目录工作区干净，才许只换 GUI（同一个 commit 号一变，`Core.dll` 的字节必然变，那只是版本戳和元数据身份字段，不代表代码变了）。
> 装着的版本戳在服务日志头和 exe 的 `ProductVersion` 里，形如 `1.0.0+<40位commit>`。
> 副作用：只换 GUI 之后，`publish\ConfigApp\Core.dll` 的戳是新 commit、`publish\Service\Core.dll` 还是旧的（同一份代码、不同戳），下次全量发布自动对齐。

### 2. 配置并运行

1. 运行 `publish\ConfigApp\EWeLinkLinker.ConfigApp.exe`
2. 输入 eWeLink 账号密码，选择区域（默认 cn）
3. 点击 **"登录获取设备"**
4. 如果设备没有 IP 地址，先点击 **"刷新IP"** 自动发现

   > 「刷新IP」和「刷新状态」走完都会顺手保存一次配置；保存被拦下时会弹「无法保存」并点名原因，
   > 那种情况下盘上的配置一个字都不会变（见[保存会被拦下的情况](#保存会被拦下的情况)）。

   > 如果自动发现仍找不到 IP，是因为 LAN 发现依赖设备的**真实物理 MAC 地址**来匹配。
   > 云端返回的 MAC 地址可能是**虚拟 MAC**，与设备底部标签的物理 MAC 不同。

5. **填写真实 MAC 地址**：在设备卡片上的"真实MAC"输入框中，填入设备底部标签的物理 MAC 地址（格式 `AA:BB:CC:DD:EE:FF`）

   > 填写后点击 **"保存配置"**，服务端会自动用真实 MAC 重新匹配 ARP 表进行发现。
   > 已填写的真实 MAC 地址在重新登录后**不会被覆盖**。

6. 再次点击 **"刷新IP"** 完成发现

   > ⚠️ 注意：设备 IP 发现只在 ConfigApp 中手动执行（点击"刷新IP"或自动发现按钮），
   > **服务端不会自动发现设备 IP**。若设备通过 DHCP 更换了 IP（重启路由器、重新插拔网线等），
   > 需要在 ConfigApp 中重新点击"刷新IP"并保存配置，否则服务端将无法控制该设备。

7. 想看设备现在的开关状态就点 **"刷新状态"**；右上角的 **"服务: 运行中/已停止"** 是服务端实时状态，
   旁边 **"轮询"** 下拉是服务判定读数的间隔（1–30 秒），**"日志"** 勾选只影响服务端主日志（详见[日志系统](#日志系统)）

### 3. 配置联动规则

1. 点击 **"+ 新建规则"**
2. 添加条件（类型、比较符、参数值）
3. 添加动作（设备、通道、状态）
4. 点击 **"保存配置"**

### 4. 安装服务

ConfigApp 中（按钮在账号那一行的最右边）：

1. 点击 **"安装服务"**（需要管理员权限）
2. 点击 **"启动"** 启动服务（服务跑起来后这个按钮变成 **"停止"**）
3. 要移除就点 **"卸载服务"**（不删配置和日志）

右上角的 **"服务: 运行中 / 已停止"** 是实时状态，装完不用重开程序就能看到。

或使用 PowerShell 脚本（同样需要管理员权限）：

| 脚本 | 功能 |
| --- | --- |
| `install.ps1` | 编译 + 安装 + 启动（一条龙） |
| `install-simple.ps1` | 仅安装 + 启动（需要已编译的二进制） |
| `uninstall.ps1` | 停止 + 删除服务（不删除配置和日志） |

---
---

## 规则里的抖动带宽与滞回

温度/使用率这类读数会在阈值上下抖，不加处理就是"到 80°C 开水冷、79.9°C 关水冷"来回打摆。
所以条件里可以填一个**抖动带宽**：触发线还是你填的那个数，**解除线由带宽推导**。

| 比较符 | 解除线 | 例 |
| --- | --- | --- |
| `≥` / `>` | 触发值 − 带宽 | `CPU温度 ≥ 80`、带宽 `3` → 解除线 **77** |
| `≤` / `<` | 触发值 + 带宽 | `CPU温度 ≤ 65`、带宽 `3` → 解除线 **68** |

- 只有 **CPU温度 / CPU使用率 / GPU温度** 且比较符是 `≥ > ≤ <` 时才有这一格；填 `=`、`范围` 之类时输入框会隐藏，
  残留值也不会写进配置。
- 界面上填的是**带宽**而不是绝对值：这样你把触发值从 80 改成 75，"允许抖 3 度"这个意图跟着走（存绝对值的话解除线会被忘在 77）。
  配置文件里对应字段是 `releaseBand`；老配置存的 `releaseParameter` 会在加载时自动换算成带宽，下次保存就收敛成新格式。
- 触发之后规则**粘住**：读数没越过解除线之前不会再造第二个上升沿（不会反复开关设备），越过解除线才复位。
- **最长粘住 30 分钟**：万一读数卡在触发线与解除线之间迟迟不越线，到点会强制重判一次把状态对齐。
  注意这一发只是重判，**不会补发动作**，日志里也不会出现 `✓ 条件触发`。

---

## 保存会被拦下的情况

点「保存配置」（以及「刷新IP」/「刷新状态」末尾那次自动保存）会先逐条试建触发器再写盘。
下面这些会被拦下，弹「无法保存」点名原因，**盘上的配置一个字都不改**：

| # | 拦下的情况 | 不拦会怎样 |
| --- | --- | --- |
| 1 | 比较符在这个类型里根本没实现（`time` 只有 `= ≠ ≥ <`） | 那条判定恒不满足，规则看着在跑其实永不执行 |
| 2 | 条件参数建不出触发器（比如类型切成「应用启动」还留着上一条的 `08:00`） | 服务端构造触发器时抛异常，**整条规则连同它所有动作被跳过** |
| 3 | 抖动带宽不是正数，或这个比较符不支持滞回 | 推导出的解除线没意义，滞回形同关闭 |
| 4 | 动作的通道号超出这台设备的路数（通道下拉按 `channelCount` 生成，8 路可选到 `CH7`） | 下发时设备报错，日志里只剩一条 fail |
| 5 | 动作没有选中设备（设备那一格是空的） | 服务端只留一行 `Device not found:`，**这一路永不下发** |

- **已停用的规则**里第 4、5 条不拦，只在 `debug.log` 写一行 `[保存] 提醒`——免得一条没启用的坏行挡住你把别的改动存下去。
- **关窗口时那次自动保存不弹框**：拦下就静默跳过写盘，日志留 `[保存] 中止`。所以"改了没存上"先去 `publish/config/debug.log` 找这两类行。
- 老配置里存着本类型不支持的比较符时，下拉**仍然把它列出来**（旁边补上该类型支持的项），不会凭空变空白，让你看得见自己选的是什么。

---

## 状态和凭据从哪来

- **控制设备只走局域网**，云端只负责给设备清单和密钥。所以设备离线/断网时状态读不到，但配置里保留的 IP 和密钥仍有效。
- **「刷新状态」**：先问局域网的 mDNS 公告（设备自己广播的实时通道状态，用该设备的 `deviceKey` 解密 TXT），
  读到的那几台**覆盖**云端；云端那份是服务器上的缓存，可能已经过时。弹窗会把两侧分别说清楚：
  读到几台、哪几台问了没应答、局域网没有可问的设备（缺 IP 或密钥）、本机 5353 端口没绑上。
  两处都没拿到才算失败，而且不写盘。
- **「刷新IP」**：ping 扫段 + ARP 表匹配 + TCP 兜底，靠**真实 MAC** 认设备，所以缺真实 MAC 的那几台只能走云端。
- **凭据自愈**：eWeLink 不返回 token 到期时间，所以按拿到的时刻计时，满 **25 天**主动换新一轮；
  云端报凭证失效时先用 refresh token 换，换不动才用账号密码重新登录，且**一分钟内最多重登一次**（防止把账号打进锁定）。
  自动重登成功会把新 token 写进配置，弹窗里会带一句"云端登录已自动续期"。
  > 这个"满 25 天"依赖配置里记下的拿到时刻；更早版本存的配置没有这个字段，那种不会主动刷，等云端报失效时由上面那条自愈接住。

---

## 配置文件与加密

- 路径：`publish/config/linker.json`，服务端和 ConfigApp 共用同一份；服务端监视文件变化热重载（日志里 `配置重载成功: N 条规则`）。
- 账号密码和 `accessToken` / `refreshToken` / `userApiKey` 在文件里是 **Windows DPAPI（本机范围 + 固定熵）密文**，
  换机器或重装系统后解不开；`deviceKey` 按明文存（局域网读状态要用它解密设备公告）。
- 服务安装目录的权限收紧过（普通用户只读、`SYSTEM` 可写），所以**替换发布文件、装/卸服务都需要管理员权限**。
- 想知道盘上跑的是哪版：看服务端日志头或 `EWeLinkLinker.Service.exe` 的版本戳，形如 `1.0.0+<40位commit>`。

---
---

## 触发器系统

### 触发器类型

| 类型 | 参数格式 | 说明 |
| --- | --- | --- |
| `time` | `HH:mm` | 每天固定时间（如 `08:00`），比较符只有 `= ≠ ≥ <` |
| `interval` | 分钟数 | 每隔 N 分钟（如 `30`），范围 1–1440 |
| `cpu_temp` | 摄氏度 | CPU 温度阈值（如 `75`），支持抖动带宽 |
| `cpu_usage` | 百分比 | CPU 使用率阈值（如 `90`），支持抖动带宽 |
| `gpu_temp` | 摄氏度 | GPU 温度阈值（如 `80`），支持抖动带宽 |
| `app_start` | 进程名 | 应用启动时（如 `notepad`） |
| `app_close` | 进程名 | 应用关闭时（如 `chrome`） |
| `boot` | 不需要 | 系统开机 |
| `shutdown` | 不需要 | 系统关机 |
| `sleep` | 不需要 | 系统睡眠 |
| `wake` | 不需要 | 系统唤醒 |

> 电源事件类（`boot` / `shutdown` / `sleep` / `wake`）**不需要参数也不需要比较符**，界面上会显示"（无需参数）"。
> 这四类由服务监听系统事件触发，不走轮询；切成这一类时残留的参数会被自动清掉。

### 比较运算符

| 运算符 | 键值 | 适用类型 |
| --- | --- | --- |
| ≥ | `Gte` | 数值型（`cpu_temp` / `cpu_usage` / `gpu_temp`）、`time` |
| > | `Gt` | 数值型 |
| ≤ | `Lte` | 数值型 |
| < | `Lt` | 数值型、`time` |
| = | `Eq` | 数值型、`time` |
| ≠ | `Neq` | 数值型、`time` |
| 范围 | `Range` | 数值型（参数格式：`min,max`） |
| 范围外 | `OutsideRange` | 数值型（参数格式：`min,max`） |

> 界面上的比较符下拉**按类型给候选**：`time` 只给 `= ≠ ≥ <` 四个，数值型给全 8 个；
> `interval`、进程名、电源事件这几类**根本没有比较符**（下拉不出现）。
> 选到本类型没实现的组合会被「保存配置」拦下（见[保存会被拦下的情况](#保存会被拦下的情况)）。

### 逻辑组合

- **AND**: 所有条件必须同时满足
- **OR**: 任一条件满足即可
- **优先级**: AND > OR（标准布尔优先级）

### 示例规则

```json
{
  "name": "高温开水冷",
  "conditions": [
    { "type": "cpu_temp", "parameter": "75", "comparison": "Gte", "operator": "And", "releaseBand": "3" },
    { "type": "gpu_temp", "parameter": "75", "comparison": "Gte", "operator": "Or",  "releaseBand": "3" }
  ],
  "actions": [
    { "deviceId": "xxx", "name": "水冷", "state": "on", "outlet": 0 }
  ]
}
```

规则解释：CPU温度 ≥ 75°C **或** GPU温度 ≥ 75°C 时，打开设备通道0；读数要降到 **72°C**（75 − 带宽 3）以下这条才会复位。

---
---

## 添加新的触发条件

### 步骤

> ⚠️ **注意**：以下是一个**开发模板示例**，`memory_usage` **并非本项目内置的触发器类型**，
> 且示例中的 `GetMemoryUsage()` 尚未实现（恒返回 0）。如需使用，请完成内存读取实现后再按步骤添加。

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

#### 3.1 登记这个类型支持哪些比较符

`RuleCondition.SupportedComparisons(type)` 里要给这个新类型列出**真的实现了**的比较符。
漏了的话界面上的比较符下拉会是空的，用户选不到东西；列了没实现的分支，保存闸门又会把它当
"本类型没实现的比较符"拦下来（见[保存会被拦下的情况](#保存会被拦下的情况)）。

数值型触发器如果想支持抖动带宽，还要把类型加进 `RuleCondition.SupportsRelease`，
评估时走 `ComparisonHelper.ResolveRelease(参数, 带宽, 比较符)` 推出的解除线；不加就只是普通阈值判定。

#### 4. 自动注册

触发器通过反射自动注册到 `TriggerRegistry`，无需手动添加代码。

### 注意事项

1. **边沿检测**: 使用 `_wasTriggered` 防止重复触发
2. **传感器缓存**: 如需频繁读取传感器，使用 `SensorCache.GetOrCreate()`
3. **资源管理**: 如有非托管资源，重写 `OnDispose()` 释放
4. **错误处理**: 读取失败时返回 `false`（安全失败）。注意**评估抛异常不等于"不满足"**——
   基类会保住已经锁存的判定结果，不会把"这轮读失败"当成"条件已经解除"
5. **日志输出**: 使用 `Log(TraceLevel.Info, message)` 记录关键信息
6. **数字一律按不变文化解析**（`ComparisonHelper.TryParseNumber` / `TryParseInt`），
   跟着系统区域走的话 `de-DE` 会把 `62.5` 读成 `625`

---
---

## 日志系统

### 日志文件

| 文件 | 位置 | 时间戳 | 用途 |
| --- | --- | --- | --- |
| `service-YYYY-MM-DD.log` | `publish/Service/logs/` | `[HH:mm:ss.fff]` | 服务端主日志：启动信息、轮询、触发器判定、规则触发 |
| `service-detail-YYYY-MM-DD.log` | `publish/Service/logs/` | `[HH:mm:ss]` | 服务端详细日志：**`[AUDIT] 设备命令 …`**、LAN/云端调用、传感器真实读数 |
| `debug.log` | `publish/config/` | `[HH:mm:ss]` | ConfigApp 自己的日志（加载、保存、刷新、校验拦下了什么） |

> **要看"命令到底发没发到设备"只能查 `service-detail-*.log`**：主日志只证明判定链走通了，
> 每条设备命令的 `[AUDIT] … 结果=ok/fail/未发出（预算不足）` 都在 detail 文件里。
> 两个文件时间戳格式不一样（主日志带毫秒、detail 不带），写脚本抓数时别用同一个正则。

### 保留与滚动

- 服务端那两个文件**按天分文件**（文件名带日期），跨零点在下一次写日志时换到新文件；启动和换日时删掉 **7 天前**的旧文件。
- 单文件截断：`service-detail-*.log` 和 ConfigApp 的 `debug.log` 超过 **2 MB** 从头部截断；主日志 `service-*.log` 不截断，只靠"按天 + 留 7 天"控制总量。
- ConfigApp 的 `debug.log` 是**单个文件**（文件名不带日期），换天不会分文件。
- 日志文件被外部清空或删掉后，服务不会自己重建——**要重建得重启服务**。

### 界面上的「日志」勾选管什么

- 勾掉只静音**服务端主日志**（`service-*.log`）。
- **`[AUDIT]` 设备命令和 detail 通道无条件留痕**——重启时机、谁在什么时候下过什么令，靠它，不该被降噪顺带抹掉。
- ConfigApp 的 `debug.log` 不受这个勾选影响。
- 旁边的**文件夹图标**是"打开日志文件夹"。

### 日志格式

```text
[HH:mm:ss.fff] [LEVEL] 消息内容
```

### 服务端日志示例（`service-*.log`）

```text
[10:45:04.403] [INFO] Logging enabled: True
[10:45:04.475] [INFO] Trigger manager started with 4 triggers, polling interval: 5s
[10:45:09.123] [INFO] [轮询] 触发器: 4, 传感器: [CPU温度, GPU温度]
[10:45:14.456] [INFO] [轮询] 触发器: 4, 传感器: [CPU温度, GPU温度]
[10:45:19.789] [INFO] ✓ 条件触发: CPU温度 (a1b2c3d4)
[10:45:19.790] [INFO] [RuleTrigger:规则 2] cpu_temp=Triggered, gpu_temp=Monitoring => 满足
[10:45:19.791] [INFO] !! 规则触发 [规则 2] 原因: cpu_temp=75(满足)
```

对应的执行痕迹在 `service-detail-*.log`：

```text
[10:45:19] [AUDIT] 设备命令 水冷 (100293cfd5) 通道0 -> on IP=192.168.1.34 结果=ok
```

> 带抖动带宽的规则**不会每次都打 `✓ 条件触发`**：触发后读数没越过解除线之前那条规则是"粘住"的，
> 最长 30 分钟的强制重判那一下也只重判、不补发动作。

---
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

## 许可与第三方组件

本项目自身代码采用 **MIT 许可证**，全文见仓库根目录的 [LICENSE](LICENSE)。

下面是本项目依赖/参考的第三方组件各自的许可（这些许可**不覆盖**本项目自身代码，但随二进制分发时需要一并保留其声明）：

| 组件 | 在项目里的作用 | 许可 |
| --- | --- | --- |
| `LibreHardwareMonitorLib` | 读 CPU/GPU 温度与使用率 | **MPL-2.0** |
| `HidSharp`（随上面那个包传递进来，发布目录里有 `HidSharp.dll`） | USB HID 设备访问 | **Apache-2.0** |
| `System.Management` | WMI（`MSAcpi_ThermalZoneTemperature` 读 CPU 温度） | MIT |
| `System.Diagnostics.PerformanceCounter` | CPU 使用率计数器 | MIT |
| `Microsoft.Extensions.Logging.Abstractions` | 日志抽象接口 | MIT |
| `System.ServiceProcess.ServiceController` | `System.ServiceProcess` 服务基类（`ServiceBase.Run`） | MIT |
| `System.IdentityModel.Tokens.Jwt` | **当前代码没有调用它**（eWeLink 的 access token 不是 JWT，按 JWT 解是错的），只是还挂在 `Core.csproj` 上、程序集仍随发布输出 | MIT |
| [.NET 运行时 / 基础库](https://dotnet.microsoft.com) | 编译与运行 | MIT |
| [AlexxIT/SonoffLAN](https://github.com/AlexxIT/SonoffLAN) | 登录流程与云端 API 协议的参考实现 | MIT |

> 上表许可标识的出处：直接依赖取自各自 NuGet 包 `.nuspec` 里的 `<license type="expression">`，
> `HidSharp` 取自包内 `LICENSE.txt` 原文。
>
> MPL-2.0 是**文件级**copyleft：以库的形式引用它不影响本项目用 MIT，只要没有修改它自己的源文件；
> 若日后改了它的文件，那些文件仍须保持 MPL-2.0 并提供源码。
>
> 这些许可**不覆盖**本项目自身代码，但分发发布产物（`publish/` 下那一堆 dll）时需要一并保留它们的版权声明。

---

## 致谢

本项目登录功能、云端 API 通信协议（HMAC-SHA256 签名、AppId/SignKey）参考了 [AlexxIT/SonoffLAN](https://github.com/AlexxIT/SonoffLAN) 开源项目。感谢原作者的工作。

SonoffLAN 使用 MIT 许可证，本项目的参考部分遵守该许可证的条款。

---
