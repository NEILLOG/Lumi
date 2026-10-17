using Lumi.Core.Secrets;

namespace Lumi.Platform.Tests;

/// <summary>
/// <see cref="ISecretStore"/> 的契約測試：每個實作（記憶體、DPAPI、Keychain）都要通過同一套。
/// 子類別若在目前平台不支援，整組測試會被略過（不是通過）。
/// </summary>
public abstract class SecretStoreContractTests : IDisposable
{
    private readonly List<SecretKey> _usedKeys = [];
    private ISecretStore? _store;

    /// <summary>這個實作在目前的作業系統上能不能跑。</summary>
    protected virtual bool IsSupported => true;

    protected virtual string UnsupportedReason => "此實作不支援目前的作業系統";

    protected abstract ISecretStore CreateStore();

    protected ISecretStore Store
    {
        get
        {
            Skip.IfNot(IsSupported, UnsupportedReason);
            return _store ??= CreateStore();
        }
    }

    /// <summary>每個測試用不重複的名稱，避免互相影響，也不會碰到正式資料。</summary>
    protected SecretKey NewKey(string suffix = "k")
    {
        var key = new SecretKey($"contract-test/{Guid.NewGuid():N}/{suffix}");
        _usedKeys.Add(key);
        return key;
    }

    public void Dispose()
    {
        if (_store is not null)
        {
            foreach (var key in _usedKeys)
                _store.DeleteAsync(key).GetAwaiter().GetResult();
        }
        (_store as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public async Task Get_missing_returns_null()
    {
        Assert.Null(await Store.GetAsync(NewKey()));
    }

    [SkippableFact]
    public async Task Set_then_get_returns_value()
    {
        var key = NewKey();

        await Store.SetAsync(key, new SecretValue("sk-ant-abc123"));

        Assert.Equal("sk-ant-abc123", (await Store.GetAsync(key))!.Reveal());
    }

    [SkippableFact]
    public async Task Set_overwrites_existing_value()
    {
        var key = NewKey();
        await Store.SetAsync(key, new SecretValue("first"));

        await Store.SetAsync(key, new SecretValue("second"));

        Assert.Equal("second", (await Store.GetAsync(key))!.Reveal());
    }

    [SkippableFact]
    public async Task Delete_removes_value_and_returns_true()
    {
        var key = NewKey();
        await Store.SetAsync(key, new SecretValue("v"));

        Assert.True(await Store.DeleteAsync(key));

        Assert.Null(await Store.GetAsync(key));
        Assert.False(await Store.ExistsAsync(key));
    }

    [SkippableFact]
    public async Task Delete_missing_returns_false()
    {
        Assert.False(await Store.DeleteAsync(NewKey()));
    }

    [SkippableFact]
    public async Task Exists_reflects_state()
    {
        var key = NewKey();
        Assert.False(await Store.ExistsAsync(key));

        await Store.SetAsync(key, new SecretValue("v"));

        Assert.True(await Store.ExistsAsync(key));
    }

    [SkippableTheory]
    [InlineData("金鑰：中文測試")]
    [InlineData("sk-❤️‍🔥-👨‍👩‍👧-🇹🇼")]
    [InlineData("  leading and trailing spaces  ")]
    [InlineData("line1\nline2\ttab")]
    public async Task Unicode_and_special_characters_round_trip(string text)
    {
        var key = NewKey();

        await Store.SetAsync(key, new SecretValue(text));

        Assert.Equal(text, (await Store.GetAsync(key))!.Reveal());
    }

    [SkippableFact]
    public async Task Long_value_round_trips()
    {
        var key = NewKey();
        var text = string.Concat(Enumerable.Range(0, 4096 / 8).Select(i => $"abc{i:D4}-"));

        await Store.SetAsync(key, new SecretValue(text));

        Assert.Equal(text, (await Store.GetAsync(key))!.Reveal());
    }

    [SkippableFact]
    public async Task Different_keys_do_not_affect_each_other()
    {
        var a = NewKey("a");
        var b = NewKey("b");
        await Store.SetAsync(a, new SecretValue("value-a"));
        await Store.SetAsync(b, new SecretValue("value-b"));

        await Store.SetAsync(a, new SecretValue("value-a2"));
        await Store.DeleteAsync(b);

        Assert.Equal("value-a2", (await Store.GetAsync(a))!.Reveal());
        Assert.Null(await Store.GetAsync(b));
    }

    [SkippableFact]
    public async Task Default_key_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SetAsync(default, new SecretValue("v")));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.DeleteAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ExistsAsync(default));
    }
}
