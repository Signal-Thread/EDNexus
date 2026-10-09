using System.IO.Compression;
using System.Text;

namespace EDNexus.Plugins.Hosting.Tests;

/// <summary>Builds in-memory manifests and <c>.ednplugin</c> zips, including deliberately hostile ones.</summary>
internal static class TestPackages
{
    public const string ValidManifestJson = """
        {
          "id": "com.acme.jumpcounter",
          "name": "Jump Counter",
          "version": "1.2.0",
          "author": "Acme",
          "description": "Counts jumps.",
          "sdkVersion": "2.0",
          "minAppVersion": "0.0.1",
          "entryAssembly": "Acme.JumpCounter.dll",
          "entryType": "Acme.JumpCounter.JumpCounterPlugin",
          "capabilities": ["events", "state"]
        }
        """;

    public static string Manifest(string id = "com.acme.jumpcounter", string entryAssembly = "Acme.JumpCounter.dll") => $$"""
        {
          "id": "{{id}}",
          "name": "Jump Counter",
          "version": "1.2.0",
          "sdkVersion": "2.0",
          "entryAssembly": "{{entryAssembly}}",
          "entryType": "Acme.JumpCounter.JumpCounterPlugin",
          "capabilities": ["events"]
        }
        """;

    /// <summary>A package with a valid manifest + entry assembly, plus any <paramref name="extra"/> entries.</summary>
    public static byte[] Valid(params (string Name, byte[] Content)[] extra)
        => Zip([("plugin.json", Utf8(ValidManifestJson)), ("Acme.JumpCounter.dll", [0x4D, 0x5A, 1, 2, 3]), .. extra]);

    public static byte[] Zip(IEnumerable<(string Name, byte[] Content)> entries, Action<ZipArchiveEntry>? configure = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                // ZipArchive writes whatever name it is given — exactly what an attacker would do.
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                configure?.Invoke(entry);
                if (!name.EndsWith('/'))
                {
                    using var stream = entry.Open();
                    stream.Write(content);
                }
            }
        }
        return buffer.ToArray();
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] Dll => [0x4D, 0x5A, 0x90, 0x00];

    /// <summary>A throwaway directory under the temp folder, deleted on dispose.</summary>
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ednexus-plugin-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, byte[] content)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(file, content);
            return file;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
