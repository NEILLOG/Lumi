using Lumi.Core.Outlook;

namespace Lumi.Outlook.Tests;

public class OutlookErrorsTests
{
    [Theory]
    [InlineData(unchecked((int)0x80080005), "Lumi 和 Outlook 的權限等級不同（其中一個用系統管理員執行），請用一般方式重新開啟兩者。")]
    [InlineData(unchecked((int)0x800706BA), "Outlook 已經關閉，請開啟後再試一次。")]
    [InlineData(unchecked((int)0x80010108), "Outlook 已經關閉，請開啟後再試一次。")]
    [InlineData(unchecked((int)0x80004004), "Outlook 拒絕了這次存取。")]
    [InlineData(unchecked((int)0x80004005), "Outlook 回報錯誤：some detail")]
    public void Known_hresults_map_to_plain_messages(int hresult, string expected)
    {
        var exception = OutlookErrors.FromHResult(hresult, "some detail");

        Assert.IsType<OutlookException>(exception);
        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public void Assembly_load_failures_are_recognised()
    {
        Assert.True(OutlookErrors.IsComponentLoadFailure(new FileNotFoundException("office, Version=15.0.0.0")));
        Assert.True(OutlookErrors.IsComponentLoadFailure(new FileLoadException()));
        Assert.True(OutlookErrors.IsComponentLoadFailure(new BadImageFormatException()));
        Assert.False(OutlookErrors.IsComponentLoadFailure(new InvalidOperationException()));
    }
}
