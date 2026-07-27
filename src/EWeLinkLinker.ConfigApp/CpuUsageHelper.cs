using System.Runtime.InteropServices;

namespace EWeLinkLinker.ConfigApp;

/// <summary>
/// 使用 Windows API 获取与任务管理器一致的 CPU 使用率
/// 基于 NtQuerySystemInformation - 与任务管理器使用相同的内核计数器
/// </summary>
internal static class CpuUsageHelper
{
    #region Native API

    private const int SystemProcessorPerformanceInformation = 8;

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint Reserved;
    }

    private static readonly int ProcessorCount = Environment.ProcessorCount;
    private static readonly int StructSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();

    #endregion

    /// <summary>
    /// 获取 CPU 使用率（与任务管理器一致）
    /// 使用多次采样取平均，提高低负载时的准确性
    /// 注意：此方法会阻塞调用线程，请在后台线程调用
    /// </summary>
    public static float GetCpuUsage(int sampleCount = 3, int sampleIntervalMs = 300)
    {
        float totalUsage = 0;

        // 第一次采样（基准）
        var infoPrev = QueryProcessorInfo();
        var idlePrev = GetTotalIdleTime(infoPrev);
        var totalPrev = GetTotalTime(infoPrev);

        for (int i = 0; i < sampleCount; i++)
        {
            System.Threading.Thread.Sleep(sampleIntervalMs);

            var infoCurr = QueryProcessorInfo();
            var idleCurr = GetTotalIdleTime(infoCurr);
            var totalCurr = GetTotalTime(infoCurr);

            var idleDelta = idleCurr - idlePrev;
            var totalDelta = totalCurr - totalPrev;

            if (totalDelta > 0)
            {
                var usage = (1.0f - (float)idleDelta / totalDelta) * 100;
                totalUsage += Math.Clamp(usage, 0, 100);
            }

            infoPrev = infoCurr;
            idlePrev = idleCurr;
            totalPrev = totalCurr;
        }

        return totalUsage / sampleCount;
    }

    private static SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] QueryProcessorInfo()
    {
        var bufferSize = ProcessorCount * StructSize;
        var buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            int returnLength;
            var status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, bufferSize, out returnLength);

            if (status != 0) // STATUS_SUCCESS = 0
                throw new InvalidOperationException($"NtQuerySystemInformation failed with status 0x{status:X8}");

            var result = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[ProcessorCount];
            var ptr = buffer;

            for (int i = 0; i < ProcessorCount; i++)
            {
                result[i] = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(ptr);
                ptr += StructSize;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long GetTotalIdleTime(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] info)
    {
        long total = 0;
        foreach (var i in info)
            total += i.IdleTime;
        return total;
    }

    private static long GetTotalTime(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] info)
    {
        long total = 0;
        foreach (var i in info)
        {
            // KernelTime 包含 IdleTime，所以 Total = Kernel + User
            total += i.KernelTime + i.UserTime;
        }
        return total;
    }
}
