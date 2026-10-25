using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OpenTypist;

/// Watches the power source and battery saver, and raises `Changed` (UI
/// thread). The Windows side of PowerMonitor.swift.
public sealed class PowerMonitor : IDisposable
{
    private readonly AppSettings settings;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private DispatcherTimer? timer;

    public bool OnBattery { get; private set; }
    public bool BatterySaver { get; private set; }
    public event Action? Changed;

    public PowerMonitor(AppSettings settings)
    {
        this.settings = settings;
        (OnBattery, BatterySaver) = Read();
    }

    public void Start()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Battery saver has no event here; it changes rarely.
        timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        timer.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var (battery, saver) = Read();
        if (battery == OnBattery && saver == BatterySaver) return;
        OnBattery = battery;
        BatterySaver = saver;
        Changed?.Invoke();
    }

    /// The behavior that applies right now; battery saver wins over battery.
    public PowerBehavior Behavior => PowerRules.Current(OnBattery, BatterySaver, settings.BatteryBehavior, settings.LowPowerBehavior);

    public string Situation => PowerRules.Situation(BatterySaver, settings.LowPowerBehavior);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static (bool OnBattery, bool BatterySaver) Read()
    {
        if (!GetSystemPowerStatus(out var status)) return (false, false);
        // ACLineStatus 0 = on battery (255 = unknown, e.g. a desktop).
        return (status.ACLineStatus == 0, status.SystemStatusFlag == 1);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        timer?.Stop();
    }
}
