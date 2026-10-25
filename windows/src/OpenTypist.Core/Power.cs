namespace OpenTypist;

/// What to do in a power situation (on battery, battery saver). A port of
/// PowerMonitor.swift's `PowerBehavior`.
public enum PowerBehavior { Normal, SmallerModel, Pause }

public static class PowerRules
{
    public static string Title(this PowerBehavior behavior) => behavior switch
    {
        PowerBehavior.SmallerModel => "Use the smaller model",
        PowerBehavior.Pause => "Pause suggestions",
        _ => "Keep going as usual",
    };

    /// The behavior that applies right now; battery saver wins over battery
    /// (like Low Power Mode on the Mac).
    public static PowerBehavior Current(bool onBattery, bool batterySaver, PowerBehavior battery, PowerBehavior saver)
    {
        if (batterySaver && saver != PowerBehavior.Normal) return saver;
        if (onBattery) return battery;
        return PowerBehavior.Normal;
    }

    public static string Situation(bool batterySaver, PowerBehavior saver) =>
        batterySaver && saver != PowerBehavior.Normal ? "in battery saver" : "on battery";

    /// Whether the model should be unloaded after `minutes` without typing.
    public static bool ShouldUnloadForIdle(bool enabled, int minutes, DateTime lastTyping, DateTime now) =>
        enabled && minutes > 0 && now - lastTyping > TimeSpan.FromMinutes(minutes);
}

/// Dotted version numbers, compared numerically ("0.10.0" > "0.9.2").
public static class Versions
{
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => v.TrimStart('v').Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    /// Release asset names, shared with release.yml.
    public static string WindowsZip(string version) => $"OpenTypist-{version}-win-x64.zip";
}
