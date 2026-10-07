using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;

namespace DeadlockAdvisor.Tests;

/// <summary>What each request tells the app about whether the internet is there.</summary>
public sealed class DeadlockApiTests
{
    private const string Url = "https://api.deadlock-api.com/v1/assets/ranks";

    private static DeadlockApi Api(Func<HttpResponseMessage> answer, out List<HostReach> reach)
    {
        var api = new DeadlockApi(new StubHandler(answer));
        var seen = new List<HostReach>();
        api.Reachability.Subscribe(seen.Add);
        reach = seen;
        return api;
    }

    [Fact]
    public async Task AnAnswerCountsAsReached()
    {
        using var api = Api(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }, out var reach);

        await api.GetBytesAsync(Url, DeadlockApi.UserAgent);

        Assert.Equal([new HostReach("api.deadlock-api.com", true)], reach);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnErrorStatusStillMeansTheServerAnswered(HttpStatusCode status)
    {
        using var api = Api(() => new HttpResponseMessage(status), out var reach);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetBytesAsync(Url, DeadlockApi.UserAgent));

        Assert.Equal(status, thrown.StatusCode);
        Assert.Equal([new HostReach("api.deadlock-api.com", true)], reach);
    }

    [Fact]
    public async Task ANotModifiedAnswerCountsAsReached()
    {
        using var api = Api(() => new HttpResponseMessage(HttpStatusCode.NotModified), out var reach);

        var file = await api.GetBytesIfChangedAsync(Url, DeadlockApi.UserAgent, "\"tag\"");

        Assert.Null(file.Bytes);
        Assert.Equal([new HostReach("api.deadlock-api.com", true)], reach);
    }

    [Fact]
    public async Task AConnectionThatFailsCountsAsNotReached()
    {
        using var api = Api(() => throw new HttpRequestException("No such host is known."), out var reach);

        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetJsonAsync(Url));

        Assert.Equal([new HostReach("api.deadlock-api.com", false)], reach);
    }

    [Fact]
    public async Task ARequestThatTimesOutCountsAsNotReachedAndThrowsATimeout()
    {
        using var api = Api(() => throw new TaskCanceledException("timed out"), out var reach);

        await Assert.ThrowsAsync<TimeoutException>(() => api.GetBytesAsync(Url, DeadlockApi.UserAgent));

        Assert.Equal([new HostReach("api.deadlock-api.com", false)], reach);
    }

    [Fact]
    public async Task ACancelledRequestSaysNothingAboutTheConnection()
    {
        using var api = Api(() => throw new TaskCanceledException("cancelled"), out var reach);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetBytesAsync(Url, DeadlockApi.UserAgent, cancelled.Token));

        Assert.Empty(reach);
    }

    [Fact]
    public async Task ADownloadReportsTheSameWay()
    {
        using var reached = Api(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }, out var ok);
        await reached.DownloadAsync(Url, DeadlockApi.UserAgent, new MemoryStream());
        Assert.Equal([new HostReach("api.deadlock-api.com", true)], ok);

        using var failed = Api(() => throw new HttpRequestException("No such host is known."), out var down);
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.DownloadAsync(Url, DeadlockApi.UserAgent, new MemoryStream()));
        Assert.Equal([new HostReach("api.deadlock-api.com", false)], down);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer());
    }
}
