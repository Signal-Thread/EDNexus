using EDNexus.Ebs.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EDNexus.Ebs.Tests;

public sealed class DataProtectionKeyRingMoveTests : IDisposable
{
    private readonly TempEbsDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    private string SeparateKeysPath => Path.Combine(_data.Path, "separate-keys");

    [Fact]
    public void Grants_encrypted_before_the_move_still_decrypt_after_it()
    {
        // An existing deployment: key ring in the default {DataDirectory}/keys.
        var issued = _data.CreateTokenStore().IssueToken("chan-1", "CMDR", "twitch-access", "twitch-refresh", DateTimeOffset.UtcNow.AddHours(4));

        var moved = DataProtectionKeyRingMove.MoveIfNeeded(_data.KeysPath, SeparateKeysPath, NullLogger.Instance);

        // Restarted against the separate directory: nobody is logged out.
        Assert.True(moved > 0);
        Assert.True(_data.CreateTokenStore(keysPath: SeparateKeysPath).TryGetByToken(issued.Token, out var found));
        Assert.Equal("twitch-access", found.TwitchAccessToken);

        // And the database's volume no longer holds the keys that decrypt it.
        Assert.Empty(Directory.GetFiles(_data.KeysPath, "*.xml"));
    }

    [Fact]
    public void Nothing_happens_when_the_configured_directory_is_the_default()
    {
        _data.CreateTokenStore().IssueToken("chan-1", "CMDR", "a", "r", DateTimeOffset.UtcNow.AddHours(4));

        Assert.Equal(0, DataProtectionKeyRingMove.MoveIfNeeded(_data.KeysPath, _data.KeysPath + Path.DirectorySeparatorChar, NullLogger.Instance));
        Assert.NotEmpty(Directory.GetFiles(_data.KeysPath, "*.xml"));
    }

    [Fact]
    public void Nothing_happens_on_a_fresh_install()
    {
        Assert.Equal(0, DataProtectionKeyRingMove.MoveIfNeeded(_data.KeysPath, SeparateKeysPath, NullLogger.Instance));
    }

    [Fact]
    public void Two_rings_are_both_left_alone()
    {
        _data.CreateTokenStore().IssueToken("chan-1", "CMDR", "a", "r", DateTimeOffset.UtcNow.AddHours(4));
        _data.CreateTokenStore(keysPath: SeparateKeysPath).IssueToken("chan-2", "CMDR", "a", "r", DateTimeOffset.UtcNow.AddHours(4));
        var before = Directory.GetFiles(_data.KeysPath, "*.xml").Length;

        // Which ring is current is not something to guess: overwriting either could orphan grants.
        Assert.Equal(0, DataProtectionKeyRingMove.MoveIfNeeded(_data.KeysPath, SeparateKeysPath, NullLogger.Instance));
        Assert.Equal(before, Directory.GetFiles(_data.KeysPath, "*.xml").Length);
    }

    [Fact]
    public void A_move_interrupted_while_copying_is_finished()
    {
        var issued = IssueWithTwoKeys();
        var legacy = Directory.GetFiles(_data.KeysPath, "*.xml").OrderBy(f => f, StringComparer.Ordinal).ToArray();

        // The first copy landed, then the process died before the second (and before any delete).
        Directory.CreateDirectory(SeparateKeysPath);
        File.Copy(legacy[0], Path.Combine(SeparateKeysPath, Path.GetFileName(legacy[0])));

        Assert.Equal(legacy.Length, DataProtectionKeyRingMove.MoveIfNeeded(_data.KeysPath, SeparateKeysPath, NullLogger.Instance));
        Assert.Equal(legacy.Length, Directory.GetFiles(SeparateKeysPath, "*.xml").Length);
        Assert.Empty(Directory.GetFiles(_data.KeysPath, "*.xml"));
        Assert.True(_data.CreateTokenStore(keysPath: SeparateKeysPath).TryGetByToken(issued, out _));
    }

    [Fact]
    public void A_move_interrupted_while_deleting_is_finished()
    {
        var issued = IssueWithTwoKeys();
        var legacy = Directory.GetFiles(_data.KeysPath, "*.xml").OrderBy(f => f, StringComparer.Ordinal).ToArray();

        // Every copy landed and the first original was deleted, then the process died.
        Directory.CreateDirectory(SeparateKeysPath);
        foreach (var file in legacy) File.Copy(file, Path.Combine(SeparateKeysPath, Path.GetFileName(file)));
        File.Delete(legacy[0]);

        DataProtectionKeyRingMove.MoveIfNeeded(_data.KeysPath, SeparateKeysPath, NullLogger.Instance);

        Assert.Empty(Directory.GetFiles(_data.KeysPath, "*.xml"));
        Assert.Equal(legacy.Length, Directory.GetFiles(SeparateKeysPath, "*.xml").Length);
        Assert.True(_data.CreateTokenStore(keysPath: SeparateKeysPath).TryGetByToken(issued, out _));
    }

    [Fact]
    public void A_same_named_file_with_different_content_leaves_both_alone()
    {
        IssueWithTwoKeys();
        var legacy = Directory.GetFiles(_data.KeysPath, "*.xml");
        Directory.CreateDirectory(SeparateKeysPath);
        File.WriteAllText(Path.Combine(SeparateKeysPath, Path.GetFileName(legacy[0])), "<key />");

        Assert.Equal(0, DataProtectionKeyRingMove.MoveIfNeeded(_data.KeysPath, SeparateKeysPath, NullLogger.Instance));
        Assert.Equal(legacy.Length, Directory.GetFiles(_data.KeysPath, "*.xml").Length);
    }

    /// <summary>
    /// Issues a grant against a default ring that holds two key files, so a move can be caught half
    /// way. The second key is one a rotation would have added.
    /// </summary>
    private string IssueWithTwoKeys()
    {
        var token = _data.CreateTokenStore().IssueToken("chan-1", "CMDR", "a", "r", DateTimeOffset.UtcNow.AddHours(4)).Token;

        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("EDNexus.Ebs").PersistKeysToFileSystem(new DirectoryInfo(_data.KeysPath));
        using (var provider = services.BuildServiceProvider())
            provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        Assert.Equal(2, Directory.GetFiles(_data.KeysPath, "*.xml").Length);
        return token;
    }
}
