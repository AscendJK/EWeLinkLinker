using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using EWeLinkLinker.Core.Cloud;
using EWeLinkLinker.Core.Config;
using EWeLinkLinker.Core.Lan;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Token;
using EWeLinkLinker.Core.Triggers;

namespace EWeLinkLinker.ConfigApp;

public partial class MainWindow : Window, IDisposable
{
    private readonly CloudClient _cloudClient;
    private readonly LanClient _lanClient;
    private readonly MdnsStatusClient _lanStatusClient = new();
    private readonly HttpClient _cloudHttpClient;
    private readonly HttpClient _lanHttpClient;
    private readonly string _configPath;
    private readonly string _logPath;
    private readonly TokenManager _tokenManager;

    private List<DeviceInfo> _allDevices = new();
    private ObservableCollection<LinkerRule> _rules = new();
    /// <summary>加载阶段出过错 ⇒ 内存里那份是残的，之后任何保存都不许覆盖磁盘。</summary>
    private bool _configLoadFailed;
    /// <summary>本次会话内由登录/自愈拿到的新 token：磁盘上那份是旧的，不能反过来覆盖内存。</summary>
    private bool _tokensRenewedThisSession;
    /// <summary>新 token 是否真的落盘成功（false ⇒ 界面不许说"已写入配置"）。</summary>
    private bool _tokensPersisted = true;
    /// <summary>控件绑定初始化阶段不许写盘（磁盘值不在下拉选项里时，回填会把它改掉）。</summary>
    private bool _settingsUiReady;
    private string _userApiKey = string.Empty;
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private DateTime? _tokenObtainedAtUtc;
    private bool _disposed;

    private bool _isLoggingIn;
    private bool _isRefreshing;
    private bool _hasAutoDiscovered;
    private DateTime _lastAutoReloginUtc = DateTime.MinValue;
    private static readonly TimeSpan AutoReloginCooldown = TimeSpan.FromMinutes(1);
    private readonly DispatcherTimer _serviceStatusTimer;

    /// <summary>
    /// 设备列表（供 UI 绑定）
    /// </summary>
    public ObservableCollection<DeviceInfo> Devices { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        // 设置 DataContext 为自身，方便绑定 Devices 属性
        DataContext = this;

        _cloudHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _lanHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _cloudClient = new CloudClient(_cloudHttpClient);
        _lanClient = new LanClient(_lanHttpClient);
        // 配置文件在上级目录的 config 文件夹（与服务端共享）
        var configDir = Path.Combine(AppContext.BaseDirectory, "..", "config");
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "linker.json");
        _logPath = Path.Combine(configDir, "debug.log");
        _tokenManager = new TokenManager(_cloudClient, _configPath);

        Core.Logging.SimpleLogger.Initialize(_logPath);
        Core.Logging.SimpleLogger.TrimLog();

        // 先设置 ItemsSource，再加载数据，这样 UI 才能接收到 ObservableCollection 的通知
        RulesItemsControl.ItemsSource = _rules;
        LoadConfig();

        // 强制刷新 UI
        Dispatcher.InvokeAsync(() =>
        {
            RulesItemsControl.Items.Refresh();
            Log("[UI] 已刷新规则列表");
        }, System.Windows.Threading.DispatcherPriority.Render);

        // Service status polling - 修复：使用安全的事件处理
        _serviceStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _serviceStatusTimer.Tick += OnServiceStatusTimerTick;

        Loaded += async (_, _) =>
        {
            await UpdateServiceStatusAsync();
            _serviceStatusTimer.Start();

            if (_hasAutoDiscovered) return;
            _hasAutoDiscovered = true;
            try { await AutoDiscoverIPsOnStartup(); }
            catch (Exception ex) { Debug.WriteLine($"Auto-discovery failed: {ex.Message}"); }
        };

        // LoadConfig 里给「日志」复选框和「轮询」下拉赋值会同步触发这两个 handler；
        // 从这行之后才算"他手动改的"，才允许写盘
        _settingsUiReady = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serviceStatusTimer.Stop();
        _tokenManager.Dispose();
        _cloudHttpClient.Dispose();
        _lanHttpClient.Dispose();
    }

    /// <summary>
    /// 安全的 Timer Tick 处理，防止 async void 异常
    /// </summary>
    private void OnServiceStatusTimerTick(object? sender, EventArgs e)
    {
        try
        {
            _ = UpdateServiceStatusAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Service status update failed: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭时自动保存配置
        // 关窗自动保存不能弹确认框（他可能只是关掉窗口），所以内存比磁盘少时直接跳过并记日志
        try { SaveConfig(interactive: false); } catch { }
        Dispose();
        base.OnClosed(e);
    }

    // ─── Config Load/Save ─────────────────────────────

    private void LoadConfig()
    {
        try
        {
            Log($"[加载] 开始加载配置，路径: {_configPath}");
            if (!File.Exists(_configPath))
            {
                Log("[加载] 配置文件不存在");
                return;
            }

            var config = LinkerConfig.Load(_configPath);
            Log($"[加载] 配置加载成功");

            AccountTextBox.Text = config.Account.Account;
            PasswordBox.Password = config.Account.Password;
            RegionComboBox.Text = config.Account.Region;
            _userApiKey = config.Tokens.UserApiKey;
            _accessToken = config.Tokens.AccessToken;
            _refreshToken = config.Tokens.RefreshToken;
            _tokenObtainedAtUtc = config.Tokens.TokenObtainedAtUtc;
            _allDevices = config.Devices;
            Devices.Clear();
            foreach (var d in _allDevices) Devices.Add(d);

            _rules.Clear();
            Log($"[加载] 从配置文件加载了 {config.Rules.Count} 条规则");
            foreach (var rule in config.Rules)
            {
                // 兼容旧格式：将 Event 转换为 Conditions
                if (rule.Conditions.Count == 0 && !string.IsNullOrEmpty(rule.Event))
                {
                    MigrateOldRule(rule);
                }

                // 确保 Conditions 和 Actions 是 ObservableCollection（JSON 反序列化后可能变成 List）
                var fixedRule = new LinkerRule
                {
                    Id = rule.Id,
                    Name = rule.Name,
                    Enabled = rule.Enabled,
                    Conditions = new ObservableCollection<RuleCondition>(rule.Conditions),
                    Actions = new ObservableCollection<LinkerAction>(rule.Actions)
                };

                Log($"[加载] 规则: {fixedRule.Name}, 条件数: {fixedRule.Conditions.Count}, 动作数: {fixedRule.Actions.Count}");
                foreach (var cond in fixedRule.Conditions)
                {
                    Log($"[加载]   条件: Type={cond.Type}, Param={cond.Parameter}");
                }
                foreach (var act in fixedRule.Actions)
                {
                    Log($"[加载]   动作: Device={act.DeviceId}, Name={act.Name}, State={act.State}");
                }
                _rules.Add(fixedRule);
            }
            Log($"[加载] 最终规则数: {_rules.Count}");

            RebuildDeviceCards();
        }
        catch (Exception ex)
        {
            _configLoadFailed = true;
            Log($"[加载] 错误: {ex.Message}");
            Log($"[加载] 堆栈: {ex.StackTrace}");
            MessageBox.Show(
                $"配置文件没有完整加载成功，界面上显示的规则和设备可能不全：\n\n{ex.Message}\n\n" +
                "在查明之前，本窗口会拒绝\"保存配置\"，也不会用内存这份覆盖磁盘那份。\n" +
                "请点「打开日志文件夹」看 debug.log 的 [加载] 段落。",
                "配置加载不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 迁移旧格式规则到新格式
    /// </summary>
    private static void MigrateOldRule(LinkerRule rule)
    {
        var eventType = rule.Event?.ToLower() ?? "";
        if (eventType == "boot" || eventType == "shutdown" || eventType == "sleep" || eventType == "wake")
        {
            rule.Conditions.Add(new RuleCondition
            {
                Type = eventType,
                Parameter = "",
                Operator = LogicalOperator.And
            });
        }
        else if (!string.IsNullOrEmpty(rule.TriggerConfig))
        {
            // 尝试解析 TriggerConfig
            try
            {
                var triggerConfig = TriggerConfig.FromJson(rule.TriggerConfig);
                if (triggerConfig != null)
                {
                    rule.Conditions.Add(new RuleCondition
                    {
                        Type = triggerConfig.Type,
                        Parameter = triggerConfig.Parameter,
                        Operator = LogicalOperator.And
                    });
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(rule.Name))
            rule.Name = eventType switch
            {
                "boot" => "开机联动",
                "shutdown" => "关机联动",
                "sleep" => "睡眠联动",
                "wake" => "唤醒联动",
                _ => "智能联动"
            };
    }

    private enum SaveOutcome { Saved, Unchanged, Aborted }

    private SaveOutcome SaveConfig(bool interactive = true)
    {
        try
        {
            var rulesList = _rules.ToList();

            // 保存前校验解除带宽：填了非数字/零/负数，或这个比较符根本不支持滞回，都会让
            // 派生出的解除线没意义；服务端构造触发器时会抛异常、这条规则静默失效，所以拦在写入之前。
            foreach (var rule in rulesList)
            {
                foreach (var cond in rule.Conditions)
                {
                    // 两种"界面说保存成功、规则其实已经死了"的情况必须拦在前面：
                    // ① 这个比较符在本类型里根本没实现（time 只有 =、≠、≥、<，其余分支恒不满足）；
                    // ② 参数建不出触发器（比如类型换成「应用启动」却还留着上一条的 08:00）——
                    //    服务端 TriggerManager 是"建不出来就跳过整条规则连同所有动作"。
                    string? problem = null;
                    if (cond.ComparisonUnsupported)
                    {
                        var cmpText = new ComparisonToDisplayConverter()
                            .Convert(cond.Comparison, typeof(string), string.Empty, System.Globalization.CultureInfo.InvariantCulture)
                            as string ?? cond.Comparison.ToString();
                        problem = $"「{cmpText}」这种比较方式，这类条件没有实现；选它规则就永远不执行（也不会报错）";
                    }
                    else if (!TriggerManager.IsPowerCondition(cond.Type)
                             && !TriggerRegistry.TryValidate(new TriggerConfig
                    {
                        Type = cond.Type,
                        Parameter = cond.Parameter,
                        Parameter2 = cond.Parameter2,
                        ReleaseBand = cond.ReleaseBand,
                        Comparison = cond.Comparison
                    }, out var buildError))
                    {
                        // 电源事件类（开机/关机/睡眠/唤醒）压根不在注册表里——那是服务的系统事件路径处理的，
                        // 拿注册表去问它必然回"Unknown trigger type"。上一版就这么把「规则 1」拦住了。
                        problem = buildError;
                    }

                    if (problem != null)
                    {
                        // 停用的规则不拦保存（他可能就是先写着半成品），但要留一行话，
                        // 免得哪天启用后到处找"为什么不执行"
                        if (!rule.Enabled)
                        {
                            Log($"[保存] 提醒：已停用的规则 [{rule.Name}] 条件「{cond.Type}」有问题 -> {problem}；启用后不会执行");
                        }
                        else
                        {
                            Log($"[保存] 中止：规则 [{rule.Name}] 条件「{cond.Type}」建不出触发器 -> {problem}");
                            MessageBox.Show($"规则「{rule.Name}」有一条条件服务端建不起来：\n\n" +
                                            $"类型：{cond.Type}　参数：{(string.IsNullOrEmpty(cond.Parameter) ? "(空)" : cond.Parameter)}\n" +
                                            $"原因：{problem}\n\n" +
                                            "这种条件在服务端会让整条规则（连同它所有动作）被直接跳过，所以本次没有保存。",
                                            "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return SaveOutcome.Aborted;
                        }
                    }

                    if (!cond.ShowRelease)
                    {
                        cond.ReleaseBand = ""; // 输入框已隐藏，残留值不写入配置
                        continue;
                    }
                    if (!ComparisonHelper.ValidateRelease(cond.Parameter, cond.ReleaseBand,
                                                          cond.Comparison, out var releaseError))
                    {
                        Log($"[保存] 中止：规则 [{rule.Name}] 解除带宽校验失败 -> {releaseError}");
                        MessageBox.Show($"规则「{rule.Name}」的抖动带宽有问题：\n\n{releaseError}",
                                        "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return SaveOutcome.Aborted;
                    }
                }

                // 通道号越界：模型允许 1-8 通道，老界面写死 CH0-CH3，越界的 outlet 存进去只会让
                // 服务端在设备上吃到错误，日志里就剩一条 fail
                foreach (var action in rule.Actions)
                {
                    var device = _allDevices.FirstOrDefault(d => d.DeviceId == action.DeviceId);
                    if (device == null) continue;   // 设备被删的情况交给 ConfigSafety/服务端处理
                    if (action.Outlet < 0 || action.Outlet >= device.ChannelCount)
                    {
                        var msg = $"动作指向「{device.Name}」的通道 {action.Outlet}，这台设备只有 {device.ChannelCount} 路（CH0–CH{device.ChannelCount - 1}）";
                        if (!rule.Enabled)
                        {
                            Log($"[保存] 提醒：已停用的规则 [{rule.Name}] {msg}；启用后这条会失败");
                        }
                        else
                        {
                            Log($"[保存] 中止：规则 [{rule.Name}] {msg}");
                            MessageBox.Show($"规则「{rule.Name}」有个动作的通道号超出这台设备的通道数：\n\n{msg}\n\n本次没有保存。",
                                            "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return SaveOutcome.Aborted;
                        }
                    }
                }
            }

            // 加载现有配置以保留 LoggingEnabled 等设置
            var existingConfig = LinkerConfig.Load(_configPath);

            // 保存是"整份重建再覆盖"，所以先问一句：内存里这份会不会比磁盘少（加载残缺、云端漏返回）
            var decision = ConfigSafety.Evaluate(_configLoadFailed, _rules.Count, existingConfig.Rules.Count,
                                                 _allDevices.Count, existingConfig.Devices.Count);
            if (decision == SaveDecision.RefuseLoadFailed)
            {
                Log($"[保存] 中止：配置加载出过错（内存 {_rules.Count} 条规则 / {_allDevices.Count} 台设备），拒绝覆盖磁盘上 {existingConfig.Rules.Count} 条 / {existingConfig.Devices.Count} 台");
                if (interactive) MessageBox.Show("配置文件之前没有完整加载成功，为避免把没加载出来的规则和设备写没，本次没有保存。\n\n" +
                                "请先看 debug.log 的 [加载] 段落再处理。",
                                "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return SaveOutcome.Aborted;
            }
            if (decision == SaveDecision.ConfirmShrink)
            {
                if (!interactive)
                {
                    Log($"[保存] 关窗自动保存被跳过：内存（{_rules.Count} 条规则 / {_allDevices.Count} 台设备）比磁盘（{existingConfig.Rules.Count} 条 / {existingConfig.Devices.Count} 台）少，不自动覆盖");
                    return SaveOutcome.Aborted;
                }
                var overwrite = MessageBox.Show(
                    $"内存里现在是 {_rules.Count} 条规则、{_allDevices.Count} 台设备，磁盘上是 {existingConfig.Rules.Count} 条、{existingConfig.Devices.Count} 台。\n\n" +
                    "保存会用内存这份整个覆盖磁盘那份。如果这些不是你自己删掉的，选【否】，先去看 debug.log。",
                    "覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (overwrite != MessageBoxResult.Yes)
                {
                    Log("[保存] 中止：他选了不覆盖（内存条目比磁盘少）");
                    return SaveOutcome.Aborted;
                }
            }

            // 磁盘上有解不开的凭据（DPAPI 换机/重装/手改），而内存里也还没有可以替换它的新凭据 ⇒ 不许覆盖。
            // 他重新填账号并「登录获取设备」之后，内存里就有了新值，这条自然放行。
            if (LinkerConfig.HasUndecryptableProtectedField(_configPath)
                && string.IsNullOrEmpty(_accessToken) && string.IsNullOrEmpty(PasswordBox.Password))
            {
                Log("[保存] 中止：磁盘上有解不开的凭据字段，而内存里没有可替换的新凭据");
                if (interactive)
                    MessageBox.Show("配置文件里的账号凭据在这台机器上解不开（通常是配置从别的机器拷来，或系统重装过）。\n\n" +
                                    "为避免把唯一还存在的那份凭据抹掉，本次没有保存。\n" +
                                    "请在「账号/密码」里重新填写，再点「登录获取设备」拿到新凭据。",
                                    "凭据无法解密", MessageBoxButton.OK, MessageBoxImage.Warning);
                return SaveOutcome.Aborted;
            }

            // 防止回写覆盖：如果磁盘 token 非空且与内存不同，说明被服务端 TokenManager 刷新过
            // 优先用磁盘 token（服务端写入的新 token）
            // 例外：本次会话刚登录/自愈拿到的是**更新**的 token，磁盘那份才是旧的，不能反过来覆盖
            var accessToken = _accessToken;
            var refreshToken = _refreshToken;
            var userApiKey = _userApiKey;
            var tokenObtainedAtUtc = _tokenObtainedAtUtc;
            if (!_tokensRenewedThisSession
                && !string.IsNullOrEmpty(existingConfig.Tokens.AccessToken)
                && existingConfig.Tokens.AccessToken != _accessToken)
            {
                accessToken = existingConfig.Tokens.AccessToken;
                refreshToken = existingConfig.Tokens.RefreshToken;
                userApiKey = existingConfig.Tokens.UserApiKey;
                tokenObtainedAtUtc = existingConfig.Tokens.TokenObtainedAtUtc;
                Log("[保存] 检测到服务端已刷新 token，使用磁盘版本");
            }

            var config = new LinkerConfig
            {
                Account = new AccountConfig
                {
                    Account = AccountTextBox.Text,
                    Password = PasswordBox.Password,
                    CountryCode = GetCountryCodeForRegion(RegionComboBox.Text),
                    Region = RegionComboBox.Text
                },
                Tokens = new TokenConfig
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    UserApiKey = userApiKey,
                    TokenObtainedAtUtc = tokenObtainedAtUtc
                },
                Devices = _allDevices,
                Rules = rulesList,
                LoggingEnabled = existingConfig.LoggingEnabled,  // 保留日志开关设置
                PollingIntervalSeconds = existingConfig.PollingIntervalSeconds  // 保留轮询间隔设置
            };

            // 详细调试日志
            Log($"[保存] 开始保存配置");
            Log($"[保存] 设备数量: {_allDevices.Count}");
            Log($"[保存] 规则数量: {rulesList.Count}");
            foreach (var rule in rulesList)
            {
                Log($"[保存] 规则: {rule.Name}, ID={rule.Id}, Enabled={rule.Enabled}");
                Log($"[保存]   条件数: {rule.Conditions.Count}");
                foreach (var cond in rule.Conditions)
                {
                    Log($"[保存]     条件: Type={cond.Type}, Param={cond.Parameter}, Op={cond.Operator}");
                }
                Log($"[保存]   动作数: {rule.Actions.Count}");
                foreach (var act in rule.Actions)
                {
                    Log($"[保存]     动作: Device={act.DeviceId}, Name={act.Name}, State={act.State}, Outlet={act.Outlet}");
                }
            }

            // 内容一字不差时不重写文件：启动时的自动 IP 发现和关窗保存都属于"没改任何东西"，
            // 以前每次都会把真配置整个替换一遍，让 mtime 和文件内容变成无法信任的信号。
            if (config.MatchesFile(_configPath))
            {
                Log("[保存] 内容与磁盘完全一致，跳过写入（配置文件未被改动）");
                return SaveOutcome.Unchanged;
            }

            if (!config.Save(_configPath))
            {
                // Save 内部吞异常只返回 false（文件被占用/只读/磁盘满），不查返回值就会把失败报成"已保存"
                Log("[保存] 失败：LinkerConfig.Save 返回 false（文件可能被占用或只读）");
                if (interactive) MessageBox.Show("写入配置文件失败：文件可能正被占用或设为只读。\n\n本次改动没有保存，请先关掉占用该文件的程序（或服务）再试。",
                                "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return SaveOutcome.Aborted;
            }
            Log($"[保存] 配置已保存到: {_configPath}");

            // 验证保存的文件
            if (File.Exists(_configPath))
            {
                var savedJson = File.ReadAllText(_configPath);
                Log($"[保存] 文件大小: {savedJson.Length} 字符");
            }
            return SaveOutcome.Saved;
        }
        catch (Exception ex)
        {
            Log($"[保存] 错误: {ex.Message}");
            Log($"[保存] 堆栈: {ex.StackTrace}");
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return SaveOutcome.Aborted;
        }
    }

    /// <summary>
    /// Bug 修复：仅保存 Token 和账户信息（用于登录后获取云端设备前）
    /// 优先使用内存中的 _allDevices（含用户刚编辑的 RealMacAddress），磁盘作为后备
    /// </summary>
    private bool SaveTokensOnly()
    {
        try
        {
            var existingConfig = LinkerConfig.Load(_configPath);

            // 注意：此方法仅在登录成功后调用，内存 token 必定是最新的。
            // 不添加 token 回写防护——否则登录后的新 token 会被磁盘旧 token 覆盖。

            // 优先使用内存中的设备列表（用户可能在 UI 上刚编辑过 RealMacAddress）
            var devicesToSave = _allDevices.Count > 0
                ? _allDevices
                : existingConfig.Devices;

            var config = new LinkerConfig
            {
                Account = new AccountConfig
                {
                    Account = AccountTextBox.Text,
                    Password = PasswordBox.Password,
                    CountryCode = GetCountryCodeForRegion(RegionComboBox.Text),
                    Region = RegionComboBox.Text
                },
                Tokens = new TokenConfig
                {
                    AccessToken = _accessToken,
                    RefreshToken = _refreshToken,
                    UserApiKey = _userApiKey,
                    TokenObtainedAtUtc = _tokenObtainedAtUtc
                },
                Devices = devicesToSave,
                Rules = existingConfig.Rules,  // M-6 修复：登录时保留旧规则，不覆盖
                LoggingEnabled = existingConfig.LoggingEnabled,
                PollingIntervalSeconds = existingConfig.PollingIntervalSeconds  // 保留轮询间隔，登录不该把它打回 5s
            };
            if (!config.Save(_configPath))
            {
                Log("[登录] Token 写入失败：LinkerConfig.Save 返回 false");
                _tokensPersisted = false;
                return false;
            }
            Log("[登录] Token 已保存");
            _tokensPersisted = true;
            return true;
        }
        catch (Exception ex)
        {
            Log($"[登录] 保存 Token 失败: {ex.Message}");
            _tokensPersisted = false;
            return false;
        }
    }

    /// <summary>
    /// Bug 修复：将用户编辑的设备信息合并到云端设备列表中
    /// 优先从内存 _allDevices 获取（用户可能在 UI 上刚编辑过），磁盘配置作为后备
    /// </summary>
    private void MergeDeviceMacAddresses(List<DeviceInfo> cloudDevices)
    {
        try
        {
            // 优先从内存获取旧设备（用户可能在 UI 上刚编辑过 RealMacAddress）
            var memoryDevices = _allDevices;

            // 从磁盘加载后备数据
            var diskDevices = LinkerConfig.Load(_configPath).Devices;

            foreach (var cloudDevice in cloudDevices)
            {
                // 先查内存，再查磁盘
                var oldDevice = memoryDevices.FirstOrDefault(d => d.DeviceId == cloudDevice.DeviceId)
                             ?? diskDevices.FirstOrDefault(d => d.DeviceId == cloudDevice.DeviceId);

                if (oldDevice != null)
                {
                    // 保留用户输入的真实 MAC 地址
                    if (!string.IsNullOrEmpty(oldDevice.RealMacAddress))
                    {
                        cloudDevice.RealMacAddress = oldDevice.RealMacAddress;
                        Log($"[合并] 设备 {cloudDevice.Name}: RealMac={oldDevice.RealMacAddress}");
                    }

                    // 保留已有的 IP 地址（如果云端没有返回新 IP）
                    if (!string.IsNullOrEmpty(oldDevice.IpAddress) &&
                        string.IsNullOrEmpty(cloudDevice.IpAddress))
                    {
                        cloudDevice.IpAddress = oldDevice.IpAddress;
                    }

                    // 保留用户可能修改过的 DeviceKey
                    if (!string.IsNullOrEmpty(oldDevice.DeviceKey))
                    {
                        cloudDevice.DeviceKey = oldDevice.DeviceKey;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[合并] 合并 MAC 地址失败: {ex.Message}");
        }
    }

    // ─── Device Cards ──────────────────────────────────

    private void RebuildDeviceCards()
    {
        DeviceCardsPanel.Children.Clear();
        foreach (var device in _allDevices)
        {
            DeviceCardsPanel.Children.Add(BuildDeviceCard(device));
        }
        DeviceCountText.Text = _allDevices.Count > 0 ? $"({_allDevices.Count} 台设备)" : string.Empty;
    }

    /// <summary>
    /// Bug 修复：保存所有规则动作的 DeviceId（在 Devices 集合清空前调用）
    /// 避免 ComboBox 的 TwoWay 绑定在 ItemsSource 清空时把 null 写回 DeviceId
    /// </summary>
    private Dictionary<string, string> SaveActionDeviceIds()
    {
        var dict = new Dictionary<string, string>();  // key = ruleId+actionIndex, value = DeviceId
        foreach (var rule in _rules)
        {
            for (int i = 0; i < rule.Actions.Count; i++)
            {
                var action = rule.Actions[i];
                string key = $"{rule.Id}_{i}";
                dict[key] = action.DeviceId;  // 保存原始值
            }
        }
        return dict;
    }

    /// <summary>
    /// Bug 修复：恢复所有规则动作的 DeviceId（在 Devices 集合重新填充后调用）
    /// </summary>
    private void RestoreActionDeviceIds(Dictionary<string, string> savedDeviceIds)
    {
        if (savedDeviceIds == null) return;

        foreach (var rule in _rules)
        {
            for (int i = 0; i < rule.Actions.Count; i++)
            {
                var action = rule.Actions[i];
                string key = $"{rule.Id}_{i}";

                if (savedDeviceIds.TryGetValue(key, out var savedDeviceId))
                {
                    // 检查保存的 DeviceId 是否在新的设备列表中存在
                    var deviceExists = _allDevices.Any(d => d.DeviceId == savedDeviceId);
                    if (deviceExists)
                    {
                        action.DeviceId = savedDeviceId;
                        action.Name = _allDevices.First(d => d.DeviceId == savedDeviceId).Name;
                    }
                    else
                    {
                        Log($"[警告] 恢复 DeviceId 失败：设备 {savedDeviceId} 不再存在于设备列表中");
                    }
                }
            }
        }
    }

    private Border BuildDeviceCard(DeviceInfo device)
    {
        var card = new Border { Style = (Style)FindResource("DeviceCardStyle") };
        var panel = new StackPanel();

        // Header: name + online status
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var nameText = new TextBlock
        {
            Text = device.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        };
        DockPanel.SetDock(nameText, Dock.Left);
        header.Children.Add(nameText);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = device.IsOnline ? (Brush)FindResource("StatusOnlineBrush") : (Brush)FindResource("StatusOfflineBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(dot, Dock.Right);
        header.Children.Add(dot);
        panel.Children.Add(header);

        // IP
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(device.IpAddress) ? "(未连接)" : device.IpAddress,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        // Channel buttons
        var channelPanel = new WrapPanel();
        for (int i = 0; i < device.ChannelCount; i++)
        {
            var outlet = i;
            var isOn = i < device.ChannelStates.Count && device.ChannelStates[i] == "on";
            var btn = new ToggleButton
            {
                Content = $"通道{outlet}",
                Style = (Style)FindResource("ChannelButton"),
                IsChecked = isOn,
                Tag = (Device: device, Outlet: outlet)
            };
            btn.Click += async (s, e) => await ToggleChannel(device, outlet, btn);
            channelPanel.Children.Add(btn);
        }
        panel.Children.Add(channelPanel);

        // MAC 地址区域
        var macPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        // 云端 MAC（只读）
        var cloudMacPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        cloudMacPanel.Children.Add(new TextBlock
        {
            Text = "云端MAC:",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 55
        });
        cloudMacPanel.Children.Add(new TextBlock
        {
            Text = device.CloudMacDisplay,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        macPanel.Children.Add(cloudMacPanel);

        // 真实 MAC（可编辑）
        var realMacPanel = new DockPanel();
        realMacPanel.Children.Add(new TextBlock
        {
            Text = "真实MAC:",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 55
        });
        var macTextBox = new TextBox
        {
            Text = device.RealMacAddress,
            FontSize = 11,
            Style = (Style)FindResource("ModernTextBox"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        macTextBox.LostFocus += (s, e) =>
        {
            // 自动格式化 MAC 地址
            var formatted = DeviceInfo.AutoFormatMac(macTextBox.Text);
            device.RealMacAddress = formatted;
            macTextBox.Text = formatted;
        };
        realMacPanel.Children.Add(macTextBox);
        macPanel.Children.Add(realMacPanel);

        panel.Children.Add(macPanel);

        card.Child = panel;
        return card;
    }

    private async Task ToggleChannel(DeviceInfo device, int outlet, ToggleButton btn)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        btn.IsEnabled = false;

        bool originalState = btn.IsChecked == true;
        try
        {
            bool turnOn = originalState;
            var success = await _lanClient.SetPowerWithRetryAsync(device, turnOn, outlet);
            if (success && outlet < device.ChannelStates.Count)
            {
                device.ChannelStates[outlet] = turnOn ? "on" : "off";
            }
            else if (!success)
            {
                // 修复：失败时恢复按钮状态
                btn.IsChecked = !turnOn;
                Log($"控制失败: {device.Name} 通道{outlet}");
            }
        }
        catch (Exception ex)
        {
            // 异常时也恢复按钮状态
            btn.IsChecked = !originalState;
            Log($"控制异常: {device.Name} 通道{outlet} - {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            btn.IsEnabled = true;
        }
    }

    // ─── Rules CRUD ────────────────────────────────────

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = new LinkerRule
        {
            Name = $"规则 {_rules.Count + 1}",
            Conditions = new ObservableCollection<RuleCondition> { new() { Type = "time", Parameter = "08:00", Operator = LogicalOperator.And } },
            Actions = new ObservableCollection<LinkerAction>()
        };
        _rules.Add(rule);
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is LinkerRule rule)
        {
            _rules.Remove(rule);
        }
    }

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        Log($"[按钮] +条件 被点击 sender={sender?.GetType().Name}");
        if (sender is Button btn)
        {
            Log($"  btn.DataContext={btn.DataContext?.GetType().Name} value={btn.DataContext}");
            if (btn.DataContext is LinkerRule rule)
            {
                rule.Conditions.Add(new RuleCondition { Type = "time", Parameter = "08:00", Comparison = ComparisonOperator.Eq, Operator = LogicalOperator.And });
                Log($"  成功添加条件，当前数量: {rule.Conditions.Count}");
            }
            else
            {
                Log($"  !! 错误: DataContext 不是 LinkerRule");
            }
        }
        else
        {
            Log($"  !! 错误: sender 不是 Button");
        }
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        Log($"[按钮] +动作 被点击 sender={sender?.GetType().Name}");
        if (sender is Button btn)
        {
            Log($"  btn.DataContext={btn.DataContext?.GetType().Name} value={btn.DataContext}");
            if (btn.DataContext is LinkerRule rule)
            {
                if (_allDevices.Count == 0)
                {
                    MessageBox.Show("没有可用设备", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                rule.Actions.Add(new LinkerAction { DeviceId = _allDevices[0].DeviceId, Name = _allDevices[0].Name, State = "on", Outlet = 0 });
                Log($"  成功添加动作，当前数量: {rule.Actions.Count}");
            }
            else
            {
                Log($"  !! 错误: DataContext 不是 LinkerRule");
            }
        }
        else
        {
            Log($"  !! 错误: sender 不是 Button");
        }
    }

    /// <summary>
    /// 条件类型切换时重置默认比较符：
    /// time -> Eq（固定时刻窗口）；数值类（cpu_temp/cpu_usage/gpu_temp）-> Gte。
    /// 仅当"确实发生了选择变化"（RemovedItems 含旧值）时应用，避免初始化绑定误改。
    /// </summary>
    private void ConditionTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0) return; // 初始化绑定/无旧值时不处理

        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (combo.DataContext is RuleCondition condition)
            {
                condition.Type = tag;

                // 比较符必须落在本类型真实现的集合里：time 只实现了 Eq/Neq/Gte/Lt，
                // 其余分支在 TimeTrigger 里是 `_ => false`，留着就等于一条永不执行还不报错的规则。
                if (!RuleCondition.SupportedComparisons(tag).Contains(condition.Comparison))
                {
                    condition.Comparison = tag == "time" ? ComparisonOperator.Eq : ComparisonOperator.Gte;
                }
                else if (tag == "time" && condition.Comparison == ComparisonOperator.Gte)
                {
                    // 数值型习惯用「大于等于」，换成固定时刻时给成「等于」更符合预期
                    condition.Comparison = ComparisonOperator.Eq;
                }

                // 切换后若已不适用滞回（如换成 time/进程/电源），清掉残留带宽
                if (!condition.ShowRelease)
                    condition.ReleaseBand = "";

                // 参数也要跟着类型走。残留上一个类型的参数（比如把「时间」改成「应用启动」却还留着 08:00）
                // 会让服务端建不出触发器，而 TriggerManager 的处理是**跳过整条规则连同它所有动作**，
                // 界面这边却已经弹了「配置已保存！」——用户完全看不出规则已经死了。
                var oldParameter = condition.Parameter;
                var oldType = (e.RemovedItems[0] as ComboBoxItem)?.Tag as string ?? "?";
                string Show(string v) => string.IsNullOrEmpty(v) ? "(空)" : v;

                if (TriggerManager.IsPowerCondition(tag))
                {
                    // 电源事件类压根不需要参数（注册表里也没有这四类，那是服务的系统事件路径）。
                    // 参数输入框已经藏起来了，留着上一条的残值就是界面上看不见、配置文件里却有的脏值。
                    condition.Parameter = "";
                    condition.Parameter2 = "";
                    if (!string.IsNullOrEmpty(oldParameter))
                        Log($"[条件] 类型 {oldType} → {tag}：这类条件不用参数，原来的「{Show(oldParameter)}」已清空");
                }
                else if (!TriggerRegistry.TryValidate(new TriggerConfig
                {
                    Type = tag,
                    Parameter = condition.Parameter,
                    Parameter2 = condition.Parameter2,
                    ReleaseBand = condition.ReleaseBand,
                    Comparison = condition.Comparison
                }, out _))
                {
                    condition.Parameter = condition.DefaultParameter;
                    Log($"[条件] 类型 {oldType} → {tag}：原参数「{Show(oldParameter)}」对新类型不可用，已重置为「{Show(condition.Parameter)}」");
                }
            }
        }
    }

    /// <summary>
    /// 比较符切换：改成 Eq/Neq/Range/OutsideRange 后解除线不再适用，清掉残留值，
    /// 避免把用户看不见的值带进保存与服务端校验。
    /// 用 AddedItems 里的新值判断而不是 condition.Comparison，因为绑定回写可能还没发生。
    /// </summary>
    private void ComparisonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0) return; // 初始化绑定不处理
        if (sender is not ComboBox { DataContext: RuleCondition condition }) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ComparisonOperator newComparison) return;

        if (!RuleCondition.SupportsRelease(condition.Type, newComparison))
            condition.ReleaseBand = "";
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is RuleCondition condition)
        {
            foreach (var rule in _rules)
            {
                if (rule.Conditions.Remove(condition))
                {
                    Log($"删除条件: {condition.Type}, 剩余: {rule.Conditions.Count}");
                    return;
                }
            }
        }
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is LinkerAction action)
        {
            foreach (var rule in _rules)
            {
                if (rule.Actions.Remove(action))
                {
                    Log($"删除动作: {action.Name}, 剩余: {rule.Actions.Count}");
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 通道候选按设备通道数生成，换设备时 ItemsSource 整个换掉，WPF 会趁这一瞬把 SelectedIndex 设成 -1。
    /// 模型那边 OutletIndex 已经拒绝这个中间态，这里再把选中项指回原来那条，界面不凭空变空白。
    /// </summary>
    private void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: < 0 } combo) return;
        if (combo.DataContext is LinkerAction action && action.Outlet >= 0)
            combo.SelectedIndex = action.Outlet;
    }

    /// <summary>
    /// 动作行换设备：名字跟着换（否则配置里留着上一台设备的名），通道号按新设备的通道数收回来。
    /// 不主动收的话候选变短，WPF 会把选中项夹成 CH0 —— 用户没碰过通道，通道号却悄悄改了，
    /// 日志里也看不出是谁改的。收回来是显式行为，并且留一行日志。
    /// </summary>
    private void ActionDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0) return;          // 初始化绑定，不是他改的
        if (e.AddedItems[0] is not DeviceInfo device) return;
        if (sender is not ComboBox { DataContext: LinkerAction action }) return;

        action.Name = device.Name;
        if (action.Outlet >= device.ChannelCount)
        {
            Log($"[动作] 设备换成「{device.Name}」（{device.ChannelCount} 路），原来的通道 {action.Outlet} 在这台设备上不存在，已收回 CH0");
            action.Outlet = 0;
        }
    }

    private void AppBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is RuleCondition condition)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择应用程序",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                condition.Parameter = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                Log($"选择应用: {condition.Parameter}");
            }
        }
    }

    /// <summary>
    /// 时间选择器值变化时更新 Parameter
    /// </summary>
    private void ConditionTimePicker_SelectedTimeChanged(object sender, EventArgs e)
    {
        if (sender is TimePicker picker && picker.DataContext is RuleCondition condition)
        {
            condition.Parameter = picker.SelectedTime;
            Log($"时间条件更新: {condition.Parameter}");
        }
    }

    /// <summary>
    /// 数字输入框失去焦点时更新 Parameter
    /// </summary>
    private void NumericTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is RuleCondition condition)
        {
            // 尝试解析数字（按不变文化：跟着系统区域走的话 "2,5" 会被当成千分位读成 25）
            if (int.TryParse(textBox.Text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                condition.Parameter = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Log($"数值条件更新: {condition.Parameter}");
            }
            else
            {
                Log($"[输入] \"{textBox.Text}\" 不是整数，这一条没写进规则（{ComparisonHelper.NumberFormatHint(textBox.Text)}）");
            }
        }
    }

    /// <summary>
    /// 测试触发条件是否满足（使用与服务端相同的 PollAsync）
    /// </summary>
    private async void TestCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RuleCondition condition)
        {
            // 电源事件无法手动测试，显示提示信息
            if (IsPowerEventType(condition.Type))
            {
                MessageBox.Show(
                    "电源事件（开机/关机/睡眠/唤醒）无法手动测试。\n\n" +
                    "这些事件由 Windows 系统触发，将在实际事件发生时自动执行规则。\n\n" +
                    "如需测试规则配置，请使用其他条件类型（如时间、CPU温度等）。",
                    "无法测试",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ITrigger? trigger = null;
            try
            {
                var config = new EWeLinkLinker.Core.Triggers.TriggerConfig
                {
                    Type = condition.Type,
                    Parameter = condition.Parameter,
                    Parameter2 = condition.Parameter2,
                    ReleaseBand = condition.ReleaseBand,
                    Comparison = condition.Comparison
                };

                trigger = Core.Triggers.TriggerRegistry.Create(config);
                trigger.Start(); // 和服务端一样先 Start

                // 使用与服务端完全相同的 PollAsync
                var triggered = await trigger.PollAsync(CancellationToken.None);

                // 获取详细状态信息（异步，避免 UI 卡顿）
                string stateInfo = await GetTriggerStateInfoAsync(trigger, condition, triggered);

                MessageBox.Show(stateInfo, "测试结果", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"测试失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                trigger?.Dispose();
            }
        }
    }

    /// <summary>
    /// 判断是否为电源事件类型（无法手动测试）
    /// </summary>
    // 判据用服务端那一份：界面这里原来另抄了一份四类名单，两处清单各写各的正是"保存被误拦"这类错的来路
    private static bool IsPowerEventType(string type) => TriggerManager.IsPowerCondition(type);

    /// <summary>
    /// 获取触发器的当前状态信息（用于测试显示）
    /// </summary>
    private static async Task<string> GetTriggerStateInfoAsync(EWeLinkLinker.Core.Triggers.ITrigger trigger, RuleCondition condition, bool triggered)
    {
        var type = condition.Type;
        var param = condition.Parameter;
        var comparison = condition.Comparison;

        // 显示比较运算符
        var comparisonText = comparison switch
        {
            ComparisonOperator.Gte => "≥",
            ComparisonOperator.Gt => ">",
            ComparisonOperator.Lte => "≤",
            ComparisonOperator.Lt => "<",
            ComparisonOperator.Eq => "=",
            ComparisonOperator.Neq => "≠",
            ComparisonOperator.Range => "范围",
            _ => ""
        };

        var triggerResult = triggered ? "✓ 会触发" : "✗ 不会触发";
        var stateInfo = $"触发结果: {triggerResult}\n当前状态: {trigger.State}\n\n";

        return type switch
        {
            "cpu_temp" => stateInfo + GetCpuTempInfo(param, comparisonText),
            "cpu_usage" => stateInfo + await GetCpuUsageInfoAsync(param, comparisonText),
            "gpu_temp" => stateInfo + GetGpuTempInfo(param, comparisonText),
            "time" => stateInfo + GetTimeInfo(param),
            "app_start" or "app_close" => stateInfo + GetAppInfo(param),
            _ => stateInfo + $"类型: {type}\n参数: {param}\n触发器类型: {trigger.DisplayName}"
        };
    }

    private static string GetCpuTempInfo(string thresholdStr, string comparisonText)
    {
        try
        {
            if (!ComparisonHelper.TryParseNumber(thresholdStr, out var thresholdValue))
                return $"阈值 \"{thresholdStr}\" 不是数字{ComparisonHelper.NumberFormatHint(thresholdStr)}";

            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");

            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var tempK = Convert.ToUInt32(obj["CurrentTemperature"]);
                    var tempC = (tempK - 2732) / 10.0f;
                    if (tempC > 0 && tempC < 150)
                    {
                        var status = tempC >= thresholdValue ? "✓ 超过阈值" : "✗ 未超过";
                        return $"CPU 温度: {tempC:F1}°C\n阈值: {comparisonText} {thresholdValue}°C\n状态: {status}";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return $"CPU 温度读取失败: {ex.Message}\n请确保以管理员身份运行";
        }
        return "CPU 温度: 无法读取（WMI 不可用）";
    }

    private static string GetGpuTempInfo(string thresholdStr, string comparisonText)
    {
        LibreHardwareMonitor.Hardware.Computer? computer = null;
        try
        {
            if (!ComparisonHelper.TryParseNumber(thresholdStr, out var thresholdValue))
                return $"阈值 \"{thresholdStr}\" 不是数字{ComparisonHelper.NumberFormatHint(thresholdStr)}";

            computer = new LibreHardwareMonitor.Hardware.Computer
            {
                IsGpuEnabled = true
            };
            computer.Open();

            foreach (var hardware in computer.Hardware)
            {
                if (hardware.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia
                    || hardware.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuAmd
                    || hardware.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.GpuIntel)
                {
                    hardware.Update();

                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && sensor.Value.HasValue)
                        {
                            var temp = sensor.Value.Value;
                            var status = temp >= thresholdValue ? "✓ 超过阈值" : "✗ 未超过";
                            return $"GPU: {hardware.Name}\nGPU 温度: {temp:F1}°C\n阈值: {comparisonText} {thresholdValue}°C\n状态: {status}";
                        }
                    }
                }
            }

            return "未检测到支持的 GPU";
        }
        catch (Exception ex)
        {
            return $"GPU 温度读取失败: {ex.Message}";
        }
        finally
        {
            computer?.Close();
        }
    }

    private static async Task<string> GetCpuUsageInfoAsync(string thresholdStr, string comparisonText)
    {
        if (!ComparisonHelper.TryParseNumber(thresholdStr, out var thresholdValue))
            return $"阈值 \"{thresholdStr}\" 不是数字{ComparisonHelper.NumberFormatHint(thresholdStr)}";
        try
        {
            // 在后台线程执行，避免 UI 卡顿（采样约 900ms）
            var usage = await Task.Run(() => CpuUsageHelper.GetCpuUsage(sampleCount: 3, sampleIntervalMs: 300));

            var status = usage >= thresholdValue ? "✓ 超过阈值" : "✗ 未超过";
            return $"CPU 使用率: {usage:F1}%\n阈值: {comparisonText} {thresholdValue}%\n状态: {status}";
        }
        catch (Exception ex)
        {
            return $"CPU 使用率读取失败: {ex.Message}";
        }
    }

    private static string GetTimeInfo(string targetTime)
    {
        var now = DateTime.Now;
        if (TimeSpan.TryParse(targetTime, System.Globalization.CultureInfo.InvariantCulture, out var target))
        {
            var todayTarget = now.Date + target;
            var diff = now - todayTarget;
            string status;
            if (Math.Abs(diff.TotalSeconds) <= 30)
                status = "✓ 在触发窗口内";
            else if (diff < TimeSpan.Zero)
                status = $"✗ 还有 {diff.Negate().TotalMinutes:F0} 分钟";
            else
                status = $"✗ 已过 {diff.TotalMinutes:F0} 分钟";
            return $"目标时间: {targetTime}\n当前时间: {now:HH:mm:ss}\n状态: {status}";
        }
        return $"目标时间: {targetTime}\n格式无效";
    }

    private static string GetAppInfo(string processName)
    {
        var processes = System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase) ||
                       (p.MainWindowTitle?.Contains(processName, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (processes.Any())
        {
            var list = string.Join("\n", processes.Take(5).Select(p => $"  - {p.ProcessName} (PID: {p.Id})"));
            return $"进程名: {processName}\n运行中的进程:\n{list}";
        }
        return $"进程名: {processName}\n状态: 未运行";
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.DataContext is LinkerAction action)
        {
            if (combo.SelectedItem is DeviceInfo device)
            {
                action.DeviceId = device.DeviceId;
                action.Name = device.Name;
                Log($"选择设备: {device.Name} ({device.DeviceId})");
            }
        }
    }

    private void Log(string message)
    {
        // 修复：使用 SimpleLogger 统一日志，避免与文件写入冲突
        Core.Logging.SimpleLogger.Log(message);
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        switch (SaveConfig())
        {
            case SaveOutcome.Saved:
                MessageBox.Show("配置已保存！", "保存配置", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case SaveOutcome.Unchanged:
                // 没改任何东西时不再假装"已保存"，也不碰文件
                MessageBox.Show("内容没有变化，配置文件未改动。", "保存配置", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }

    // ─── Service Control ────────────────────────────────

    private const string ServiceName = "EWeLinkLinker";

    private async void InstallService_Click(object sender, RoutedEventArgs e)
    {
        // 检查当前服务状态
        var status = await GetServiceStatusAsync();

        if (status == "RUNNING")
        {
            var reinstall = MessageBox.Show(
                "服务已安装且正在运行。是否重新安装？\n（会先停止并卸载旧服务，再安装新服务）",
                "重新安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reinstall != MessageBoxResult.Yes) return;
        }
        else if (status == "STOPPED")
        {
            var reinstall = MessageBox.Show(
                "服务已安装但已停止。是否重新安装？\n（会先卸载旧服务，再安装新服务）",
                "重新安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reinstall != MessageBoxResult.Yes) return;
        }

        try
        {
            // 必须规范化：sc create 会把 binPath 原样记进注册表，留下 "ConfigApp\..\Service\..."
            // 这种路径，将来删掉 ConfigApp 目录服务就起不来，也和 install.ps1 写的干净路径互踩
            var exePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Service", "EWeLinkLinker.Service.exe"));
            if (!File.Exists(exePath))
            {
                MessageBox.Show($"找不到服务程序：\n\n{exePath}\n\n请先确认 publish\\Service 目录完整，再点安装。",
                                "安装服务", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1. 停止并删除旧服务
            if (status != "NOT_INSTALLED")
            {
                Log($"安装服务: 停止旧服务...");
                await RunScCommand($"stop {ServiceName}", true);
                await WaitForStatusAsync("STOPPED", 8000);
                Log($"安装服务: 删除旧服务...");
                var delCode = await RunScCommand($"delete {ServiceName}", true);
                var gone = await WaitForStatusAsync("NOT_INSTALLED", 5000);
                if (delCode != 0 || gone != "NOT_INSTALLED")
                {
                    // 旧服务还在，下一步 sc create 必然回 1056；与其弹一句看不懂的"创建失败"，
                    // 不如在这里把真实情况说清楚
                    MessageBox.Show($"旧服务没能删干净（sc delete 退出码 {delCode}，当前状态 {gone}）。\n" +
                                    "通常是有句柄还开着，等几秒再点一次「安装服务」。",
                                    "安装服务", MessageBoxButton.OK, MessageBoxImage.Warning);
                    await UpdateServiceStatusAsync();
                    return;
                }
            }

            // 2. 创建新服务
            Log($"安装服务: 创建服务，exe路径={exePath}");
            var createPsi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"create {ServiceName} binPath= \"{exePath}\" start= auto DisplayName= \"EWeLink Linker Service\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (var process = Process.Start(createPsi))
            {
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                    {
                        MessageBox.Show($"创建服务失败 (退出码: {process.ExitCode})。请以管理员身份运行程序。",
                            "安装服务", MessageBoxButton.OK, MessageBoxImage.Warning);
                        await UpdateServiceStatusAsync();
                        return;
                    }
                }
            }

            // 3. 设置描述（不影响能不能跑，失败只记一笔）
            var descCode = await RunScCommand($"description {ServiceName} \"Automatically controls eWeLink devices based on PC power events\"", true);

            // 4. 启动服务
            Log($"安装服务: 启动服务...");
            var startCode = await RunScCommand($"start {ServiceName}", true);
            var finalStatus = await WaitForStatusAsync("RUNNING", 10000);
            Log($"安装服务结果: start 退出码={startCode}，最终状态={finalStatus}，描述退出码={descCode}");

            if (finalStatus == "RUNNING")
                MessageBox.Show("服务已安装并正在运行。", "安装服务", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"服务已经创建，但**没有运行起来**。\nsc start 退出码：{startCode}\n当前状态：{finalStatus}\n\n" +
                                "可以到「打开日志文件夹」里看服务日志找原因。",
                                "部分完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("已取消安装（UAC 被拒绝）。需要管理员权限才能安装服务。",
                "安装服务", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"安装失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await UpdateServiceStatusAsync();
    }

    private async void ToggleService_Click(object sender, RoutedEventArgs e)
    {
        var status = await GetServiceStatusAsync();

        if (status == "NOT_INSTALLED")
        {
            MessageBox.Show("服务未安装。请先点击\"安装服务\"按钮。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (status == "RUNNING" || status == "PAUSED")
        {
            // 停止服务
            try
            {
                var code = await RunScCommand($"stop {ServiceName}");
                var now = await WaitForStatusAsync("STOPPED", 8000);
                Log($"停止服务: sc 退出码={code}，等待后状态={now}");
                if (now == "STOPPED")
                    MessageBox.Show("服务已停止。", "停止服务", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"停止命令没能把服务停下来。\nsc 退出码：{code}\n当前状态：{now}",
                                    "未停止", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"停止失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (status == "STOPPED")
        {
            // 启动服务
            try
            {
                var code = await RunScCommand($"start {ServiceName}");
                var now = await WaitForStatusAsync("RUNNING", 8000);
                Log($"启动服务: sc 退出码={code}，等待后状态={now}");
                if (now == "RUNNING")
                    MessageBox.Show("服务已运行。", "启动服务", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show($"服务没有起来。\nsc 退出码：{code}\n当前状态：{now}\n\n" +
                                    "常见原因是服务程序本身启动失败，请看服务日志。",
                                    "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show($"服务状态未知: {status}。请尝试重新安装服务。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await UpdateServiceStatusAsync();
    }

    private void LoggingCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        try
        {
            var want = LoggingCheckBox.IsChecked == true;
            if (!_settingsUiReady) return;
            var config = LinkerConfig.Load(_configPath);
            // 打开窗口时绑定初始化也会触发这个事件，磁盘值没变就不要重写配置
            if (config.LoggingEnabled == want) return;

            config.LoggingEnabled = want;
            if (!config.Save(_configPath))
            {
                Log("[设置] 日志开关写入失败：Save 返回 false");
                LoggingCheckBox.IsChecked = !want; // 盘上没改，界面上也不能留着改过的样子
                MessageBox.Show("写入配置文件失败：日志开关没有改成" + (want ? "启用" : "禁用") + "。文件可能被占用或只读。",
                                "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Log($"日志已{(want ? "启用" : "禁用")}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoggingCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        LoggingCheckBox_Checked(sender, e);
    }

    /// <summary>
    /// 轮询间隔选择改变
    /// </summary>
    private void PollingIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            int[] intervals = { 1, 2, 3, 5, 10, 15, 30 };
            if (!_settingsUiReady) return;
            int index = PollingIntervalCombo.SelectedIndex;
            if (index < 0 || index >= intervals.Length) return;

            int newInterval = intervals[index];
            var config = LinkerConfig.Load(_configPath);
            // 同样：加载配置填充下拉框时也会触发，磁盘上已经是这个值就不要重写
            if (config.PollingIntervalSeconds == newInterval) return;

            config.PollingIntervalSeconds = newInterval;
            if (!config.Save(_configPath))
            {
                Log("[设置] 轮询间隔写入失败：Save 返回 false");
                MessageBox.Show($"写入配置文件失败：轮询间隔没有改成 {newInterval} 秒。文件可能被占用或只读。",
                                "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 配置文件变更会触发服务端的 FileSystemWatcher 自动重载
            Log($"轮询间隔已改为 {newInterval} 秒");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存轮询间隔失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "..", "Service", "logs");
            if (Directory.Exists(logDir))
            {
                Process.Start("explorer.exe", logDir);
            }
            else
            {
                MessageBox.Show("日志文件夹不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志文件夹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveService_Click(object sender, RoutedEventArgs e)
    {
        var status = await GetServiceStatusAsync();

        if (status == "NOT_INSTALLED")
        {
            MessageBox.Show("服务未安装，无需卸载。",
                "卸载服务", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"确定要卸载 EWeLink Linker 服务吗？\n当前状态: {status}\n\n卸载后服务将不再自动运行。",
            "卸载服务", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            if (status == "RUNNING" || status == "PAUSED")
            {
                await RunScCommand($"stop {ServiceName}");
                await WaitForStatusAsync("STOPPED", 8000);
            }
            var delCode = await RunScCommand($"delete {ServiceName}");
            // sc delete 可能只是"标记删除"（1072），要等 SCM 关掉句柄才真没；查两秒
            var now = await WaitForStatusAsync("NOT_INSTALLED", 3000);
            Log($"卸载服务: sc delete 退出码={delCode}，等待后状态={now}");
            if (now == "NOT_INSTALLED")
                MessageBox.Show("服务已卸载。", "卸载服务", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"服务还没被删除干净。\nsc delete 退出码：{delCode}\n当前状态：{now}",
                                "未卸载", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"卸载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await UpdateServiceStatusAsync();
    }

    /// <summary>
    /// 获取服务状态字符串
    /// </summary>
    private async Task<string> GetServiceStatusAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {ServiceName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return "UNKNOWN";

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return "NOT_INSTALLED";

            // 解析状态
            if (output.Contains("RUNNING")) return "RUNNING";
            if (output.Contains("STOPPED")) return "STOPPED";
            if (output.Contains("PAUSED")) return "PAUSED";
            if (output.Contains("START_PENDING")) return "START_PENDING";
            if (output.Contains("STOP_PENDING")) return "STOP_PENDING";

            return "UNKNOWN";
        }
        catch
        {
            return "NOT_INSTALLED";
        }
    }

    /// <summary>
    /// 跑一条 sc.exe，把**退出码**交回调用方。以前它返回 Task 且非零码只在 suppressErrors=false 时弹窗，
    /// 调用方一律把"没抛异常"当成功，于是 sc start 失败也弹「服务启动成功！」、
    /// 安装流程末尾无条件弹「服务安装完成！」。失败与否现在由调用方按码＋真实服务状态判定。
    /// -1 表示进程根本没跑起来（含 UAC 被拒，那条仍会抛出）。
    /// </summary>
    private async Task<int> RunScCommand(string arguments, bool suppressErrors = false)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                Log($"sc.exe {arguments} -> 进程没起来（拿不到句柄）");
                return -1;
            }
            await process.WaitForExitAsync();
            Log($"sc.exe {arguments} -> 退出码 {process.ExitCode}");
            // 检查退出码：0=成功，其他=失败
            if (process.ExitCode != 0 && !suppressErrors)
            {
                var errorDetail = process.ExitCode switch
                {
                    1060 => "服务未安装",
                    1056 => "服务已存在",
                    1062 => "服务未启动",
                    1058 => "服务已禁用",
                    1072 => "服务标记为删除（等 SCM 关完句柄才会真删）",
                    _ => $"错误码 {process.ExitCode}"
                };
                MessageBox.Show($"sc.exe 操作失败: {errorDetail}\n命令: sc {arguments}", "服务控制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC 被拒绝。这台机器是静默放行的，所以不提"点『是』"这种根本不存在的步骤
            if (!suppressErrors)
                MessageBox.Show("这一步需要管理员权限，提权被取消或没通过。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            throw;
        }
        catch (Exception ex)
        {
            if (!suppressErrors)
                MessageBox.Show($"操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    /// <summary>
    /// sc start/stop 是"命令已受理"而不是"已经到位"，所以文案只能等真状态。
    /// 最多等 timeoutMs，期间每 400ms 查一次；返回最后看到的状态。
    /// </summary>
    private async Task<string> WaitForStatusAsync(string want, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var status = await GetServiceStatusAsync();
        while (status != want && Environment.TickCount64 < deadline)
        {
            await Task.Delay(400);
            status = await GetServiceStatusAsync();
        }
        return status;
    }

    private async Task UpdateServiceStatusAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query EWeLinkLinker",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                string statusText;
                Brush statusBrush;
                string toggleContent;
                bool toggleEnabled;
                bool installEnabled;
                bool removeEnabled;

                if (process.ExitCode != 0)
                {
                    // 未安装
                    statusText = "服务: 未安装";
                    statusBrush = (Brush)FindResource("StatusOfflineBrush");
                    toggleContent = "启动";
                    toggleEnabled = false;
                    installEnabled = true;
                    removeEnabled = false;
                }
                else if (output.Contains("RUNNING"))
                {
                    statusText = "服务: 运行中";
                    statusBrush = (Brush)FindResource("StatusOnlineBrush");
                    toggleContent = "停止";
                    toggleEnabled = true;
                    installEnabled = false;
                    removeEnabled = true;
                }
                else if (output.Contains("STOPPED"))
                {
                    statusText = "服务: 已停止";
                    statusBrush = (Brush)FindResource("StatusOfflineBrush");
                    toggleContent = "启动";
                    toggleEnabled = true;
                    installEnabled = false;
                    removeEnabled = true;
                }
                else
                {
                    statusText = "服务: 未知";
                    statusBrush = (Brush)FindResource("StatusOfflineBrush");
                    toggleContent = "启动";
                    toggleEnabled = false;
                    installEnabled = true;
                    removeEnabled = true;
                }

                ServiceStatusText.Text = statusText;
                ServiceStatusDot.Fill = statusBrush;
                ToggleServiceBtn.Content = toggleContent;
                ToggleServiceBtn.IsEnabled = toggleEnabled;
                InstallServiceBtn.IsEnabled = installEnabled;
                RemoveServiceBtn.IsEnabled = removeEnabled;
            }
        }
        catch
        {
            ServiceStatusText.Text = "服务: 检测失败";
            ServiceStatusDot.Fill = (Brush)FindResource("StatusOfflineBrush");
            ToggleServiceBtn.Content = "启动";
            ToggleServiceBtn.IsEnabled = false;
            InstallServiceBtn.IsEnabled = true;
            RemoveServiceBtn.IsEnabled = false;
        }

        // 同步日志开关状态和轮询间隔
        try
        {
            var config = LinkerConfig.Load(_configPath);
            LoggingCheckBox.IsChecked = config.LoggingEnabled;

            // 同步轮询间隔下拉框。盘上的合法值不一定在下拉那几个档里（手改 7 秒就行），
            // 以前这种情况被强制按成"5 秒"，SelectionChanged 立刻把 5 写回配置——
            // 你手改的值就在状态刷新的第一秒被悄悄改掉。现在认不出来就不动控件、也不写盘。
            int[] intervals = { 1, 2, 3, 5, 10, 15, 30 };
            int index = Array.IndexOf(intervals, config.PollingIntervalSeconds);
            if (index >= 0)
            {
                PollingIntervalCombo.SelectedIndex = index;
            }
            else if (PollingIntervalCombo.SelectedIndex != -1)
            {
                PollingIntervalCombo.SelectedIndex = -1;   // 空白＝"不在预设档里"，handler 对 -1 直接早退
                Log($"[设置] 盘上轮询间隔 {config.PollingIntervalSeconds}s 不在下拉预设档里，界面不改动它");
            }
        }
        catch (Exception ex)
        {
            // 这里以前是裸 catch{}：状态同步失败在 GUI 侧一个字都不留
            Log($"[设置] 同步日志开关/轮询间隔失败: {ex.Message}");
        }
    }

    // ─── Login ──────────────────────────────────────────

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoggingIn) return;
        _isLoggingIn = true;
        LoginButton.IsEnabled = false;

        try
        {
            var account = AccountTextBox.Text;
            var password = PasswordBox.Password;
            var region = RegionComboBox.Text;

            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("请输入账号和密码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _cloudClient.Region = region;
            var (tokens, _) = await _cloudClient.LoginAsync(account, password, GetCountryCodeForRegion(region));
            AdoptTokens(tokens.AccessToken, tokens.RefreshToken, tokens.UserApiKey, DateTime.UtcNow);

            // Bug 修复：先保存 Token（不保存设备列表），然后获取云端设备并合并旧 MAC 地址
            var tokensPersisted = SaveTokensOnly();

            // Bug 修复：在清空前保存所有动作的 DeviceId，避免 TwoWay 绑定被清空
            var savedActionDeviceIds = SaveActionDeviceIds();

            var devices = await _cloudClient.GetDevicesAsync(tokens.AccessToken);

            // 云端偶发返回空表**或只返回一部分**（分页截断、设备被移到别的空间、账号侧异常）。
            // 整表替换会把被漏掉那台在规则里的动作 DeviceId 写成 null 并落盘，事后无法自愈，
            // 所以本地已有而云端没给的设备一律保留。
            var (mergedDevices, keptLocalOnly) = ConfigSafety.KeepMissingLocalDevices(devices, _allDevices);
            devices = mergedDevices;
            if (keptLocalOnly.Count > 0)
                Log($"[登录] 云端这次没返回 {keptLocalOnly.Count} 台，已保留本地条目：{string.Join("、", keptLocalOnly)}");

            // Bug 修复：从旧配置中合并用户输入的 RealMacAddress，避免登录后丢失
            MergeDeviceMacAddresses(devices);

            _allDevices = devices;
            Devices.Clear();
            foreach (var d in _allDevices) Devices.Add(d);

            Title = "EWeLink Linker - 正在发现设备IP...";
            _allDevices = await _lanClient.DiscoverDevicesAsync(_allDevices);
            Devices.Clear();
            foreach (var d in _allDevices) Devices.Add(d);

            // Bug 修复：恢复动作的 DeviceId（在 Devices 集合更新后）
            RestoreActionDeviceIds(savedActionDeviceIds);

            // 设备发现完成后，保存完整配置（包含 Token + 设备 + IP）
            var savedOutcome = SaveConfig();

            RebuildDeviceCards();
            Title = "EWeLink Linker";

            var devicesWithIp = _allDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress));
            var notes = new List<string>();
            if (keptLocalOnly.Count > 0)
                notes.Add($"云端这次没返回 {keptLocalOnly.Count} 台，已保留本地条目：{string.Join("、", keptLocalOnly)}");
            if (!tokensPersisted)
                notes.Add("新 token 没能写入配置文件，重启后需要重新登录");
            if (savedOutcome == SaveOutcome.Aborted)
                notes.Add("配置没有写进磁盘（原因见上一条弹窗或 debug.log）");
            MessageBox.Show(
                $"登录成功！获取到 {_allDevices.Count} 个设备，{devicesWithIp} 个有IP地址" +
                (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : ""),
                "登录", MessageBoxButton.OK,
                notes.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Title = "EWeLink Linker";
            MessageBox.Show($"登录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoggingIn = false;
            LoginButton.IsEnabled = true;
        }
    }

    private static string GetCountryCodeForRegion(string region) => region?.ToLower() switch
    {
        "cn" => "+86",
        "eu" => "+44",
        "us" => "+1",
        "as" => "+65",
        _ => "+86"
    };

    // ─── Refresh ────────────────────────────────────────

    private async void RefreshIP_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _allDevices.Count == 0) return;
        _isRefreshing = true;

        try
        {
            Title = "EWeLink Linker - 正在刷新IP...";

            // Bug 修复：在清空前保存所有动作的 DeviceId
            var savedActionDeviceIds = SaveActionDeviceIds();

            _allDevices = await _lanClient.DiscoverDevicesAsync(_allDevices);
            Devices.Clear();
            foreach (var d in _allDevices) Devices.Add(d);

            // Bug 修复：恢复动作的 DeviceId
            RestoreActionDeviceIds(savedActionDeviceIds);

            RebuildDeviceCards();
            var ipSave = SaveConfig();
            Title = "EWeLink Linker";
            MessageBox.Show(ipSave == SaveOutcome.Aborted
                    ? "IP 已在界面上刷新，但配置没有写进磁盘（原因见上一条弹窗）。"
                    : "IP 刷新完成",
                ipSave == SaveOutcome.Aborted ? "部分完成" : "刷新完成",
                MessageBoxButton.OK, ipSave == SaveOutcome.Aborted ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Title = "EWeLink Linker";
            MessageBox.Show($"刷新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async void RefreshState_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _allDevices.Count == 0) return;
        _isRefreshing = true;
        var renewedByRelogin = false;

        try
        {
            Title = "EWeLink Linker - 正在刷新状态...";

            // 局域网先问一次：设备此刻的通道状态在 mDNS 公告里，云端凭证死了也读得到
            var lan = new MdnsStatusClient.LanStatusResult(
                new Dictionary<string, MdnsStatusClient.LanDeviceStatus>(), 0,
                MdnsStatusClient.LanAttempt.NoTargets, new List<string>());
            try
            {
                lan = await _lanStatusClient.QueryStatusAsync(_allDevices, TimeSpan.FromMilliseconds(2500));
            }
            catch (Exception ex)
            {
                Log($"[刷新状态] 局域网状态读取异常，本次只用云端: {ex.Message}");
            }

            List<DeviceInfo>? cloudDevices = null;
            string? cloudError = null;
            try
            {
                var token = SyncAccessTokenFromDisk();
                if (string.IsNullOrEmpty(token))
                {
                    Log("[刷新状态] 没有可用的访问令牌，直接走自愈阶梯");
                    cloudDevices = await RecoverFromAuthFailureAsync();
                    renewedByRelogin = true;
                }
                else
                {
                    try
                    {
                        cloudDevices = await _cloudClient.GetDevicesAsync(token);
                    }
                    catch (CloudApiException ex) when (ex.IsAuthFailure)
                    {
                        Log($"[刷新状态] 凭证失效（error={ex.ErrorCode} {ex.CloudMessage}），走自愈阶梯");
                        cloudDevices = await RecoverFromAuthFailureAsync();
                        renewedByRelogin = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // 云端失败不再等于整次刷新失败：局域网那份照样能显示，剩下 3 台只能等云端
                cloudError = ex.Message;
                Log($"[刷新状态] 云端未完成: {ex.Message}");
            }

            if (cloudDevices != null)
            {
                foreach (var localDevice in _allDevices)
                {
                    var cloudDevice = cloudDevices.FirstOrDefault(d => d.DeviceId == localDevice.DeviceId);
                    if (cloudDevice != null)
                    {
                        localDevice.ChannelCount = cloudDevice.ChannelCount;
                        localDevice.ChannelStates = new List<string>(cloudDevice.ChannelStates);
                        localDevice.IsOnline = cloudDevice.IsOnline;
                    }
                }
            }

            // 局域网那份覆盖云端：云端是服务器上的缓存，局域网是设备自己报的此刻状态
            var lanApplied = 0;
            foreach (var localDevice in _allDevices)
            {
                if (!lan.Devices.TryGetValue(localDevice.DeviceId.ToLowerInvariant(), out var live)) continue;
                foreach (var (outlet, on) in live.Channels)
                {
                    if (outlet < 0 || outlet >= localDevice.ChannelStates.Count) continue;
                    localDevice.ChannelStates[outlet] = on ? "on" : "off";
                }
                lanApplied++;
                Log($"[刷新状态] {localDevice.Name} 的通道状态取自局域网实时值 (seq={live.Seq}, {live.SourceIp})");
            }

            if (cloudDevices == null && lanApplied == 0)
            {
                // 两侧都没东西可显示：不重建卡片也不写盘，但要把"云端怎么了"和"局域网为什么没读到"一起说
                Title = "EWeLink Linker";
                MessageBox.Show(RefreshFeedback.Build(false, cloudError, lan, 0, _allDevices.Count, renewedByRelogin),
                                "刷新失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RebuildDeviceCards();
            // Bug 修复：刷新状态后保存配置，防止崩溃后丢失
            var stateSave = SaveConfig();
            Title = "EWeLink Linker";

            var feedback = RefreshFeedback.Build(cloudDevices != null, cloudError, lan, lanApplied,
                                                 _allDevices.Count, renewedByRelogin,
                                                 _tokensPersisted, stateSave != SaveOutcome.Aborted);
            MessageBox.Show(feedback, cloudDevices == null ? "部分完成" : "刷新完成",
                MessageBoxButton.OK, cloudDevices == null ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Title = "EWeLink Linker";
            MessageBox.Show($"刷新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// 盘上那份 token 可能已被服务端的 TokenManager 换掉，而界面里是启动时读的快照。
    /// 不重读盘，"刷新状态"就会一直抱着死 token 失败到你手动登录为止。
    /// </summary>
    private string SyncAccessTokenFromDisk()
    {
        try
        {
            var disk = LinkerConfig.Load(_configPath).Tokens;
            if (!string.IsNullOrEmpty(disk.AccessToken) && disk.AccessToken != _accessToken)
            {
                Log("[刷新状态] 采用磁盘上更新的 token（服务端刷新过）");
                AdoptTokens(disk.AccessToken, disk.RefreshToken, disk.UserApiKey, disk.TokenObtainedAtUtc);
            }
        }
        catch (Exception ex)
        {
            Log($"[刷新状态] 读取磁盘 token 失败，沿用内存值: {ex.Message}");
        }

        return _accessToken;
    }

    private void AdoptTokens(string accessToken, string refreshToken, string userApiKey, DateTime? obtainedAtUtc)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        if (!string.IsNullOrEmpty(userApiKey)) _userApiKey = userApiKey;
        _tokenObtainedAtUtc = obtainedAtUtc;
        _tokensRenewedThisSession = true;
    }

    /// <summary>
    /// 凭证被云端拒绝后的自愈阶梯。先只拿 refresh token 换新的：它不要密码，而且按官方口径
    /// 401（账号在别处登录把这份 token 顶掉）也是它就能救的那种。换不动才动用账号密码重新登录——
    /// 登录会把上一份 token 顶掉，是更重的一锤，且一分钟内只敲一次：密码若在别处改过，
    /// 反复打登录接口只会把账号打进锁定。
    /// </summary>
    private async Task<List<DeviceInfo>> RecoverFromAuthFailureAsync()
    {
        try
        {
            var refreshed = await _tokenManager.RefreshNowAsync();
            AdoptTokens(refreshed.AccessToken, refreshed.RefreshToken, refreshed.UserApiKey, DateTime.UtcNow);
            Log("[刷新状态] 已用 refresh token 换新凭证");
            return await _cloudClient.GetDevicesAsync(refreshed.AccessToken);
        }
        catch (Exception ex)
        {
            Log($"[刷新状态] refresh token 换不动（{ex.Message}），改用账号密码重新登录");
        }

        if (DateTime.UtcNow - _lastAutoReloginUtc < AutoReloginCooldown)
            throw new Exception("云端登录已过期，自动重登刚试过不到一分钟。请确认账号密码后点「登录获取设备」。");
        _lastAutoReloginUtc = DateTime.UtcNow;

        var account = AccountTextBox.Text;
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password))
            throw new Exception("云端登录已过期，且界面上没有可用的账号密码。请填写后点「登录获取设备」。");

        var region = RegionComboBox.Text;
        _cloudClient.Region = region;
        var (tokens, _) = await _cloudClient.LoginAsync(account, password, GetCountryCodeForRegion(region));
        AdoptTokens(tokens.AccessToken, tokens.RefreshToken, tokens.UserApiKey, DateTime.UtcNow);
        SaveTokensOnly();
        Log("[刷新状态] 自动重新登录成功，新 token 已写入配置");

        return await _cloudClient.GetDevicesAsync(_accessToken);
    }

    private async Task AutoDiscoverIPsOnStartup()
    {
        if (_allDevices.Count == 0 || !_allDevices.Any(d => string.IsNullOrEmpty(d.IpAddress))) return;

        try
        {
            var withIpBefore = _allDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress));

            // DiscoverDevicesAsync 修改设备对象本身（设置 IPAddress），不需要重新创建集合
            _allDevices = await _lanClient.DiscoverDevicesAsync(_allDevices);
            // 修复：检查窗口是否已关闭
            if (!_disposed)
            {
                // 只刷新 UI，不清除集合（避免 ComboBox 失去选中项）
                await Dispatcher.InvokeAsync(RebuildDeviceCards);
                // 只在真发现了新 IP 时才写盘：那几台只能走云端的设备永远没 IP，
                // 无条件保存会让"打开工具"每次都改一次配置文件
                if (_allDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress)) > withIpBefore)
                    SaveConfig();
            }
        }
        catch { }
    }
}
