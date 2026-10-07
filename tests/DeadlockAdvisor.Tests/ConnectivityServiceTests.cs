using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Text.Json.Nodes;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests;

/// <summary>Knowing whether the internet is there: from how requests go, and from the system's word on its adapters.</summary>
public sealed class ConnectivityServiceTests : IDisposable
{
    private const string Api = "api.deadlock-api.com";
    private const string GitHub = "github.com";

    private readonly ProbeApi _api = new();
    private readonly FakeNetwork _network = new();
    private readonly HistoricalScheduler _clock = new();
    private readonly List<ConnectivityState> _seen = [];
    private int _reconnected;
    private ConnectivityService? _service;

    public void Dispose() => _service?.Dispose();

    private ConnectivityService Service()
    {
        _service = new ConnectivityService(_api, _network, _clock, TimeSpan.Zero);
        _service.States.Subscribe(_seen.Add);
        _service.Reconnected.Subscribe(_ => _reconnected++);
        return _service;
    }

    [Fact]
    public void ItStartsOnlineBeforeAnythingHasFailed()
    {
        var service = Service();

        Assert.Equal(ConnectivityState.Online, service.State);
        Assert.False(((IConnectivityService)service).IsOffline);
    }

    [Fact]
    public void ItGoesOfflineOnceEveryHostItHasTriedFails()
    {
        var service = Service();

        _api.Outcomes.OnNext(new HostReach(Api, false));
        Assert.Equal(ConnectivityState.Offline, service.State);

        _api.Outcomes.OnNext(new HostReach(GitHub, false));
        Assert.Equal(ConnectivityState.Offline, service.State);
    }

    [Fact]
    public void OneHostDownWhileAnotherAnswersIsNotOffline()
    {
        var service = Service();

        _api.Outcomes.OnNext(new HostReach(Api, true));
        _api.Outcomes.OnNext(new HostReach(GitHub, false));

        Assert.Equal(ConnectivityState.Online, service.State);
        Assert.Equal(0, _reconnected);
    }

    [Fact]
    public void AnAnswerAfterFailuresBringsItBackAndSaysSoOnce()
    {
        var service = Service();
        _api.Outcomes.OnNext(new HostReach(Api, false));

        _api.Outcomes.OnNext(new HostReach(Api, true));
        _api.Outcomes.OnNext(new HostReach(Api, true));

        Assert.Equal(ConnectivityState.Online, service.State);
        Assert.Equal([ConnectivityState.Online, ConnectivityState.Offline, ConnectivityState.Online], _seen);
        Assert.Equal(1, _reconnected);
    }

    [Fact]
    public void OfflineItAsksEachHostEveryHalfMinuteUntilOneAnswers()
    {
        var service = Service();
        _api.Outcomes.OnNext(new HostReach(Api, false));

        _clock.AdvanceBy(ConnectivityService.ProbeInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(_api.Asked);

        _clock.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _api.Asked.Count);
        Assert.Equal(ConnectivityState.Offline, service.State);

        _api.AnswerAll();
        _clock.AdvanceBy(ConnectivityService.ProbeInterval);

        Assert.Equal(ConnectivityState.Online, service.State);
        Assert.Equal(1, _reconnected);
        var asked = _api.Asked.Count;
        _clock.AdvanceBy(ConnectivityService.ProbeInterval * 3);
        Assert.Equal(asked, _api.Asked.Count);
    }

    [Fact]
    public void RetryChecksAtOnceAndShowsItIsChecking()
    {
        var service = Service();
        _api.Outcomes.OnNext(new HostReach(Api, false));
        _seen.Clear();

        service.Retry();

        Assert.Equal([ConnectivityState.Checking, ConnectivityState.Offline], _seen);
        Assert.Equal(2, _api.Asked.Count);
    }

    [Fact]
    public void ARetryThatReachesTheInternetGoesStraightBackOnline()
    {
        var service = Service();
        _api.Outcomes.OnNext(new HostReach(Api, false));
        _seen.Clear();
        _api.AnswerAll();

        service.Retry();

        Assert.Equal([ConnectivityState.Checking, ConnectivityState.Online], _seen);
        Assert.Equal(1, _reconnected);
    }

    [Fact]
    public void RetryDoesNothingWhileOnline()
    {
        var service = Service();

        service.Retry();

        Assert.Empty(_api.Asked);
        Assert.Equal([ConnectivityState.Online], _seen);
    }

    [Fact]
    public void RetryComesBackWhenOnlyOneHostAnswers()
    {
        var service = Service();
        _api.Outcomes.OnNext(new HostReach(Api, false));
        _api.Outcomes.OnNext(new HostReach(GitHub, false));
        _api.Answering = host => host == Api;

        service.Retry();

        Assert.Equal(ConnectivityState.Online, service.State);
    }

    [Fact]
    public void WithoutAnAdapterItStartsOfflineAndComesBackWhenOneAppears()
    {
        _network.IsAvailable = false;
        var service = Service();
        Assert.Equal(ConnectivityState.Offline, service.State);

        _network.Changes.OnNext(true);

        Assert.Equal(ConnectivityState.Online, service.State);
        Assert.Equal(1, _reconnected);
    }

    [Fact]
    public void AnAdapterGoingDownIsOfflineAtOnceEvenAfterAnswers()
    {
        var service = Service();
        _api.Outcomes.OnNext(new HostReach(Api, true));

        _network.Changes.OnNext(false);

        Assert.Equal(ConnectivityState.Offline, service.State);
    }

    [Fact]
    public void AnAdapterComingBackWithNothingAnsweringProbesAfterAMoment()
    {
        var service = Service();
        _api.Outcomes.OnNext(new HostReach(Api, false));
        _network.Changes.OnNext(false);
        _network.Changes.OnNext(true);
        Assert.Empty(_api.Asked);
        Assert.Equal(ConnectivityState.Offline, service.State);

        _api.AnswerAll();
        _clock.AdvanceBy(TimeSpan.FromSeconds(3));

        Assert.Equal(2, _api.Asked.Count);
        Assert.Equal(ConnectivityState.Online, service.State);
    }

    [Fact]
    public void AnAnswerOutweighsAnAdapterThatSaysDown()
    {
        var service = Service();
        _network.Changes.OnNext(false);

        _api.Outcomes.OnNext(new HostReach(Api, true));

        Assert.Equal(ConnectivityState.Online, service.State);
    }

    /// <summary>An API that answers the probes as a test says, reporting how each went as the real one does.</summary>
    private sealed class ProbeApi : IDeadlockApi
    {
        public Subject<HostReach> Outcomes { get; } = new();
        public List<string> Asked { get; } = [];

        /// <summary>Which hosts answer.</summary>
        public Func<string, bool> Answering { private get; set; } = _ => false;

        public void AnswerAll() => Answering = _ => true;

        public long BytesReceived => 0;

        public IObservable<HostReach> Reachability => Outcomes;

        public Task<byte[]> GetBytesAsync(string url, string userAgent, CancellationToken cancellationToken = default)
        {
            Asked.Add(url);
            var host = new Uri(url).Host;
            var reached = Answering(host);
            Outcomes.OnNext(new HostReach(host, reached));
            return reached ? Task.FromResult(Array.Empty<byte>()) : throw new HttpRequestException($"offline (test): {url}");
        }

        public Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ChangedFile> GetBytesIfChangedAsync(string url, string userAgent, string? etag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DownloadAsync(string url, string userAgent, Stream destination, IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeNetwork : INetworkSignal
    {
        public bool IsAvailable { get; set; } = true;
        public Subject<bool> Changes { get; } = new();
        IObservable<bool> INetworkSignal.Changes => Changes;
    }
}
