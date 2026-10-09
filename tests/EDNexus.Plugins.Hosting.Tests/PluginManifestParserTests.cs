using System.Text;
using System.Text.Json.Nodes;
using EDNexus.Plugins.Abstractions;

namespace EDNexus.Plugins.Hosting.Tests;

public class PluginManifestParserTests
{
    private static string With(string key, JsonNode? value)
    {
        var node = JsonNode.Parse(TestPackages.ValidManifestJson)!.AsObject();
        node[key] = value;
        return node.ToJsonString();
    }

    private static string Without(string key)
    {
        var node = JsonNode.Parse(TestPackages.ValidManifestJson)!.AsObject();
        node.Remove(key);
        return node.ToJsonString();
    }

    private static PluginManifestParseResult AssertRejected(string json, string expectedFragment)
    {
        var result = PluginManifestParser.Parse(json);
        Assert.False(result.IsValid);
        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    [Fact]
    public void Parse_ValidManifest_PopulatesEveryField()
    {
        var result = PluginManifestParser.Parse(TestPackages.ValidManifestJson);

        Assert.True(result.IsValid, result.ErrorSummary);
        var m = result.Manifest!;
        Assert.Equal("com.acme.jumpcounter", m.Id);
        Assert.Equal("Jump Counter", m.Name);
        Assert.Equal("1.2.0", m.Version);
        Assert.Equal("Acme", m.Author);
        Assert.Equal("Counts jumps.", m.Description);
        Assert.Equal("2.0", m.SdkVersion);
        Assert.Equal("0.0.1", m.MinAppVersion);
        Assert.Equal("Acme.JumpCounter.dll", m.EntryAssembly);
        Assert.Equal("Acme.JumpCounter.JumpCounterPlugin", m.EntryType);
        Assert.Equal(["events", "state"], m.Capabilities);
        Assert.True(m.Declares(PluginCapabilities.Events));
        Assert.False(m.Declares(PluginCapabilities.Network));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_CommittedSampleManifest_IsValid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plugin.sample.json");

        var result = PluginManifestParser.ParseFile(path);

        Assert.True(result.IsValid, result.ErrorSummary);
        Assert.Equal("com.acme.jumpcounter", result.Manifest!.Id);
        Assert.All(result.Manifest.Capabilities, c => Assert.True(PluginCapabilities.IsKnown(c)));
    }

    [Fact]
    public void Parse_OptionalFieldsMissing_IsValidWithDefaults()
    {
        var json = """
            { "id": "a.b", "name": "N", "version": "0.1.0", "sdkVersion": "1.0",
              "entryAssembly": "A.dll", "entryType": "A.Plugin" }
            """;

        var result = PluginManifestParser.Parse(json);

        Assert.True(result.IsValid, result.ErrorSummary);
        Assert.Null(result.Manifest!.Author);
        Assert.Null(result.Manifest.Description);
        Assert.Null(result.Manifest.MinAppVersion);
        Assert.Empty(result.Manifest.Capabilities);
    }

    [Fact]
    public void Parse_UnknownExtraFields_AreIgnored()
    {
        var json = With("futureField", new JsonObject { ["nested"] = new JsonArray(1, 2, 3) });

        Assert.True(PluginManifestParser.Parse(json).IsValid);
    }

    [Fact]
    public void Parse_ToleratesCommentsTrailingCommasAndCaseInsensitiveKeys()
    {
        var json = """
            {
              // a comment
              "ID": "a.b", "Name": "N", "VERSION": "0.1.0", "sdkversion": "1.0",
              "EntryAssembly": "A.dll", "entryType": "A.Plugin",
            }
            """;

        var result = PluginManifestParser.Parse(json);

        Assert.True(result.IsValid, result.ErrorSummary);
        Assert.Equal("a.b", result.Manifest!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyInput_IsRejected(string? json)
    {
        var result = PluginManifestParser.Parse(json);
        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorSummary);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"id\": }")]
    [InlineData("not json")]
    [InlineData("{\"id\": \"a.b\"} trailing")]
    public void Parse_MalformedJson_IsRejectedNotThrown(string json)
        => AssertRejected(json, "not valid JSON");

    [Theory]
    [InlineData("[]", "an array")]
    [InlineData("\"text\"", "a string")]
    [InlineData("42", "a number")]
    [InlineData("null", "null")]
    public void Parse_NonObjectRoot_IsRejected(string json, string kind)
        => AssertRejected(json, "root must be a JSON object, not " + kind);

    [Fact]
    public void Parse_DeeplyNestedJson_IsRejectedNotThrown()
    {
        var json = "{\"x\":" + new string('[', 100) + new string(']', 100) + "}";
        AssertRejected(json, "not valid JSON");
    }

    [Fact]
    public void Parse_OversizedManifest_IsRejected()
    {
        var json = With("description", new string('x', PluginManifestParser.MaxManifestBytes));
        AssertRejected(json, "larger than");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("sdkVersion")]
    [InlineData("entryAssembly")]
    [InlineData("entryType")]
    public void Parse_MissingRequiredField_IsRejectedWithFieldName(string key)
    {
        AssertRejected(Without(key), $"'{key}' is required");
        AssertRejected(With(key, null), $"'{key}' is required");
        AssertRejected(With(key, "  "), $"'{key}' must not be empty");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("author")]
    [InlineData("entryType")]
    public void Parse_WrongFieldType_IsRejected(string key)
    {
        AssertRejected(With(key, 42), $"'{key}' must be a string, not a number");
        AssertRejected(With(key, new JsonObject()), $"'{key}' must be a string, not an object");
        AssertRejected(With(key, true), $"'{key}' must be a string, not a boolean");
    }

    [Fact]
    public void Parse_ReportsEveryProblemAtOnce()
    {
        var result = PluginManifestParser.Parse("""{ "id": "Bad Id", "version": "1.0", "capabilities": ["telepathy"] }""");

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 6, result.ErrorSummary); // id, name, version, sdkVersion, entryAssembly, entryType, capability
    }

    [Fact]
    public void Parse_DuplicateProperty_IsRejected()
    {
        // Different JSON readers keep different copies of a repeated key — treat it as ambiguous.
        var json = """{ "id": "a.b", "ID": "evil.plugin", "name": "N", "version": "1.0.0", "sdkVersion": "1.0", "entryAssembly": "A.dll", "entryType": "A.P" }""";
        AssertRejected(json, "appears more than once");
    }

    [Theory]
    [InlineData("com.acme.jumpcounter")]
    [InlineData("a.b")]
    [InlineData("io.github.some-user.my_plugin2")]
    [InlineData("com.3m.x")]
    public void Parse_ValidIds_AreAccepted(string id)
    {
        Assert.True(PluginManifestParser.IsValidId(id));
        Assert.True(PluginManifestParser.Parse(With("id", id)).IsValid);
    }

    [Theory]
    [InlineData("jumpcounter")]           // not reverse-DNS
    [InlineData("Com.Acme.Jump")]         // uppercase (ambiguous on case-insensitive filesystems)
    [InlineData("com..acme")]             // empty segment
    [InlineData(".com.acme")]
    [InlineData("com.acme.")]
    [InlineData("com.acme/../evil")]      // path traversal via the folder name
    [InlineData("..")]
    [InlineData("com.acme\\evil")]
    [InlineData("com.-acme")]
    [InlineData("com.acme-")]
    [InlineData("com acme")]
    [InlineData("con.acme")]              // CON.acme is a Windows device name
    [InlineData("nul.plugin")]
    [InlineData("com.äcme")]
    public void Parse_InvalidIds_AreRejected(string id)
    {
        Assert.False(PluginManifestParser.IsValidId(id));
        AssertRejected(With("id", id), "'id'");
    }

    [Fact]
    public void Parse_OverlongId_IsRejected()
    {
        var id = "com." + new string('a', 200);
        AssertRejected(With("id", id), "'id' is longer than");
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.0.1")]
    [InlineData("10.20.30")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("1.0.0-0.3.7")]
    [InlineData("1.0.0+20130313144700")]
    [InlineData("1.0.0-beta+exp.sha.5114f85")]
    public void Parse_ValidSemVer_IsAccepted(string version)
    {
        Assert.True(PluginManifestParser.Parse(With("version", version)).IsValid);
        Assert.True(PluginManifestParser.Parse(With("minAppVersion", version)).IsValid);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-alpha..1")]
    [InlineData("99999999999.0.0")]   // overflows int
    [InlineData("latest")]
    public void Parse_BadSemVer_IsRejected(string version)
    {
        AssertRejected(With("version", version), "'version'");
        AssertRejected(With("minAppVersion", version), "'minAppVersion'");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("^1.0")]
    [InlineData("01.0")]
    [InlineData("one")]
    public void Parse_BadSdkVersion_IsRejected(string sdkVersion)
        => AssertRejected(With("sdkVersion", sdkVersion), "'sdkVersion'");

    [Theory]
    [InlineData("Acme.dll")]
    [InlineData("lib/Acme.Plugin.DLL")]
    public void Parse_SafeEntryAssembly_IsAccepted(string path)
        => Assert.True(PluginManifestParser.Parse(With("entryAssembly", path)).IsValid);

    [Theory]
    [InlineData("../Acme.dll", "traversal")]
    [InlineData("lib/../../Acme.dll", "traversal")]
    [InlineData("..\\Acme.dll", "backslash")]
    [InlineData("/usr/lib/Acme.dll", "absolute")]
    [InlineData("C:/Windows/Acme.dll", "':'")]
    [InlineData("Acme.dll:stream", "':'")]
    [InlineData("Acme.exe", "must be a .dll")]
    [InlineData("Acme", "must be a .dll")]
    public void Parse_UnsafeEntryAssembly_IsRejected(string path, string reason)
        => AssertRejected(With("entryAssembly", path), reason);

    [Theory]
    [InlineData("Acme.Plugin")]
    [InlineData("Plugin")]
    [InlineData("Acme.Outer+Inner")]
    [InlineData("_Acme._Plugin1")]
    public void Parse_ValidEntryType_IsAccepted(string type)
        => Assert.True(PluginManifestParser.Parse(With("entryType", type)).IsValid);

    [Theory]
    [InlineData("Acme.Plugin, Acme")]         // assembly-qualified
    [InlineData("Acme.Plugin`1")]             // generic
    [InlineData("1Acme.Plugin")]
    [InlineData("Acme..Plugin")]
    [InlineData("Acme.Plugin.")]
    public void Parse_InvalidEntryType_IsRejected(string type)
        => AssertRejected(With("entryType", type), "'entryType'");

    [Fact]
    public void Parse_AllKnownCapabilities_AreAccepted()
    {
        var all = new JsonArray(PluginCapabilities.All.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());

        var result = PluginManifestParser.Parse(With("capabilities", all));

        Assert.True(result.IsValid, result.ErrorSummary);
        Assert.Equal(PluginCapabilities.All.Count, result.Manifest!.Capabilities.Count);
    }

    [Theory]
    [InlineData("telepathy")]
    [InlineData("Events")]       // capabilities are exact, lowercase
    [InlineData("ui")]
    [InlineData("ui.*")]
    [InlineData("")]
    public void Parse_UnknownCapability_IsRejected(string capability)
        => AssertRejected(With("capabilities", new JsonArray(capability)), "not a known capability");

    [Fact]
    public void Parse_CapabilitiesWrongShape_IsRejected()
    {
        AssertRejected(With("capabilities", "events"), "'capabilities' must be an array");
        AssertRejected(With("capabilities", new JsonArray(1)), "'capabilities[0]' must be a string");
        AssertRejected(With("capabilities", new JsonArray("events", new JsonObject())), "'capabilities[1]' must be a string");
    }

    [Fact]
    public void Parse_TooManyCapabilities_IsRejected()
    {
        var many = new JsonArray(Enumerable.Repeat("events", 100).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        AssertRejected(With("capabilities", many), "more than");
    }

    [Fact]
    public void Parse_DuplicateCapabilities_AreCollapsed()
    {
        var result = PluginManifestParser.Parse(With("capabilities", new JsonArray("events", "state", "events")));

        Assert.True(result.IsValid, result.ErrorSummary);
        Assert.Equal(["events", "state"], result.Manifest!.Capabilities);
    }

    [Fact]
    public void Parse_NullCapabilities_MeansNone()
    {
        var result = PluginManifestParser.Parse(With("capabilities", null));
        Assert.True(result.IsValid, result.ErrorSummary);
        Assert.Empty(result.Manifest!.Capabilities);
    }

    [Fact]
    public void Parse_ControlCharactersInName_AreRejected()
        => AssertRejected(With("name", "Jump\u001b[31mCounter"), "control characters");

    [Fact]
    public void Parse_MultiLineDescription_IsAccepted()
        => Assert.True(PluginManifestParser.Parse(With("description", "Line one.\nLine two.")).IsValid);

    [Fact]
    public void Parse_OverlongName_IsRejected()
        => AssertRejected(With("name", new string('n', 101)), "'name' is longer than");

    [Fact]
    public void ParseStream_ToleratesUtf8Bom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(TestPackages.ValidManifestJson)).ToArray();

        var result = PluginManifestParser.Parse(new MemoryStream(bytes));

        Assert.True(result.IsValid, result.ErrorSummary);
    }

    [Fact]
    public void ParseStream_InvalidUtf8_IsRejected()
    {
        var result = PluginManifestParser.Parse(new MemoryStream([0x7B, 0xFF, 0xFE, 0x7D]));
        Assert.False(result.IsValid);
        Assert.Contains("UTF-8", result.ErrorSummary);
    }

    [Fact]
    public void ParseStream_StopsReadingPastTheSizeLimit()
    {
        var stream = new CountingStream(new MemoryStream(new byte[10 * PluginManifestParser.MaxManifestBytes]));

        var result = PluginManifestParser.Parse(stream);

        Assert.False(result.IsValid);
        Assert.Contains("larger than", result.ErrorSummary);
        Assert.True(stream.BytesRead <= PluginManifestParser.MaxManifestBytes + 1);
    }

    [Fact]
    public void ParseFile_MissingFile_IsRejectedNotThrown()
    {
        var result = PluginManifestParser.ParseFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "plugin.json"));
        Assert.False(result.IsValid);
        Assert.Contains("could not be read", result.ErrorSummary);
    }

    [Fact]
    public void FindDuplicateIds_ReportsIdsSeenMoreThanOnce()
    {
        PluginManifest M(string id) => new(id, "N", "1.0.0", "1.0");

        var duplicates = PluginManifestParser.FindDuplicateIds([M("a.b"), M("c.d"), M("A.B"), M("e.f"), M("c.d")]);

        Assert.Equal(2, duplicates.Count);
        Assert.Contains(duplicates, d => d.Equals("a.b", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("c.d", duplicates);
    }

    [Fact]
    public void FindDuplicateIds_NoDuplicates_IsEmpty()
        => Assert.Empty(PluginManifestParser.FindDuplicateIds([new("a.b", "N", "1.0.0", "1.0"), new("c.d", "N", "1.0.0", "1.0")]));

    [Fact]
    public void Parse_SameJsonTwice_ProducesEqualManifests()
    {
        var a = PluginManifestParser.Parse(TestPackages.ValidManifestJson).Manifest!;
        var b = PluginManifestParser.Parse(TestPackages.ValidManifestJson).Manifest!;

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Parse_ManifestCapabilities_AreNotAWritableList()
    {
        var manifest = PluginManifestParser.Parse(TestPackages.ValidManifestJson).Manifest!;

        Assert.IsNotType<List<string>>(manifest.Capabilities);
        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)manifest.Capabilities).Add(PluginCapabilities.Network));
    }

    [Theory]
    [InlineData("name", "Jump\u202ECounter")]     // right-to-left override
    [InlineData("name", "Jump\u200BCounter")]     // zero-width space
    [InlineData("name", "Jump\u200DCounter")]     // zero-width joiner
    [InlineData("name", "\uFEFFJump Counter")]    // BOM / zero-width no-break space
    [InlineData("author", "Acme\u2066Corp")]      // left-to-right isolate
    [InlineData("description", "Line one.\n\u202Egnirts")]
    public void Parse_InvisibleFormattingCharacters_AreRejected(string key, string value)
        => AssertRejected(With(key, value), "invisible formatting character");

    [Fact]
    public void Parse_UnpairedSurrogateEscape_IsRejectedNotThrown()
    {
        // A lone "\uD800" escape: either the JSON reader or the text rules must reject it.
        var json = TestPackages.ValidManifestJson.Replace("\"Jump Counter\"", "\"Jump\\uD800Counter\"");
        Assert.NotEqual(TestPackages.ValidManifestJson, json);

        var result = PluginManifestParser.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains("'name' contains invalid Unicode", result.ErrorSummary);
    }

    [Fact]
    public void Parse_RawUnpairedSurrogateChar_IsRejectedNotThrown()
    {
        // A raw lone surrogate (not a JSON escape) makes JsonDocument.Parse throw ArgumentException
        // while transcoding to UTF-8. Built at runtime so no invisible character sits in source.
        var json = "{\"id\":\"a" + (char)0xD800 + "\"}";

        var result = PluginManifestParser.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains("invalid Unicode", result.ErrorSummary);
    }

    [Fact]
    public void Parse_UnpairedSurrogateEscapeInPropertyNameOrCapability_IsRejectedNotThrown()
    {
        var inKey = TestPackages.ValidManifestJson.Replace("\"author\"", "\"auth\\uDC00or\"");
        var inCapability = TestPackages.ValidManifestJson.Replace("\"state\"", "\"st\\uD800ate\"");

        Assert.Contains("invalid Unicode", PluginManifestParser.Parse(inKey).ErrorSummary);
        Assert.Contains("'capabilities[1]' contains invalid Unicode", PluginManifestParser.Parse(inCapability).ErrorSummary);
    }

    [Fact]
    public void Parse_NonAsciiButVisibleText_IsAccepted()
    {
        var result = PluginManifestParser.Parse(With("name", "Zähler für Sprünge ✦"));
        Assert.True(result.IsValid, result.ErrorSummary);
    }

    [Theory]
    [InlineData("com.acme.x\n")]
    [InlineData("com.acme.x\r\n")]
    public void IsValidId_RejectsTrailingNewline(string id)
        => Assert.False(PluginManifestParser.IsValidId(id));

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
