using System.Globalization;
using System.Resources;

namespace Lumi.App.Resources;

/// <summary>介面文字的存取點，XAML 用 {x:Static res:Strings.Xxx} 引用。對應 Strings.resx，新增字串時兩邊一起改。</summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("Lumi.App.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string name) =>
        Manager.GetString(name, CultureInfo.CurrentUICulture) ?? $"!{name}!";

    public static string Workbench_Title => Get(nameof(Workbench_Title));
    public static string Workbench_NewConversation => Get(nameof(Workbench_NewConversation));
    public static string Workbench_NewConversationTitle => Get(nameof(Workbench_NewConversationTitle));
    public static string Workbench_InputPlaceholder => Get(nameof(Workbench_InputPlaceholder));
    public static string Workbench_Send => Get(nameof(Workbench_Send));
    public static string Workbench_Stop => Get(nameof(Workbench_Stop));
    public static string Workbench_WaitingHint => Get(nameof(Workbench_WaitingHint));
    public static string Workbench_Thinking => Get(nameof(Workbench_Thinking));
    public static string Workbench_Stopped => Get(nameof(Workbench_Stopped));
    public static string Workbench_ErrorKept => Get(nameof(Workbench_ErrorKept));
    public static string Workbench_Retry => Get(nameof(Workbench_Retry));
    public static string Workbench_UnexpectedError => Get(nameof(Workbench_UnexpectedError));
    public static string Workbench_StatusFormat => Get(nameof(Workbench_StatusFormat));
    public static string ApiKey_Title => Get(nameof(ApiKey_Title));
    public static string ApiKey_Missing => Get(nameof(ApiKey_Missing));
    public static string ApiKey_Invalid => Get(nameof(ApiKey_Invalid));
    public static string ApiKey_Unreadable => Get(nameof(ApiKey_Unreadable));
    public static string ApiKey_Note => Get(nameof(ApiKey_Note));
    public static string ApiKey_Placeholder => Get(nameof(ApiKey_Placeholder));
    public static string ApiKey_Save => Get(nameof(ApiKey_Save));
    public static string ApiKey_Cancel => Get(nameof(ApiKey_Cancel));
    public static string ApiKey_SaveFailed => Get(nameof(ApiKey_SaveFailed));
    public static string Workbench_NoModel => Get(nameof(Workbench_NoModel));
    public static string Workbench_DismissNotice => Get(nameof(Workbench_DismissNotice));
}
