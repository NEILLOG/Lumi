using Lumi.Core.Memory;

namespace Lumi.Core.Tests.Memory;

/// <summary>M14 14A：<see cref="MemoryRules"/> 的每個函式（純函式，不需要 store）。</summary>
public sealed class MemoryRulesTests
{
    private static readonly MemoryOptions Options = new();

    private static MemoryItem Item(string content, DateOnly? expiresAt = null) => new(
        1, content, MemoryCategory.Profile, MemorySource.Manual, null,
        DateTimeOffset.Parse("2026-10-20T00:00:00+00:00"), DateTimeOffset.Parse("2026-10-20T00:00:00+00:00"), expiresAt);

    // ---- NormalizeContent ----

    [Theory]
    [InlineData("第一行\n第二行", "第一行 第二行")]
    [InlineData("第一行\r\n第二行", "第一行 第二行")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\t\tb", "a b")]
    [InlineData("a    b   c", "a b c")]
    [InlineData("  前後有空白  ", "前後有空白")]
    [InlineData("\n\t 中間   \n 空白 \t", "中間 空白")]
    [InlineData("已經正規化", "已經正規化")]
    public void NormalizeContent_CollapsesWhitespace(string raw, string expected) =>
        Assert.Equal(expected, MemoryRules.NormalizeContent(raw));

    [Fact]
    public void NormalizeContent_Null_IsEmpty() =>
        Assert.Equal("", MemoryRules.NormalizeContent(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t\r\n")]
    public void NormalizeContent_OnlyWhitespace_IsEmpty(string raw) =>
        Assert.Equal("", MemoryRules.NormalizeContent(raw));

    // ---- Length ----

    [Fact]
    public void Length_CountsChineseCharactersOneEach() =>
        Assert.Equal(6, MemoryRules.Length("繁體中文回答"));

    [Fact]
    public void Length_CountsFamilyEmojiAsOne() =>
        Assert.Equal(1, MemoryRules.Length("👨‍👩‍👧"));

    [Fact]
    public void Length_CountsEmojiSequenceAsOne() =>
        Assert.Equal(1, MemoryRules.Length("❤️‍🔥"));

    [Fact]
    public void Length_CountsCombiningMarksWithTheirBase() =>
        Assert.Equal(1, MemoryRules.Length("é"));

    [Fact]
    public void Length_MixedText() =>
        Assert.Equal(8, MemoryRules.Length("我喜歡 Tea👍"));   // 我 喜 歡 空白 T e a 👍

    [Fact]
    public void Length_EmptyAndNull_AreZero()
    {
        Assert.Equal(0, MemoryRules.Length(""));
        Assert.Equal(0, MemoryRules.Length(null));
    }

    // ---- ValidateContent ----

    [Fact]
    public void ValidateContent_Empty_IsRejected() =>
        Assert.NotNull(MemoryRules.ValidateContent("", Options));

    [Fact]
    public void ValidateContent_ExactlyMaxLength_IsAccepted() =>
        Assert.Null(MemoryRules.ValidateContent(new string('字', 200), Options));

    [Fact]
    public void ValidateContent_OneOverMaxLength_IsRejectedWithTheCount()
    {
        var message = MemoryRules.ValidateContent(new string('字', 201), Options);

        Assert.NotNull(message);
        Assert.Contains("201 字", message);
        Assert.Contains("上限 200 字", message);
    }

    [Fact]
    public void ValidateContent_LengthIsCountedInClusters()
    {
        // 200 個家庭 emoji 只算 200 字（每個是一個字形叢集），不會因為 UTF-16 長度超過而被擋
        var content = string.Concat(Enumerable.Repeat("👨‍👩‍👧", 200));

        Assert.Null(MemoryRules.ValidateContent(content, Options));
    }

    [Fact]
    public void ValidateContent_SecretLikeContent_IsRejectedByDefault() =>
        Assert.NotNull(MemoryRules.ValidateContent("sk-ant-api03-abcdefghijklmnop", Options));

    [Fact]
    public void ValidateContent_CheckSecretFalse_SkipsTheSecretCheck() =>
        Assert.Null(MemoryRules.ValidateContent("sk-ant-api03-abcdefghijklmnop", Options, checkSecret: false));

    [Fact]
    public void ValidateContent_CheckSecretFalse_StillChecksLength() =>
        Assert.NotNull(MemoryRules.ValidateContent(new string('字', 201), Options, checkSecret: false));

    [Fact]
    public void ValidateContent_NormalSentence_IsAccepted() =>
        Assert.Null(MemoryRules.ValidateContent("我是後端工程師，在做內部工具", Options));

    // ---- LooksLikeSecret ----

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnop")]
    [InlineData("金鑰是 sk-proj-AbCdEf0123456789xyz")]
    [InlineData("Bearer abcdefghijklmnop1234")]
    [InlineData("bearer abcdefghijklmnop1234")]
    [InlineData("abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4111111111111111")]
    public void LooksLikeSecret_Catches(string text) =>
        Assert.True(MemoryRules.LooksLikeSecret(text));

    [Theory]
    [InlineData("我偏好用繁體中文回答")]
    [InlineData("生日是 2026-10-23")]
    [InlineData("電話 0912345678")]
    [InlineData("hello world")]
    [InlineData("my_variable_name")]
    [InlineData("abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("")]
    public void LooksLikeSecret_DoesNotCatchOrdinaryText(string text) =>
        Assert.False(MemoryRules.LooksLikeSecret(text));

    // ---- Fingerprint ----

    [Theory]
    [InlineData("I Like Tea", "i like tea")]
    [InlineData("  I   LIKE\ttea ", "i like tea")]
    public void Fingerprint_NormalizesAndLowercases(string raw, string expected) =>
        Assert.Equal(expected, MemoryRules.Fingerprint(raw));

    [Fact]
    public void Fingerprint_Null_IsEmpty() =>
        Assert.Equal("", MemoryRules.Fingerprint(null));

    // ---- TryParseId ----

    [Theory]
    [InlineData("m3", 3)]
    [InlineData("M3", 3)]
    [InlineData("3", 3)]
    [InlineData(" 3 ", 3)]
    [InlineData(" m12 ", 12)]
    public void TryParseId_Accepts(string text, int expected)
    {
        Assert.True(MemoryRules.TryParseId(text, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("m")]
    [InlineData("0")]
    [InlineData("m0")]
    [InlineData("-1")]
    [InlineData("m-1")]
    [InlineData("m3x")]
    [InlineData("3.5")]
    [InlineData("mm3")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("99999999999")]
    public void TryParseId_Rejects(string text)
    {
        Assert.False(MemoryRules.TryParseId(text, out var id));
        Assert.Equal(0, id);
    }

    [Fact]
    public void TryParseId_Null_IsRejected()
    {
        Assert.False(MemoryRules.TryParseId(null, out var id));
        Assert.Equal(0, id);
    }

    [Fact]
    public void FormatId_UsesTheMPrefix() =>
        Assert.Equal("m42", MemoryRules.FormatId(42));

    // ---- TryParseCategory ----

    [Theory]
    [InlineData("profile", MemoryCategory.Profile)]
    [InlineData("PREFERENCE", MemoryCategory.Preference)]
    [InlineData("Work", MemoryCategory.Work)]
    [InlineData(" project ", MemoryCategory.Project)]
    public void TryParseCategory_AcceptsNamesIgnoringCaseAndSpaces(string text, MemoryCategory expected)
    {
        Assert.True(MemoryRules.TryParseCategory(text, out var category));
        Assert.Equal(expected, category);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("unknown")]
    [InlineData("profile, work")]
    [InlineData("work,project")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseCategory_RejectsNumbersAndUnknownNames(string text) =>
        Assert.False(MemoryRules.TryParseCategory(text, out _));

    [Fact]
    public void TryParseCategory_Null_IsRejected() =>
        Assert.False(MemoryRules.TryParseCategory(null, out _));

    // ---- TryParseDate ----

    [Fact]
    public void TryParseDate_AcceptsIsoDate()
    {
        Assert.True(MemoryRules.TryParseDate("2026-10-23", out var date));
        Assert.Equal(new DateOnly(2026, 10, 23), date);
    }

    [Theory]
    [InlineData("2026/10/23")]
    [InlineData("10-23")]
    [InlineData("2026-10")]
    [InlineData("2026-02-30")]
    [InlineData("20261023")]
    [InlineData("")]
    public void TryParseDate_RejectsOtherFormats(string text) =>
        Assert.False(MemoryRules.TryParseDate(text, out _));

    // ---- CategoryKey / CategoryName ----

    [Theory]
    [InlineData(MemoryCategory.Profile, "profile", "本人")]
    [InlineData(MemoryCategory.Preference, "preference", "偏好")]
    [InlineData(MemoryCategory.Work, "work", "工作")]
    [InlineData(MemoryCategory.Project, "project", "專案")]
    public void CategoryKeyAndName_AreStable(MemoryCategory category, string key, string name)
    {
        Assert.Equal(key, MemoryRules.CategoryKey(category));
        Assert.Equal(name, MemoryRules.CategoryName(category));
    }

    // ---- IsActive ----

    [Fact]
    public void IsActive_ExpiringToday_IsStillActive()
    {
        var today = new DateOnly(2026, 10, 23);

        Assert.True(MemoryRules.IsActive(Item("x", today), today));
    }

    [Fact]
    public void IsActive_ExpiredYesterday_IsNotActive()
    {
        var today = new DateOnly(2026, 10, 23);

        Assert.False(MemoryRules.IsActive(Item("x", today.AddDays(-1)), today));
    }

    [Fact]
    public void IsActive_ExpiringTomorrow_IsActive()
    {
        var today = new DateOnly(2026, 10, 23);

        Assert.True(MemoryRules.IsActive(Item("x", today.AddDays(1)), today));
    }

    [Fact]
    public void IsActive_NoExpiry_IsPermanent()
    {
        var today = new DateOnly(2100, 1, 1);

        Assert.True(MemoryRules.IsActive(Item("x", null), today));
    }

    // ---- ContainsTriggerWord ----

    [Theory]
    [InlineData("請記住我偏好繁體中文", "記住")]
    [InlineData("我記得你之前說過", "記得")]
    [InlineData("別忘了我下午不吃辣", "別忘了")]
    [InlineData("以後都用條列式", "以後")]
    [InlineData("忘掉這件事", "忘掉")]
    [InlineData("Please REMEMBER this", "remember")]
    [InlineData("please forget that", "FORGET")]
    public void ContainsTriggerWord_MatchesChineseAndEnglishIgnoringCase(string text, string trigger) =>
        Assert.True(MemoryRules.ContainsTriggerWord(text, [trigger]));

    [Fact]
    public void ContainsTriggerWord_UsesTheDefaultListFromOptions() =>
        Assert.True(MemoryRules.ContainsTriggerWord("remember that", Options.TriggerWords));

    [Fact]
    public void ContainsTriggerWord_NoMatch_IsFalse() =>
        Assert.False(MemoryRules.ContainsTriggerWord("今天天氣不錯", Options.TriggerWords));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ContainsTriggerWord_EmptyOrNullText_IsFalse(string? text) =>
        Assert.False(MemoryRules.ContainsTriggerWord(text, Options.TriggerWords));

    [Fact]
    public void ContainsTriggerWord_BlankTriggerWordsAreIgnored() =>
        Assert.False(MemoryRules.ContainsTriggerWord("abc", ["", "   "]));
}
