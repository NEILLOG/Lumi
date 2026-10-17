using System.Runtime.Versioning;
using Lumi.Core.Secrets;
using Lumi.Platform.Secrets;

namespace Lumi.Platform.Tests;

public sealed class InMemorySecretStoreContractTests : SecretStoreContractTests
{
    protected override ISecretStore CreateStore() => new InMemorySecretStore();
}

/// <summary>檔案處理邏輯（用假的加密）：任何平台都能跑，Windows 以外也能驗證 DPAPI 實作共用的部分。</summary>
public sealed class EncryptedFileSecretStoreContractTests : SecretStoreContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-tests-" + Guid.NewGuid().ToString("N"));

    protected override ISecretStore CreateStore() =>
        new EncryptedFileSecretStore(Path.Combine(_directory, "secrets.json"), new FakeSecretProtector());

    ~EncryptedFileSecretStoreContractTests()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class WindowsDpapiSecretStoreContractTests : SecretStoreContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-tests-" + Guid.NewGuid().ToString("N"));

    protected override bool IsSupported => OperatingSystem.IsWindows();

    protected override string UnsupportedReason => "DPAPI 只在 Windows 可用";

    protected override ISecretStore CreateStore()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        return CreateWindowsStore(Path.Combine(_directory, "secrets.json"));
    }

    [SupportedOSPlatform("windows")]
    private static ISecretStore CreateWindowsStore(string path) =>
        new WindowsDpapiSecretStore(Microsoft.Extensions.Options.Options.Create(new SecretStoreOptions { DpapiFilePath = path }));

    ~WindowsDpapiSecretStoreContractTests()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>會真的寫進登入鑰匙圈，service 用測試專用名稱，不會碰到正式資料；每個測試結束都會刪除。</summary>
public sealed class MacKeychainSecretStoreContractTests : SecretStoreContractTests
{
    protected override bool IsSupported => OperatingSystem.IsMacOS();

    protected override string UnsupportedReason => "Keychain 只在 macOS 可用";

    protected override ISecretStore CreateStore()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        return CreateMacStore();
    }

    [SupportedOSPlatform("macos")]
    private static ISecretStore CreateMacStore() => new MacKeychainSecretStore("io.github.neillog.lumi.tests");
}
