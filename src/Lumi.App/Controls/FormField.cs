using Avalonia;
using Avalonia.Controls;
using Lumi.App.Resources;

namespace Lumi.App.Controls;

/// <summary>欄位的必填／選填標示。</summary>
public enum FieldRequirement { None, Required, Optional }

/// <summary>
/// 表單欄位（ui-guide.html 第 5 節）：標籤在輸入框上方、右側標「必填／選填」；輸入框放在 Content；
/// 說明文字（<see cref="Hint"/>，Caption）在輸入框下方；錯誤（<see cref="Error"/>）用 Danger 色文字顯示，
/// 一定要有文字，不能只靠輸入框的紅框。樣式在 <c>Themes/Controls.axaml</c>。
/// </summary>
public class FormField : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<FormField, string?>(nameof(Label));

    public static readonly StyledProperty<FieldRequirement> RequirementProperty =
        AvaloniaProperty.Register<FormField, FieldRequirement>(nameof(Requirement));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<FormField, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> ErrorProperty =
        AvaloniaProperty.Register<FormField, string?>(nameof(Error));

    public static readonly DirectProperty<FormField, string?> RequirementTextProperty =
        AvaloniaProperty.RegisterDirect<FormField, string?>(nameof(RequirementText), o => o.RequirementText);

    private string? _requirementText;

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public FieldRequirement Requirement
    {
        get => GetValue(RequirementProperty);
        set => SetValue(RequirementProperty, value);
    }

    /// <summary>輸入框下方的說明（Caption）；空的時候不顯示。</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>錯誤文字；有值就用 Danger 色顯示在輸入框下方。</summary>
    public string? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    /// <summary>右側的「必填」「選填」文字（<see cref="Requirement"/> 是 None 時為 null）。</summary>
    public string? RequirementText
    {
        get => _requirementText;
        private set => SetAndRaise(RequirementTextProperty, ref _requirementText, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RequirementProperty)
        {
            RequirementText = Requirement switch
            {
                FieldRequirement.Required => Strings.Field_Required,
                FieldRequirement.Optional => Strings.Field_Optional,
                _ => null,
            };
        }
    }
}
