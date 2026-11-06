using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace EDNexus.App.Telemetry;

/// <summary>
/// Decides whether an exception came from plugin code, so crash reporting can keep a plugin's
/// failure out of EDNexus's own Sentry project (the message is plugin-controlled and the bug is not
/// EDNexus's) while still leaving a local record that names the plugin.
/// </summary>
/// <remarks>
/// An exception is plugin-attributed when any frame of its stack trace, or of the stack trace of
/// any exception reachable through <see cref="Exception.InnerException"/> /
/// <see cref="AggregateException.InnerExceptions"/>, belongs to an assembly loaded into a plugin's
/// <see cref="AssemblyLoadContext"/> (named <see cref="ContextNamePrefix"/> + the plugin id, as
/// <c>EDNexus.Plugins.Hosting.PluginLoadContext</c> names it), or when the exception's own type is
/// defined in one. This covers a throw inside the plugin, a host or framework call that the plugin
/// made, and the usual unhandled-exception cases (a plugin thread, timer, or task continuation: the
/// plugin's lambda is on that stack). It is a heuristic about <em>where the failing code
/// came from</em>, not a security boundary: a plugin can create a context with this name too, which
/// only suppresses sending, never causes anything to be sent. Nothing here calls
/// <see cref="Exception.Message"/>, <see cref="Exception.StackTrace"/> or
/// <see cref="object.ToString"/> on the exception being inspected (a plugin can override them to
/// throw), and nothing throws.
/// </remarks>
public static class PluginCrashAttribution
{
    /// <summary>The prefix of a plugin load context's name; the rest is the plugin id.</summary>
    public const string ContextNamePrefix = "EDNexus plugin ";

    private const int MaxDepth = 32;
    private const int MaxNodes = 256;

    /// <summary>
    /// Finds the plugin an exception came from. Returns the plugin id (as it appears in the load
    /// context's name, reduced to the characters a plugin id may contain, or <c>unknown</c>), or
    /// <see langword="null"/> when no plugin code is involved.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string? FindPlugin(Exception? exception)
    {
        if (exception is null) return null;
        try
        {
            var budget = MaxNodes;
            return Visit(exception, 0, ref budget);
        }
        catch
        {
            return null;   // a diagnostic helper must never turn a crash report into a second crash
        }
    }

    /// <summary>Whether the exception is plugin-attributed (see <see cref="FindPlugin"/>).</summary>
    public static bool IsFromPlugin(Exception? exception) => FindPlugin(exception) is not null;

    /// <summary>
    /// Whether the exception may be sent to EDNexus's crash reporting: never when it is
    /// plugin-attributed.
    /// </summary>
    public static bool ShouldSendToSentry(Exception? exception) => !IsFromPlugin(exception);

    /// <summary>
    /// The text of <c>last_crash.txt</c>: the timestamp and the exception, preceded by a line naming
    /// the plugin when the exception is plugin-attributed. Never throws.
    /// </summary>
    public static string FormatCrashFile(DateTime utcNow, Exception exception)
    {
        var plugin = FindPlugin(exception);
        var header = plugin is null
            ? ""
            : $"PLUGIN CRASH: plugin '{plugin}' caused this crash; it is not a fault in EDNexus itself and was not sent to crash reporting. Disable or uninstall the plugin.\n";
        return header + utcNow.ToString("o") + "\n" + Describe(exception);
    }

    /// <summary><see cref="object.ToString"/> that survives an exception which overrides it to throw.</summary>
    public static string Describe(Exception exception)
    {
        try { return exception.ToString(); }
        catch { return exception.GetType().FullName + " (the exception could not be described)"; }
    }

    private static string? Visit(Exception exception, int depth, ref int budget)
    {
        if (depth > MaxDepth || budget-- <= 0) return null;

        if (PluginOf(exception.GetType().Assembly) is { } byType)
            return byType;

        // new StackTrace(Exception) reads the runtime's captured frames; it does not call the
        // (virtual, plugin-overridable) Exception.StackTrace.
        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly ?? frame.GetMethod()?.Module.Assembly;
            if (assembly is not null && PluginOf(assembly) is { } found)
                return found;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                if (Visit(inner, depth + 1, ref budget) is { } found)
                    return found;
        }
        else if (exception.InnerException is { } single)
        {
            return Visit(single, depth + 1, ref budget);
        }
        return null;
    }

    private static string? PluginOf(System.Reflection.Assembly assembly)
    {
        var name = AssemblyLoadContext.GetLoadContext(assembly)?.Name;
        if (name is null || !name.StartsWith(ContextNamePrefix, StringComparison.Ordinal))
            return null;
        var id = name[ContextNamePrefix.Length..];
        // A real id is [a-z0-9._-] (validated by the manifest parser); anything else is not trusted
        // into a file or log line.
        return id.Length is > 0 and <= 100 && id.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-')
            ? id
            : "unknown";
    }
}
