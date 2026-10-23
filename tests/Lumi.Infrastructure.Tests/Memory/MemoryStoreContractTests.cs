using Lumi.Core.Memory;

namespace Lumi.Infrastructure.Tests.Memory;

/// <summary>
/// <see cref="IMemoryStore"/> 的契約測試：記憶體版與 SQLite 版都繼承這個類別，跑<b>同一組</b>測試。
/// 子類別只提供 store 的工廠（與假時間）；需要清理資源的子類別覆寫 <see cref="Dispose"/>。
/// </summary>
public abstract class MemoryStoreContractTests : IDisposable
{
    protected static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-23T08:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture);
    protected static readonly DateOnly Today = new(2026, 10, 23);

    /// <summary>建立要測的 store。<paramref name="clock"/> 就是 store 用來取時間的 <see cref="TimeProvider"/>。</summary>
    protected abstract IMemoryStore CreateStore(MemoryOptions options, MemoryTestClock clock);

    public virtual void Dispose()
    {
    }

    protected static MemoryTestClock NewClock() => new(Start);

    protected static NewMemory New(
        string content,
        MemoryCategory category = MemoryCategory.Profile,
        MemorySource source = MemorySource.Explicit,
        DateOnly? expires = null,
        string? conversation = null) => new(content, category, source, conversation, expires);

    protected static async Task<MemoryItem> AddOk(IMemoryStore store, NewMemory memory)
    {
        var result = await store.AddAsync(memory);
        Assert.True(result.Succeeded, result.Message);
        return result.Item!;
    }

    // ---- 新增 ----

    [Fact]
    public async Task Add_Succeeds_AndStoresEverything()
    {
        var clock = NewClock();
        var store = CreateStore(new MemoryOptions(), clock);

        var result = await store.AddAsync(New("我是後端工程師", MemoryCategory.Work, MemorySource.Suggested, new DateOnly(2026, 12, 31), "conv-1"));

        Assert.Equal(MemoryAddOutcome.Added, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Null(result.Message);
        var item = result.Item!;
        Assert.True(item.Id > 0);
        Assert.Equal("我是後端工程師", item.Content);
        Assert.Equal(MemoryCategory.Work, item.Category);
        Assert.Equal(MemorySource.Suggested, item.Source);
        Assert.Equal("conv-1", item.SourceConversationId);
        Assert.Equal(Start, item.CreatedAt);
        Assert.Equal(Start, item.UpdatedAt);
        Assert.Equal(new DateOnly(2026, 12, 31), item.ExpiresAt);
        Assert.Equal(item, await store.GetAsync(item.Id));
    }

    [Fact]
    public async Task Add_NormalizesContent_BeforeStoring()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var item = await AddOk(store, New("  我偏好\n用\t繁體中文   回答  "));

        Assert.Equal("我偏好 用 繁體中文 回答", item.Content);
        Assert.Equal("我偏好 用 繁體中文 回答", Assert.Single(await store.ListAsync()).Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t\r\n")]
    public async Task Add_EmptyContent_IsInvalid(string content)
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var result = await store.AddAsync(New(content));

        Assert.Equal(MemoryAddOutcome.Invalid, result.Outcome);
        Assert.Null(result.Item);
        Assert.Equal("記憶的內容不可為空。", result.Message);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Add_TooLong_IsInvalid_WithTheRulesMessage()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var result = await store.AddAsync(New(new string('字', 201)));

        Assert.Equal(MemoryAddOutcome.Invalid, result.Outcome);
        Assert.Contains("201 字", result.Message);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Add_ExactlyMaxLength_IsAccepted()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var result = await store.AddAsync(New(new string('字', 200)));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnop")]
    [InlineData("4111 1111 1111 1111")]
    public async Task Add_SecretLike_FromModelOrUser_IsInvalid(string content)
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var explicitResult = await store.AddAsync(New(content, source: MemorySource.Explicit));
        var suggestedResult = await store.AddAsync(New(content, source: MemorySource.Suggested));

        Assert.Equal(MemoryAddOutcome.Invalid, explicitResult.Outcome);
        Assert.Equal(MemoryAddOutcome.Invalid, suggestedResult.Outcome);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Add_SecretLike_Manual_IsAccepted_BecauseTheUserTypedIt()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var result = await store.AddAsync(New("sk-ant-api03-abcdefghijklmnop", source: MemorySource.Manual));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("I like Tea", "  i   LIKE\ttea\n")]
    [InlineData("我偏好 繁體", "  我偏好   繁體\n")]
    public async Task Add_Duplicate_IgnoresCaseAndWhitespace(string first, string second)
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New(first));

        var result = await store.AddAsync(New(second));

        Assert.Equal(MemoryAddOutcome.Duplicate, result.Outcome);
        Assert.Equal(original.Id, result.Item!.Id);
        Assert.NotNull(result.Message);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Add_Duplicate_CountsExpiredItemsToo()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("已經過期的事", expires: new DateOnly(2026, 10, 1)));

        var result = await store.AddAsync(New("已經過期的事"));

        Assert.Equal(MemoryAddOutcome.Duplicate, result.Outcome);
        Assert.Equal(original.Id, result.Item!.Id);
    }

    [Fact]
    public async Task Add_ItemLimit_IsEnforced_AndCountsExpiredItems()
    {
        var store = CreateStore(new MemoryOptions { MaxItems = 2 }, NewClock());
        await AddOk(store, New("第一則", expires: new DateOnly(2026, 10, 1)));
        await AddOk(store, New("第二則"));

        var result = await store.AddAsync(New("第三則"));

        Assert.Equal(MemoryAddOutcome.TooManyItems, result.Outcome);
        Assert.Null(result.Item);
        Assert.Equal("長期記憶已滿（2 則，上限 2 則）。", result.Message);
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task Add_Duplicate_TakesPrecedenceOverTheItemLimit()
    {
        var store = CreateStore(new MemoryOptions { MaxItems = 1 }, NewClock());
        await AddOk(store, New("唯一的一則"));

        var result = await store.AddAsync(New("唯一的一則"));

        Assert.Equal(MemoryAddOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task Add_TotalCharacterLimit_IsEnforced()
    {
        var store = CreateStore(new MemoryOptions { MaxTotalLength = 10 }, NewClock());
        await AddOk(store, New("一二三四五"));
        await AddOk(store, New("六七"));

        var result = await store.AddAsync(New("八九十甲乙丙"));

        Assert.Equal(MemoryAddOutcome.TooManyCharacters, result.Outcome);
        Assert.Null(result.Item);
        Assert.Equal("長期記憶的總字數會超過上限（目前 7 字，這則 6 字，上限 10 字）。", result.Message);
    }

    [Fact]
    public async Task Add_TotalCharacterLimit_UsesTheThousandsSeparatorInMessages()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        for (var i = 0; i < 10; i++)
            await AddOk(store, New(new string('一', 194) + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));   // 每則 195 字，共 1,950 字

        var result = await store.AddAsync(New(new string('二', 80)));

        Assert.Equal(MemoryAddOutcome.TooManyCharacters, result.Outcome);
        Assert.Equal("長期記憶的總字數會超過上限（目前 1,950 字，這則 80 字，上限 2,000 字）。", result.Message);
    }

    [Fact]
    public async Task Add_TotalCharacterLimit_CountsClustersNotCodeUnits()
    {
        // 每個家庭 emoji 是 1 字（UTF-16 卻有 8 個字元）。總字數上限 3 字：1 + 2 = 3 字剛好可以，再加一個中文字就超過
        var store = CreateStore(new MemoryOptions { MaxTotalLength = 3 }, NewClock());
        await AddOk(store, New("👨‍👩‍👧"));
        await AddOk(store, New("👨‍👩‍👧👨‍👩‍👧"));

        var result = await store.AddAsync(New("甲"));

        Assert.Equal(MemoryAddOutcome.TooManyCharacters, result.Outcome);
        Assert.Equal("長期記憶的總字數會超過上限（目前 3 字，這則 1 字，上限 3 字）。", result.Message);
    }

    [Fact]
    public async Task Add_ConcurrentlyWithTheSameContent_OnlyOneIsAdded()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () => await store.AddAsync(New("同一則記憶")))));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(9, results.Count(r => r.Outcome == MemoryAddOutcome.Duplicate));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Add_ConcurrentlyPastTheLimit_NeverExceedsIt()
    {
        var store = CreateStore(new MemoryOptions { MaxItems = 10 }, NewClock());

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () => await store.AddAsync(New($"並行的記憶 {i}")))));

        Assert.Equal(10, results.Count(r => r.Succeeded));
        Assert.Equal(10, results.Count(r => r.Outcome == MemoryAddOutcome.TooManyItems));
        Assert.Equal(10, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task Ids_AreNotReused_AfterDeletingTheLast()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        await AddOk(store, New("甲"));
        var b = await AddOk(store, New("乙"));
        var c = await AddOk(store, New("丙"));
        Assert.True(await store.DeleteAsync(c.Id));

        var d = await AddOk(store, New("丁"));

        Assert.True(d.Id > c.Id);
        Assert.True(d.Id > b.Id);
    }

    [Fact]
    public async Task Ids_AreNotReused_AfterDeleteAll()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var first = await AddOk(store, New("甲"));
        await AddOk(store, New("乙"));
        await store.DeleteAllAsync();

        var next = await AddOk(store, New("丙"));

        Assert.True(next.Id > first.Id);
    }

    // ---- 修改 ----

    [Fact]
    public async Task Update_Content_ChangesOnlyTheContentAndUpdatedAt()
    {
        var clock = NewClock();
        var store = CreateStore(new MemoryOptions(), clock);
        var original = await AddOk(store, New("舊的內容", MemoryCategory.Work, MemorySource.Suggested, new DateOnly(2026, 12, 31), "conv-1"));
        clock.Advance(TimeSpan.FromHours(1));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(Content: "  新的\n內容 "));

        Assert.Equal(MemoryUpdateOutcome.Updated, result.Outcome);
        Assert.True(result.Succeeded);
        var updated = result.Item!;
        Assert.Equal("新的 內容", updated.Content);
        Assert.Equal(Start.AddHours(1), updated.UpdatedAt);
        Assert.Equal(Start, updated.CreatedAt);
        Assert.Equal(MemorySource.Suggested, updated.Source);
        Assert.Equal("conv-1", updated.SourceConversationId);
        Assert.Equal(MemoryCategory.Work, updated.Category);
        Assert.Equal(new DateOnly(2026, 12, 31), updated.ExpiresAt);
        Assert.Equal(updated, await store.GetAsync(original.Id));
    }

    [Fact]
    public async Task Update_CategoryAndExpiry_AreChanged()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("在做一個專案", MemoryCategory.Work));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(Category: MemoryCategory.Project, ExpiresAt: new DateOnly(2027, 1, 1)));

        Assert.Equal(MemoryCategory.Project, result.Item!.Category);
        Assert.Equal(new DateOnly(2027, 1, 1), result.Item.ExpiresAt);
        Assert.Equal("在做一個專案", result.Item.Content);
    }

    [Fact]
    public async Task Update_ClearExpiresAt_MakesItPermanent()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("暫時的事", expires: new DateOnly(2026, 12, 31)));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(ClearExpiresAt: true));

        Assert.Null(result.Item!.ExpiresAt);
        Assert.Null((await store.GetAsync(original.Id))!.ExpiresAt);
    }

    [Fact]
    public async Task Update_ClearExpiresAt_WinsOverExpiresAt()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("暫時的事", expires: new DateOnly(2026, 12, 31)));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(ExpiresAt: new DateOnly(2027, 1, 1), ClearExpiresAt: true));

        Assert.Null(result.Item!.ExpiresAt);
    }

    [Fact]
    public async Task Update_ExpiredItem_CanBeExtended()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("已經過期", expires: new DateOnly(2026, 10, 1)));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(ExpiresAt: new DateOnly(2026, 12, 31)));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { original.Id }, (await store.ListActiveAsync(Today)).Select(i => i.Id));
    }

    [Fact]
    public async Task Update_NotFound()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var result = await store.UpdateAsync(999, new MemoryPatch(Content: "不存在"));

        Assert.Equal(MemoryUpdateOutcome.NotFound, result.Outcome);
        Assert.Null(result.Item);
        Assert.Contains("m999", result.Message);
    }

    [Fact]
    public async Task Update_NotFound_TakesPrecedenceOverAnEmptyPatch()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var result = await store.UpdateAsync(999, new MemoryPatch());

        Assert.Equal(MemoryUpdateOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Update_EmptyPatch_IsInvalid()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("保持不變"));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch());

        Assert.Equal(MemoryUpdateOutcome.Invalid, result.Outcome);
        Assert.Equal("沒有要修改的欄位。", result.Message);
        Assert.Equal(original, await store.GetAsync(original.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_EmptyContent_IsInvalid(string content)
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("保持不變"));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(Content: content));

        Assert.Equal(MemoryUpdateOutcome.Invalid, result.Outcome);
        Assert.Equal("保持不變", (await store.GetAsync(original.Id))!.Content);
    }

    [Fact]
    public async Task Update_TooLong_IsInvalid()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("短的"));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(Content: new string('字', 201)));

        Assert.Equal(MemoryUpdateOutcome.Invalid, result.Outcome);
        Assert.Contains("201 字", result.Message);
    }

    [Fact]
    public async Task Update_SecretLike_IsInvalid_UnlessSkipped()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("短的"));

        var rejected = await store.UpdateAsync(original.Id, new MemoryPatch(Content: "sk-ant-api03-abcdefghijklmnop"));
        var accepted = await store.UpdateAsync(original.Id, new MemoryPatch(Content: "sk-ant-api03-abcdefghijklmnop", SkipSecretCheck: true));

        Assert.Equal(MemoryUpdateOutcome.Invalid, rejected.Outcome);
        Assert.Equal(MemoryUpdateOutcome.Updated, accepted.Outcome);
    }

    [Fact]
    public async Task Update_IntoAnotherItemsContent_IsDuplicate()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var tea = await AddOk(store, New("我喜歡 茶"));
        var coffee = await AddOk(store, New("我喜歡咖啡"));

        var result = await store.UpdateAsync(coffee.Id, new MemoryPatch(Content: " 我喜歡   茶 "));

        Assert.Equal(MemoryUpdateOutcome.Duplicate, result.Outcome);
        Assert.Equal(tea.Id, result.Item!.Id);
        Assert.Equal("我喜歡咖啡", (await store.GetAsync(coffee.Id))!.Content);
    }

    [Fact]
    public async Task Update_ToItsOwnContentWithDifferentCase_IsNotADuplicate()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var original = await AddOk(store, New("Hello World"));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(Content: "HELLO world"));

        Assert.Equal(MemoryUpdateOutcome.Updated, result.Outcome);
        Assert.Equal("HELLO world", result.Item!.Content);
    }

    [Fact]
    public async Task Update_GrowingPastTheTotalLimit_IsRejected_ButShrinkingIsAllowed()
    {
        var store = CreateStore(new MemoryOptions { MaxTotalLength = 10 }, NewClock());
        var a = await AddOk(store, New("一二三四五"));
        var b = await AddOk(store, New("六七"));

        var grown = await store.UpdateAsync(b.Id, new MemoryPatch(Content: "六七八九十甲"));
        var shrunk = await store.UpdateAsync(a.Id, new MemoryPatch(Content: "一"));
        var grownAgain = await store.UpdateAsync(b.Id, new MemoryPatch(Content: "六七八九十甲"));

        Assert.Equal(MemoryUpdateOutcome.TooManyCharacters, grown.Outcome);
        Assert.Null(grown.Item);
        Assert.Equal(MemoryUpdateOutcome.Updated, shrunk.Outcome);
        Assert.Equal(MemoryUpdateOutcome.Updated, grownAgain.Outcome);
    }

    [Fact]
    public async Task Update_CategoryOnly_DoesNotCheckTheTotalLimit()
    {
        var store = CreateStore(new MemoryOptions { MaxTotalLength = 3 }, NewClock());
        var original = await AddOk(store, New("甲乙丙"));

        var result = await store.UpdateAsync(original.Id, new MemoryPatch(Category: MemoryCategory.Work));

        Assert.True(result.Succeeded);
    }

    // ---- 列出 ----

    [Fact]
    public async Task List_IsOrderedByCategory_ThenId()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var project = await AddOk(store, New("專案一", MemoryCategory.Project));
        var profile1 = await AddOk(store, New("本人一", MemoryCategory.Profile));
        var preference = await AddOk(store, New("偏好一", MemoryCategory.Preference));
        var profile2 = await AddOk(store, New("本人二", MemoryCategory.Profile));

        var list = await store.ListAsync();

        Assert.Equal([profile1.Id, profile2.Id, preference.Id, project.Id], list.Select(i => i.Id));
    }

    [Fact]
    public async Task ListAsync_IncludesExpired_ButListActive_DoesNot()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var permanent = await AddOk(store, New("永久"));
        var expiredYesterday = await AddOk(store, New("昨天到期", expires: Today.AddDays(-1)));
        var expiringToday = await AddOk(store, New("今天到期", expires: Today));
        var expiringTomorrow = await AddOk(store, New("明天到期", expires: Today.AddDays(1)));

        var all = await store.ListAsync();
        var active = await store.ListActiveAsync(Today);

        Assert.Equal([permanent.Id, expiredYesterday.Id, expiringToday.Id, expiringTomorrow.Id], all.Select(i => i.Id));
        Assert.Equal([permanent.Id, expiringToday.Id, expiringTomorrow.Id], active.Select(i => i.Id));
        Assert.DoesNotContain(expiredYesterday.Id, active.Select(i => i.Id));
    }

    [Fact]
    public async Task ListAsync_Empty_IsEmpty()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        Assert.Empty(await store.ListAsync());
        Assert.Empty(await store.ListActiveAsync(Today));
    }

    // ---- 單筆讀取 ----

    [Fact]
    public async Task GetAsync_And_TryGet_FindExistingAndReturnNullForMissing()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var item = await AddOk(store, New("要找的"));

        Assert.Equal(item, await store.GetAsync(item.Id));
        Assert.Equal(item, store.TryGet(item.Id));
        Assert.Null(await store.GetAsync(999));
        Assert.Null(store.TryGet(999));
    }

    [Fact]
    public async Task TryGet_ReturnsExpiredItemsToo()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var item = await AddOk(store, New("已經過期", expires: new DateOnly(2026, 10, 1)));

        Assert.Equal(item, store.TryGet(item.Id));
    }

    // ---- 刪除 ----

    [Fact]
    public async Task Delete_ExistingReturnsTrue_MissingReturnsFalse()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var item = await AddOk(store, New("要刪的"));

        Assert.True(await store.DeleteAsync(item.Id));
        Assert.False(await store.DeleteAsync(item.Id));
        Assert.Null(await store.GetAsync(item.Id));
    }

    [Fact]
    public async Task DeleteAll_ReturnsTheCount_AndEmptiesTheStore()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        await AddOk(store, New("甲"));
        await AddOk(store, New("乙", expires: new DateOnly(2026, 10, 1)));

        Assert.Equal(2, await store.DeleteAllAsync());
        Assert.Empty(await store.ListAsync());
        Assert.Equal(0, await store.DeleteAllAsync());
    }

    [Fact]
    public async Task PurgeExpired_RespectsTheGraceBoundary()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var beyondGrace = await AddOk(store, New("寬限期外", expires: Today.AddDays(-31)));
        var onBoundary = await AddOk(store, New("剛好在界線", expires: Today.AddDays(-30)));
        var expiringToday = await AddOk(store, New("今天到期", expires: Today));
        var permanent = await AddOk(store, New("永久"));

        var deleted = await store.PurgeExpiredAsync(Today, graceDays: 30);

        Assert.Equal(1, deleted);
        Assert.Null(await store.GetAsync(beyondGrace.Id));
        Assert.NotNull(await store.GetAsync(onBoundary.Id));
        Assert.NotNull(await store.GetAsync(expiringToday.Id));
        Assert.NotNull(await store.GetAsync(permanent.Id));
    }

    [Fact]
    public async Task PurgeExpired_WithNothingToDelete_ReturnsZero()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        await AddOk(store, New("永久"));

        Assert.Equal(0, await store.PurgeExpiredAsync(Today, graceDays: 30));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task PurgeExpired_ZeroGrace_DeletesOnlyBeforeToday()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        await AddOk(store, New("昨天到期", expires: Today.AddDays(-1)));
        await AddOk(store, New("今天到期", expires: Today));

        Assert.Equal(1, await store.PurgeExpiredAsync(Today, graceDays: 0));
    }

    // ---- 變動通知 ----

    [Fact]
    public void ChangeToken_IsNotCancelled_Initially()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        Assert.False(store.ChangeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ChangeToken_IsCancelled_AndReplaced_OnSuccessfulAdd()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var before = store.ChangeToken;

        await AddOk(store, New("新的一則"));

        Assert.True(before.IsCancellationRequested);
        Assert.False(store.ChangeToken.IsCancellationRequested);
        Assert.NotEqual(before, store.ChangeToken);
    }

    [Fact]
    public async Task ChangeToken_IsNotCancelled_ByARejectedAdd()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        await AddOk(store, New("已經有了"));
        var before = store.ChangeToken;

        var result = await store.AddAsync(New("已經有了"));

        Assert.Equal(MemoryAddOutcome.Duplicate, result.Outcome);
        Assert.False(before.IsCancellationRequested);
        Assert.Equal(before, store.ChangeToken);
    }

    [Fact]
    public async Task ChangeToken_IsCancelled_OnSuccessfulUpdate_AndNotOnRejectedOne()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var item = await AddOk(store, New("原本"));

        var before = store.ChangeToken;
        var rejected = await store.UpdateAsync(999, new MemoryPatch(Content: "不存在"));
        Assert.Equal(MemoryUpdateOutcome.NotFound, rejected.Outcome);
        Assert.False(before.IsCancellationRequested);

        await store.UpdateAsync(item.Id, new MemoryPatch(Content: "改過了"));
        Assert.True(before.IsCancellationRequested);
        Assert.False(store.ChangeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ChangeToken_IsCancelled_OnSuccessfulDelete_AndNotOnMissingOne()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        var item = await AddOk(store, New("要刪的"));

        var before = store.ChangeToken;
        Assert.False(await store.DeleteAsync(999));
        Assert.False(before.IsCancellationRequested);

        Assert.True(await store.DeleteAsync(item.Id));
        Assert.True(before.IsCancellationRequested);
    }

    [Fact]
    public async Task ChangeToken_DeleteAll_IsCancelled_OnlyWhenSomethingWasDeleted()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());

        var empty = store.ChangeToken;
        Assert.Equal(0, await store.DeleteAllAsync());
        Assert.False(empty.IsCancellationRequested);

        await AddOk(store, New("一則"));
        var withItems = store.ChangeToken;
        Assert.Equal(1, await store.DeleteAllAsync());
        Assert.True(withItems.IsCancellationRequested);
    }

    [Fact]
    public async Task ChangeToken_Purge_IsCancelled_OnlyWhenSomethingWasDeleted()
    {
        var store = CreateStore(new MemoryOptions(), NewClock());
        await AddOk(store, New("永久"));

        var before = store.ChangeToken;
        Assert.Equal(0, await store.PurgeExpiredAsync(Today, graceDays: 30));
        Assert.False(before.IsCancellationRequested);

        await AddOk(store, New("很久以前到期", expires: Today.AddDays(-100)));
        Assert.Equal(1, await store.PurgeExpiredAsync(Today, graceDays: 30));
        Assert.True(before.IsCancellationRequested);
    }
}
