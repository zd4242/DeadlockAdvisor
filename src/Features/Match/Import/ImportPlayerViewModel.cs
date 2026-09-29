using System.Reactive;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Enums;
using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.Match.Import;

/// <summary>One player of a looked-up match: their hero, their Steam name once it's known, and a way to say they're you.</summary>
public class ImportPlayerViewModel : ViewModelBase
{
    public ImportPlayerViewModel(MatchPlayer player, Hero? hero, Action<ImportPlayerViewModel> setSelf)
    {
        Player = player;
        HeroId = hero?.HeroId;
        HeroName = hero?.HeroName ?? $"Unknown hero ({player.HeroGameId})";
        SetSelfCommand = ReactiveCommand.Create(() => setSelf(this));
    }

    public MatchPlayer Player { get; }
    public long AccountId => Player.AccountId;
    public int Team => Player.Team;

    /// <summary>Null for a hero this data doesn't have yet, who's shown but left out of the match.</summary>
    public string? HeroId { get; }
    public string HeroName { get; }

    [Reactive] public string PlayerName { get; set; } = "";

    [Reactive] public Role Role { get; private set; }
    [Reactive] public string RoleText { get; private set; } = "";
    public bool IsYou => Role == Role.Self;

    public ReactiveCommand<Unit, Unit> SetSelfCommand { get; }

    public void SetRole(Role role)
    {
        Role = role;
        RoleText = role switch
        {
            Role.Self => "YOU",
            Role.None => "",
            _ => role.Label().ToLowerInvariant(),
        };
        this.RaisePropertyChanged(nameof(IsYou));
    }
}
