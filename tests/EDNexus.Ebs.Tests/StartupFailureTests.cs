using System.Diagnostics;
using Xunit;

namespace EDNexus.Ebs.Tests;

/// <summary>
/// An operator who forgets a required setting must get a clear message and a configuration exit code,
/// not an unhandled-exception crash (which also trips crash reporters in the container). This runs the
/// real service as a child process, because the failure happens in <c>Program</c>'s entry point.
/// </summary>
public class StartupFailureTests
{
    [Fact]
    public async Task Missing_twitch_ids_exit_with_a_clear_message_instead_of_crashing()
    {
        var dll = typeof(Program).Assembly.Location;
        if (!File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json")))
            return; // not runnable as a standalone process in this layout; the logic is covered by ValidateOnStart tests

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
            psi.Environment["Twitch__ClientId"] = "";      // the two the operator forgot
            psi.Environment["Twitch__ExtensionId"] = "";
            psi.Environment["Twitch__ClientSecret"] = "test-client-secret";
            psi.Environment["Twitch__ExtensionSecret"] = EbsHostFactory.ExtensionSecret;
            psi.Environment["Twitch__OAuthRedirectUri"] = "https://ebs.example.com/oauth/callback";

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

            Assert.Equal(78, process.ExitCode);
            Assert.Contains("cannot start", error);
            Assert.Contains("Twitch:ClientId", error);
            Assert.Contains("Twitch:ExtensionId", error);
            Assert.DoesNotContain("Unhandled exception", error);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }
}
