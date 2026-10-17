namespace Lumi.Platform;

public sealed class SecretStoreOptions
{
    /// <summary>macOS Keychain 的 service 名稱，同 bundle id，定案後不得更改（agent.md 第 10 節）。</summary>
    public const string DefaultKeychainService = "io.github.neillog.lumi";

    /// <summary>Keychain 的 service 名稱。測試請改用 <c>io.github.neillog.lumi.tests</c>，不要碰到正式資料。</summary>
    public string KeychainService { get; set; } = DefaultKeychainService;

    /// <summary>Windows DPAPI 加密檔的路徑；null 表示 <c>%APPDATA%\Lumi\secrets.json</c>。</summary>
    public string? DpapiFilePath { get; set; }

    public string ResolveDpapiFilePath() =>
        DpapiFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi", "secrets.json");
}
