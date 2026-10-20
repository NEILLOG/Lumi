using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

public sealed record PersonaStyleOption(PersonaStyle Style)
{
    public string Text => Style switch
    {
        PersonaStyle.Professional => Strings.Persona_Style_Professional,
        PersonaStyle.Playful => Strings.Persona_Style_Playful,
        PersonaStyle.Calm => Strings.Persona_Style_Calm,
        _ => Strings.Persona_Style_Warm,
    };

    public string Hint => Style switch
    {
        PersonaStyle.Professional => Strings.Persona_StyleHint_Professional,
        PersonaStyle.Playful => Strings.Persona_StyleHint_Playful,
        PersonaStyle.Calm => Strings.Persona_StyleHint_Calm,
        _ => Strings.Persona_StyleHint_Warm,
    };

    public static IReadOnlyList<PersonaStyleOption> All { get; } = Enum.GetValues<PersonaStyle>().Select(s => new PersonaStyleOption(s)).ToList();
}

public sealed record ProactivityOption(Proactivity Proactivity)
{
    public string Text => Proactivity switch
    {
        Proactivity.Reserved => Strings.Persona_Proactivity_Reserved,
        Proactivity.Proactive => Strings.Persona_Proactivity_Proactive,
        _ => Strings.Persona_Proactivity_Balanced,
    };

    public string Hint => Proactivity switch
    {
        Proactivity.Reserved => Strings.Persona_ProactivityHint_Reserved,
        Proactivity.Proactive => Strings.Persona_ProactivityHint_Proactive,
        _ => Strings.Persona_ProactivityHint_Balanced,
    };

    public static IReadOnlyList<ProactivityOption> All { get; } = Enum.GetValues<Proactivity>().Select(p => new ProactivityOption(p)).ToList();
}

/// <summary>
/// 設定視窗「人格與主動性」：個性、積極度、自訂人設。編輯的是草稿，按「儲存」才寫進設定
/// （所以和連線、模型一樣，關視窗前有未儲存的變更會詢問）。儲存後下一則訊息起生效。
/// </summary>
public partial class PersonaViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private PersonaSettings _saved;

    public PersonaViewModel(IModelCatalog catalog, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _saved = catalog.Current.Persona;
        ApplyDraft(_saved);
        catalog.Changed += (_, settings) => dispatcher.Post(() => OnCatalogChanged(settings.Persona));
    }

    public IReadOnlyList<PersonaStyleOption> Styles => PersonaStyleOption.All;

    public IReadOnlyList<ProactivityOption> Proactivities => ProactivityOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(SelectedStyleHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private PersonaStyleOption _selectedStyle = PersonaStyleOption.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(SelectedProactivityHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private ProactivityOption _selectedProactivity = ProactivityOption.All[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CustomError), nameof(HasCustomError), nameof(CustomCounter))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _customPersona = "";

    /// <summary>儲存結果的一行說明：成功時是「已儲存…」，失敗時是原因。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public string SelectedStyleHint => SelectedStyle.Hint;

    public string SelectedProactivityHint => SelectedProactivity.Hint;

    public bool HasMessage => Message is not null;

    /// <summary>去掉前後空白後的字數（和儲存時的規則一致）。</summary>
    private int CustomLength => CustomPersona.Trim().Length;

    public string CustomCounter => string.Format(CultureInfo.CurrentCulture, Strings.Persona_CustomCounter, CustomLength, PersonaSettings.CustomPersonaMaxLength);

    public string? CustomError => CustomLength > PersonaSettings.CustomPersonaMaxLength
        ? string.Format(CultureInfo.CurrentCulture, Strings.Persona_TooLong, PersonaSettings.CustomPersonaMaxLength)
        : null;

    public bool HasCustomError => CustomError is not null;

    private PersonaSettings Draft => new PersonaSettings(SelectedStyle.Style, SelectedProactivity.Proactivity, CustomPersona).Normalize();

    public bool IsDirty => Draft != _saved;

    partial void OnSelectedStyleChanged(PersonaStyleOption value) => Message = null;

    partial void OnSelectedProactivityChanged(ProactivityOption value) => Message = null;

    partial void OnCustomPersonaChanged(string value) => Message = null;

    private bool CanSave() => IsDirty && !HasCustomError;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var draft = Draft;
        try
        {
            await _catalog.SetPersonaAsync(draft);
            _saved = draft;
            Notify();
            SetMessage(Strings.Persona_Saved, isError: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 設定檔版本太新（唯讀）時，InvalidOperationException 的訊息本身就是說明
            SetMessage(ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed, isError: true);
        }
    }

    /// <summary>把草稿改回預設值（還沒儲存）。</summary>
    [RelayCommand]
    private void Reset()
    {
        ApplyDraft(PersonaSettings.Default);
        Message = null;
    }

    private void OnCatalogChanged(PersonaSettings persona)
    {
        if (persona == _saved)
            return;
        var wasDirty = IsDirty;
        _saved = persona;
        if (!wasDirty)
            ApplyDraft(persona);   // 沒有改到一半：跟著目前的設定
        Notify();
    }

    private void ApplyDraft(PersonaSettings persona)
    {
        SelectedStyle = PersonaStyleOption.All.First(o => o.Style == persona.Style);
        SelectedProactivity = ProactivityOption.All.First(o => o.Proactivity == persona.Proactivity);
        CustomPersona = persona.CustomPersona ?? "";
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void SetMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}
