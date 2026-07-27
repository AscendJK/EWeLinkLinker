using EWeLinkLinker.Service;

// Use explicit --console flag for console mode. Environment.UserInteractive is unreliable
// when running as a Windows service (e.g., via Task Scheduler or SCM).
var isConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

if (isConsole)
{
    var service = new LinkerWindowsService();
    service.StartAsConsole(args);
}
else
{
    var service = new LinkerWindowsService();
    System.ServiceProcess.ServiceBase.Run(service);
}
