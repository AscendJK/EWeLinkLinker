using System.ServiceProcess;
using System.Text;
using EWeLinkLinker.Core.Cloud;
using EWeLinkLinker.Core.Config;
using EWeLinkLinker.Core.Lan;
using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Services;
using EWeLinkLinker.Core.Token;
using EWeLinkLinker.Core.Triggers;

namespace EWeLinkLinker.Service;

public class LinkerWindowsService : ServiceBase
{
    private readonly string _configPath;
    private readonly string _logPath;
    private HttpClient _sharedHttpClient;
    private readonly ServiceLogger _logger;
    private LanClient? _lanClient;
    private CloudClient? _cloudClient;
    private TokenManager? _tokenManager;
    private TriggerManager? _triggerManager;
    private FileSystemWatcher? _configWatcher;
    private CancellationTokenSource? _wakeCts; // 修复：唤醒任务取消支持
    private string? _triggerSignature;  // 当前已加载规则的签名，用于跳过无谓的触发器重建

    public LinkerWindowsService()
    {
        ServiceName = "EWeLinkLinker";

        // 关键：必须设置这些属性才能接收电源事件通知
        CanShutdown = true;                          // 接收关机通知
        CanHandlePowerEvent = true;                  // 接收睡眠/唤醒通知
        CanStop = true;                              // 允许手动停止
        CanPauseAndContinue = false;                 // 不支持暂停/继续

        // 共享配置文件路径（与 ConfigApp 共用）
        _configPath = Path.Combine(AppContext.BaseDirectory, "..", "config", "linker.json");

        // Set up log path
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, $"service-{DateTime.Now:yyyy-MM-dd}.log");

        // Initialize logger (disabled by default, will be enabled after config load)
        _logger = new ServiceLogger(_logPath, enabled: false);

        // 初始化 SimpleLogger（触发器传感器读数、LAN/云端详情、动作执行细节写入独立文件，
        // 避免与 ServiceLogger 共用文件句柄导致写入冲突/日志截断）
        Core.Logging.SimpleLogger.Initialize(Path.Combine(logDir, $"service-detail-{DateTime.Now:yyyy-MM-dd}.log"));

        // 修复：使用 SocketsHttpHandler 并设置 PooledConnectionLifetime 以支持 DNS 变更
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _sharedHttpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            // Allow connection reuse for long-running service
            DefaultRequestHeaders = { ConnectionClose = false }
        };
    }

    protected override void OnStart(string[] args)
    {
        // 启动阶段先开日志：_logger.Enabled 原本要等配置读完才置位，
        // 若配置正好损坏，损坏原因就会被写入开关吞掉
        LoggerConfig.IsEnabled = true;
        _logger.Enabled = true;

        // 调试输出：确认服务启动（写入控制台和调试输出）
        Console.WriteLine($"[DEBUG] EWeLink Linker Service starting...");
        System.Diagnostics.Debug.WriteLine($"[DEBUG] EWeLink Linker Service starting...");

        Log("========================================");
        Log($"EWeLink Linker Service v1.2.0");
        Log($"Build: {GetType().Assembly.GetName().Version}");
        Log("========================================");
        Log("Service starting...");
        Log($"Config path: {_configPath}");

        _lanClient = new LanClient(_sharedHttpClient);
        InitializeClients();

        // 根据配置启用/禁用日志（同步到全局开关）
        // 配置不可读时保持日志开启，否则启动失败的原因无处可查
        var config = TryLoadConfig();
        LoggerConfig.IsEnabled = config?.LoggingEnabled ?? true;
        _logger.Enabled = config?.LoggingEnabled ?? true;
        Log($"Logging enabled: {_logger.Enabled}");

        // 加载并启动扩展触发器（时间、温度、应用等）
        LoadAndStartTriggers();

        // 只有在系统启动时（而非手动启动）执行开机联动
        if (IsSystemRecentlyBooted())
        {
            Log("System recently booted, executing boot actions...");
            _ = Task.Run(async () => await ExecuteBootActions());
        }
        else
        {
            Log("Manual service start, skipping boot actions");
        }

        Log("Service started");
    }

    /// <summary>
    /// 加载并启动扩展触发器（异步）
    /// </summary>
    private void LoadAndStartTriggers()
    {
        try
        {
            var config = TryLoadConfig();
            if (config == null)
            {
                Log("Triggers not started: config unreadable, will retry on next config change");
                return;
            }
            var service = CreateLinkerService();
            if (service == null)
            {
                Log("Cannot load triggers: service not available");
                return;
            }

            _triggerManager = new TriggerManager(service, _logPath, _logger);
            _triggerSignature = BuildTriggerSignature(config);

            // 异步加载和启动（使用配置的轮询间隔）
            int pollingInterval = config.PollingIntervalSeconds;
            int activeTriggerCount = config.Rules.Sum(r => r.Conditions.Count(c => !TriggerManager.IsPowerCondition(c.Type)));
            _ = Task.Run(async () =>
            {
                try
                {
                    await _triggerManager.LoadRulesAsync(config.Rules, pollingInterval);
                    await _triggerManager.StartAllAsync();
                    Log($"Trigger manager started with {activeTriggerCount} triggers, polling interval: {pollingInterval}s");
                }
                catch (Exception ex)
                {
                    Log($"Failed to start triggers: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to load triggers: {ex.Message}");
        }
        finally
        {
            // 必须放 finally：早退（return）时也要挂上监视器，用户修好配置后才能自动重载
            StartConfigWatcher();
        }
    }

    /// <summary>
    /// 启动配置文件监控，当配置改变时自动重载
    /// </summary>
    private void StartConfigWatcher()
    {
        if (_configWatcher != null) return;  // 可由重载路径二次进入，避免重复挂监视器

        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (string.IsNullOrEmpty(configDir)) return;

            _configWatcher = new FileSystemWatcher(configDir)
            {
                Filter = Path.GetFileName(_configPath),
                // 监控更多变化类型，包括原子保存（重命名）
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName
            };

            // C-4 修复：使用 lock 保护防抖变量，避免多线程并发问题
            var debounceLock = new object();
            var lastRead = DateTime.MinValue;
            void HandleConfigChange(object s, FileSystemEventArgs e)
            {
                // FileSystemWatcher 的 Filter 是前缀匹配，原子写入用的 linker.json.tmp
                // 之类也会进来；只有真正的配置文件名才需要重载
                var configFileName = Path.GetFileName(_configPath);
                if (!string.IsNullOrEmpty(e.Name) &&
                    !e.Name.Equals(configFileName, StringComparison.OrdinalIgnoreCase)) return;

                // 防抖：500ms 内只处理一次
                lock (debounceLock)
                {
                    var now = DateTime.Now;
                    if ((now - lastRead).TotalMilliseconds < 500) return;
                    lastRead = now;
                }

                Log($"Config file changed: {e.ChangeType} - {e.FullPath}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(200); // 等待文件写入完成
                        await ReloadConfigAsync();
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to reload config: {ex.Message}");
                    }
                });
            }

            _configWatcher.Changed += HandleConfigChange;
            _configWatcher.Renamed += (_, e) => HandleConfigChange(_, e);  // 原子保存时会触发
            _configWatcher.Created += HandleConfigChange;   // 新文件创建时触发
            _configWatcher.EnableRaisingEvents = true;
            Log($"Config watcher started for: {_configPath}");
            Log($"Config watcher filter: {Path.GetFileName(_configPath)}");
            Log($"Config watcher directory: {configDir}");
        }
        catch (Exception ex)
        {
            Log($"Failed to start config watcher: {ex.Message}");
        }
    }

    /// <summary>
    /// 重新加载配置
    /// H-2 修复：重新初始化客户端以获取新 Token
    /// </summary>
    private async Task ReloadConfigAsync()
    {
        // 坏配置不重载，保留当前正在运行的触发器
        var config = TryLoadConfig();
        if (config == null)
        {
            Log("Reload skipped: config unreadable, keeping current triggers");
            return;
        }

        // 触发器还没起来（启动时配置损坏，或启动时还没登录）：此刻配置可用，补一次初始化
        if (_triggerManager == null)
        {
            Log("Triggers not running, initializing from reloaded config");
            LoadAndStartTriggers();
            return;
        }

        // 更新日志开关（同步到全局开关和本地开关）
        LoggerConfig.IsEnabled = config.LoggingEnabled;
        _logger.Enabled = config.LoggingEnabled;

        Log("Reloading config...");

        // H-2 修复：重新初始化客户端，使新 Token 生效
        // 用户在 ConfigApp 登录后，Token 已更新到配置文件
        // 服务端需要重新读取 Token 才能正常使用云端 API
        InitializeClients();

        // 重建触发器会清空每个触发器的边沿记忆（_wasTriggered / _previousCompositeResult），
        // 仍处「满足」状态的规则会在下一轮轮询被当成新跳变，重复下发一次设备命令。
        // 所以只有真正影响规则评估的字段变了才重建。
        var signature = BuildTriggerSignature(config);
        if (_triggerSignature == signature)
        {
            Log("配置变更不涉及规则或轮询间隔，跳过触发器重建");
            return;
        }
        _triggerSignature = signature;

        // 使用 TriggerManager.ReloadAsync 正确停止旧触发器并加载新触发器
        await _triggerManager.ReloadAsync(config.Rules, config.PollingIntervalSeconds);

        Log($"Config reloaded: {config.Rules.Count} rules, polling interval: {config.PollingIntervalSeconds}s");
    }

    /// <summary>
    /// 只覆盖「会影响触发器评估」的字段：轮询间隔、启用的规则、每个条件的类型与参数、
    /// 每个动作的目标通道与期望状态。设备 IP/名称、账户与 token 都不在内——
    /// 那些在动作执行时由 LinkerService 从磁盘重读，不需要重建触发器。
    /// </summary>
    private static string BuildTriggerSignature(LinkerConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("P").Append(config.PollingIntervalSeconds);

        foreach (var rule in config.Rules)
        {
            if (!rule.Enabled) continue;

            sb.Append("|C[");
            foreach (var c in rule.Conditions)
            {
                sb.Append(c.Type).Append(':').Append(c.Parameter).Append('/').Append(c.Parameter2)
                  .Append("/R").Append(c.ReleaseParameter)
                  .Append(':').Append(c.Comparison).Append(':').Append(c.Operator).Append(',');
            }
            sb.Append("]A[");
            foreach (var a in rule.Actions)
            {
                sb.Append(a.DeviceId).Append('#').Append(a.Outlet).Append('#').Append(a.State).Append(',');
            }
            sb.Append(']');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 判断是否为电源事件
    /// </summary>
    private static bool IsPowerEvent(string? eventName) =>
        !string.IsNullOrEmpty(eventName) &&
        (eventName.Equals("boot", StringComparison.OrdinalIgnoreCase) ||
         eventName.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
         eventName.Equals("sleep", StringComparison.OrdinalIgnoreCase) ||
         eventName.Equals("wake", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 检查系统是否在近期启动（2分钟内），用于区分开机启动和手动启动
    /// </summary>
    private static bool IsSystemRecentlyBooted()
    {
        try
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            // 如果系统运行时间小于2分钟，认为是开机启动
            return uptime.TotalMinutes < 2;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnStop()
    {
        Log("Service stopping...");

        // 停止配置文件监控
        StopConfigWatcher();

        // 释放 LAN/Cloud 客户端引用，避免 Dispose 后被使用
        _lanClient = null;
        _cloudClient = null;
        _tokenManager = null;

        // H-2 修复：使用 GetAwaiter().GetResult() 避免 STA 线程死锁
        // 停止所有触发器（同步等待完成）
        if (_triggerManager != null)
        {
            try
            {
                _triggerManager.StopAllAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log($"Error stopping triggers: {ex.Message}");
            }
            _triggerManager = null;
        }

        // C-6 修复：取消并释放唤醒任务 CTS
        _wakeCts?.Cancel();
        _wakeCts?.Dispose();
        _wakeCts = null;

        // 释放日志后台写入任务
        try
        {
            _logger.Dispose();
        }
        catch { }

        // C-5 修复：OnStop 也释放 HttpClient（服务停止时可能非关机）
        var httpClient = System.Threading.Interlocked.Exchange(ref _sharedHttpClient, null!);
        httpClient?.Dispose();
    }

    protected override void OnShutdown()
    {
        _logger.Info("========== 系统关机信号收到 ==========");
        try
        {
            // 取消唤醒任务（如果正在运行）
            _wakeCts?.Cancel();
            _wakeCts?.Dispose();
            _wakeCts = null;

            // 先停止触发器，避免执行过程中被中断（预算 1s）
            if (_triggerManager != null)
            {
                try
                {
                    _triggerManager.StopAllAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
                }
                catch { /* 超时继续执行关机动作 */ }
            }

            // 关机动作：串行执行 + 总超时 3.5s，避免系统杀进程导致动作未完成
            try
            {
                var shutdownTask = ExecuteShutdownActions();
                if (!shutdownTask.Wait(TimeSpan.FromSeconds(3.5)))
                {
                    _logger.Warn("关机联动动作超时（系统正在关机）");
                }
                else
                {
                    _logger.Info("关机联动动作执行完成");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("关机联动动作执行失败", ex);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("关机联动动作执行失败", ex);
        }
        finally
        {
            // 释放所有资源，总预算控制在 5s 内
            _lanClient = null;
            _cloudClient = null;
            _tokenManager = null;
            StopConfigWatcher();
            if (_triggerManager != null)
            {
                try
                {
                    _triggerManager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(0.5));
                }
                catch { }
                _triggerManager = null;
            }
            // 使用 Interlocked.Exchange 确保只 Dispose 一次
            var httpClient = System.Threading.Interlocked.Exchange(ref _sharedHttpClient, null!);
            httpClient?.Dispose();
            base.OnShutdown();
        }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                Log("=== SYSTEM SUSPENDING ===");
                // 睡眠时必须同步执行，系统会等待服务完成才进入睡眠
                // 加总超时 1.5s，避免长时间阻塞导致系统进入睡眠延迟
                try
                {
                    var sleepTask = ExecuteSleepActions();
                    if (!sleepTask.Wait(TimeSpan.FromSeconds(1.5)))
                    {
                        _logger.Warn("睡眠联动动作超时");
                    }
                    else
                    {
                        Log("Sleep actions completed successfully");
                    }
                }
                catch (Exception ex)
                {
                    Log($"ERROR: Sleep actions failed: {ex.Message}");
                }
                return true; // 告诉系统我们已经处理了

            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeCritical:
            case PowerBroadcastStatus.ResumeSuspend:
                Log("=== SYSTEM RESUMING ===");
                // 唤醒时可以异步执行，系统不会等待
                // 延迟3秒让网络设备恢复连接
                // C-6 修复：覆盖前释放旧 CTS
                _wakeCts?.Dispose();
                _wakeCts = new CancellationTokenSource();
                var wakeCts = _wakeCts; // 局部变量捕获，避免 OnStop/OnShutdown 并发 Dispose
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Log("Wake: waiting 3s for network to restore...");
                        await Task.Delay(3000, wakeCts.Token);
                        await ExecuteWakeActions(wakeCts.Token);
                        Log("Wake actions completed successfully");
                    }
                    catch (OperationCanceledException)
                    {
                        Log("Wake actions cancelled (shutdown in progress)");
                    }
                    catch (Exception ex)
                    {
                        Log($"ERROR: Wake actions failed: {ex.Message}");
                    }
                });
                return true; // 告诉系统我们已经处理了

            default:
                // 未知的电源事件，返回 false 让系统处理
                return base.OnPowerEvent(powerStatus);
        }
    }

    /// <summary>
    /// Initialize shared clients once so TokenManager's refresh lock is reused across events.
    /// </summary>
    private void InitializeClients()
    {
        var config = TryLoadConfig();
        if (config == null)
        {
            // 配置不可读：保留现有客户端——一次坏写入不该毁掉可用的 token 客户端
            if (_cloudClient != null) return;
            config = new LinkerConfig();
        }
        _cloudClient = new CloudClient(_sharedHttpClient)
        {
            Region = config.Account.Region
        };
        // H-? 修复：先 Dispose 旧的 _tokenManager，释放 SemaphoreSlim，防止泄漏
        (_tokenManager as IDisposable)?.Dispose();
        _tokenManager = new TokenManager(_cloudClient, _configPath);
    }

    /// <summary>
    /// 读取共享配置文件。解析失败时记录错误并返回 null，
    /// 不让异常冒到 OnStart（否则 SCM 会直接判定服务启动失败）。
    /// </summary>
    private LinkerConfig? TryLoadConfig()
    {
        try
        {
            return LinkerConfig.Load(_configPath);
        }
        catch (Exception ex)
        {
            Log($"ERROR: 配置文件无法解析: {ex.Message} ({_configPath})");
            return null;
        }
    }

    public void StartAsConsole(string[] args)
    {
        // 注意：不在此处创建 _lanClient，OnStart 内部会调用 InitializeClients 并创建
        Log("Starting in console mode...");
        OnStart(args);

        Console.WriteLine("EWeLink Linker Service running in console mode. Press Ctrl+C to stop.");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // WaitHandle.WaitOne() 不会抛出 OperationCanceledException，
        // 它只在等待成功时返回 true，超时或信号量释放后返回 false。
        // 实际取消由 cts.Cancel() 触发 WaitHandle 释放 → WaitOne 返回 false。
        while (!cts.Token.WaitHandle.WaitOne(100))
        {
            // 每 100ms 轮询一次，允许响应取消请求
        }

        OnStop();
    }

    private async Task ExecuteBootActions()
    {
        try
        {
            var service = CreateLinkerService();
            if (service != null)
            {
                Log("Executing boot actions...");
                await service.ExecuteEventAsync("boot");
                Log("Boot actions completed");
            }
            else
            {
                Log("No boot actions configured or config not available");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR executing boot actions: {ex.Message}");
        }
    }

    private async Task ExecuteShutdownActions()
    {
        try
        {
            // 关机是本地事件（纯 LAN 控制），不要求云 Token
            var service = CreateLinkerService(requireToken: false);
            if (service != null)
            {
                Log("Executing shutdown actions...");
                await service.ExecuteEventAsync("shutdown");
                Log("Shutdown actions completed");
            }
            else
            {
                Log("No shutdown actions configured or config not available");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR executing shutdown actions: {ex.Message}");
        }
    }

    private async Task ExecuteSleepActions()
    {
        try
        {
            // 睡眠是本地事件（纯 LAN 控制），不要求云 Token
            var service = CreateLinkerService(requireToken: false);
            if (service != null)
            {
                Log("Executing sleep actions...");
                await service.ExecuteEventAsync("sleep");
                Log("Sleep actions completed");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR executing sleep actions: {ex.Message}");
        }
    }

    private async Task ExecuteWakeActions(CancellationToken ct)
    {
        try
        {
            var service = CreateLinkerService();
            if (service != null)
            {
                Log("Executing wake actions...");
                await service.ExecuteEventAsync("wake", ct);
                Log("Wake actions completed");
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 重新抛出取消异常
        }
        catch (Exception ex)
        {
            Log($"ERROR executing wake actions: {ex.Message}");
        }
    }

    private LinkerService? CreateLinkerService(bool requireToken = true)
    {
        if (!File.Exists(_configPath))
        {
            Log($"Config file not found at {_configPath}");
            return null;
        }

        var config = LinkerConfig.Load(_configPath);
        // 仅非本地事件（boot/wake）要求云 Token；shutdown/sleep 是纯 LAN 本地事件，
        // 与 LinkerService.ExecuteEventAsync 的 isLocalOnlyEvent 语义保持一致
        if (requireToken && string.IsNullOrEmpty(config.Tokens.AccessToken))
        {
            Log("Access token not configured. Please login via ConfigApp first.");
            return null;
        }

        // Reuse shared clients so TokenManager's refresh lock works across events
        if (_lanClient == null || _tokenManager == null)
        {
            InitializeClients();
        }

        // Pass log path so LinkerService can write detailed logs
        return new LinkerService(_lanClient!, _tokenManager!, _configPath, logPath: _logPath);
    }

    /// <summary>
    /// 安全停止配置文件监控（修复：取消事件订阅防止内存泄漏）
    /// </summary>
    private void StopConfigWatcher()
    {
        if (_configWatcher == null) return;

        try
        {
            _configWatcher.EnableRaisingEvents = false;
            // FileSystemWatcher 的 Dispose 会清理事件订阅
            _configWatcher.Dispose();
        }
        catch { }
        _configWatcher = null;
    }

    private void Log(string message)
    {
        _logger.Info(message);
        // Also write to console for debug mode
        if (Environment.UserInteractive)
        {
            try { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"); }
            catch { }
        }
    }
}
