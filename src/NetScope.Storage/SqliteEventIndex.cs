using Microsoft.Data.Sqlite;
using NetScope.Domain;

namespace NetScope.Storage;

/// <summary>One row of the query projection.</summary>
public sealed record IndexedEvent(string EventId, int Sequence, EventType EventType, EventStatus Status, bool Synthetic);

/// <summary>
/// SQLite projection of the event log, used for queries and timelines.
/// <para>
/// The projection is not the source of truth: it can always be rebuilt from the NDJSON log. A
/// duplicate insert is ignored rather than replaced, so an event keeps the identity it was
/// written with.
/// </para>
/// </summary>
public sealed class SqliteEventIndex : IDisposable
{
    private readonly SqliteConnection _connection;

    private bool _disposed;

    private SqliteEventIndex(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Opens (or creates) a projection database in WAL mode.</summary>
    public static SqliteEventIndex Open(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("The database path must not be empty.", nameof(databasePath));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        var index = new SqliteEventIndex(connection);
        index.EnsureSchema();
        return index;
    }

    /// <summary>Opens a private in-memory projection; used by tests and by offline replay.</summary>
    public static SqliteEventIndex OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var index = new SqliteEventIndex(connection);
        index.EnsureSchema();
        return index;
    }

    public void EnsureSchema()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS events (
                event_id     TEXT    PRIMARY KEY,
                sequence     INTEGER NOT NULL,
                event_type   TEXT    NOT NULL,
                status       TEXT    NOT NULL,
                synthetic    INTEGER NOT NULL,
                observed_at  TEXT    NOT NULL
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_events_sequence ON events(sequence);");
    }

    /// <summary>Projects one event; returns false when the event id was already indexed.</summary>
    public bool Project(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var transaction = _connection.BeginTransaction();

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO events (event_id, sequence, event_type, status, synthetic, observed_at)
            VALUES ($id, $sequence, $type, $status, $synthetic, $observedAt);
            """;
        command.Parameters.AddWithValue("$id", envelope.EventId);
        command.Parameters.AddWithValue("$sequence", envelope.Sequence);
        command.Parameters.AddWithValue("$type", envelope.EventType.ToString());
        command.Parameters.AddWithValue("$status", envelope.Status.ToString());
        command.Parameters.AddWithValue("$synthetic", envelope.Synthetic ? 1 : 0);
        command.Parameters.AddWithValue("$observedAt", envelope.ObservedAt.ToString("O"));

        var inserted = command.ExecuteNonQuery();
        transaction.Commit();
        return inserted == 1;
    }

    /// <summary>Projects a batch in one transaction; returns how many rows were new.</summary>
    public int ProjectAll(IEnumerable<EventEnvelope> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var inserted = 0;

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO events (event_id, sequence, event_type, status, synthetic, observed_at)
            VALUES ($id, $sequence, $type, $status, $synthetic, $observedAt);
            """;

        var id = command.Parameters.Add("$id", SqliteType.Text);
        var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
        var type = command.Parameters.Add("$type", SqliteType.Text);
        var status = command.Parameters.Add("$status", SqliteType.Text);
        var synthetic = command.Parameters.Add("$synthetic", SqliteType.Integer);
        var observedAt = command.Parameters.Add("$observedAt", SqliteType.Text);

        foreach (var envelope in events)
        {
            id.Value = envelope.EventId;
            sequence.Value = envelope.Sequence;
            type.Value = envelope.EventType.ToString();
            status.Value = envelope.Status.ToString();
            synthetic.Value = envelope.Synthetic ? 1 : 0;
            observedAt.Value = envelope.ObservedAt.ToString("O");
            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return inserted;
    }

    /// <summary>Drops every row and rebuilds the projection from the given events.</summary>
    public int Rebuild(IEnumerable<EventEnvelope> events)
    {
        Execute("DELETE FROM events;");
        return ProjectAll(events);
    }

    public int Count()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<IndexedEvent> ByStatus(EventStatus status)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, sequence, event_type, status, synthetic
            FROM events WHERE status = $status ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$status", status.ToString());

        using var reader = command.ExecuteReader();
        var rows = new List<IndexedEvent>();

        while (reader.Read())
        {
            rows.Add(new IndexedEvent(
                reader.GetString(0),
                reader.GetInt32(1),
                Enum.Parse<EventType>(reader.GetString(2)),
                Enum.Parse<EventStatus>(reader.GetString(3)),
                reader.GetInt32(4) == 1));
        }

        return rows;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
