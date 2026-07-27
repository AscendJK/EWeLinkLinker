using System.ServiceProcess;
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
        Log("========================================");
        Log($"EWeLink Linker Service v1.2.0");
        Log($"Build: {GetType().Assembly.GetName().Version}");
        Log("========================================");
        Log("Service starting...");
        Log($"Config path: {_configPath}");

        _lanClient = new LanClient(_sharedHttpClient);
        InitializeClients();

        // 根据配置启用/禁用日志（同步到全局开关）
        var config = Core.Config.LinkerConfig.Load(_configPath);
        LoggerConfig.IsEnabled = config.LoggingEnabled;
        _logger.Enabled = config.LoggingEnabled;
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
            var config = LinkerConfig.Load(_configPath);
            var service = CreateLinkerService();
            if (service == null)
            {
                Log("Cannot load triggers: service not available");
                return;
            }

            _triggerManager = new TriggerManager(service, _logPath);

            // 异步加载和启动
            _ = Task.Run(async () =>
            {
                try
                {
                    await _triggerManager.LoadRulesAsync(config.Rules);
                    await _triggerManager.StartAllAsync();
                    Log($"Trigger manager started with {config.Rules.Count(r => !IsPowerEvent(r.Event))} triggers");
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

        // 启动配置文件监控
        StartConfigWatcher();
    }

    /// <summary>
    /// 启动配置文件监控，当配置改变时自动重载
    /// </summary>
    private void StartConfigWatcher()
    {
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

            // 使用防抖，避免短时间内多次触发
            var lastRead = DateTime.MinValue;
            void HandleConfigChange(object s, FileSystemEventArgs e)
            {
                // 防抖：500ms 内只处理一次
                var now = DateTime.Now;
                if ((now - lastRead).TotalMilliseconds < 500) return;
                lastRead = now;

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
    /// </summary>
    private async Task ReloadConfigAsync()
    {
        if (_triggerManager == null) return;

        // 重新加载配置
        var config = LinkerConfig.Load(_configPath);

        // 更新日志开关（同步到全局开关和本地开关）
        LoggerConfig.IsEnabled = config.LoggingEnabled;
        _logger.Enabled = config.LoggingEnabled;

        Log("Reloading config...");

        // 使用 TriggerManager.ReloadAsync 正确停止旧触发器并加载新触发器
        await _triggerManager.ReloadAsync(config.Rules);

        Log($"Config reloaded: {config.Rules.Count} rules");
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

        // 停止所有触发器（同步等待完成）
        if (_triggerManager != null)
        {
            try
            {
                _triggerManager.StopAllAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log($"Error stopping triggers: {ex.Message}");
            }
            _triggerManager = null;
        }

        // 取消唤醒任务
        _wakeCts?.Cancel();
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

            // 先停止触发器，避免执行过程中被中断
            if (_triggerManager != null)
            {
                try
                {
                    _triggerManager.StopAllAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                }
                catch (System.TimeoutException)
                {
                    Log("Stop triggers timeout during shutdown");
                }
            }

            // 修复：使用 GetAwaiter().GetResult() 避免潜在死锁
            ExecuteShutdownActions().GetAwaiter().GetResult();
            _logger.Info("关机联动动作执行完成");
        }
        catch (System.TimeoutException)
        {
            _logger.Warn("关机联动动作超时（系统正在关机）");
        }
        catch (Exception ex)
        {
            _logger.Error("关机联动动作执行失败", ex);
        }
        finally
        {
            // 释放所有资源（使用 null 合并赋值确保只释放一次）
            StopConfigWatcher();
            if (_triggerManager != null)
            {
                try
                {
                    _triggerManager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
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
                try
                {
                    ExecuteSleepActions().GetAwaiter().GetResult();
                    Log("Sleep actions completed successfully");
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
                _wakeCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Log("Wake: waiting 3s for network to restore...");
                        await Task.Delay(3000, _wakeCts.Token);
                        await ExecuteWakeActions(_wakeCts.Token);
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
        var config = LinkerConfig.Load(_configPath);
        _cloudClient = new CloudClient(_sharedHttpClient)
        {
            Region = config.Account.Region
        };
        _tokenManager = new TokenManager(_cloudClient, _configPath);
    }

    public void StartAsConsole(string[] args)
    {
        _lanClient = new LanClient(_sharedHttpClient);
        Log("Starting in console mode...");
        OnStart(args);

        Console.WriteLine("EWeLink Linker Service running in console mode. Press Ctrl+C to stop.");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            cts.Token.WaitHandle.WaitOne();
        }
        catch (OperationCanceledException)
        {
            Log("Console mode stopped by user");
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
            var service = CreateLinkerService();
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
            var service = CreateLinkerService();
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

    private LinkerService? CreateLinkerService()
    {
        if (!File.Exists(_configPath))
        {
            Log($"Config file not found at {_configPath}");
            return null;
        }

        var config = LinkerConfig.Load(_configPath);
        if (string.IsNullOrEmpty(config.Tokens.AccessToken))
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
