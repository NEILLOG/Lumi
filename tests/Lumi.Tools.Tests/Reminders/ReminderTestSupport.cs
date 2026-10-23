using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Tools.Tests.Reminders;

/// <summary>可以手動跳時間的時鐘（提醒服務與工具都只讀 <see cref="GetUtcNow"/>）。</summary>
internal sealed class ReminderClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetNow(DateTimeOffset now) => _now = now;
}

/// <summary>
/// 真的 <see cref="ReminderService"/>（記憶體儲存、不響的通知、假時鐘）。工具測試直接用它，檢查的也是實際寫進去的項目。
/// </summary>
internal sealed class ReminderRig
{
    public ReminderRig(DateTimeOffset now)
    {
        Clock = new ReminderClock(now);
        Store = new InMemoryScheduledItemStore();
        Options = new ReminderOptions();
        Service = new ReminderService(Store, new NullNotificationSink(), Clock, Options, NullLogger<ReminderService>.Instance);
    }

    public ReminderClock Clock { get; }

    public InMemoryScheduledItemStore Store { get; }

    public ReminderOptions Options { get; }

    public ReminderService Service { get; }

    public ToolContext Context => new("c1", ChatMode.Workbench, Clock, EmptyFileScope.Instance);

    /// <summary>把提醒工具需要的服務註冊進 DI（給 <c>AddLumiTools</c> 的註冊測試用）。</summary>
    public static void RegisterFor(IServiceCollection services)
    {
        var rig = new ReminderRig(DateTimeOffset.UtcNow);
        services.AddSingleton(rig.Options);
        services.AddSingleton<IReminderService>(rig.Service);
    }
}

internal static class ReminderJson
{
    /// <summary>解析工具回傳的 JSON 文字（單行物件或陣列）。</summary>
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>項目的 id（工具回傳的 JSON 裡的 <c>id</c>）。</summary>
    public static string IdOf(ToolResult result) => Parse(result.Content).GetProperty("id").GetString()!;
}
