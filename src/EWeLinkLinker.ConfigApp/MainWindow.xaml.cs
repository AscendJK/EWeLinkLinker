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
using EWeLinkLinker.Core.Triggers;

namespace EWeLinkLinker.ConfigApp;

public partial class MainWindow : Window, IDisposable
{
    private readonly CloudClient _cloudClient;
    private readonly LanClient _lanClient;
    private readonly HttpClient _cloudHttpClient;
    private readonly HttpClient _lanHttpClient;
    private readonly string _configPath;
    private readonly string _logPath;

    private List<DeviceInfo> _allDevices = new();
    private ObservableCollection<LinkerRule> _rules = new();
    private string _userApiKey = string.Empty;
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private bool _disposed;

    private bool _isLoggingIn;
    private bool _isRefreshing;
    private bool _hasAutoDiscovered;
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serviceStatusTimer.Stop();
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
        try { SaveConfig(); } catch { }
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
            Log($"[加载] 错误: {ex.Message}");
            Log($"[加载] 堆栈: {ex.StackTrace}");
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

    private void SaveConfig()
    {
        try
        {
            var rulesList = _rules.ToList();
            // 加载现有配置以保留 LoggingEnabled 等设置
            var existingConfig = LinkerConfig.Load(_configPath);

            // 防止回写覆盖：如果磁盘 token 非空且与内存不同，说明被服务端 TokenManager 刷新过
            // 优先用磁盘 token（服务端写入的新 token）
            var accessToken = _accessToken;
            var refreshToken = _refreshToken;
            var userApiKey = _userApiKey;
            if (!string.IsNullOrEmpty(existingConfig.Tokens.AccessToken)
                && existingConfig.Tokens.AccessToken != _accessToken)
            {
                accessToken = existingConfig.Tokens.AccessToken;
                refreshToken = existingConfig.Tokens.RefreshToken;
                userApiKey = existingConfig.Tokens.UserApiKey;
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
                    UserApiKey = userApiKey
                },
                Devices = _allDevices,
                Rules = rulesList,
                LoggingEnabled = existingConfig.LoggingEnabled  // 保留日志开关设置
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

            config.Save(_configPath);
            Log($"[保存] 配置已保存到: {_configPath}");

            // 验证保存的文件
            if (File.Exists(_configPath))
            {
                var savedJson = File.ReadAllText(_configPath);
                Log($"[保存] 文件大小: {savedJson.Length} 字符");
            }
        }
        catch (Exception ex)
        {
            Log($"[保存] 错误: {ex.Message}");
            Log($"[保存] 堆栈: {ex.StackTrace}");
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Bug 修复：仅保存 Token 和账户信息（用于登录后获取云端设备前）
    /// 优先使用内存中的 _allDevices（含用户刚编辑的 RealMacAddress），磁盘作为后备
    /// </summary>
    private void SaveTokensOnly()
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
                    UserApiKey = _userApiKey
                },
                Devices = devicesToSave,
                Rules = existingConfig.Rules,  // M-6 修复：登录时保留旧规则，不覆盖
                LoggingEnabled = existingConfig.LoggingEnabled
            };
            config.Save(_configPath);
            Log("[登录] Token 已保存");
        }
        catch (Exception ex)
        {
            Log($"[登录] 保存 Token 失败: {ex.Message}");
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
                rule.Conditions.Add(new RuleCondition { Type = "time", Parameter = "08:00", Operator = LogicalOperator.And });
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
            // 尝试解析数字
            if (int.TryParse(textBox.Text, out var value))
            {
                condition.Parameter = value.ToString();
                Log($"数值条件更新: {condition.Parameter}");
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
    private static bool IsPowerEventType(string type) =>
        type.Equals("boot", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("sleep", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("wake", StringComparison.OrdinalIgnoreCase);

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
                        var threshold = float.Parse(thresholdStr);
                        var status = tempC >= threshold ? "✓ 超过阈值" : "✗ 未超过";
                        return $"CPU 温度: {tempC:F1}°C\n阈值: {comparisonText} {threshold}°C\n状态: {status}";
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
                            var threshold = float.Parse(thresholdStr);
                            var status = temp >= threshold ? "✓ 超过阈值" : "✗ 未超过";
                            return $"GPU: {hardware.Name}\nGPU 温度: {temp:F1}°C\n阈值: {comparisonText} {threshold}°C\n状态: {status}";
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
        try
        {
            // 在后台线程执行，避免 UI 卡顿（采样约 900ms）
            var usage = await Task.Run(() => CpuUsageHelper.GetCpuUsage(sampleCount: 3, sampleIntervalMs: 300));

            var threshold = float.Parse(thresholdStr);
            var status = usage >= threshold ? "✓ 超过阈值" : "✗ 未超过";
            return $"CPU 使用率: {usage:F1}%\n阈值: {comparisonText} {threshold}%\n状态: {status}";
        }
        catch (Exception ex)
        {
            return $"CPU 使用率读取失败: {ex.Message}";
        }
    }

    private static string GetTimeInfo(string targetTime)
    {
        var now = DateTime.Now;
        if (TimeSpan.TryParse(targetTime, out var target))
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
        SaveConfig();
        MessageBox.Show("配置已保存！", "保存配置", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var exePath = Path.Combine(AppContext.BaseDirectory, "..", "Service", "EWeLinkLinker.Service.exe");

            // 1. 停止并删除旧服务
            if (status != "NOT_INSTALLED")
            {
                Log($"安装服务: 停止旧服务...");
                await RunScCommand($"stop {ServiceName}", true);
                await Task.Delay(1000);
                Log($"安装服务: 删除旧服务...");
                await RunScCommand($"delete {ServiceName}", true);
                await Task.Delay(1000);
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

            // 3. 设置描述
            await RunScCommand($"description {ServiceName} \"Automatically controls eWeLink devices based on PC power events\"", true);
            await Task.Delay(500);

            // 4. 启动服务
            Log($"安装服务: 启动服务...");
            await RunScCommand($"start {ServiceName}", true);

            MessageBox.Show("服务安装完成！", "安装服务", MessageBoxButton.OK, MessageBoxImage.Information);
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
                await RunScCommand($"stop {ServiceName}");
                Log("停止服务: 命令已发送");
                MessageBox.Show("服务已停止。", "停止服务", MessageBoxButton.OK, MessageBoxImage.Information);
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
                await RunScCommand($"start {ServiceName}");
                Log("启动服务: 命令已发送");
                MessageBox.Show("服务启动成功！", "启动服务", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var config = LinkerConfig.Load(_configPath);
            config.LoggingEnabled = LoggingCheckBox.IsChecked == true;
            config.Save(_configPath);
            Log($"日志已{(config.LoggingEnabled ? "启用" : "禁用")}");
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
            int index = PollingIntervalCombo.SelectedIndex;
            if (index < 0 || index >= intervals.Length) return;

            int newInterval = intervals[index];
            var config = LinkerConfig.Load(_configPath);
            config.PollingIntervalSeconds = newInterval;
            config.Save(_configPath);

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
                await Task.Delay(1000);
            }
            await RunScCommand($"delete {ServiceName}");
            Log("卸载服务: 命令已发送");
            MessageBox.Show("服务已卸载。", "卸载服务", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async Task RunScCommand(string arguments, bool suppressErrors = false)
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
            if (process != null)
            {
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
                        1072 => "服务标记为删除",
                        _ => $"错误码 {process.ExitCode}"
                    };
                    if (!suppressErrors)
                        MessageBox.Show($"sc.exe 操作失败: {errorDetail}\n命令: sc {arguments}", "服务控制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC 被拒绝
            if (!suppressErrors)
                MessageBox.Show("需要管理员权限！请点击\"是\"允许 UAC 提示。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            throw;
        }
        catch (Exception ex)
        {
            if (!suppressErrors)
                MessageBox.Show($"操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
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

            // 同步轮询间隔下拉框
            int[] intervals = { 1, 2, 3, 5, 10, 15, 30 };
            int index = Array.IndexOf(intervals, config.PollingIntervalSeconds);
            PollingIntervalCombo.SelectedIndex = index >= 0 ? index : 3; // 默认 5s
        }
        catch { }
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
            _userApiKey = tokens.UserApiKey;
            _accessToken = tokens.AccessToken;
            _refreshToken = tokens.RefreshToken;

            // Bug 修复：先保存 Token（不保存设备列表），然后获取云端设备并合并旧 MAC 地址
            SaveTokensOnly();

            // Bug 修复：在清空前保存所有动作的 DeviceId，避免 TwoWay 绑定被清空
            var savedActionDeviceIds = SaveActionDeviceIds();

            var devices = await _cloudClient.GetDevicesAsync(tokens.AccessToken);

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
            SaveConfig();

            RebuildDeviceCards();
            Title = "EWeLink Linker";

            var devicesWithIp = _allDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress));
            MessageBox.Show($"登录成功！获取到 {_allDevices.Count} 个设备，{devicesWithIp} 个有IP地址",
                "登录", MessageBoxButton.OK, MessageBoxImage.Information);
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
            SaveConfig();
            Title = "EWeLink Linker";
            MessageBox.Show("IP 刷新完成", "刷新完成", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (_isRefreshing || _allDevices.Count == 0 || string.IsNullOrEmpty(_accessToken)) return;
        _isRefreshing = true;

        try
        {
            Title = "EWeLink Linker - 正在刷新状态...";
            var cloudDevices = await _cloudClient.GetDevicesAsync(_accessToken);

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

            RebuildDeviceCards();
            // Bug 修复：刷新状态后保存配置，防止崩溃后丢失
            SaveConfig();
            Title = "EWeLink Linker";
            MessageBox.Show("状态刷新完成", "刷新完成", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async Task AutoDiscoverIPsOnStartup()
    {
        if (_allDevices.Count == 0 || !_allDevices.Any(d => string.IsNullOrEmpty(d.IpAddress))) return;

        try
        {
            // DiscoverDevicesAsync 修改设备对象本身（设置 IPAddress），不需要重新创建集合
            _allDevices = await _lanClient.DiscoverDevicesAsync(_allDevices);
            // 修复：检查窗口是否已关闭
            if (!_disposed)
            {
                // 只刷新 UI，不清除集合（避免 ComboBox 失去选中项）
                await Dispatcher.InvokeAsync(RebuildDeviceCards);
                // Bug 修复：自动发现 IP 后保存配置
                SaveConfig();
            }
        }
        catch { }
    }
}
