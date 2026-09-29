using DeadlockAdvisor.Models;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Features.Match.Import;

/// <summary>Import a finished match by its ID, start to finish: the lookup modal, then the match written in, in place of whatever was there.</summary>
public class ImportMatchAction
{
    private readonly IDataService _data;
    private readonly ISettingsService _settings;
    private readonly IModalService _modals;
    private readonly IMatchLookupService _lookup;
    private readonly ILoggingService _log;

    public ImportMatchAction(IDataService data, ISettingsService settings, IModalService modals, IMatchLookupService lookup, ILoggingService log)
    {
        _data = data;
        _settings = settings;
        _modals = modals;
        _lookup = lookup;
        _log = log;
    }

    /// <summary>Open the import into <paramref name="match"/>; <paramref name="applied"/> is called if it's applied.</summary>
    public void Run(MatchState match, Action applied)
    {
        if (_modals.IsModalOpen)
            return;

        var heroes = _data.Store.Heroes.Values
            .Where(hero => hero.GameId != 0)
            .DistinctBy(hero => hero.GameId)
            .ToDictionary(hero => hero.GameId);
        ImportMatchViewModel? dialog = null;
        dialog = new ImportMatchViewModel(_lookup, heroes, _settings.Current.SteamAccountId, _log,
            result =>
            {
                Close(dialog!);
                Apply(match, result);
                applied();
            },
            () => Close(dialog!));
        _modals.ShowModal(dialog);
    }

    private void Apply(MatchState match, ImportMatchResult result)
    {
        match.Clear();
        foreach (var (heroId, role) in result.Roles)
            match.SetRole(heroId, role);
        if (result.RememberAccount is { } account)
            _settings.Update(s => s.SteamAccountId = account);
        _log.Information($"Import: applied {result.Roles.Count} hero(es)"
                         + (result.RememberAccount is { } saved ? $", and saved account {saved} as yours" : ""));
    }

    private void Close(ImportMatchViewModel dialog)
    {
        _modals.CloseModal();
        dialog.Dispose();
    }
}
