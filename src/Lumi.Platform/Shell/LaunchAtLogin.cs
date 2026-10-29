using System.Security;
using System.Text;
using Lumi.Core.Platform;
using Microsoft.Win32;

namespace Lumi.Platform.Shell;

/// <summary>
/// 登入系統後自動啟動（<see cref="ILaunchAtLogin"/>）。Windows：目前使用者的登錄檔 <c>Run</c>（不需要系統管理員）；
/// macOS：<c>~/Library/LaunchAgents</c> 的 plist（用 <c>open -a</c> 開啟 .app 套件，Dock 圖示與啟動行為和手動開啟相同）。
/// 不在安裝位置執行（<c>dotnet run</c>、不在 .app 裡的執行檔）時不支援，避免把開發用的路徑寫進系統。
/// </summary>
public sealed class LaunchAtLogin : ILaunchAtLogin
{
    internal const string WindowsValueName = "Lumi";
    internal const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string MacLabel = "io.github.neillog.lumi";

    private readonly string? _processPath;
    private readonly string _launchAgentsDirectory;

    public LaunchAtLogin() : this(Environment.ProcessPath, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"))
    {
    }

    internal LaunchAtLogin(string? processPath, string launchAgentsDirectory)
    {
        _processPath = processPath;
        _launchAgentsDirectory = launchAgentsDirectory;
    }

    private string PlistPath => Path.Combine(_launchAgentsDirectory, MacLabel + ".plist");

    public bool IsSupported => OperatingSystem.IsWindows() ? WindowsTarget(_processPath) is not null
        : OperatingSystem.IsMacOS() && MacBundle(_processPath) is not null;

    public bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
            return key?.GetValue(WindowsValueName) is string value && value.Length > 0;
        }

        return OperatingSystem.IsMacOS() && File.Exists(PlistPath);
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                SetWindows(enabled);
            else if (OperatingSystem.IsMacOS())
                SetMac(enabled);
        }
        catch (Exception ex) when (ex is SecurityException or System.ComponentModel.Win32Exception)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    private void SetWindows(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true);
        if (!enabled)
        {
            key.DeleteValue(WindowsValueName, throwOnMissingValue: false);
            return;
        }

        var target = WindowsTarget(_processPath) ?? throw new InvalidOperationException("目前的執行方式不支援開機自動啟動。");
        key.SetValue(WindowsValueName, WindowsCommand(target));
    }

    private void SetMac(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(PlistPath))
                File.Delete(PlistPath);
            return;
        }

        var bundle = MacBundle(_processPath) ?? throw new InvalidOperationException("目前的執行方式不支援開機自動啟動。");
        Directory.CreateDirectory(_launchAgentsDirectory);
        File.WriteAllText(PlistPath, MacPlist(bundle), new UTF8Encoding(false));
    }

    /// <summary>執行檔路徑；開發時用 <c>dotnet</c> 啟動（執行檔是 dotnet 本身）不算。</summary>
    internal static string? WindowsTarget(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return null;
        var file = processPath[(processPath.LastIndexOfAny(['\\', '/']) + 1)..];   // 兩種分隔符號都認，測試在哪個系統都一樣
        var name = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase) ? null : processPath;
    }

    /// <summary>登錄檔的值：路徑加引號，路徑含空白也能正確啟動。</summary>
    internal static string WindowsCommand(string exePath) => "\"" + exePath + "\"";

    /// <summary>執行檔所在的 .app 套件資料夾（往上找副檔名是 <c>.app</c> 的那一層）；不在套件裡回 null。</summary>
    internal static string? MacBundle(string? processPath)
    {
        for (var directory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
             !string.IsNullOrEmpty(directory);
             directory = Path.GetDirectoryName(directory))
        {
            if (directory.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return directory;
        }

        return null;
    }

    internal static string MacPlist(string bundlePath)
    {
        var path = SecurityElement.Escape(bundlePath);   // 路徑裡的 & < > 要轉義
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{MacLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/usr/bin/open</string>
                    <string>-a</string>
                    <string>{path}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>LimitLoadToSessionType</key>
                <string>Aqua</string>
            </dict>
            </plist>
            """ + "\n";
    }
}
