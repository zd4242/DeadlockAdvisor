using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Services;

/// <summary>What the operating system says about its network adapters.</summary>
internal interface INetworkSignal
{
    /// <summary>Whether an adapter other than loopback is up. Often true with no internet behind it.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether an adapter is up, each time that changes.</summary>
    IObservable<bool> Changes { get; }
}

internal sealed class SystemNetworkSignal : INetworkSignal
{
    public bool IsAvailable
    {
        get
        {
            try
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                return true;
            }
        }
    }

    // A platform that can't say leaves the requests to.
    public IObservable<bool> Changes => Observable
        .FromEvent<NetworkAvailabilityChangedEventHandler, bool>(
            handler => (_, e) => handler(e.IsAvailable),
            handler => NetworkChange.NetworkAvailabilityChanged += handler,
            handler => NetworkChange.NetworkAvailabilityChanged -= handler)
        .Catch<bool, Exception>(_ => Observable.Never<bool>());
}

/// <summary>
/// Watches <see cref="IDeadlockApi.Reachability"/> for whether the internet is there. Each host's latest
/// answer counts, and the app is offline only while every host it has tried has failed, so one that's
/// down or blocked doesn't read as no connection. While offline it asks again every <see cref="ProbeInterval"/>,
/// and a moment after the system says an adapter came up, with one small request to each host, since a
/// router with no internet behind it raises no event when the internet returns.
/// </summary>
public sealed class ConnectivityService : IConnectivityService, IDisposable
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan _adapterUpDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _checkShownFor = TimeSpan.FromMilliseconds(600);
    private static readonly string[] _probeUrls = [ModelManifest.ManifestUrl, MatchStatsService.Ranks];

    private readonly IDeadlockApi _api;
    private readonly IScheduler _scheduler;
    private readonly TimeSpan _checkShown;
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _hosts = [];
    private readonly BehaviorSubject<ConnectivityState> _states = new(ConnectivityState.Online);
    private readonly Subject<Unit> _reconnected = new();
    private readonly CompositeDisposable _subscriptions = new();
    private readonly SerialDisposable _probing = new();
    private bool _adaptersDown;
    private bool _checking;
    private Task _probe = Task.CompletedTask;

    public ConnectivityService(IDeadlockApi api)
        : this(api, new SystemNetworkSignal(), Scheduler.Default, _checkShownFor)
    {
    }

    /// <param name="checkShown">How long "checking" shows at least, so a check that fails at once still looks like it happened.</param>
    internal ConnectivityService(IDeadlockApi api, INetworkSignal network, IScheduler scheduler, TimeSpan checkShown)
    {
        _api = api;
        _scheduler = scheduler;
        _checkShown = checkShown;
        _subscriptions.Add(_probing);

        api.Reachability
            .Subscribe(reach => Update(() =>
            {
                _hosts[reach.Host] = reach.Reached;
                // A server answered, so whatever the adapters say, there's a connection.
                if (reach.Reached)
                    _adaptersDown = false;
            }))
            .DisposeWith(_subscriptions);
        Update(() => _adaptersDown = !network.IsAvailable);
        network.Changes.Subscribe(AdaptersChanged).DisposeWith(_subscriptions);
    }

    public ConnectivityState State => _states.Value;

    public IObservable<ConnectivityState> States => _states.AsObservable();

    public IObservable<Unit> Reconnected => _reconnected.AsObservable();

    public void Retry() => _ = RetryAsync();

    private async Task RetryAsync()
    {
        var start = false;
        Update(() =>
        {
            if (_states.Value != ConnectivityState.Online && !_checking)
                _checking = start = true;
        });
        if (!start)
            return;
        try
        {
            await Task.WhenAll(Probe(), Task.Delay(_checkShown));
        }
        finally
        {
            Update(() => _checking = false);
        }
    }

    private void AdaptersChanged(bool up)
    {
        Update(() => _adaptersDown = !up);
        // A moment for the address and DNS to settle before asking.
        if (up && State != ConnectivityState.Online)
            Observable.Timer(_adapterUpDelay, _scheduler).Subscribe(tick => _ = Probe()).DisposeWith(_subscriptions);
    }

    /// <summary>One small request to each host, the one already under way if there is one. How each goes comes back through <see cref="IDeadlockApi.Reachability"/>.</summary>
    private Task Probe()
    {
        lock (_gate)
        {
            if (!_probe.IsCompleted)
                return _probe;
            return _probe = Task.WhenAll(_probeUrls.Select(ProbeAsync));
        }
    }

    private async Task ProbeAsync(string url)
    {
        try
        {
            await _api.GetBytesAsync(url, DeadlockApi.UserAgent);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
        }
    }

    /// <summary>Applies <paramref name="change"/>, then announces the state it leaves, inside the lock so that announcements keep their order.</summary>
    private void Update(Action change)
    {
        lock (_gate)
        {
            var before = _states.Value;
            change();
            var after = Compute();
            if (after == before)
                return;

            if (before == ConnectivityState.Online)
                _probing.Disposable = Observable.Interval(ProbeInterval, _scheduler).Subscribe(tick => _ = Probe());
            else if (after == ConnectivityState.Online)
                _probing.Disposable = Disposable.Empty;
            _states.OnNext(after);
            if (after == ConnectivityState.Online)
                _reconnected.OnNext(Unit.Default);
        }
    }

    private ConnectivityState Compute()
    {
        var down = _adaptersDown || _hosts.Count > 0 && !_hosts.ContainsValue(true);
        return !down ? ConnectivityState.Online : _checking ? ConnectivityState.Checking : ConnectivityState.Offline;
    }

    public void Dispose() => _subscriptions.Dispose();
}
