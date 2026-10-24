using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenTypist;

public static class Hardware
{
    private static HardwareInfo? current;

    /// CPU, RAM and the graphics card llama.cpp would use (from ggml's device
    /// list, so it's the GPU the model actually runs on).
    public static HardwareInfo Current => current ??= Detect();

    private static HardwareInfo Detect()
    {
        string cpu = "Unknown CPU";
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            if (key?.GetValue("ProcessorNameString") is string name) cpu = name.Trim();
        }
        var status = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        int memoryGB = Win32.GlobalMemoryStatusEx(ref status) ? (int)Math.Round(status.ullTotalPhys / 1073741824.0) : 8;

        ComputeDevice? gpu = null;
        try
        {
            gpu = LlamaEngine.Devices().Where(d => d.IsGpu).OrderByDescending(d => d.TotalMiB).FirstOrDefault();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppStatus.Log($"couldn't list compute devices: {e.Message}");
        }
        return new HardwareInfo(cpu, memoryGB, gpu is null ? null : $"{gpu.Description} ({gpu.Name})", gpu?.TotalMiB ?? 0);
    }
}
