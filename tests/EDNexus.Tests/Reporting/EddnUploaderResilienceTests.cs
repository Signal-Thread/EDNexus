using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using EliteDangerous.Eddn;
using Xunit;

namespace EDNexus.Tests.Reporting;

/// <summary>Outage behaviour of <see cref="EddnUploader"/>: bounded queue, Retry-After, prompt dispose.</summary>
public class EddnUploaderResilienceTests
{
    private static EddnMessage Message(int n) => new()
    {
        SchemaRef = EddnSchemas.Journal,
        Envelope = new JsonObject { ["$schemaRef"] = EddnSchemas.Journal, ["message"] = new JsonObject { ["n"] = n } },
    };

    private static EddnClientOptions Options(Action<EddnClientOptionsBuilder>? tweak = null)
    {
        var b = new EddnClientOptionsBuilder();
        tweak?.Invoke(b);
        return new EddnClientOptions
        {
            SoftwareName = "T",
            SoftwareVersion = "1",
            RetryDelay = b.RetryDelay,
            MaxQueueLength = b.MaxQueueLength,
            MaxMessageAge = b.MaxMessageAge,
            MaxRetryAfter = b.MaxRetryAfter,
            DisposeGrace = b.DisposeGrace,
        };
    }

    private sealed class EddnClientOptionsBuilder
    {
        public TimeSpan RetryDelay = TimeSpan.Zero;
        public int MaxQueueLength = 200;
        public TimeSpan MaxMessageAge = TimeSpan.FromMinutes(15);
        public TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
        public TimeSpan DisposeGrace = TimeSpan.FromMilliseconds(50);
    }

    /// <summary>A handler whose responses come from a callback that can block on a gate.</summary>
    private sealed class ScriptedHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<DateTimeOffset> Times { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Times) Times.Add(DateTimeOffset.UtcNow);
            return respond(Interlocked.Increment(ref _calls), cancellationToken);
        }
    }

    [Fact]
    public async Task Queue_is_bounded_and_drops_the_oldest_queued_messages()
    {
        var gate = new TaskCompletionSource();
        var handler = new ScriptedHandler(async (_, _) =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var results = new List<EddnUploadResult>();
        await using var uploader = new EddnUploader(Options(o => { o.MaxQueueLength = 3; o.DisposeGrace = TimeSpan.FromSeconds(5); }), new HttpClient(handler));
        uploader.Completed += r => { lock (results) results.Add(r); };

        var messages = Enumerable.Range(0, 10).Select(Message).ToList();
        uploader.Enqueue(messages[0]);
        while (handler.Calls == 0) await Task.Delay(5);      // message 0 is now in flight, queue empty
        foreach (var m in messages.Skip(1)) uploader.Enqueue(m);

        gate.SetResult();
        await uploader.FlushAsync();

        // 1 in flight + the newest 3 queued were sent; the 6 oldest queued were dropped.
        Assert.Equal(4, handler.Calls);
        var dropped = results.Where(r => !r.Success).ToList();
        Assert.Equal(6, dropped.Count);
        Assert.All(dropped, r => Assert.Contains("dropped", r.Error));
        Assert.Equal(messages.Skip(1).Take(6), dropped.Select(r => r.Message));
        Assert.Equal(new[] { messages[0], messages[7], messages[8], messages[9] }, results.Where(r => r.Success).Select(r => r.Message));
    }

    [Fact]
    public async Task Messages_that_waited_too_long_are_dropped_unsent()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        EddnUploadResult? seen = null;
        await using var uploader = new EddnUploader(Options(o => o.MaxMessageAge = TimeSpan.FromSeconds(-1)), new HttpClient(handler));
        uploader.Completed += r => seen = r;

        uploader.Enqueue(Message(1));
        await uploader.FlushAsync();

        Assert.Equal(0, handler.Calls);
        Assert.False(seen!.Success);
        Assert.Contains("too old", seen.Error);
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_retry_wait_promptly()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var uploader = new EddnUploader(Options(o => o.RetryDelay = TimeSpan.FromHours(1)), new HttpClient(handler));

        uploader.Enqueue(Message(1));
        while (handler.Calls == 0) await Task.Delay(5);   // first attempt failed; now sleeping the retry delay

        var sw = Stopwatch.StartNew();
        await uploader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);   // the retry never ran
    }

    [Fact]
    public async Task Dispose_cancels_an_in_flight_send_and_waits_for_it_before_releasing_the_client()
    {
        var started = new TaskCompletionSource();
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);   // a hung request
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var uploader = new EddnUploader(Options(), new HttpClient(handler));

        uploader.Enqueue(Message(1));
        await started.Task;

        await uploader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(uploader.FlushAsync().IsCompleted);   // the worker has actually stopped
    }

    [Fact]
    public async Task Retry_After_on_429_is_honoured()
    {
        var handler = new ScriptedHandler((n, _) =>
        {
            if (n > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            var r = new HttpResponseMessage((HttpStatusCode)429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(r);
        });
        await using var uploader = new EddnUploader(Options(o => o.DisposeGrace = TimeSpan.FromSeconds(5)), new HttpClient(handler));

        uploader.Enqueue(Message(1));
        await uploader.FlushAsync();

        Assert.Equal(2, handler.Calls);
        Assert.True(handler.Times[1] - handler.Times[0] >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task Retry_After_is_capped_at_the_configured_maximum()
    {
        var handler = new ScriptedHandler((n, _) =>
        {
            if (n > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(r);
        });
        await using var uploader = new EddnUploader(Options(o => o.MaxRetryAfter = TimeSpan.FromMilliseconds(50)), new HttpClient(handler));

        uploader.Enqueue(Message(1));
        await uploader.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.Calls);
    }
}
