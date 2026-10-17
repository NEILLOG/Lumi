using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

public partial class WorkbenchViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = Strings.Workbench_Title;

    /// <summary>1A 驗收用的示範文字（字型、emoji）；1D 起由訊息列表取代。</summary>
    [ObservableProperty]
    private string _demoText = Strings.Workbench_DemoText;
}
