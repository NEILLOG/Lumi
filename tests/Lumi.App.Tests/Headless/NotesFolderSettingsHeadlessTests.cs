using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Notes;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M10 10B：設定頁「一般」的「筆記資料夾」列——欄位出現、浮水印是預設位置、Enter 套用、錯誤文字、恢復預設的命令。</summary>
public class NotesFolderSettingsHeadlessTests
{
    private const int GeneralIndex = 4;

    private static string Folder(string name) => Path.Combine(Path.GetTempPath(), "lumi-notes-headless", name);

    private static SettingsWindow Open(SettingsHost host)
    {
        var window = new SettingsWindow(host.Settings) { Width = 1040, Height = 760 };
        window.Show();
        host.Settings.CategoryIndex = GeneralIndex;
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>按 Enter 會啟動套用命令；寫檔是真的非同步 I/O，要等命令跑完才能看到結果。</summary>
    private static async Task PressEnter(TextBox box, GeneralViewModel vm)
    {
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        if (vm.CommitNotesFolderCommand.ExecutionTask is { } running)
            await running;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void GeneralPage_ShowsFolderRow_WithDefaultWatermark_AndCommands()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Open(host);

        var box = window.FindByName<TextBox>("NotesFolderBox");
        Assert.Equal("", box.Text);
        Assert.Equal(NoteFolderRules.DefaultFolder, box.Watermark);
        Assert.Equal(Strings.NotesFolder_Label, box.GetValue(Avalonia.Automation.AutomationProperties.NameProperty));

        var choose = window.FindByName<Button>("NotesFolderChooseButton");
        Assert.Equal(Strings.NotesFolder_Choose, choose.Content);
        Assert.Same(host.General.ChooseNotesFolderCommand, choose.Command);

        var reset = window.FindByName<Button>("NotesFolderResetButton");
        Assert.Equal(Strings.NotesFolder_Reset, reset.Content);
        Assert.Same(host.General.ResetNotesFolderCommand, reset.Command);
        // 預設位置時沒有東西可以恢復
        Assert.False(reset.IsEnabled);

        window.Close();
    }

    [AvaloniaFact]
    public async Task EnterKey_AppliesTheTypedFolder_AndEnablesReset()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Open(host);
        var box = window.FindByName<TextBox>("NotesFolderBox");

        box.Text = Folder("typed");
        await PressEnter(box, host.General);

        Assert.Equal(Folder("typed"), host.Catalog.Current.Notes.Folder);
        Assert.True(window.FindByName<Button>("NotesFolderResetButton").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task InvalidFolder_ShowsTheReason_AndKeepsTheText()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        var window = Open(host);
        var box = window.FindByName<TextBox>("NotesFolderBox");

        box.Text = "relative/typed";
        await PressEnter(box, host.General);

        Assert.Equal("relative/typed", box.Text);
        Assert.Equal("", host.Catalog.Current.Notes.Folder);
        var message = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Single(t => t.Contains("不合法"));
        Assert.Contains("完整路徑", message);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ResetCommand_ReturnsToDefault()
    {
        using var host = new SettingsHost(new AvaloniaUiDispatcher());
        await host.Catalog.SetNotesFolderAsync(Folder("custom"));
        var window = Open(host);

        var reset = window.FindByName<Button>("NotesFolderResetButton");
        Assert.True(reset.IsEnabled);
        await host.General.ResetNotesFolderCommand.ExecuteAsync(null);
        Settle(window);

        Assert.Equal("", host.Catalog.Current.Notes.Folder);
        Assert.Equal("", window.FindByName<TextBox>("NotesFolderBox").Text);
        Assert.False(reset.IsEnabled);
        window.Close();
    }
}
