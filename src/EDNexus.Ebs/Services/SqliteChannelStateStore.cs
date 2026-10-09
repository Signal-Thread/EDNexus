using System.Globalization;
using System.Text.Json;

namespace EDNexus.Ebs.Services;

/// <summary>
/// <see cref="IChannelStateStore"/> backed by <see cref="EbsDatabase"/>, so
/// <c>GET /api/initial-state/{channelId}</c> still answers with the last published state after the
/// EBS restarts instead of 404ing until the broadcaster's next update. Rows older than the
/// configured maximum age are treated as absent (the age is compared in code, so a read is a pure
/// SELECT that never takes the SQLite writer lock) and are deleted by <see cref="PruneExpired"/>,
/// which runs on a timer — so a snapshot whose clear never arrived does not stay public forever.
/// </summary>
public sealed class SqliteChannelStateStore : IChannelStateStore
{
    private readonly EbsDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _maxAge;

    /// <param name="maxAge">Oldest snapshot served; null keeps snapshots until they are removed.</param>
    public SqliteChannelStateStore(EbsDatabase database, TimeProvider? timeProvider = null, TimeSpan? maxAge = null)
    {
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxAge = maxAge;
    }

    /// <inheritdoc />
    public void Set(string channelId, JsonElement state)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO channel_state (channel_id, state_json, updated_at) VALUES ($channel, $state, $updated)
            ON CONFLICT(channel_id) DO UPDATE SET state_json = excluded.state_json, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$channel", channelId);
        command.Parameters.AddWithValue("$state", state.GetRawText());
        command.Parameters.AddWithValue("$updated", Timestamp(_timeProvider.GetUtcNow()));
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public bool TryGet(string channelId, out JsonElement state)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json, updated_at FROM channel_state WHERE channel_id = $channel;";
        command.Parameters.AddWithValue("$channel", channelId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            state = default;
            return false;
        }

        var json = reader.GetString(0);
        if (_maxAge is { } maxAge && IsExpired(reader.GetString(1), maxAge))
        {
            // Past the limit counts as gone even before the timer has deleted the row.
            state = default;
            return false;
        }

        using var document = JsonDocument.Parse(json);
        state = document.RootElement.Clone();
        return true;
    }

    /// <inheritdoc />
    public void Remove(string channelId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM channel_state WHERE channel_id = $channel;";
        command.Parameters.AddWithValue("$channel", channelId);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public int PruneExpired()
    {
        if (_maxAge is not { } maxAge) return 0;

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        // Round-trip ("O") UTC timestamps are fixed-width, so they compare correctly as text — and
        // that is what lets ix_channel_state_updated_at serve this range delete.
        command.CommandText = "DELETE FROM channel_state WHERE updated_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", Timestamp(_timeProvider.GetUtcNow() - maxAge));
        return command.ExecuteNonQuery();
    }

    // An unparseable timestamp is treated as expired: nothing this service wrote can be one, and
    // failing closed keeps an unreadable row from being served forever.
    private bool IsExpired(string updatedAt, TimeSpan maxAge) =>
        !DateTimeOffset.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
        || _timeProvider.GetUtcNow() - parsed > maxAge;

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
