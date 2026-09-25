using System.Reactive;
using DeadlockAdvisor.Scoring;

namespace DeadlockAdvisor.Services.Contracts;

/// <summary>The CSVs an editor can change, each flushed separately by the autosave.</summary>
[Flags]
public enum DataFiles
{
    None = 0,
    HeroScores = 1,
    ItemCoefficients = 2,
    TraitWeights = 4,
    StatRules = 8,
}

public enum SaveState
{
    Idle,
    Saving,
    Saved,
    Failed,
}

/// <summary>
/// Owns the data folder and the loaded <see cref="DataStore"/>: where the data lives, seeding it on
/// first run, the weight matrix scoring reads, reloading, and the debounced autosave. Edits write
/// through to <see cref="Store"/> immediately; <see cref="MarkEdited"/> schedules the flush.
/// </summary>
public interface IDataService
{
    /// <summary>The folder holding data/ and assets/.</summary>
    string DataRoot { get; }
    string DataDir { get; }
    string AssetsDir { get; }

    DataStore Store { get; }
    IReadOnlyDictionary<MatrixKey, double> Matrix { get; }

    /// <summary>A different store was loaded (reload, sync, data folder change): heroes and items may differ.</summary>
    IObservable<Unit> StoreReplaced { get; }

    /// <summary>The matrix was rebuilt after an edit, so every score may have moved.</summary>
    IObservable<Unit> ScoresChanged { get; }

    IObservable<SaveState> SaveStates { get; }

    /// <summary>Load from the configured data folder, seeding the default one on first run.</summary>
    void Initialize();

    /// <summary>Record that <paramref name="files"/> changed in memory: rescore soon, save after a short pause.</summary>
    void MarkEdited(DataFiles files);

    /// <summary>Write any pending edits now. False (with the error reported) if a write failed.</summary>
    bool FlushSaves();

    /// <summary>Re-read everything from disk, after flushing pending edits. Throws if the files can't be read.</summary>
    void Reload();

    /// <summary>Rebuild the matrix and tell everyone the data changed, after a bulk change such as a sync.</summary>
    void NotifyReplaced();

    /// <summary>
    /// Switch to another data folder (the folder holding data/, or data/ itself) and remember it.
    /// Pending edits are saved first. Throws, leaving the current folder in use, if it has no data.
    /// </summary>
    void ChangeDataRoot(string folder);
}
