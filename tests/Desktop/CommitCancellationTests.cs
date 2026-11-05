using System.Threading.Channels;
using NetScope.Desktop.Services;
using NetScope.Storage;

namespace NetScope.Desktop.Tests;

public sealed class CommitCancellationTests
{
    [Fact]
    public async Task Pause_between_write_and_flush_finishes_the_record_and_resumes_with_the_next_sequence()
    {
        using var temp = new TempAppPaths();
        ObservationSession? active = null;
        var files = new PausingFiles(() => active!.Pause());
        await using var session = new ObservationSession(new StubSnapshotCollector(), new StubProbeRunner(),
            new TestClock(), temp.Paths, sessionId: "session-flush-cancel", bootId: "boot-test", logFiles: files);
        active = session;
        session.Start(TimeSpan.FromSeconds(30));
        await files.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await session.PauseAsync();
        Assert.Equal(1, session.EventCount);
        session.Start(TimeSpan.FromSeconds(30));
        await files.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await session.PauseAsync();
        var replay = new ReplayService().Replay(session.SessionPath);
        Assert.True(replay.IsClean, string.Join("; ", replay.EvidenceProblems));
        Assert.Equal(new[] { 1, 2 }, replay.Log.Events.Select(item => item.Sequence));
        Assert.All(files.FlushTokens, cancellable => Assert.False(cancellable));
        Assert.Null(session.PersistenceError);
    }

    private sealed class PausingFiles(Action pause) : IEventLogFileAccess
    {
        public Channel<bool> Writes { get; } = Channel.CreateUnbounded<bool>();
        public List<bool> FlushTokens { get; } = new();
        public Stream OpenForAppend(string path) => new WriteCallbackStream(
            new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.Asynchronous),
            () => { pause(); Writes.Writer.TryWrite(true); }, FlushTokens);
        public void FlushToDisk(Stream stream) => ((WriteCallbackStream)stream).Inner.Flush(flushToDisk: true);
    }

    private sealed class WriteCallbackStream(FileStream inner, Action written, List<bool> flushTokens) : Stream
    {
        public FileStream Inner => inner;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            written();
        }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            flushTokens.Add(cancellationToken.CanBeCanceled);
            return inner.FlushAsync(cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
