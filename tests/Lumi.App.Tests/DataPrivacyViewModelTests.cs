using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;

namespace Lumi.App.Tests;

/// <summary>M20 20C：「資料與隱私」的 ViewModel——開啟與複製、錯誤訊息、大小計算、大小文字、「怎麼控制」的導覽。</summary>
public sealed class DataPrivacyViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-privacy-" + Guid.NewGuid().ToString("N"));

    public DataPrivacyViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static DataLocation FolderLocation(string id, string path) => new(id, id, "desc", path, IsFile: false);

    private static DataLocation FileLocation(string id, string path) => new(id, id, "desc", path, IsFile: true);

    private static DataLocation NoPathLocation(string id) => new(id, id, "desc", null, IsFile: false);

    private DataPrivacyViewModel NewViewModel(FakeDataLocationProvider provider, FakeFolderOpener folders, FakeClipboardService clipboard) =>
        new(provider, folders, clipboard);

    [Fact]
    public async Task OpenFile_OpensItsParentFolder()
    {
        var folders = new FakeFolderOpener();
        var vm = NewViewModel(new FakeDataLocationProvider(FileLocation("database", Path.Combine(_root, "lumi.db"))), folders, new FakeClipboardService());

        await ((IAsyncRelayCommand)vm.Locations[0].OpenCommand).ExecuteAsync(null);

        Assert.Single(folders.Opened);
        Assert.Equal(_root, folders.Opened[0]);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public async Task OpenFolder_OpensItself()
    {
        var folders = new FakeFolderOpener();
        var vm = NewViewModel(new FakeDataLocationProvider(FolderLocation("attachments", _root)), folders, new FakeClipboardService());

        await ((IAsyncRelayCommand)vm.Locations[0].OpenCommand).ExecuteAsync(null);

        Assert.Single(folders.Opened);
        Assert.Equal(_root, folders.Opened[0]);
    }

    [Fact]
    public async Task Copy_PutsThePathOnTheClipboard_AndSaysSo()
    {
        var clipboard = new FakeClipboardService();
        var vm = NewViewModel(new FakeDataLocationProvider(FolderLocation("data", _root)), new FakeFolderOpener(), clipboard);

        await ((IAsyncRelayCommand)vm.Locations[0].CopyCommand).ExecuteAsync(null);

        Assert.Single(clipboard.Texts);
        Assert.Equal(_root, clipboard.Texts[0]);
        Assert.Equal(Strings.Privacy_Copied, vm.Message);
        Assert.True(vm.HasMessage);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public async Task OpenFailure_ShowsTheErrorMessage()
    {
        var vm = NewViewModel(new FakeDataLocationProvider(FolderLocation("data", _root)), new FakeFolderOpener(succeeds: false), new FakeClipboardService());

        await ((IAsyncRelayCommand)vm.Locations[0].OpenCommand).ExecuteAsync(null);

        Assert.Equal(Strings.Privacy_OpenFailed, vm.Message);
        Assert.True(vm.MessageIsError);
    }

    [Fact]
    public void LocationWithoutPath_CannotOpenOrCopy()
    {
        var vm = NewViewModel(new FakeDataLocationProvider(NoPathLocation("secrets")), new FakeFolderOpener(), new FakeClipboardService());

        var item = vm.Locations[0];

        Assert.False(item.HasPath);
        Assert.Equal("", item.PathText);
        Assert.False(item.OpenCommand.CanExecute(null));
        Assert.False(item.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshSizes_SumsTheFilesInAFolder()
    {
        var folder = Path.Combine(_root, "attachments");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "a.bin"), new byte[1000]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "nested", "b.bin"), new byte[2000]);
        var vm = NewViewModel(new FakeDataLocationProvider(FolderLocation("attachments", folder)), new FakeFolderOpener(), new FakeClipboardService());

        await vm.RefreshSizesAsync();

        var item = vm.Locations[0];
        Assert.Equal(string.Format(Strings.Privacy_SizeFormat, "2.9 KB"), item.SizeText);   // 3000 位元組
        Assert.False(item.NotCreated);
    }

    [Fact]
    public async Task RefreshSizes_MissingPath_IsNotCreated()
    {
        var missing = Path.Combine(_root, "not-yet");
        var vm = NewViewModel(new FakeDataLocationProvider(FolderLocation("logs", missing), FileLocation("database", Path.Combine(_root, "lumi.db"))),
            new FakeFolderOpener(), new FakeClipboardService());

        await vm.RefreshSizesAsync();

        Assert.True(vm.Locations[0].NotCreated);
        Assert.Null(vm.Locations[0].SizeText);
        Assert.True(vm.Locations[1].NotCreated);
    }

    [Fact]
    public async Task RefreshSizes_File_UsesItsLength()
    {
        var file = Path.Combine(_root, "lumi.db");
        await File.WriteAllBytesAsync(file, new byte[5 * 1024 * 1024]);
        var vm = NewViewModel(new FakeDataLocationProvider(FileLocation("database", file)), new FakeFolderOpener(), new FakeClipboardService());

        await vm.RefreshSizesAsync();

        Assert.Equal(string.Format(Strings.Privacy_SizeFormat, "5 MB"), vm.Locations[0].SizeText);
        Assert.False(vm.Locations[0].NotCreated);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void FormatSize_Boundaries(long bytes, string expected) =>
        Assert.Equal(expected, DataPrivacyViewModel.FormatSize(bytes));

    [Fact]
    public void GoTo_KnownSection_RaisesNavigateRequested()
    {
        var vm = NewViewModel(new FakeDataLocationProvider(), new FakeFolderOpener(), new FakeClipboardService());
        SettingsSection? requested = null;
        vm.NavigateRequested += section => requested = section;

        vm.GoToCommand.Execute("Memory");

        Assert.Equal(SettingsSection.Memory, requested);
    }

    [Fact]
    public void GoTo_UnknownText_DoesNothing()
    {
        var vm = NewViewModel(new FakeDataLocationProvider(), new FakeFolderOpener(), new FakeClipboardService());
        var raised = false;
        vm.NavigateRequested += _ => raised = true;

        vm.GoToCommand.Execute("不存在的分類");

        Assert.False(raised);
    }
}

/// <summary>位置清單的假實作：測試直接給定的項目。</summary>
internal sealed class FakeDataLocationProvider(params DataLocation[] locations) : IDataLocationProvider
{
    public IReadOnlyList<DataLocation> GetLocations() => locations;
}
