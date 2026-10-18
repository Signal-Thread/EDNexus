using System.Net;
using System.Text.Json.Nodes;
using EliteDangerous.Eddn;
using Xunit;

namespace EDNexus.Tests.Reporting;

/// <summary>
/// The uploader's background worker hands work back and forth with <c>Enqueue</c>: it drains the queue,
/// goes idle, and the next <c>Enqueue</c> starts it again. These pin down that a message queued at the
/// moment the worker goes idle is never left behind.
/// </summary>
public class EddnUploaderHandOffTests
{
    private static EddnMessage SampleMessage() => new()
    {
        SchemaRef = EddnSchemas.Journal,
        Envelope = new JsonObject { ["$schemaRef"] = EddnSchemas.Journal, ["message"] = new JsonObject() },
    };

    private static EddnClientOptions FastOptions() => new()
    {
        SoftwareName = "T",
        SoftwareVersion = "1",
        RetryDelay = TimeSpan.Zero,
    };

    /// <summary>
    /// A message enqueued just as the worker finishes the previous one used to be stranded: the worker
    /// decided "queue empty, I'm done" under the lock but only marked itself idle in a second lock
    /// acquisition, so an <c>Enqueue</c> in between saw a worker still running, started none, and the
    /// worker then exited without it. In a real session that is an EDDN message that sits unsent until
    /// the next upload (and is lost if the app closes first); in the bridge's tests it showed up as
    /// one upload where two were expected, about once in a few thousand runs under CPU load.
    ///
    /// A race this narrow cannot be pinned with a single run, so this makes the window busy: many
    /// uploaders on many threads, each with a second message arriving as the first is being sent.
    /// </summary>
    [Fact]
    public async Task A_message_enqueued_as_the_worker_goes_idle_is_never_stranded()
    {
        const int Iterations = 12_000;
        var stranded = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, Iterations), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (_, _) =>
        {
            var handler = new RecordingHandler(HttpStatusCode.OK);
            var uploader = new EddnUploader(FastOptions(), new HttpClient(handler));

            uploader.Enqueue(SampleMessage());
            await Task.Yield();   // let the worker get to the end of the first message
            uploader.Enqueue(SampleMessage());
            await uploader.DisposeAsync();

            if (handler.CallCount != 2) Interlocked.Increment(ref stranded);
        });

        Assert.Equal(0, stranded);
    }

    [Fact]
    public async Task Messages_queued_after_the_worker_has_gone_idle_start_it_again()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        await using var uploader = new EddnUploader(FastOptions(), new HttpClient(handler));

        uploader.Enqueue(SampleMessage());
        await uploader.FlushAsync();
        Assert.Equal(1, handler.CallCount);

        // The worker has fully gone idle by now; a later message must start a fresh one.
        uploader.Enqueue(SampleMessage());
        await uploader.FlushAsync();

        Assert.Equal(2, handler.CallCount);
    }

    /// <summary>
    /// Messages must go out one at a time and in order. The hazard here is the mirror image of the one
    /// above: if an idle worker's bookkeeping ever clobbered the "running" flag of a worker that an
    /// <c>Enqueue</c> had since started, the next <c>Enqueue</c> would start a second, concurrent one.
    /// Same approach: a busy window across many uploaders, with several messages arriving while sends
    /// are in flight, asserting that no uploader ever had two sends overlap.
    /// </summary>
    [Fact]
    public async Task Only_one_worker_sends_at_a_time()
    {
        const int Iterations = 6_000;
        var overlapped = 0;
        var lost = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, Iterations), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (_, _) =>
        {
            var handler = new ConcurrencyProbeHandler();
            var uploader = new EddnUploader(FastOptions(), new HttpClient(handler));

            for (var i = 0; i < 4; i++)
            {
                uploader.Enqueue(SampleMessage());
                await Task.Yield();
            }
            await uploader.DisposeAsync();

            if (handler.MaxConcurrent > 1) Interlocked.Increment(ref overlapped);
            if (handler.Sent != 4) Interlocked.Increment(ref lost);
        });

        Assert.Equal(0, overlapped);
        Assert.Equal(0, lost);
    }

    /// <summary>Records how many sends overlap, and takes a moment over each so that overlap is possible.</summary>
    private sealed class ConcurrencyProbeHandler : HttpMessageHandler
    {
        private int _current;
        private int _max;
        private int _sent;

        public int MaxConcurrent => Volatile.Read(ref _max);
        public int Sent => Volatile.Read(ref _sent);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            do { seen = Volatile.Read(ref _max); if (now <= seen) break; }
            while (Interlocked.CompareExchange(ref _max, now, seen) != seen);

            await Task.Yield();

            Interlocked.Decrement(ref _current);
            Interlocked.Increment(ref _sent);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
