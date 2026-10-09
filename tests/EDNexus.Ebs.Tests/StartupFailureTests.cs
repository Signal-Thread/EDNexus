using System.Diagnostics;
using Xunit;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// An operator who gets a setting wrong must see a clear message and a configuration exit code, not an
/// unhandled-exception crash (which also trips crash reporters in the container). The failure happens in
/// <c>Program</c>'s entry point, so these run the real service as a child process.
/// </summary>
public class StartupFailureTests
{
    private sealed record Outcome(int ExitCode, string StdErr);

    /// <summary>Runs the service with a valid configuration plus <paramref name="overrides"/>; null when it cannot be run standalone.</summary>
    private static async Task<Outcome?> RunAsync(Dictionary<string, string> overrides)
    {
        var dll = typeof(Program).Assembly.Location;
        if (!File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json")))
            return null; // not runnable as a standalone process in this layout

        var temp = Path.Combine(Path.GetTempPath(), "ebs-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            psi.Environment["Ebs__DataDirectory"] = Path.Combine(temp, "data");
            psi.Environment["Ebs__DataProtectionKeysDirectory"] = Path.Combine(temp, "keys");
            psi.Environment["Twitch__ClientId"] = "test-client-id";
            psi.Environment["Twitch__ExtensionId"] = "test-extension-id";
            psi.Environment["Twitch__ClientSecret"] = "test-client-secret";
            psi.Environment["Twitch__ExtensionSecret"] = EbsHostFactory.ExtensionSecret;
            psi.Environment["Twitch__OAuthRedirectUri"] = "https://ebs.example.com/oauth/callback";
            foreach (var (key, value) in overrides) psi.Environment[key] = value;

            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The service neither started nor failed within 60 s.");
            }

            var error = await stderr;
            await stdout;
            return new Outcome(process.ExitCode, error);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Missing_twitch_ids_exit_with_a_clear_message_instead_of_crashing()
    {
        var outcome = await RunAsync(new() { ["Twitch__ClientId"] = "", ["Twitch__ExtensionId"] = "" });
        if (outcome is null) return;

        Assert.Equal(78, outcome.ExitCode);
        Assert.Contains("cannot start", outcome.StdErr);
        Assert.Contains("Twitch:ClientId", outcome.StdErr);
        Assert.Contains("Twitch:ExtensionId", outcome.StdErr);
        Assert.DoesNotContain("Unhandled exception", outcome.StdErr);
    }

    [Fact]
    public async Task An_invalid_ebs_setting_that_is_read_while_the_host_is_built_is_also_reported_cleanly()
    {
        // The storage services read EbsOptions while the host is still being built, before it runs, so
        // this validation failure surfaces earlier than the Twitch one above.
        var outcome = await RunAsync(new() { ["Ebs__MaxStatePayloadBytes"] = "6000" });
        if (outcome is null) return;

        Assert.Equal(78, outcome.ExitCode);
        Assert.Contains("Ebs:MaxStatePayloadBytes", outcome.StdErr);
        Assert.DoesNotContain("Unhandled exception", outcome.StdErr);
    }

    [Fact]
    public async Task A_setting_that_cannot_be_converted_to_its_type_is_reported_cleanly()
    {
        var outcome = await RunAsync(new() { ["Ebs__MaxStatePayloadBytes"] = "not-a-number" });
        if (outcome is null) return;

        Assert.Equal(78, outcome.ExitCode);
        Assert.Contains("cannot start", outcome.StdErr);
        Assert.Contains("Ebs:MaxStatePayloadBytes", outcome.StdErr);
        Assert.DoesNotContain("Unhandled exception", outcome.StdErr);
    }
}
