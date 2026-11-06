using Microsoft.Win32;

namespace ReadNow.Services;

/// <summary>
/// 开机启动项管理：读写 HKCU\...\CurrentVersion\Run 注册表，无需管理员权限。
/// 勾选状态直接以注册表为准，天然持久。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReadNow";

    private static string ExePath => Environment.ProcessPath
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string s
                   && string.Equals(s.Trim('"'), ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key == null) return;
        if (enable)
            key.SetValue(ValueName, $"\"{ExePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
