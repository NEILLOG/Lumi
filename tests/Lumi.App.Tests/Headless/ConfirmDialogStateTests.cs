using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Lumi.App.Views;
using Xunit.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-4：ConfirmDialog 的按鈕層級在 XAML／建構子一致套用（一般確認＝primary、破壞性＝danger、取消＝secondary，確認區永遠只有一個 primary 或 danger），
/// 並用 <see cref="ButtonStateStyleTests.CheckButton"/> 驗證三種情境在 正常／Hover／Pressed／Disabled／Focus 下實際生效的底色、字色、框線都是 token。
/// </summary>
public class ConfirmDialogStateTests
{
    private readonly ITestOutputHelper _output;

    public ConfirmDialogStateTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string, bool> Scenarios()
    {
        var data = new TheoryData<string, bool>();
        foreach (var scenario in new[] { "normal-confirm", "destructive-confirm", "cancel" })
        {
            data.Add(scenario, false);
            data.Add(scenario, true);
        }
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Scenarios))]
    public void ConfirmDialogButtons_HaveTokenStates_InEveryState(string scenario, bool dark)
    {
        var destructive = scenario == "destructive-confirm";
        var dialog = new ConfirmDialog(destructive ? "要刪除嗎？" : "要繼續嗎？", destructive ? "刪除" : "繼續", destructive)
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        dialog.Show();
        dialog.UpdateLayout();
        try
        {
            var (button, layer) = scenario switch
            {
                "normal-confirm" => ((ContentControl)dialog.ConfirmButton, ButtonStateStyleTests.Primary),
                "destructive-confirm" => (dialog.ConfirmButton, ButtonStateStyleTests.Danger),
                _ => (dialog.CancelButton, ButtonStateStyleTests.Secondary),
            };
            var c = new ButtonStateStyleTests.Case("confirm dialog " + scenario, "", ButtonStateStyleTests.Kind.Button, "", layer);
            var problems = ButtonStateStyleTests.CheckButton(c, dialog, button, dark);
            foreach (var problem in problems)
                _output.WriteLine(problem);
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmDialog_AppliesTheLevelsOnCreation_AndHasExactlyOnePrimaryOrDanger(bool destructive)
    {
        var dialog = new ConfirmDialog("訊息", "確定", destructive);
        // 還沒顯示就已經有層級 class（不是等到 Show 才補）
        Assert.Contains(destructive ? "danger" : "primary", dialog.ConfirmButton.Classes);
        Assert.DoesNotContain(destructive ? "primary" : "danger", dialog.ConfirmButton.Classes);
        Assert.Contains("secondary", dialog.CancelButton.Classes);
        Assert.Single(new[] { dialog.ConfirmButton, dialog.CancelButton }, b => b.Classes.Contains("primary") || b.Classes.Contains("danger"));
        Assert.True(dialog.ConfirmButton.IsDefault);
        Assert.True(dialog.CancelButton.IsCancel);
    }
}
