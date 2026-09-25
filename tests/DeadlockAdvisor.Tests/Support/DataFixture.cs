using System.Reactive.Concurrency;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Tests.Fakes;

namespace DeadlockAdvisor.Tests.Support;

/// <summary>A data service over a throwaway copy of the golden data, with saves and rescoring on a virtual clock.</summary>
public sealed class DataFixture : IDisposable
{
    public DataFixture()
    {
        var data = System.IO.Path.Combine(Root.Path, "data");
        Directory.CreateDirectory(data);
        foreach (var file in Directory.GetFiles(Golden.DataDir))
            File.Copy(file, System.IO.Path.Combine(data, System.IO.Path.GetFileName(file)));

        Settings.Current.DataRoot = Root.Path;
        Data = new DataService(Settings, new FakeLoggingService(), new NotificationService(new FakeLoggingService()), Clock);
        Data.Initialize();
        Modals = new ModalService(new FakeLoggingService());
    }

    public TempDirectory Root { get; } = new();
    public FakeSettingsService Settings { get; } = new();
    public HistoricalScheduler Clock { get; } = new();
    public DataService Data { get; }
    public ModalService Modals { get; }

    /// <summary>What's on disk now, after any pending save has run.</summary>
    public DataStore Saved()
    {
        Data.FlushSaves();
        return DataStore.Load(Data.DataDir);
    }

    public void Dispose()
    {
        Data.Dispose();
        Root.Dispose();
    }
}
