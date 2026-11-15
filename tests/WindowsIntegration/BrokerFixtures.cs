using System.Buffers;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using NetScope.Broker;
using NetScope.Domain;
using NetScope.Repair;

namespace NetScope.WindowsIntegration.Tests;

/// <summary>Manual monotonic clock and timer source so every broker bound is testable instantly.</summary>
internal sealed class FakeBrokerTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<FakeTimer> _timers = new();
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_sync) return _ticks;
    }

    public override DateTimeOffset GetUtcNow() => Origin.AddTicks(GetTimestamp());

    public static DateTimeOffset Origin { get; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        lock (_sync)
        {
            timer.ChangeCore(dueTime, period);
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
        FakeTimer[] due;
        lock (_sync)
        {
            _ticks = checked(_ticks + delta.Ticks);
            due = _timers.Where(timer => timer.TakeIfDue(_ticks)).ToArray();
        }

        foreach (var timer in due) timer.Fire();
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeBrokerTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long? _dueTicks;
        private bool _disposed;

        public FakeTimer(FakeBrokerTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._sync) return ChangeCore(dueTime, period);
        }

        internal bool ChangeCore(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
                throw new NotSupportedException("The fake broker timer supports one-shot scheduling only.");
            if (_disposed) return false;
            _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : checked(_owner._ticks + dueTime.Ticks);
            return true;
        }

        internal bool TakeIfDue(long now)
        {
            if (_disposed || _dueTicks is not { } due || due > now) return false;
            _dueTicks = null;
            return true;
        }

        internal void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._sync)
            {
                _disposed = true;
                _dueTicks = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// In-memory duplex stream pair. The broker side runs on one end and a scripted peer on the other;
/// no socket, pipe or file is opened.
/// </summary>
internal static class BrokerDuplex
{
    public static (Stream Client, Stream Server) Create()
    {
        var clientToServer = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var serverToClient = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        return (
            new Endpoint(serverToClient.Reader, clientToServer.Writer),
            new Endpoint(clientToServer.Reader, serverToClient.Writer));
    }

    private sealed class Endpoint : Stream
    {
        private readonly ChannelReader<byte[]> _inbound;
        private readonly ChannelWriter<byte[]> _outbound;
        private byte[] _pending = Array.Empty<byte>();
        private int _pendingOffset;

        public Endpoint(ChannelReader<byte[]> inbound, ChannelWriter<byte[]> outbound)
        {
            _inbound = inbound;
            _outbound = outbound;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pendingOffset >= _pending.Length)
            {
                while (await _inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_inbound.TryRead(out var chunk))
                    {
                        _pending = chunk;
                        _pendingOffset = 0;
                        break;
                    }
                }

                if (_pendingOffset >= _pending.Length) return 0;
            }

            var take = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, take).CopyTo(buffer);
            _pendingOffset += take;
            return take;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WriteCoreAsync(buffer, cancellationToken));

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken);

        private async Task WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0) return;
            await _outbound.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _outbound.TryComplete();
            base.Dispose(disposing);
        }
    }
}

internal sealed class FakePeerLease : IBrokerPeerLease
{
    public FakePeerLease(int processId = 4242) => ProcessId = processId;

    public int ProcessId { get; }

    public bool IsHeld { get; private set; } = true;

    public int DisposeCount { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
        IsHeld = false;
    }
}

internal sealed class FakeBrokerConnection : IBrokerConnection
{
    public FakeBrokerConnection(Stream stream, BrokerPeerEvidence evidence)
    {
        Stream = stream;
        PeerEvidence = evidence;
        Lease = new FakePeerLease(evidence.PeerProcessId);
    }

    public Stream Stream { get; }

    public BrokerPeerEvidence PeerEvidence { get; }

    public IBrokerPeerLease Lease { get; }

    public FakePeerLease FakeLease => (FakePeerLease)Lease;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        Lease.Dispose();
    }
}

internal sealed class FakeBrokerListener : IBrokerListener
{
    private readonly Func<CancellationToken, Task<IBrokerConnection>> _accept;

    public FakeBrokerListener(Func<CancellationToken, Task<IBrokerConnection>> accept, string? pipeName = null)
    {
        _accept = accept;
        PipeName = pipeName ?? "NetScopeRepair.Broker." + new string('a', 32);
    }

    public string PipeName { get; }

    public int AcceptCount { get; private set; }

    public Task<IBrokerConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        AcceptCount++;
        return _accept(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeDeploymentVerifier : IBrokerDeploymentVerifier
{
    private readonly BrokerDeploymentFacts _facts;

    public FakeDeploymentVerifier(BrokerDeploymentFacts facts) => _facts = facts;

    public int InspectCount { get; private set; }

    public bool? LastDevelopmentIsolation { get; private set; }

    public BrokerDeploymentFacts Inspect(bool developmentIsolation)
    {
        InspectCount++;
        LastDevelopmentIsolation = developmentIsolation;
        return _facts with { DevelopmentIsolation = developmentIsolation };
    }
}

internal sealed class FakeElevationProbe : IBrokerElevationProbe
{
    public FakeElevationProbe(BrokerElevationState state) => State = state;

    public BrokerElevationState State { get; }

    public int ProbeCount { get; private set; }

    public BrokerElevationState Probe()
    {
        ProbeCount++;
        return State;
    }
}

internal sealed class FixedBrokerNonceSource : IBrokerNonceSource
{
    private readonly byte[] _nonce;
    private int _calls;

    public FixedBrokerNonceSource(byte fill = 0x2A)
    {
        _nonce = Enumerable.Repeat(fill, BrokerProtocol.NonceBytes).ToArray();
    }

    public string Hex => Convert.ToHexStringLower(_nonce);

    public byte[] CreateNonce()
    {
        _calls++;
        return _nonce.ToArray();
    }

    public int Calls => _calls;
}

/// <summary>
/// Scripted client peer for protocol tests. It can emit arbitrary frames, which is exactly what a
/// strict protocol must tolerate without exiting the bounded rules.
/// </summary>
internal sealed class ScriptedBrokerClient
{
    public const string ClientNonceHex = "1111111111111111111111111111111111111111111111111111111111111111";

    private readonly Stream _stream;

    public ScriptedBrokerClient(Stream stream) => _stream = stream;

    public string ClientNonce { get; set; } = ClientNonceHex;

    public string TransactionId { get; set; } = "tx-t14";

    public string PlanDigest { get; set; } = "sha256:" + new string('0', 64);

    public List<BrokerFrame> Received { get; } = new();

    public Task SendFrameAsync(BrokerFrame frame) =>
        BrokerFrameCodec.WriteAsync(_stream, frame, CancellationToken.None);

    public async Task SendHelloAsync(int processId, DateTimeOffset creation, string? protocolVersion = null)
    {
        await BrokerFrameCodec.WriteAsync(_stream, BrokerMessages.Frame(BrokerMessageKind.Hello, new BrokerHelloMessage
        {
            ProtocolVersion = protocolVersion ?? BrokerProtocol.Version,
            ClientNonce = ClientNonce,
            ClientProcessId = processId,
            ClientProcessCreationUtc = creation,
        }), CancellationToken.None);
    }

    public async Task<BrokerChallengeMessage> ReadChallengeAsync()
    {
        var frame = await BrokerFrameCodec.ReadAsync(_stream, CancellationToken.None);
        Received.Add(frame);
        Assert.Equal(BrokerMessageKind.Challenge, frame.Kind);
        return frame.Payload!.Value.Deserialize<BrokerChallengeMessage>(BrokerJson.Options)!;
    }

    public static string BindingFor(BrokerChallengeMessage challenge, string clientNonce, string transactionId, string planDigest) =>
        BrokerNonceBinding.Compute(clientNonce, challenge.BrokerNonce, transactionId, planDigest);

    public async Task SendAuthenticateAsync(
        BrokerChallengeMessage challenge, string? transactionId = null, string? planDigest = null, string? binding = null)
    {
        var tx = transactionId ?? TransactionId;
        var digest = planDigest ?? PlanDigest;
        await BrokerFrameCodec.WriteAsync(_stream, BrokerMessages.Frame(
            BrokerMessageKind.Authenticate, new BrokerAuthenticateMessage
            {
                BrokerNonce = challenge.BrokerNonce,
                ClientNonce = ClientNonce,
                TransactionId = tx,
                PlanDigest = digest,
                NonceBinding = binding ?? BindingFor(challenge, ClientNonce, tx, digest),
            }), CancellationToken.None);
    }

    public async Task SendRawAsync(byte[] frameBytes, int? declaredLength = null)
    {
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            header, declaredLength ?? frameBytes.Length);
        await _stream.WriteAsync(header, CancellationToken.None);
        if (frameBytes.Length > 0) await _stream.WriteAsync(frameBytes, CancellationToken.None);
        await _stream.FlushAsync(CancellationToken.None);
    }

    public async Task<BrokerFrame> ReadFrameAsync()
    {
        var frame = await BrokerFrameCodec.ReadAsync(_stream, CancellationToken.None);
        Received.Add(frame);
        return frame;
    }

    public Task SendSubmitAsync(BrokerSubmitTransactionMessage submit) =>
        BrokerFrameCodec.WriteAsync(
            _stream, BrokerMessages.Frame(BrokerMessageKind.SubmitTransaction, submit), CancellationToken.None);

    public async Task SendCloseAsync()
    {
        await BrokerFrameCodec.WriteAsync(_stream, BrokerMessages.Frame(
            BrokerMessageKind.Close, new { }), CancellationToken.None);
    }
}






