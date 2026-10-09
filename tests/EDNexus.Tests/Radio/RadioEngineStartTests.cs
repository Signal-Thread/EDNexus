using EDNexus.Core.Radio;
using Xunit;

namespace EDNexus.Tests.Radio;

/// <summary>
/// Starting the radio brings up the native LibVLC engine, which can be slow or stall. The UI thread
/// reads <see cref="RadioPlayerService.Snapshot"/> on every refresh tick, so the start must never
/// hold the lock that read takes, and a start that hangs must end in an error rather than hang the play.
/// The real engine is replaced by a factory that behaves like a slow or broken native start.
/// </summary>
public class RadioEngineStartTests
{
    private static string StationId => RadioStationCatalog.Stations[0].Id;

    private static RadioPlayerService NewRadio(Func<RadioEngineHandle> factory, TimeSpan startTimeout)
        => new(settings: null, store: null, saveDelay: null, postSave: null,
               nativeTeardownTimeout: TimeSpan.FromMilliseconds(200),
               engineFactory: factory, engineStartTimeout: startTimeout);

    [Fact]
    public async Task The_snapshot_stays_readable_while_the_engine_is_starting()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var radio = NewRadio(() =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("native start finished badly");
        }, startTimeout: TimeSpan.FromSeconds(30));

        var play = radio.PlayAsync(StationId);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the engine start never began");
        try
        {
            // This is what the UI thread does every 250 ms; it must not wait for the native start.
            var read = Task.Run(() => radio.Snapshot);
            var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(read, finished);
        }
        finally
        {
            release.Set();
        }

        await play.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = radio.Snapshot;
        Assert.Equal(RadioPlaybackStatus.Error, snapshot.Status);
        Assert.Contains("native start finished badly", snapshot.LastError);
    }

    [Fact]
    public async Task An_engine_start_that_hangs_ends_in_an_error_instead_of_hanging_the_play()
    {
        using var release = new ManualResetEventSlim();
        var radio = NewRadio(() =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("late failure");
        }, startTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            await radio.PlayAsync(StationId).WaitAsync(TimeSpan.FromSeconds(10));

            var snapshot = radio.Snapshot;
            Assert.Equal(RadioPlaybackStatus.Error, snapshot.Status);
            Assert.Contains("did not start", snapshot.LastError);
        }
        finally
        {
            release.Set();
            radio.Dispose();
        }
    }

    [Fact]
    public async Task A_failed_engine_start_is_reported_and_tried_again_on_the_next_play()
    {
        var attempts = 0;
        using var radio = NewRadio(() =>
        {
            Interlocked.Increment(ref attempts);
            throw new DllNotFoundException("libvlc is missing");
        }, startTimeout: TimeSpan.FromSeconds(10));

        await radio.PlayAsync(StationId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RadioPlaybackStatus.Error, radio.Snapshot.Status);
        Assert.Contains("libvlc is missing", radio.Snapshot.LastError);

        await radio.PlayAsync(StationId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, attempts);
    }
}
