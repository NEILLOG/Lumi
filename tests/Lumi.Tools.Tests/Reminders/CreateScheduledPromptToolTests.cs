using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Tools.Reminders;
using Lumi.Tools.Tests.Memory;

namespace Lumi.Tools.Tests.Reminders;

/// <summary>M24 24D：<c>create_scheduled_prompt</c> 的參數驗證、確認卡、授權規則、範圍與回傳。</summary>
public sealed class CreateScheduledPromptToolTests
{
    // 2026-10-23（週五）09:05 台北
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 5, 0, TimeSpan.Zero);

    private readonly ReminderRig _rig = new(Now);
    private readonly FakePromptReminderService _service;
    private readonly FakeModelCatalog _catalog = new();
    private readonly CreateScheduledPromptTool _tool;

    public CreateScheduledPromptToolTests()
    {
        _service = new FakePromptReminderService(_rig);
        _tool = new CreateScheduledPromptTool(_service, _rig.Options, _catalog);
    }

    private ToolContext Context(IFileScope? scope = null) =>
        new("c1", ChatMode.Workbench, _rig.Clock, scope ?? EmptyFileScope.Instance);

    private Task<ToolResult> Run(string json, IFileScope? scope = null) => _tool.ExecuteAsync(json, Context(scope), default);

    private ToolPreview Preview(string json, IFileScope? scope = null) => _tool.Preview(json, Context(scope));

    /// <summary>每天 09:00 台北，名稱「晨間整理」，prompt「整理資料夾」。</summary>
    private const string DailyJson = """{"name":"晨間整理","prompt":"整理資料夾","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""";

    /// <summary>每天 09:00 的排程 JSON，額外欄位接在後面。額外欄位用單引號寫，這裡再換成雙引號，測試裡就不用一直跳脫。</summary>
    private static string DailyWith(string extra) =>
        $$"""{"name":"晨間整理","prompt":"整理資料夾","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei",{{extra.Replace('\'', '"')}}}""";

    private static AppSettings ModelCatalogWithDefault()
    {
        var connection = new ConnectionSettings("c1", "測試供應商", ProviderFormat.OpenAICompatible, "https://example.test");
        var model = new ModelSettings("m1", "c1", "gpt-test", "測試模型", 1024, 200_000, false);
        return AppSettings.Empty with { Connections = [connection], Models = [model], WorkbenchModelId = "m1" };
    }

    // ---- 描述符與說明 ----

    [Fact]
    public void Descriptor_IsWriteBothAlwaysAsk_AndNeverAlwaysAllowed()
    {
        var d = _tool.Descriptor;

        Assert.Equal("create_scheduled_prompt", d.Name);
        Assert.Equal("建立排程", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
        Assert.Equal(ToolGroups.Reminders, d.Group);
        Assert.True(d.AlwaysAsk);
        Assert.False(d.CanAlwaysAllow);
        Assert.False(string.IsNullOrWhiteSpace(d.Summary));
    }

    [Fact]
    public void Description_SaysNotRunningMeansNoTrigger_AndNamesTheRightTools()
    {
        var description = _tool.Descriptor.Description;

        Assert.Contains("Lumi 沒有在執行時不會觸發", description);
        Assert.Contains("2 小時", description);
        Assert.Contains("「排程」分頁", description);
        Assert.Contains("create_reminder", description);
        Assert.Contains("像上次那樣", description);
    }

    [Fact]
    public void Schema_ExposesSuggestedAndGrantedPermissions_WithSnakeCaseNames()
    {
        var properties = _tool.InputSchema["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("name"));
        Assert.True(properties.ContainsKey("prompt"));
        Assert.True(properties.ContainsKey("suggested_permissions"));
        Assert.True(properties.ContainsKey("granted_permissions"));
        Assert.True(properties.ContainsKey("day_of_month"));
    }

    // ---- 參數驗證 ----

    [Theory]
    [InlineData("""{"name":"  ","prompt":"p","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"name":"排程","prompt":"   ","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"prompt":"p","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"name":"排程","prompt":"p","at":"not a time","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"name":"排程","prompt":"p","at":"2026-10-25T09:00","time_zone":"Mars/Olympus"}""")]
    [InlineData("""{"name":"排程","prompt":"p"}""")]
    [InlineData("""{"name":"排程","prompt":"p","at":"2026-10-25T09:00","recurrence":"daily","time":"09:00","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"name":"排程","prompt":"p","recurrence":"daily","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"name":"排程","prompt":"p","recurrence":"weekly","time":"09:00","days":[],"time_zone":"Asia/Taipei"}""")]
    public async Task InvalidArguments_AreErrorResults_AndNothingIsCreated(string json)
    {
        var result = await Run(json);

        Assert.True(result.IsError);
        Assert.Empty(_service.Requests);
    }

    [Fact]
    public async Task EmptyPrompt_ErrorMentionsPrompt()
    {
        var result = await Run("""{"name":"排程","prompt":"","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("prompt", result.Content);
    }

    [Fact]
    public async Task NameOfExactlyMaxLength_IsAccepted()
    {
        var name = new string('名', _rig.Options.MaxTitleChars);

        var result = await Run($$"""{"name":"{{name}}","prompt":"p","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal(name, _service.Requests.Single().Name);
    }

    [Fact]
    public async Task NameTooLong_IsErrorResult()
    {
        var name = new string('名', _rig.Options.MaxTitleChars + 1);

        var result = await Run($$"""{"name":"{{name}}","prompt":"p","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("name", result.Content);
        Assert.Empty(_service.Requests);
    }

    [Fact]
    public async Task PromptTooLong_IsErrorResult()
    {
        var prompt = new string('字', _rig.Options.MaxPromptChars + 1);

        var result = await Run($$"""{"name":"排程","prompt":"{{prompt}}","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.True(result.IsError);
        Assert.Contains("prompt", result.Content);
        Assert.Empty(_service.Requests);
    }

    [Fact]
    public async Task PromptAtMaxLength_IsAccepted()
    {
        var prompt = new string('字', _rig.Options.MaxPromptChars);

        var result = await Run($$"""{"name":"排程","prompt":"{{prompt}}","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError, result.Content);
    }

    [Fact]
    public async Task TimingIsParsedLikeCreateReminder_OneTime()
    {
        var result = await Run("""{"name":"排程","prompt":"p","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError, result.Content);
        var request = _service.Requests.Single();
        var oneTime = Assert.IsType<OneTimeTrigger>(request.Trigger);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(8)), oneTime.At);
        Assert.Equal("Asia/Taipei", request.TimeZoneId);
    }

    [Fact]
    public async Task TimingIsParsedLikeCreateReminder_Recurring()
    {
        var result = await Run("""{"name":"每週","prompt":"p","recurrence":"weekly","days":["monday","5"],"time":"18:30","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError, result.Content);
        var recurrence = Assert.IsType<RecurrenceTrigger>(_service.Requests.Single().Trigger);
        Assert.Equal(RecurrenceKind.Weekly, recurrence.Kind);
        Assert.Equal(new TimeOnly(18, 30), recurrence.TimeOfDay);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], recurrence.Days);
    }

    // ---- 授權規則 ----

    [Fact]
    public async Task WithoutGrantedPermissions_DestructiveSuggestionIsIgnored()
    {
        var result = await Run(DailyWith("'suggested_permissions':['write','destructive']"));

        Assert.False(result.IsError, result.Content);
        var grant = _service.Requests.Single().Grant;
        Assert.True(grant.Write);
        Assert.False(grant.Network);
        Assert.False(grant.Destructive);
    }

    [Fact]
    public async Task WithoutSuggestedOrGranted_NothingIsGranted()
    {
        var result = await Run(DailyJson);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(ScheduleGrant.None, _service.Requests.Single().Grant);
    }

    [Fact]
    public async Task GrantedPermissionsWithDestructive_AreAdopted()
    {
        var result = await Run(DailyWith("'suggested_permissions':['write'],'granted_permissions':['write','destructive']"));

        Assert.False(result.IsError, result.Content);
        var grant = _service.Requests.Single().Grant;
        Assert.True(grant.Write);
        Assert.False(grant.Network);
        Assert.True(grant.Destructive);
        Assert.Contains("destructive", result.Content);
    }

    [Fact]
    public async Task GrantedPermissions_OverrideSuggestion_AndTheDifferenceIsExplained()
    {
        var result = await Run(DailyWith("'suggested_permissions':['network'],'granted_permissions':['write']"));

        Assert.False(result.IsError, result.Content);
        var grant = _service.Requests.Single().Grant;
        Assert.True(grant.Write);
        Assert.False(grant.Network);

        var json = ReminderJson.Parse(result.Content);
        Assert.Contains("實際授權與建議不同", json.GetProperty("permission_note").GetString()!);
    }

    [Fact]
    public async Task GrantedPermissionsMatchingSuggestion_HaveNoPermissionNote()
    {
        var result = await Run(DailyWith("'suggested_permissions':['write'],'granted_permissions':['write']"));

        Assert.False(result.IsError, result.Content);
        Assert.False(ReminderJson.Parse(result.Content).TryGetProperty("permission_note", out _));
    }

    [Fact]
    public async Task SuggestedDestructiveNotGranted_IsExplainedInResult()
    {
        var result = await Run(DailyWith("'suggested_permissions':['destructive']"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("建議中的 destructive 已忽略", ReminderJson.Parse(result.Content).GetProperty("permission_note").GetString()!);
    }

    [Fact]
    public async Task PermissionNames_AreCaseAndSpaceInsensitive()
    {
        var result = await Run(DailyWith("'suggested_permissions':[' Network ','WRITE','other']"));

        Assert.False(result.IsError, result.Content);
        var grant = _service.Requests.Single().Grant;
        Assert.True(grant.Write);
        Assert.True(grant.Network);
    }

    [Fact]
    public async Task SuggestedWithUnknownValues_OnlyWriteAndNetworkCount()
    {
        var result = await Run(DailyWith("'suggested_permissions':['read','memory','destructive']"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal(ScheduleGrant.None, _service.Requests.Single().Grant);
    }

    // ---- 範圍 ----

    [Fact]
    public async Task ScopeRoots_FolderAndFile_AreCarriedIntoTheSchedule()
    {
        var scope = new FakeFileScope(
            new ScopeRoot("報表", "/Users/me/報表", true),
            new ScopeRoot("說明.txt", "/Users/me/說明.txt", false));

        var result = await Run(DailyJson, scope);

        Assert.False(result.IsError, result.Content);
        var items = _service.Requests.Single().Scope;
        Assert.Equal(2, items.Count);
        Assert.Equal(new ScheduleScopeItem(ContextKind.Folder, "/Users/me/報表", "報表"), items[0]);
        Assert.Equal(new ScheduleScopeItem(ContextKind.File, "/Users/me/說明.txt", "說明.txt"), items[1]);
        Assert.Equal(["報表", "說明.txt"], ReminderJson.Parse(result.Content).GetProperty("scope").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public async Task NoScope_CreatesScheduleWithoutScopeItems()
    {
        var result = await Run(DailyJson);

        Assert.False(result.IsError, result.Content);
        Assert.Empty(_service.Requests.Single().Scope);
    }

    // ---- 確認卡（Describe） ----

    [Fact]
    public void Preview_Recurring_ShowsNameWhenNextRunsAndProposal()
    {
        var preview = Preview(DailyWith("'suggested_permissions':['write','network','destructive']"), new FakeFileScope(new ScopeRoot("報表", "/x/報表", true)));

        Assert.Equal("晨間整理", preview.Target);
        Assert.StartsWith("每天 09:00，接下來：2026-10-24（週六）09:00、2026-10-25（週日）09:00", preview.Detail);

        var schedule = preview.Schedule!;
        Assert.Equal("晨間整理", schedule.Name);
        Assert.Equal("每天 09:00", schedule.WhenText);
        Assert.Equal("2026-10-24（週六）09:00", schedule.NextRunText);
        Assert.Equal("整理資料夾", schedule.Prompt);
        Assert.Equal(["報表"], schedule.ScopeNames);
        Assert.True(schedule.SuggestedWrite);
        Assert.True(schedule.SuggestedNetwork);
    }

    [Fact]
    public void Preview_OneTime_ShowsSingleRunText()
    {
        var preview = Preview("""{"name":"開會","prompt":"p","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.Equal("2026-10-25（週日）09:00 · Asia/Taipei", preview.Detail);
        Assert.Equal("2026-10-25（週日）09:00（只一次）", preview.Schedule!.WhenText);
        Assert.Equal("2026-10-25（週日）09:00", preview.Schedule.NextRunText);
    }

    [Fact]
    public void Preview_IgnoresGrantedPermissions_AndDestructiveIsNeverPreChecked()
    {
        var preview = Preview(DailyWith("'suggested_permissions':[],'granted_permissions':['write','network','destructive']"));

        var schedule = preview.Schedule!;
        Assert.False(schedule.SuggestedWrite);
        Assert.False(schedule.SuggestedNetwork);
    }

    [Fact]
    public void Preview_SuggestedDestructiveAlone_DoesNotPreCheckAnything()
    {
        var preview = Preview(DailyWith("'suggested_permissions':['destructive']"));

        Assert.False(preview.Schedule!.SuggestedWrite);
        Assert.False(preview.Schedule.SuggestedNetwork);
    }

    [Fact]
    public void Preview_ModelTextUsesWorkbenchDefaultModel()
    {
        _catalog.Current = ModelCatalogWithDefault();

        var preview = Preview(DailyJson);

        Assert.Equal("預設模型（目前是 測試供應商 · gpt-test）", preview.Schedule!.ModelText);
    }

    [Fact]
    public void Preview_ModelTextWithoutDefaultModel_SaysNotSet()
    {
        var preview = Preview(DailyJson);

        Assert.Equal("預設模型（尚未設定）", preview.Schedule!.ModelText);
    }

    [Fact]
    public void Preview_EmptyScope_HasNoScopeNames()
    {
        var preview = Preview(DailyJson);

        Assert.Empty(preview.Schedule!.ScopeNames);
    }

    [Theory]
    [InlineData("""{"name":"晨間整理","prompt":"p","at":"not a time","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"name":"晨間整理","prompt":"","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""")]
    [InlineData("""{"name":"晨間整理","prompt":"p","at":"2026-10-25T09:00","time_zone":"Mars/Olympus"}""")]
    [InlineData("""{"name":"晨間整理","prompt":"p"}""")]
    public void Preview_Unparseable_ShowsNameWithoutDetailOrProposal(string json)
    {
        var preview = Preview(json);

        Assert.Equal("晨間整理", preview.Target);
        Assert.Null(preview.Detail);
        Assert.Null(preview.Schedule);
    }

    [Fact]
    public async Task Preview_DoesNotCreateAnything()
    {
        Preview(DailyJson);

        Assert.Empty(_service.Requests);
        Assert.Empty(await _rig.Store.ListAsync(new ScheduledItemFilter(), default));
    }

    // ---- 建立與回傳 ----

    [Fact]
    public async Task Success_ReturnsIdNextRunGrantAndRecordNote()
    {
        var result = await Run(DailyWith("'suggested_permissions':['write'],'granted_permissions':['write']"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("已建立排程 · 每天 09:00", result.Summary);

        var json = ReminderJson.Parse(result.Content);
        var stored = await _rig.Store.ListAsync(new ScheduledItemFilter(), default);
        Assert.Equal(stored.Single().Id, json.GetProperty("id").GetString());
        Assert.Equal("晨間整理", json.GetProperty("name").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(8)), DateTimeOffset.Parse(json.GetProperty("next_run_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("2026-10-24（週六）09:00", json.GetProperty("next_run_at_local").GetString());
        Assert.Equal(["read", "write"], json.GetProperty("granted").EnumerateArray().Select(e => e.GetString()!));
        Assert.Contains("「排程」分頁", json.GetProperty("note").GetString()!);
        Assert.Contains("30 次", json.GetProperty("note").GetString()!);
        Assert.False(json.TryGetProperty("permission_note", out _));
    }

    [Fact]
    public async Task Success_CreatedWithAgentAsCreatorAndWithTheScheduleFields()
    {
        var result = await Run(DailyJson);

        Assert.False(result.IsError, result.Content);
        var request = _service.Requests.Single();
        Assert.Equal("晨間整理", request.Name);
        Assert.Equal("整理資料夾", request.Prompt);
        Assert.Equal(ScheduledCreator.Agent, request.CreatedBy);
        Assert.Equal("Asia/Taipei", request.TimeZoneId);
    }

    [Fact]
    public async Task ReminderException_BecomesErrorResultWithItsMessage()
    {
        _service.Fail = new ReminderException("排程數量已達上限，請先刪除不用的排程。");

        var result = await Run(DailyJson);

        Assert.True(result.IsError);
        Assert.Equal("排程數量已達上限，請先刪除不用的排程。", result.Content);
        Assert.Equal("無法建立", result.Summary);
    }

    [Fact]
    public async Task OneTimeSuccess_SummaryShowsSingleRun()
    {
        var result = await Run("""{"name":"開會","prompt":"p","at":"2026-10-25T09:00","time_zone":"Asia/Taipei"}""");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("已建立排程 · 2026-10-25（週日）09:00（只一次）", result.Summary);
        Assert.Equal("2026-10-25（週日）09:00", ReminderJson.Parse(result.Content).GetProperty("next_run_at_local").GetString());
    }
}
