using System.IO;
using Microsoft.Win32;

namespace WordCatcher.App.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WordCatcherDesktop";
    private readonly string _registryPath;
    private readonly string _executablePath;

    public StartupService() : this(RunKey,
        Path.Combine(AppContext.BaseDirectory, "WordCatcher.App.exe")) { }

    internal StartupService(string registryPath, string executablePath)
    {
        _registryPath = registryPath;
        _executablePath = executablePath;
    }

    public bool IsEnabled
    {
        get
        {
            return !string.IsNullOrWhiteSpace(ReadCommand());
        }
    }

    public bool NeedsPathRepair => IsEnabled && !string.Equals(ReadCommand(),
        StartupCommand, StringComparison.OrdinalIgnoreCase);

    private string StartupCommand => $"\"{_executablePath}\" --startup";

    private string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath);
        return key?.GetValue(ValueName) as string;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && !File.Exists(_executablePath))
            throw new FileNotFoundException("找不到应用程序，请从安装目录启动后重试。", _executablePath);

        if (enabled)
        {
            // Run entries have a documented maximum command-line length of 260 characters.
            if (StartupCommand.Length > 260)
                throw new IOException("程序路径过长，请将应用移到较短的目录后重新开启自启动。");
            using var key = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true);
            key.SetValue(ValueName, StartupCommand, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
