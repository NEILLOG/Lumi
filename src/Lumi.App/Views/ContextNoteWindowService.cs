using Avalonia.Controls;
using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Views;

/// <summary>
/// 研究本視窗只會有一個，永遠顯示工作台目前那則對話的研究本（M35 35G）。
/// 視窗開著時換對話會跟著換（沒有研究本就關閉）；換對話或關閉前，編輯中會先存檔。
/// </summary>
public sealed class ContextNoteWindowService : IContextNoteWindowService
{
    private readonly Func<ContextNoteWindow> _factory;
    private readonly IWindowHost _host;
    private readonly IContextNoteService _notes;
    private readonly IConversationStore _conversations;
    private readonly TimeProvider _time;
    private ContextNoteWindow? _window;
    private ContextNoteWindowMemory? _memory;

    /// <param name="factory">建立研究本視窗（DI 解析，視窗的 ViewModel 也從 DI 來）。</param>
    public ContextNoteWindowService(Func<ContextNoteWindow> factory, IWindowHost host, IContextNoteService notes,
        IConversationStore conversations, TimeProvider time)
    {
        _factory = factory;
        _host = host;
        _notes = notes;
        _conversations = conversations;
        _time = time;
    }

    public bool IsOpen => _window is { IsVisible: true };

    public async Task ShowAsync(string conversationId)
    {
        var view = await _notes.GetViewAsync(conversationId);
        if (view is null)
            return;

        ContextNoteWindow window;
        if (_window is { } existing)
        {
            window = existing;
            if (window.ViewModel.ConversationId != conversationId)
                await window.ViewModel.LeaveEditAsync();
        }
        else
        {
            window = CreateWindow();
            await _memory!.RestoreAsync();   // 位置要在顯示前還原，否則會先閃在預設位置
        }

        await window.ViewModel.LoadAsync(conversationId);
        if (!window.IsVisible)
            window.Show();
        UpdateSideBySideAvailability(window);
        window.Activate();
    }

    public async Task FollowConversationAsync(string? conversationId)
    {
        if (_window is not { } window)
            return;
        if (conversationId is null)
        {
            await CloseAsync();
            return;
        }

        var view = await _notes.GetViewAsync(conversationId);
        if (view is null)
        {
            await CloseAsync();
            return;
        }
        if (window.ViewModel.ConversationId == conversationId)
            return;

        // 換對話前先處理編輯中的內容（存檔、超過字數就不存），之後才載入新對話
        await window.ViewModel.LeaveEditAsync();
        await window.ViewModel.LoadAsync(conversationId);
        UpdateSideBySideAvailability(window);
    }

    public async Task CloseAsync()
    {
        if (_window is not { } window)
            return;
        await window.CloseWindowAsync();
    }

    /// <summary>
    /// 並排（不在介面上，由視窗的「並排」按鈕呼叫）：工作台放左半、研究本視窗放右半，兩個都先還原成一般大小。
    /// 工作台沒顯示或工作區太窄就不做事。之後兩個視窗各自獨立。
    /// </summary>
    public void ArrangeSideBySide()
    {
        if (_window is not { } window)
            return;
        if (_host.GetWorkbenchScreen() is not { } screen)
            return;
        if (SideBySideLayout.Compute(screen.WorkingArea, screen.Scaling) is not { } layout)
            return;

        var (left, right) = layout;
        var scaling = screen.Scaling;
        _host.PlaceWorkbench(left.Position, left.Width / scaling, left.Height / scaling);

        window.WindowState = WindowState.Normal;
        window.Position = right.Position;
        window.Width = right.Width / scaling;
        window.Height = right.Height / scaling;
        window.Activate();
    }

    private ContextNoteWindow CreateWindow()
    {
        var window = _factory();
        _window = window;
        _memory = new ContextNoteWindowMemory(window, _conversations, _time);
        window.SideBySideRequested += (_, _) => ArrangeSideBySide();
        window.Activated += (_, _) =>
        {
            UpdateSideBySideAvailability(window);
            _ = window.ViewModel.SyncExternalAsync();
        };
        window.Closing += (_, _) => _memory?.OnClosing();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
                _memory?.Dispose();
                _memory = null;
            }
            window.ViewModel.Dispose();
        };
        return window;
    }

    /// <summary>「並排」能不能按：工作台顯示中，而且工作區夠寬。</summary>
    private void UpdateSideBySideAvailability(ContextNoteWindow window)
    {
        var screen = _host.GetWorkbenchScreen();
        window.ViewModel.CanSideBySide = screen is { } s && SideBySideLayout.Compute(s.WorkingArea, s.Scaling) is not null;
    }
}
