using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;

namespace Lumi.App.Tests;

/// <summary>主題色快速切換：寫檔比操作慢時，晚到的舊 <c>Changed</c> 事件不能蓋掉剛選的新值。</summary>
public class GeneralPaletteSelectionTests
{
    /// <summary>事件先排隊，由測試決定何時送達（模擬 UI 執行緒還沒處理到）。</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _actions = new();

        public void Post(Action action) => _actions.Enqueue(action);

        public void Flush()
        {
            while (_actions.TryDequeue(out var action))
                action();
        }
    }

    private static PaletteOption Palette(string id) => PaletteOption.All.First(o => o.Id == id);

    [Fact]
    public void Late_event_from_an_earlier_write_does_not_overwrite_the_newer_selection()
    {
        var catalog = new FakeModelCatalog { HoldThemeWrites = true };
        var dispatcher = new QueuedDispatcher();
        var vm = new GeneralViewModel(catalog, dispatcher);

        vm.SelectedPalette = Palette("terracotta");
        vm.SelectedPalette = Palette("teal");

        // terracotta 寫完、事件送達時，teal 還在寫
        catalog.CompleteNextThemeWrite();
        dispatcher.Flush();
        Assert.Equal("teal", vm.SelectedPalette.Id);

        catalog.CompleteNextThemeWrite();
        dispatcher.Flush();
        Assert.Equal("teal", vm.SelectedPalette.Id);
        Assert.Equal("teal", catalog.Current.ThemeId);
    }

    [Fact]
    public void Change_from_elsewhere_still_updates_the_selection()
    {
        var catalog = new FakeModelCatalog();
        var dispatcher = new QueuedDispatcher();
        var vm = new GeneralViewModel(catalog, dispatcher);

        catalog.Current = catalog.Current with { ThemeId = "indigo" };
        catalog.Raise();
        dispatcher.Flush();

        Assert.Equal("indigo", vm.SelectedPalette.Id);
    }

    [Fact]
    public void Change_from_elsewhere_after_our_write_finishes_is_applied()
    {
        var catalog = new FakeModelCatalog { HoldThemeWrites = true };
        var dispatcher = new QueuedDispatcher();
        var vm = new GeneralViewModel(catalog, dispatcher);

        vm.SelectedPalette = Palette("teal");
        catalog.CompleteNextThemeWrite();
        dispatcher.Flush();

        catalog.Current = catalog.Current with { ThemeId = "indigo" };
        catalog.Raise();
        dispatcher.Flush();

        Assert.Equal("indigo", vm.SelectedPalette.Id);
    }
}
