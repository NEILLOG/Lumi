using Avalonia;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// 訊息區的焦點不應該讓畫面自己捲動：點過使用者的長文字之後，切走再切回視窗（焦點被還原）不能把訊息區拉回那段文字。
/// 鍵盤（Tab、方向鍵）移動焦點時仍要捲進可視範圍。
/// </summary>
public class ConversationFocusScrollTests
{
    private const string UserTextMarker = "第一行長提問";

    private sealed class Rig
    {
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public ScrollViewer Scroller { get; }

        public Rig(int answerCount = 1)
        {
            var turns = Enumerable.Range(0, answerCount).Select(i => SequenceChatProvider.Answer($"回答 {i}")).ToArray();
            var store = new InMemoryConversationStore();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(turns), store, catalog);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(Vm) { Width = 800, Height = 600 };
            Window.Show();
            Window.UpdateLayout();
            Scroller = Window.FindByName<ScrollViewer>("MessageScroller");
        }

        public async Task SendAsync(string text)
        {
            Vm.InputText = text;
            await Vm.SendCommand.ExecuteAsync(null);
            Window.UpdateLayout();
        }

        // 300 行的長提問，把使用者文字撐得比視窗還高
        public Task SendLongPromptAsync() =>
            SendAsync(UserTextMarker + string.Concat(Enumerable.Range(1, 300).Select(i => $"\n第 {i} 行內容")));

        public EmojiTextBlock UserText =>
            Window.GetVisualDescendants().OfType<EmojiTextBlock>().First(t => t.Text?.StartsWith(UserTextMarker) == true);

        public double DistanceToBottom => Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y;

        public void ScrollToBottom()
        {
            Scroller.ScrollToEnd();
            Window.UpdateLayout();
        }

        // headless 的 Activate() 不會真的清焦點，直接叫平台層的 LostFocus／Deactivated／Activated，
        // 走真實視窗「失焦再啟用」時 FocusManager 還原焦點的路徑
        public void DeactivateAndReactivate()
        {
            var impl = ((TopLevel)Window).PlatformImpl!;
            Invoke(impl, "LostFocus");
            Invoke(impl, "Deactivated");
            Dispatcher.UIThread.RunJobs();
            Invoke(impl, "Activated");
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        private static void Invoke(object impl, string name)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = new[] { impl.GetType() }.Concat(impl.GetType().GetInterfaces())
                .Select(t => t.GetProperty(name, flags)).First(p => p is not null)!;
            (property.GetValue(impl) as Action)?.Invoke();
        }
    }

    [AvaloniaFact]
    public async Task ReactivatingTheWindow_WithFocusOnTheLongUserText_DoesNotScrollTheMessages()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        var userText = rig.UserText;
        Assert.True(userText.Bounds.Height > rig.Scroller.Viewport.Height);

        // 先點使用者文字（焦點留在上面），再捲到底，視窗失焦再啟用時才驗證不動
        userText.Focus(NavigationMethod.Pointer);
        rig.ScrollToBottom();
        var before = rig.Scroller.Offset.Y;
        Assert.True(before > 0);
        Assert.True(userText.IsFocused);

        rig.DeactivateAndReactivate();

        Assert.True(userText.IsFocused);   // 確認焦點確實被還原，測的是還原那一刻不捲動
        Assert.Equal(before, rig.Scroller.Offset.Y);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task FocusingTheUserTextWithThePointer_DoesNotScrollTheMessages()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        rig.ScrollToBottom();
        var before = rig.Scroller.Offset.Y;

        rig.UserText.Focus(NavigationMethod.Pointer);
        rig.Window.UpdateLayout();

        Assert.Equal(before, rig.Scroller.Offset.Y);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task AfterTheFocusCycle_NewContentStillFollowsToTheBottom()
    {
        var rig = new Rig(answerCount: 2);
        await rig.SendLongPromptAsync();
        rig.UserText.Focus(NavigationMethod.Pointer);
        rig.ScrollToBottom();
        rig.DeactivateAndReactivate();
        Assert.True(rig.DistanceToBottom <= 24);

        var extentBefore = rig.Scroller.Extent.Height;
        await rig.SendAsync("第二個問題\n多一行\n再多一行");

        Assert.True(rig.Scroller.Extent.Height > extentBefore);
        Assert.True(rig.DistanceToBottom <= 24, $"內容變長後沒有跟到底，距離底部 {rig.DistanceToBottom}");
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TabbingToAControlInsideTheMessages_ScrollsItIntoView()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        rig.Scroller.ScrollToHome();
        rig.Window.UpdateLayout();
        Assert.Equal(0, rig.Scroller.Offset.Y);

        // 最後一則助理訊息的動作按鈕在文字下方，一開始不在可視範圍
        var button = rig.Window.GetVisualDescendants().OfType<Button>()
            .Last(b => b.Classes.Contains("message-action") && b.IsEffectivelyVisible);
        var top = button.TranslatePoint(default, rig.Scroller)!.Value.Y;
        Assert.True(top > rig.Scroller.Viewport.Height);

        button.Focus(NavigationMethod.Tab);
        rig.Window.UpdateLayout();

        var after = button.TranslatePoint(default, rig.Scroller)!.Value.Y;
        Assert.True(rig.Scroller.Offset.Y > 0);
        Assert.InRange(after, 0, rig.Scroller.Viewport.Height);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task DirectionalFocusToAControlInsideTheMessages_ScrollsItIntoView()
    {
        var rig = new Rig();
        await rig.SendLongPromptAsync();
        rig.Scroller.ScrollToHome();
        rig.Window.UpdateLayout();
        var button = rig.Window.GetVisualDescendants().OfType<Button>()
            .Last(b => b.Classes.Contains("message-action") && b.IsEffectivelyVisible);

        button.Focus(NavigationMethod.Directional);
        rig.Window.UpdateLayout();

        var after = button.TranslatePoint(default, rig.Scroller)!.Value.Y;
        Assert.InRange(after, 0, rig.Scroller.Viewport.Height);
        rig.Window.Close();
    }
}
