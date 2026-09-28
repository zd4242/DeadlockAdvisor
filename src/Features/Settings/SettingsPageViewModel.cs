using System.Runtime.CompilerServices;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;

namespace DeadlockAdvisor.Features.Settings;

/// <summary>
/// One page of the Settings page. Its options read straight through to <see cref="AppSettings"/>
/// and write back with <see cref="Change"/>, so a change is saved the moment it's made.
/// </summary>
public abstract class SettingsPageViewModel(ISettingsService settings) : ViewModelBase
{
    protected ISettingsService Settings { get; } = settings;

    protected AppSettings Current => Settings.Current;

    /// <summary>Re-read anything shown that the rest of the app can change, as the page is shown.</summary>
    public virtual void Refresh()
    {
    }

    protected void Change(Action<AppSettings> write, [CallerMemberName] string? property = null)
    {
        Settings.Update(write);
        this.RaisePropertyChanged(property);
    }
}
