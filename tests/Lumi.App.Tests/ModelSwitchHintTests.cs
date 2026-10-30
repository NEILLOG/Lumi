using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Tests;

/// <summary>對話中途換模型的確認區（plan/23 第 7.2 節第 1 條）：上下文夠大時先確認，兩個按鈕各自的結果。</summary>
public sealed class ModelSwitchHintTests
{
    private sealed class Rig : IDisposable
    {
        public SettingsHost Host { get; } = new();
        public InMemoryUsageHintLog Log { get; } = new();
        public UsageHintCoordinator Hints { get; }
        public ModelSwitcherViewModel Switcher { get; }
        public int ContextTokens { get; set; } = 92_000;
        public int NewConversations { get; private set; }

        public Rig(bool withHints = true)
        {
            OriginalModelId = Host.Catalog.Current.WorkbenchModelId;
            Hints = new UsageHintCoordinator(Log, Host.Catalog, new FakeTimeProvider());
            Switcher = new ModelSwitcherViewModel(Host.Catalog, Host.Status, new FakeSettingsWindowService(), new SyncUiDispatcher(), () => false,
                afterSelect: null,
                currentContextTokens: () => ContextTokens,
                newConversation: () =>
                {
                    NewConversations++;
                    return Task.CompletedTask;
                },
                hints: withHints ? Hints : null,
                currentConversationId: () => "c1");
        }

        public ModelPickerEntry Entry(string text) => Switcher.Entries.Single(e => e.Text == text);

        public string? OriginalModelId { get; }

        public string? CurrentModelId => Host.Catalog.Current.WorkbenchModelId;

        public void Dispose() => Host.Dispose();
    }

    /// <summary>目前是預設的模型（SettingsHost 的預設）；另外加了 Ollama 的 Llama 3 與 Qwen 3 可以選。</summary>
    private static async Task<Rig> CreateAsync(bool withHints = true)
    {
        var rig = new Rig(withHints);
        var ollama = new ConnectionSettings("ollama", "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");
        await rig.Host.Catalog.SaveConnectionAsync(ollama, null);
        await rig.Host.Catalog.SaveModelAsync(new ModelSettings("m-llama", "ollama", "llama3", "Llama 3", 4096, 128_000, false));
        await rig.Host.Catalog.SaveModelAsync(new ModelSettings("m-qwen", "ollama", "qwen3:8b", "Qwen 3", 4096, 32_000, false));
        return rig;
    }

    [Fact]
    public async Task BelowTheThreshold_SwitchesDirectly_WithoutAConfirmation()
    {
        using var rig = await CreateAsync();
        rig.ContextTokens = UsageHintThresholds.ModelSwitchContextTokens - 1;

        var switched = await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        Assert.True(switched);
        Assert.Equal("m-llama", rig.CurrentModelId);
        Assert.Null(rig.Switcher.PendingSwitch);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task AtTheThreshold_HoldsTheSwitch_AndShowsTheConfirmationText()
    {
        using var rig = await CreateAsync();

        var switched = await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        Assert.False(switched);
        Assert.Equal(rig.OriginalModelId, rig.CurrentModelId);
        Assert.True(rig.Switcher.HasPendingSwitch);
        Assert.Equal("Llama 3", rig.Switcher.PendingSwitch!.Target.Text);
        Assert.Equal(string.Format(Strings.Hint_ModelSwitchFormat, "92K"), rig.Switcher.PendingSwitch.Text);
        Assert.Single(rig.Log.Entries);
        Assert.Equal(UsageHintOutcome.Shown, rig.Log.Entries[0].Outcome);
    }

    [Fact]
    public async Task SwitchAnyway_SwitchesNow_AndRecordsActed()
    {
        using var rig = await CreateAsync();
        await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        await rig.Switcher.SwitchAnywayCommand.ExecuteAsync(null);

        Assert.Equal("m-llama", rig.CurrentModelId);
        Assert.False(rig.Switcher.HasPendingSwitch);
        Assert.Equal(0, rig.NewConversations);
        Assert.Equal(UsageHintOutcome.Acted, rig.Log.Entries.Single().Outcome);
    }

    [Fact]
    public async Task SwitchInNewConversation_OpensAConversationFirst_ThenSwitches_AndRecordsActed()
    {
        using var rig = await CreateAsync();
        await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        await rig.Switcher.SwitchInNewConversationCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.NewConversations);
        Assert.Equal("m-llama", rig.CurrentModelId);
        Assert.False(rig.Switcher.HasPendingSwitch);
        Assert.Equal(UsageHintOutcome.Acted, rig.Log.Entries.Single().Outcome);
    }

    [Fact]
    public async Task DismissingThePendingSwitch_KeepsTheModel_AndRecordsDismissed()
    {
        using var rig = await CreateAsync();
        await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        await rig.Switcher.DismissPendingSwitchAsync();

        Assert.Equal(rig.OriginalModelId, rig.CurrentModelId);
        Assert.False(rig.Switcher.HasPendingSwitch);
        Assert.Equal(UsageHintOutcome.Dismissed, rig.Log.Entries.Single().Outcome);
    }

    [Fact]
    public async Task ChoosingAnotherModelWhileConfirming_DismissesTheOldOne_AndAsksAgain_OnlyIfNotAskedBefore()
    {
        using var rig = await CreateAsync();
        await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        // 同一個模型再選一次：確認區照舊，不另外寫紀錄
        Assert.False(await rig.Switcher.SelectAsync(rig.Entry("Llama 3")));
        Assert.Single(rig.Log.Entries);

        // 換成別的模型：舊的確認區收起（記為 Dismissed），這則對話已經提示過，所以直接換
        Assert.True(await rig.Switcher.SelectAsync(rig.Entry("Qwen 3")));
        Assert.Equal("m-qwen", rig.CurrentModelId);
        Assert.Equal(UsageHintOutcome.Dismissed, rig.Log.Entries[0].Outcome);
        Assert.Single(rig.Log.Entries);   // 已經提示過，不再寫新的紀錄
    }

    [Fact]
    public async Task MutedRule_SwitchesDirectly()
    {
        using var rig = await CreateAsync();
        await rig.Host.Catalog.SetUsageAsync(new UsageSettings(MutedHints: [UsageHintIds.ModelSwitch]));

        var switched = await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        Assert.True(switched);
        Assert.Equal("m-llama", rig.CurrentModelId);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task WithoutACoordinator_SwitchesDirectly_AsBefore()
    {
        using var rig = await CreateAsync(withHints: false);

        var switched = await rig.Switcher.SelectAsync(rig.Entry("Llama 3"));

        Assert.True(switched);
        Assert.Equal("m-llama", rig.CurrentModelId);
        Assert.False(rig.Switcher.HasPendingSwitch);
    }
}
