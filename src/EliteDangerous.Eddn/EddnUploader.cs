using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace EliteDangerous.Eddn;

/// <summary>
/// The result of a single upload attempt, surfaced to the optional observer for logging.
/// <paramref name="Message"/> identifies which queued message the result belongs to (dropped messages
/// report too, so observers that pair results with payloads stay in step);
/// <paramref name="RetryAfter"/> is the relay's <c>Retry-After</c> hint, when it sent one.
/// </summary>
public sealed record EddnUploadResult(bool Success, HttpStatusCode? Status, string SchemaRef, string? Error,
    EddnMessage? Message = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Uploads <see cref="EddnMessage"/> envelopes to the EDDN relay off the caller's thread. Messages
/// are sent one at a time in submission order from a bounded queue (<see cref="EddnClientOptions.MaxQueueLength"/>,
/// oldest dropped first) so a slow or unreachable relay neither blocks the journal pump nor piles up
/// unbounded, hours-stale uploads. Follows the EDDN retry rules: a 400/426 is a permanent reject
/// (dropped, never retried); other failures get one retry after <see cref="EddnClientOptions.RetryDelay"/>
/// (or the relay's <c>Retry-After</c> on 429/503, bounded) before being dropped. Disposal cancels any
/// in-flight send or retry wait, so it never races the shared <see cref="HttpClient"/>.
/// </summary>
public sealed class EddnUploader : IAsyncDisposable
{
    private readonly record struct Item(EddnMessage Message, byte[] Body, DateTimeOffset Queued);

    private readonly EddnClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();
    private readonly Queue<Item> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _disposed;

    /// <summary>Raised after every attempt (success or failure) and for dropped messages. Never throws back into the pump.</summary>
    public event Action<EddnUploadResult>? Completed;

    public EddnUploader(EddnClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Queues a message for upload. Returns immediately; the send happens in the background.</summary>
    public void Enqueue(EddnMessage message)
    {
        var item = new Item(message, message.ToUtf8Bytes(), DateTimeOffset.UtcNow);
        Item? dropped = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_queue.Count >= Math.Max(1, _options.MaxQueueLength))
                dropped = _queue.Dequeue();
            _queue.Enqueue(item);
            if (!_running)
            {
                _running = true;
                _worker = Task.Run(WorkerAsync);
            }
        }

        if (dropped is { } d)
            Raise(new EddnUploadResult(false, null, d.Message.SchemaRef, "dropped: upload queue full", d.Message));
    }

    /// <summary>Awaits the currently-queued uploads (used on shutdown and in tests).</summary>
    public Task FlushAsync()
    {
        lock (_gate) return _worker;
    }

    private async Task WorkerAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (true)
            {
                Item item;
                lock (_gate)
                {
                    if (_queue.Count == 0 || ct.IsCancellationRequested) return;
                    item = _queue.Dequeue();
                }

                var result = DateTimeOffset.UtcNow - item.Queued > _options.MaxMessageAge
                    ? new EddnUploadResult(false, null, item.Message.SchemaRef, "dropped: message too old to upload", item.Message)
                    : await SendWithRetryAsync(item, ct).ConfigureAwait(false);
                Raise(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal: abandon whatever is left.
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if (ct.IsCancellationRequested) _queue.Clear();
            }
        }
    }

    private void Raise(EddnUploadResult result)
    {
        try { Completed?.Invoke(result); } catch { /* observer must never break the pump */ }
    }

    private async Task<EddnUploadResult> SendWithRetryAsync(Item item, CancellationToken ct)
    {
        var first = await TrySendAsync(item, ct).ConfigureAwait(false);
        if (first.Success || first.Status is HttpStatusCode.BadRequest or HttpStatusCode.UpgradeRequired)
            return first; // success, or a permanent reject we must not retry

        var delay = _options.RetryDelay;
        if (first.RetryAfter is { } hint && hint > delay)
            delay = hint > _options.MaxRetryAfter ? _options.MaxRetryAfter : hint;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);

        return await TrySendAsync(item, ct).ConfigureAwait(false);
    }

    private async Task<EddnUploadResult> TrySendAsync(Item item, CancellationToken ct)
    {
        var schemaRef = item.Message.SchemaRef;
        try
        {
            using var content = BuildContent(item.Body);
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.UploadEndpoint)
            {
                Version = HttpVersion.Version11,
                Content = content,
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return new EddnUploadResult(true, response.StatusCode, schemaRef, null, item.Message);

            TimeSpan? retryAfter = null;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                retryAfter = ReadRetryAfter(response.Headers.RetryAfter);
            return new EddnUploadResult(false, response.StatusCode, schemaRef,
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), item.Message, retryAfter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // disposal, not a timeout
        }
        catch (Exception ex)
        {
            return new EddnUploadResult(false, null, schemaRef, ex.Message, item.Message);
        }
    }

    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta && delta >= TimeSpan.Zero) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private HttpContent BuildContent(byte[] body)
    {
        if (!_options.UseGzip)
        {
            var raw = new ByteArrayContent(body);
            raw.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return raw;
        }

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(body, 0, body.Length);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    /// <summary>
    /// Lets already-queued messages drain for <see cref="EddnClientOptions.DisposeGrace"/>, then cancels
    /// any in-flight send / retry wait and waits for the worker to stop before releasing the HTTP client.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
        }

        try { await Task.WhenAny(worker, Task.Delay(_options.DisposeGrace)).ConfigureAwait(false); }
        catch { /* best effort */ }

        try { _cts.Cancel(); } catch { }
        try { await worker.ConfigureAwait(false); }
        catch { /* best effort */ }

        _cts.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
