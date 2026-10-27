using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

/// <summary>M22 22G：「圖片生成」表單的存檔、取消、尺寸選項與摘要。</summary>
public sealed class ImageGenerationViewModelTests
{
    [Fact]
    public void SizeOptions_MatchTheSupportedSizes_InOrder()
    {
        using var host = new SettingsHost();
        Assert.Equal(ImageGenerationSettings.Sizes, host.ImageGeneration.SizeOptions.Select(o => o.Size).ToList());
        Assert.Equal([Strings.ImageGen_SizeSquare, Strings.ImageGen_SizeLandscape, Strings.ImageGen_SizePortrait],
            host.ImageGeneration.SizeOptions.Select(o => o.Label).ToList());
    }

    [Fact]
    public async Task Open_LoadsTheSavedSettings_AndSizeDefaultsWhenMissing()
    {
        using var host = new SettingsHost();
        var connection = host.Catalog.Current.Connections[0];
        await host.Catalog.SetImageGenerationAsync(new ImageGenerationSettings(connection.Id, "gpt-image-1", "1536x1024"));
        var vm = host.ImageGeneration;

        await vm.OpenAsync();

        Assert.True(vm.HasEditor);
        Assert.Equal(connection.Id, vm.SelectedConnection?.Id);
        Assert.Equal("gpt-image-1", vm.ModelId);
        Assert.Equal("1536x1024", vm.SelectedSize?.Size);
        Assert.False(vm.IsDirty);
        Assert.True(vm.CanSave);   // 有連線與模型 ID 時可以存
    }

    [Fact]
    public async Task Cancel_RevertsTheDraftToTheSavedValues()
    {
        using var host = new SettingsHost();
        var connection = host.Catalog.Current.Connections[0];
        await host.Catalog.SetImageGenerationAsync(new ImageGenerationSettings(connection.Id, "gpt-image-1"));
        var vm = host.ImageGeneration;
        await vm.OpenAsync();

        vm.ModelId = "草稿";
        vm.SelectedSize = vm.SizeOptions[2];
        Assert.True(vm.IsDirty);

        await vm.RevertCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        Assert.Equal("gpt-image-1", vm.ModelId);
        Assert.Equal("1024x1024", vm.SelectedSize?.Size);
        Assert.Equal("gpt-image-1", host.Catalog.Current.ImageGeneration.ModelId);   // 取消不寫入目錄
    }

    [Fact]
    public async Task Save_IsOffWhileTheModelIdIsBlank_AndNeedsAConnection()
    {
        using var host = new SettingsHost();
        var vm = host.ImageGeneration;
        await vm.OpenAsync();
        Assert.False(vm.CanSave);   // 還沒有連線
        Assert.True(vm.HasFieldErrors);

        vm.SelectedConnection = vm.Connections[0];
        Assert.False(vm.CanSave);   // 有連線、還沒有模型 ID
        Assert.True(vm.HasFieldErrors);

        vm.ModelId = "  gpt-image-1  ";
        Assert.True(vm.CanSave);
        Assert.False(vm.HasFieldErrors);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new ImageGenerationSettings(vm.Connections[0].Id, "gpt-image-1"), host.Catalog.Current.ImageGeneration);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Summary_ReadsConnectionAndModel_ThenNotConfigured_WhenTheConnectionIsGone()
    {
        using var host = new SettingsHost();
        var connection = host.Catalog.Current.Connections[0];
        Assert.Equal(Strings.ImageGen_NotConfigured, host.ImageGeneration.Summary);

        await host.Catalog.SetImageGenerationAsync(new ImageGenerationSettings(connection.Id, "gpt-image-1"));
        Assert.Equal(string.Format(Strings.ImageGen_SummaryFormat, connection.Name, "gpt-image-1"), host.ImageGeneration.Summary);

        await host.Catalog.DeleteConnectionAsync(connection.Id);
        Assert.Equal(Strings.ImageGen_NotConfigured, host.ImageGeneration.Summary);
        Assert.Null(host.Catalog.Current.ImageGeneration.ConnectionId);
    }
}
