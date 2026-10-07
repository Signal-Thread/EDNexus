using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace EDNexus.App.Services;

/// <summary>
/// Plays the short "found something worth mining" chime. Best-effort by design: no audio device, an
/// unsupported platform, a missing player binary — every failure mode here is swallowed rather than
/// surfaced, because a missed chime is not worth disturbing the mining card over.
/// </summary>
public static class MiningAlertSound
{
    private const string AssetUri = "avares://EDNexus.App/Assets/mining-alert.wav";

    private static byte[]? _bytes;
    private static string? _tempPath;   // the external-player path needs a real file on disk

    // Windows plays asynchronously from memory, so the buffer has to stay at a fixed address for as
    // long as winmm might be reading it — a managed byte[] is only pinned for the duration of the
    // P/Invoke call, and the GC may move it while the chime is still playing. One small unmanaged
    // copy, allocated once and kept for the life of the process, sidesteps that entirely.
    private static IntPtr _nativeBuffer;
    private static readonly object NativeGate = new();

    /// <summary>Play the chime on a background thread; returns immediately.</summary>
    public static void Play()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) PlayViaWinmm();
                else PlayViaExternalPlayer();
            }
            catch
            {
                // Best-effort — see class remarks.
            }
        });
    }

    /// <summary>
    /// Windows: play straight from memory via winmm's <c>PlaySound</c>, so there's no temp file to
    /// clean up and no extra package (<c>System.Windows.Extensions</c>) just for one short chime.
    /// </summary>
    private static void PlayViaWinmm()
    {
        var buffer = EnsureNativeBuffer();
        if (buffer == IntPtr.Zero) return;
        PlaySound(buffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    private static IntPtr EnsureNativeBuffer()
    {
        lock (NativeGate)
        {
            if (_nativeBuffer != IntPtr.Zero) return _nativeBuffer;

            var bytes = LoadBytes();
            if (bytes is null) return IntPtr.Zero;

            var buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            _nativeBuffer = buffer;
            return buffer;
        }
    }

    /// <summary>
    /// Linux (and, harmlessly, macOS): shell out to whichever common player is on PATH. Best-effort —
    /// a desktop with none of these installed simply gets no chime rather than a crash.
    /// </summary>
    private static void PlayViaExternalPlayer()
    {
        var path = EnsureTempFile();
        if (path is null) return;

        foreach (var exe in new[] { "paplay", "aplay", "afplay" })
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                proc?.WaitForExit(3000);
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // That player isn't installed — try the next one.
            }
        }
    }

    private static byte[]? LoadBytes()
    {
        if (_bytes is not null) return _bytes;
        try
        {
            using var stream = AssetLoader.Open(new Uri(AssetUri));
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _bytes = ms.ToArray();
        }
        catch
        {
            _bytes = null;
        }
        return _bytes;
    }

    private static string? EnsureTempFile()
    {
        if (_tempPath is { } cached && File.Exists(cached)) return cached;

        var bytes = LoadBytes();
        if (bytes is null) return null;
        try
        {
            // Under the user's own app-data folder, not the shared system temp directory: a fixed,
            // guessable name in a world-writable /tmp lets another local user plant a symlink there
            // and have this write follow it.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EDNexus", "sounds");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mining-alert.wav");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                File.WriteAllBytes(path, bytes);
            _tempPath = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    // --- winmm.dll PlaySound: SND_MEMORY plays from a byte buffer, SND_ASYNC returns immediately
    // (we're already on a background thread, but this also avoids blocking winmm's own worker),
    // SND_NODEFAULT means silence rather than the Windows default "ding" if the buffer is bad. ---

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
}
