using System.Text;
using Lumi.Core.Secrets;
using Lumi.Platform.Secrets;

namespace Lumi.Platform.Tests;

/// <summary>DPAPI 實作共用的檔案處理：檔案內沒有明文、損毀與解密失敗的處理、寫入序列化。</summary>
public sealed class EncryptedFileSecretStoreTests : IDisposable
{
    private const string Plain = "sk-ant-PLAINTEXT-MARKER-12345";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "nested", "secrets.json");
    private static readonly SecretKey Key = new("connection/dev-anthropic/api-key");

    private EncryptedFileSecretStore NewStore() => new(FilePath, new FakeSecretProtector());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task File_contains_no_plain_text_and_uses_expected_shape()
    {
        await NewStore().SetAsync(Key, new SecretValue(Plain));

        var bytes = await File.ReadAllBytesAsync(FilePath);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(Plain, json);
        Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Plain)) >= 0);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"connection/dev-anthropic/api-key\"", json);
    }

    [Fact]
    public async Task Value_survives_a_new_store_instance()
    {
        await NewStore().SetAsync(Key, new SecretValue(Plain));

        Assert.Equal(Plain, (await NewStore().GetAsync(Key))!.Reveal());
    }

    [Fact]
    public async Task No_temp_file_is_left_behind()
    {
        var store = NewStore();
        await store.SetAsync(Key, new SecretValue("a"));
        await store.SetAsync(Key, new SecretValue("b"));
        await store.DeleteAsync(Key);

        Assert.Equal(["secrets.json"], Directory.GetFiles(Path.GetDirectoryName(FilePath)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Delete_missing_does_not_create_file()
    {
        Assert.False(await NewStore().DeleteAsync(Key));

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Corrupt_file_throws_without_leaking_content()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, "{ not json " + Plain);

        var ex = await Assert.ThrowsAsync<SecretStoreException>(() => NewStore().GetAsync(Key));

        Assert.DoesNotContain(Plain, ex.ToString());
    }

    [Fact]
    public async Task Unsupported_version_throws()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, """{ "version": 99, "items": {} }""");

        await Assert.ThrowsAsync<SecretStoreException>(() => NewStore().GetAsync(Key));
    }

    [Fact]
    public async Task Undecryptable_item_throws_secret_store_exception()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var garbage = Convert.ToBase64String(Encoding.UTF8.GetBytes("not encrypted by this user"));
        await File.WriteAllTextAsync(FilePath, $$"""{ "version": 1, "items": { "{{Key.Name}}": "{{garbage}}" } }""");

        var ex = await Assert.ThrowsAsync<SecretStoreException>(() => NewStore().GetAsync(Key));

        Assert.Contains(Key.Name, ex.Message);
        // Exists 只看有沒有項目，不需要解密
        Assert.True(await NewStore().ExistsAsync(Key));
    }

    [Fact]
    public async Task Invalid_base64_throws_secret_store_exception()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, $$"""{ "version": 1, "items": { "{{Key.Name}}": "***not base64***" } }""");

        await Assert.ThrowsAsync<SecretStoreException>(() => NewStore().GetAsync(Key));
    }

    [Fact]
    public async Task Concurrent_sets_do_not_lose_entries()
    {
        var store = NewStore();
        var keys = Enumerable.Range(0, 30).Select(i => new SecretKey($"concurrent/k{i}")).ToList();

        await Task.WhenAll(keys.Select((k, i) => store.SetAsync(k, new SecretValue($"v{i}"))));

        for (var i = 0; i < keys.Count; i++)
            Assert.Equal($"v{i}", (await store.GetAsync(keys[i]))!.Reveal());
    }

    [Fact]
    public async Task Canceled_token_throws_without_writing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewStore().SetAsync(Key, new SecretValue(Plain), cts.Token));

        Assert.False(File.Exists(FilePath));
    }
}
