using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Settings.General;

/// <summary>Zoom, what the app reopens on, your Steam account, and the model editors.</summary>
public class GeneralSettingsViewModel : SettingsPageViewModel
{
    public GeneralSettingsViewModel(ISettingsService settings, ICommand zoomIn, ICommand zoomOut, ICommand resetZoom) : base(settings)
    {
        ZoomInCommand = zoomIn;
        ZoomOutCommand = zoomOut;
        ResetZoomCommand = resetZoom;

        settings.SettingsChanged
            .Select(s => ZoomLevels.Clamp(s.ZoomIndex))
            .DistinctUntilChanged()
            .Subscribe(index => ZoomText = $"{Math.Round(ZoomLevels.Steps[index] * 100):0}%")
            .DisposeWith(Disposables);
        LoadSteamAccount();
    }

    [Reactive] public string ZoomText { get; private set; } = "";

    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }

    public bool ReopenLastPage
    {
        get => Current.ReopenLastPage;
        set => Change(s => s.ReopenLastPage = value);
    }

    public bool ReopenLastMatch
    {
        get => Current.ReopenLastMatch;
        set => Change(s => s.ReopenLastMatch = value);
    }

    public bool ShowRandomButtons
    {
        get => Current.ShowRandomButtons;
        set => Change(s => s.ShowRandomButtons = value);
    }

    public bool ShowExplainMath
    {
        get => Current.ShowExplainMath;
        set => Change(s => s.ShowExplainMath = value);
    }

    public bool ShowModelEditors
    {
        get => Current.ShowModelEditors;
        set => Change(s => s.ShowModelEditors = value);
    }

    /// <summary>Your Steam account as typed: saved whenever it reads as one, and cleared when emptied.</summary>
    public string SteamAccountText
    {
        get => _steamAccountText;
        set
        {
            this.RaiseAndSetIfChanged(ref _steamAccountText, value);
            if (string.IsNullOrWhiteSpace(value))
                Settings.Update(s => s.SteamAccountId = null);
            else if (SteamAccount.TryParse(value, out var accountId))
                Settings.Update(s => s.SteamAccountId = accountId);
            ShowSteamAccountStatus();
        }
    }

    private string _steamAccountText = "";

    [Reactive] public string SteamAccountStatus { get; private set; } = "";

    /// <summary>What's typed isn't an account, so the saved one is left as it was.</summary>
    [Reactive] public bool IsSteamAccountInvalid { get; private set; }

    /// <summary>An import can save the account, so the box starts from what's saved each time the page is shown.</summary>
    public override void Refresh() => LoadSteamAccount();

    private void LoadSteamAccount()
    {
        _steamAccountText = Current.SteamAccountId?.ToString(CultureInfo.InvariantCulture) ?? "";
        this.RaisePropertyChanged(nameof(SteamAccountText));
        ShowSteamAccountStatus();
    }

    private void ShowSteamAccountStatus()
    {
        IsSteamAccountInvalid = !string.IsNullOrWhiteSpace(_steamAccountText) && !SteamAccount.TryParse(_steamAccountText, out _);
        SteamAccountStatus = IsSteamAccountInvalid ? "Not an account ID, SteamID64 or steamcommunity.com/profiles/ link"
            : Current.SteamAccountId is { } accountId ? $"Saved: account {accountId.ToString(CultureInfo.InvariantCulture)}"
            : "Not set";
    }
}
