using System.Text.RegularExpressions;

namespace Lumi.Core.Commands;

/// <summary>
/// 指令的擋掉清單與風險提示（M31 設計 2）。
/// 擋掉的寫法直接回原因（不詢問）；沒擋的依規則找出風險，顯示在確認卡上。
/// 防手滑用，不是安全邊界：靜態比對擋不住刻意繞過的寫法，真正的防線是使用者每次都要按「允許執行」。
/// </summary>
public sealed partial class CommandGuard(string skillsDirectory) : ICommandGuard
{
    private const int MaxLength = 8000;

    private const string EmptyOrTooLong = "指令是空的或超過 8,000 字";
    private const string Elevation = "要求系統管理員權限";
    private const string Encoded = "指令內容被編碼或動態產生，無法顯示給你確認";
    private const string DownloadAndRun = "下載後直接執行網路上的程式";
    private const string SystemDamage = "會影響整台電腦（格式化、關機、登錄檔、開機設定）";
    private const string RootOrHomeDelete = "會整個刪除磁碟根目錄或你的使用者資料夾";
    private const string SecuritySettings = "會改變系統的安全設定";

    /// <summary>技能資料夾（統一成 <c>/</c>，去掉結尾的 <c>/</c>）；空字串表示只比對 <c>Lumi/skills</c>。</summary>
    private readonly string _skillsDirectory = (skillsDirectory ?? "").Replace('\\', '/').TrimEnd('/');

    public CommandCheck Check(string command)
    {
        var length = command.Trim().Length;
        if (length == 0 || length > MaxLength)
            return new CommandCheck(EmptyOrTooLong, []);

        var text = Normalize(command);

        var reason = BlockReason(text);
        if (reason is not null)
            return new CommandCheck(reason, []);

        return new CommandCheck(null, Risks(text));
    }

    /// <summary>比對用文字：拿掉 PowerShell 的 <c>`</c> 與 cmd 的 <c>^</c>，連續空白壓成一個空格。</summary>
    private static string Normalize(string command) =>
        WhitespaceRun().Replace(command.Replace("`", "").Replace("^", ""), " ").Trim();

    /// <summary>依設計 2 表格的順序比對，第一個符合的原因為準。</summary>
    private string? BlockReason(string text)
    {
        if (Any(text, Sudo(), RunAsVerb(), SuAtStart()))
            return Elevation;

        if (Any(text, EncodedArgument(), InvokeExpression(), ScriptBlockCreate(), EvalAtStart(), Base64Decode()))
            return Encoded;

        if (DownloadPipeToShell().IsMatch(text))
            return DownloadAndRun;

        if (Any(text, FormatDrive(), FormatVolume(), Diskpart(), Mkfs(), DdToDevice(), DiskutilErase(),
                PowerState(), RegDelete(), RegistryRemove(), Bcdedit(), VssadminDelete(), CipherWipe()))
            return SystemDamage;

        if (Any(text, RmRootOrHome(), NoPreserveRoot(), RemoveItemRootOrHome(), RdRoot(), DelRoot()))
            return RootOrHomeDelete;

        if (Any(text, SetExecutionPolicy(), SetMpPreference(), AddMpPreference(), NetshAdvfirewall(),
                SpctlMasterDisable(), Csrutil()))
            return SecuritySettings;

        return null;
    }

    /// <summary>風險提示，依 <see cref="CommandRisk"/> 列舉順序。</summary>
    private List<CommandRisk> Risks(string text)
    {
        var risks = new List<CommandRisk>();

        if (Any(text, DeleteWords(), GitDestructive()))
            risks.Add(CommandRisk.Deletes);

        var installs = Any(text, PackageInstall(), Npx(), PipInstall(), BrewInstall(), WingetInstall(),
            ChocoInstall(), InstallModule(), InstallPackage());
        if (installs)
            risks.Add(CommandRisk.InstallsPackages);

        if (installs || Any(text, NetworkCommand(), GitNetwork()))
            risks.Add(CommandRisk.Network);

        if (Any(text, SystemChangeCommand(), RegAdd(), ScConfig(), ServiceControl(), DefaultsWrite(), KillAtStart()))
            risks.Add(CommandRisk.ChangesSystem);

        if (TouchesSkills(text))
            risks.Add(CommandRisk.TouchesSkills);

        return risks;
    }

    /// <summary>指令裡出現技能資料夾，或 <c>Lumi/skills</c>（兩種斜線都算，不分大小寫）。</summary>
    private bool TouchesSkills(string text)
    {
        var normalized = text.Replace('\\', '/');
        if (normalized.Contains("Lumi/skills", StringComparison.OrdinalIgnoreCase))
            return true;

        return _skillsDirectory.Length > 0 && normalized.Contains(_skillsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Any(string text, params Regex[] patterns) => patterns.Any(p => p.IsMatch(text));

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    // 提權
    [GeneratedRegex(@"\b(sudo|doas|runas)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sudo();

    [GeneratedRegex(@"-Verb\s+RunAs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunAsVerb();

    [GeneratedRegex(@"(^|[;&|]\s*)su(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuAtStart();

    // 編碼或動態執行
    [GeneratedRegex(@"\s-e[a-z]*\s+[A-Za-z0-9+/]{20,}={0,2}(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodedArgument();

    [GeneratedRegex(@"\b(Invoke-Expression|iex)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InvokeExpression();

    [GeneratedRegex(@"\[ScriptBlock\]::Create", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptBlockCreate();

    [GeneratedRegex(@"(^|[;&|]\s*)eval\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EvalAtStart();

    [GeneratedRegex(@"base64\s+(-d|--decode|-D)\b.*\|\s*(ba|z)?sh\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Base64Decode();

    // 下載後直接執行
    [GeneratedRegex(@"\b(curl|wget|iwr|irm|Invoke-WebRequest|Invoke-RestMethod|DownloadString)\b.*\|\s*(sh|bash|zsh|iex|Invoke-Expression|powershell|pwsh|cmd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DownloadPipeToShell();

    // 破壞系統
    [GeneratedRegex(@"\bformat(\.com)?\s+[a-z]:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatDrive();

    [GeneratedRegex(@"\bFormat-Volume\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatVolume();

    [GeneratedRegex(@"\bdiskpart\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Diskpart();

    [GeneratedRegex(@"\bmkfs", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mkfs();

    [GeneratedRegex(@"\bdd\s.*\bof=/dev/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DdToDevice();

    [GeneratedRegex(@"\bdiskutil\s+(erase|partition|zero|secureErase)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiskutilErase();

    [GeneratedRegex(@"\b(shutdown|Stop-Computer|Restart-Computer|reboot|halt)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerState();

    [GeneratedRegex(@"\breg(\.exe)?\s+delete\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegDelete();

    [GeneratedRegex(@"\b(Remove-Item|ri|rm|del)\b.*\b(HKLM|HKCU|HKCR|HKU):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegistryRemove();

    [GeneratedRegex(@"\bbcdedit\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bcdedit();

    [GeneratedRegex(@"\bvssadmin\s+delete\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VssadminDelete();

    [GeneratedRegex(@"\bcipher\s+/w", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CipherWipe();

    // 刪除根目錄或使用者資料夾
    [GeneratedRegex(@"\brm\s+(-[a-z]+\s+)*-[a-z]*r[a-z]*\s+(-[a-z]+\s+)*(""?(/|/\*|~|~/|~/\*|\$HOME|\$HOME/\*)""?)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RmRootOrHome();

    [GeneratedRegex(@"--no-preserve-root", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoPreserveRoot();

    [GeneratedRegex(@"\b(Remove-Item|ri|rm|del|rd|rmdir)\b.*-r[a-z]*\b.*\s(""?([a-z]:\\?|~|\$HOME|\$env:USERPROFILE|\$env:HOMEDRIVE)""?)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoveItemRootOrHome();

    [GeneratedRegex(@"\b(rd|rmdir)\s+/s\b.*\s(""?([a-z]:\\?|%USERPROFILE%|%HOMEDRIVE%)""?)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RdRoot();

    [GeneratedRegex(@"\b(del|erase)\s+(/[a-z]\s+)*/s\b.*\s(""?([a-z]:\\\*?|%USERPROFILE%\\?\*?)""?)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelRoot();

    // 改變安全設定
    [GeneratedRegex(@"\bSet-ExecutionPolicy\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetExecutionPolicy();

    [GeneratedRegex(@"\bSet-MpPreference\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetMpPreference();

    [GeneratedRegex(@"\bAdd-MpPreference\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddMpPreference();

    [GeneratedRegex(@"\bnetsh\s+advfirewall\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetshAdvfirewall();

    [GeneratedRegex(@"\bspctl\s+--master-disable\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpctlMasterDisable();

    [GeneratedRegex(@"\bcsrutil\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Csrutil();

    // 風險提示：刪除
    [GeneratedRegex(@"\b(rm|rmdir|rd|del|erase|Remove-Item|ri|unlink|shred)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteWords();

    [GeneratedRegex(@"\bgit\s+(clean|reset\s+--hard)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitDestructive();

    // 風險提示：安裝套件
    [GeneratedRegex(@"\b(npm|pnpm|yarn)\s+(i|install|add|ci)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageInstall();

    [GeneratedRegex(@"\bnpx\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Npx();

    [GeneratedRegex(@"\bpip3?\s+install\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PipInstall();

    [GeneratedRegex(@"\bbrew\s+install\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrewInstall();

    [GeneratedRegex(@"\bwinget\s+install\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WingetInstall();

    [GeneratedRegex(@"\bchoco\s+install\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChocoInstall();

    [GeneratedRegex(@"\bInstall-Module\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallModule();

    [GeneratedRegex(@"\bInstall-Package\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallPackage();

    // 風險提示：網路
    [GeneratedRegex(@"\b(curl|wget|iwr|irm|Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer|scp|sftp|ssh|ftp|rsync)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkCommand();

    [GeneratedRegex(@"\bgit\s+(clone|pull|push|fetch)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitNetwork();

    // 風險提示：變更系統
    [GeneratedRegex(@"\b(setx|Set-ItemProperty|New-ItemProperty|launchctl|crontab|schtasks|Register-ScheduledTask|chmod|chown|taskkill|Stop-Process|killall|pkill)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SystemChangeCommand();

    [GeneratedRegex(@"\breg(\.exe)?\s+add\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegAdd();

    [GeneratedRegex(@"\bsc(\.exe)?\s+(config|create|stop|delete)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScConfig();

    [GeneratedRegex(@"\b(Stop|Set)-Service\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceControl();

    [GeneratedRegex(@"\bdefaults\s+write\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DefaultsWrite();

    [GeneratedRegex(@"(^|[;&|]\s*)kill\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KillAtStart();
}
