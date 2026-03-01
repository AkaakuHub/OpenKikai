using System.IO;
using Microsoft.Win32;

namespace LLMeta.App.Services;

public sealed class StartupRegistryService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "LLMeta";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string;
    }

    public void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, Quote(executablePath));
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        key?.DeleteValue(ValueName, false);
    }

    public string ResolveExecutablePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "LLMeta.App.exe");
    }

    private static string Quote(string path) => $"\"{path}\"";
}
