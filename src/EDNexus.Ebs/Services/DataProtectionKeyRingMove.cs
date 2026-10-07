using Microsoft.Extensions.Logging;

namespace EDNexus.Ebs.Services;

/// <summary>
/// Moves an existing key ring out of the default <c>{DataDirectory}/keys</c> when the service is
/// pointed at a separate key directory. Without this, moving the keys to their own volume would
/// start a fresh, empty ring and every stored Twitch grant would become undecryptable, logging every
/// broadcaster out. The old copy is deleted afterwards: separation that leaves the keys next to the
/// database is not separation.
/// </summary>
public static class DataProtectionKeyRingMove
{
    /// <returns>How many key files were moved; zero when there was nothing to do.</returns>
    public static int MoveIfNeeded(string defaultDirectory, string configuredDirectory, ILogger logger)
    {
        var from = Path.GetFullPath(defaultDirectory);
        var to = Path.GetFullPath(configuredDirectory);
        if (string.Equals(from.TrimEnd(Path.DirectorySeparatorChar), to.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            return 0;
        if (!Directory.Exists(from)) return 0;

        var legacy = Directory.GetFiles(from, "*.xml");
        if (legacy.Length == 0) return 0;

        Directory.CreateDirectory(to);

        // Key files are compared by name and content. A move that was interrupted (the process died,
        // the disk filled) leaves one side a subset of the other with identical files; anything else
        // is two separate rings.
        var source = legacy.ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);
        var destination = Directory.GetFiles(to, "*.xml").ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);

        bool Same(string name) =>
            source.TryGetValue(name, out var a) && destination.TryGetValue(name, out var b) && a.AsSpan().SequenceEqual(b);

        var destinationIsSubset = destination.Keys.All(Same);
        var sourceIsSubset = source.Keys.All(Same);
        if (!destinationIsSubset && !sourceIsSubset)
        {
            // Someone seeded the new directory by hand, or the service already ran against it. Leave
            // both alone rather than guess which ring is current: deleting either could orphan grants.
            logger.LogWarning(
                "Data Protection keys exist in both {Default} and {Configured}; using {Configured} and leaving {Default} untouched. Delete it once you are sure it is not needed.",
                from, to, to, from);
            return 0;
        }

        // Copy whatever has not landed yet, then delete only once every copy is in place, so a
        // failure part-way leaves the original ring intact and the next start picks up from here.
        // Each copy goes through a temporary name, so a file with the real name is always complete.
        var copied = 0;
        foreach (var (name, bytes) in source)
        {
            if (destination.ContainsKey(name)) continue;
            var target = Path.Combine(to, name);
            var temp = target + ".moving";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: false);
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                throw new IOException($"The copy of Data Protection key file {name} in {to} does not match the original; the original was kept.");
            copied++;
        }
        foreach (var file in legacy)
            File.Delete(file);

        if (copied < legacy.Length)
            logger.LogInformation(
                "Finished an interrupted move of Data Protection keys from {Default} to {Configured} ({Copied} of {Count} file(s) still to copy).",
                from, to, copied, legacy.Length);
        else
            logger.LogInformation("Moved {Count} Data Protection key file(s) from {Default} to {Configured}.", legacy.Length, from, to);
        return legacy.Length;
    }
}
