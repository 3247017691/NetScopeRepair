using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetScope.Domain;
using NetScope.Storage;

namespace NetScope.Replay.Tests;

public sealed class StorageAdmissionTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("sequence")]
    [InlineData("monotonic")]
    [InlineData("reference")]
    [InlineData("long_id")]
    [InlineData("null_reference")]
    public async Task Invalid_core_events_are_rejected_by_both_writer_and_replay(string fault)
    {
        using var workspace = new TempWorkspace();
        var node = JsonNode.Parse(Fixtures.RawLines()[0])!.AsObject();
        switch (fault)
        {
            case "version": node["schema_version"] = "99.0"; break;
            case "sequence": node["sequence"] = 0; break;
            case "monotonic": node["monotonic_ms"] = -1; break;
            case "reference": node["refs"]!["snapshot_id"] = ""; break;
            case "long_id": node["event_id"] = new string('x', 129); break;
            case "null_reference": node["refs"]!["snapshot_id"] = null; break;
        }
        var badPath = workspace.File("invalid.ndjson");
        File.WriteAllText(badPath, node.ToJsonString() + "\n");
        var replay = new ReplayService().Replay(badPath);
        Assert.Empty(replay.Log.Events);
        Assert.Equal(0, replay.Summary.SuccessCount);
        Assert.Single(replay.Log.Problems);
        Assert.False(replay.Log.HasTruncatedTail); // Complete JSON with a bad contract is not an interrupted write.
        Assert.False(replay.Log.IsClean);

        var valid = Fixtures.Event(0, "writer-invalid", 1);
        var invalid = fault switch
        {
            "version" => valid with { SchemaVersion = "99.0" },
            "sequence" => valid with { Sequence = 0 },
            "monotonic" => valid with { MonotonicMs = -1 },
            "long_id" => valid with { EventId = new string('x', 129) },
            _ => valid with { Refs = new EventRefs { SnapshotId = "" } },
        };
        var writerPath = workspace.File("writer.ndjson");
        using var writer = new NdjsonEventLog(writerPath);
        await Assert.ThrowsAsync<JsonException>(() => writer.AppendAsync(invalid, CancellationToken.None));
        Assert.False(File.Exists(writerPath));
    }

    [Fact]
    public async Task Failed_open_does_not_reserve_an_event_id_and_retry_succeeds()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("retry.ndjson");
        using var log = new NdjsonEventLog(path);
        await log.AppendAsync(Fixtures.Event(0, "seed", 1), CancellationToken.None);
        var next = Fixtures.Event(1, "retry", 2);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => log.AppendAsync(next, CancellationToken.None));
        await log.AppendAsync(next, CancellationToken.None);
        Assert.Equal(new[] { "seed", "retry" }, log.Read().Events.Select(x => x.EventId));
    }

    [Fact]
    public async Task Failed_write_can_be_retried_when_no_bytes_were_written()
    {
        using var workspace = new TempWorkspace();
        var files = new FaultFiles(Fault.BeforeWrite);
        using var log = new NdjsonEventLog(workspace.File("write.ndjson"), files);
        var next = Fixtures.Event(0, "retry", 1);
        await Assert.ThrowsAsync<IOException>(() => log.AppendAsync(next, CancellationToken.None));
        await log.AppendAsync(next, CancellationToken.None);
        Assert.Single(log.Read().Events);
        Assert.True(log.Read().IsClean);
    }

    [Theory]
    [InlineData(Fault.PartialWrite)]
    [InlineData(Fault.Flush)]
    public async Task Write_or_flush_failures_are_not_acknowledged_and_retries_consult_actual_file(Fault fault)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("failed.ndjson");
        File.WriteAllText(path, EnvelopeJson.Serialize(Fixtures.Event(0, "seed", 1)) + "\n");
        using var log = new NdjsonEventLog(path, new FaultFiles(fault));
        var next = Fixtures.Event(1, "new", 2);
        await Assert.ThrowsAsync<IOException>(() => log.AppendAsync(next, CancellationToken.None));
        var before = File.ReadAllBytes(path);
        if (fault == Fault.PartialWrite)
        {
            Assert.Single(log.Read().Events);
            Assert.True(log.Read().HasTruncatedTail);
            await Assert.ThrowsAsync<IOException>(() => log.AppendAsync(next, CancellationToken.None));
        }
        else
        {
            // Full bytes exist despite a flush failure: do not write a second copy or claim the first call succeeded.
            Assert.Equal(2, log.Read().Events.Count);
            await Assert.ThrowsAsync<DuplicateEventException>(() => log.AppendAsync(next, CancellationToken.None));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Corrupt_tail_refuses_append_and_preserves_every_existing_byte()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("tail.ndjson");
        var bytes = Encoding.UTF8.GetBytes(EnvelopeJson.Serialize(Fixtures.Event(0, "seed", 1)) + "\n{\"schema_version\":");
        File.WriteAllBytes(path, bytes);
        using var log = new NdjsonEventLog(path);
        await Assert.ThrowsAsync<IOException>(() => log.AppendAsync(Fixtures.Event(1, "new", 2), CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Single(log.Read().Events);
    }

    [Fact]
    public async Task Complete_last_value_without_newline_gets_a_separator_before_append()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("no-newline.ndjson");
        File.WriteAllText(path, EnvelopeJson.Serialize(Fixtures.Event(0, "seed", 1)));
        using var log = new NdjsonEventLog(path);
        await log.AppendAsync(Fixtures.Event(1, "new", 2), CancellationToken.None);
        Assert.Equal(2, log.Read().Events.Count);
        Assert.True(log.Read().IsClean);
    }

    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32")]
    [InlineData("utf32be")]
    public async Task Non_utf8_logs_are_rejected_before_append_can_mix_encodings(string encodingName)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("encoding.ndjson");
        var encoding = encodingName switch
        {
            "utf16le" => Encoding.Unicode,
            "utf16be" => Encoding.BigEndianUnicode,
            "utf32be" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
            _ => Encoding.UTF32,
        };
        File.WriteAllText(path, EnvelopeJson.Serialize(Fixtures.Event(0, "seed", 1)) + "\n", encoding);
        var before = File.ReadAllBytes(path);
        using var log = new NdjsonEventLog(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => log.AppendAsync(Fixtures.Event(1, "new", 2), CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(log.Read().IsClean);
        Assert.Empty(log.Read().Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Short_prefix_reads_cannot_bypass_encoding_admission(bool bigEndian)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("short-prefix.ndjson");
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        File.WriteAllText(path, EnvelopeJson.Serialize(Fixtures.Event(0, "seed", 1)) + "\n", encoding);
        var before = File.ReadAllBytes(path);
        using var log = new NdjsonEventLog(path, new FaultFiles(Fault.ShortFirstRead));
        await Assert.ThrowsAsync<InvalidDataException>(() => log.AppendAsync(Fixtures.Event(1, "new", 2), CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_second_writer_sees_ids_added_after_it_was_created()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("two-writers.ndjson");
        using var first = new NdjsonEventLog(path);
        using var second = new NdjsonEventLog(path);
        var item = Fixtures.Event(0, "shared-id", 1);
        await first.AppendAsync(item, CancellationToken.None);
        await Assert.ThrowsAsync<DuplicateEventException>(() => second.AppendAsync(item, CancellationToken.None));
        Assert.Single(first.Read().Events);
    }

    public enum Fault { BeforeWrite, PartialWrite, Flush, ShortFirstRead }

    private sealed class FaultFiles(Fault fault) : IEventLogFileAccess
    {
        private bool _armed = true;
        public Stream OpenForAppend(string path)
        {
            var inner = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var armed = _armed;
            _armed = false;
            return new FaultStream(inner, armed ? fault : null);
        }
        public void FlushToDisk(Stream stream)
        {
            var wrapper = (FaultStream)stream;
            if (wrapper.Mode == Fault.Flush) throw new IOException("synthetic flush failure");
            wrapper.Inner.Flush(true);
        }
    }

    private sealed class FaultStream(FileStream inner, Fault? mode) : Stream
    {
        public FileStream Inner { get; } = inner;
        public Fault? Mode { get; } = mode;
        private bool _readOnce;
        public override bool CanRead => Inner.CanRead;
        public override bool CanWrite => Inner.CanWrite;
        public override bool CanSeek => Inner.CanSeek;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }
        public override void Flush() => Inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var requested = Mode == Fault.ShortFirstRead && !_readOnce ? Math.Min(1, count) : count;
            _readOnce = true;
            return Inner.Read(buffer, offset, requested);
        }
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => Inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Mode == Fault.BeforeWrite) throw new IOException("synthetic write failure");
            if (Mode == Fault.PartialWrite)
            {
                await Inner.WriteAsync(buffer[..(buffer.Length / 2)], cancellationToken);
                throw new IOException("synthetic partial write failure");
            }
            await Inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) Inner.Dispose(); base.Dispose(disposing); }
    }
}
