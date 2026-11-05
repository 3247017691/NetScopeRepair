using System.Text;
using NetScope.Adapters;
using NetScope.Application;

namespace NetScope.Adapters.Tests;

/// <summary>Review regressions use synthetic text/streams only; no user files, sockets or machine readers.</summary>
public sealed class ReviewAdapterTests
{
    private const int Limit = 2 * 1024 * 1024;
    private const string Marker = "SYNTHETIC_REVIEW_PRIVATE_VALUE";
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.UnixEpoch.AddHours(1);
    private const string SyntheticPath = "synthetic://review/runtime.yaml";

    [Theory]
    [InlineData("proxies:\n  - name: node\n    port: 443\n    server: SYNTHETIC_REVIEW_PRIVATE_VALUE\n    password: SYNTHETIC_REVIEW_PRIVATE_VALUE\n")]
    [InlineData("proxies:\n- name: node\n  port: 443\n  password: SYNTHETIC_REVIEW_PRIVATE_VALUE\n")]
    [InlineData("proxy-providers:\n  provider:\n    port: 443\n    server: SYNTHETIC_REVIEW_PRIVATE_VALUE\n")]
    [InlineData("unknown:\n  port: 443\n  mixed-port: 444\n  mode: global\n  tun:\n    enable: true\n  dns:\n    fake-ip-range: 10.0.0.0/8\n")]
    [InlineData("unknown: |\n  port: 443\n  password:\tSYNTHETIC_REVIEW_PRIVATE_VALUE\n")]
    public async Task Nice_nested_port_never_conflicts_with_the_real_root_port(string subtree)
    {
        var evidence = await ReadNice("mixed-port: 7890\nmode: rule\n" + subtree);
        Assert.True(evidence.Valid);
        Assert.Equal("7890", evidence.Facts["mixed_port"]);
        Assert.Equal("rule", evidence.Facts["mode"]);
        Assert.Equal(2, evidence.Facts.Count);
        Assert.NotEmpty(evidence.Gaps);
        Assert.DoesNotContain(Marker, EvidenceText.Describe(evidence), StringComparison.Ordinal);
        Assert.DoesNotContain("443", evidence.Facts.Values);
    }

    [Theory]
    [InlineData("proxies:\n  - name: node\n    port: 443\n")]
    [InlineData("- name: node\n  port: 443\n")]
    [InlineData("wrapper:\n  port: 443\n  tun:\n    enable: true\n")]
    [InlineData("  mixed-port: 443\n  mode: global\n")]
    public async Task Nice_missing_root_port_stays_unknown_instead_of_using_a_nested_value(string subtree)
    {
        var evidence = await ReadNice("mode: rule\n" + subtree);
        Assert.True(evidence.Valid);
        Assert.Equal(new Dictionary<string, string> { ["mode"] = "rule" }, evidence.Facts);
        Assert.Contains(evidence.Gaps, gap => gap.Contains("mixed_port", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nice_only_direct_tun_and_dns_children_are_facts_and_root_siblings_resume_normally()
    {
        var evidence = await ReadNice("""
            mode: rule
            tun:
              unknown:
                enable: true
                mtu: 1500
              enable: false
              mtu: 9000
            dns:
              nameserver:
                fake-ip-range: 10.0.0.0/8
              fake-ip-range: 198.18.0.1/16
            mixed-port: 7890
            """);
        Assert.True(evidence.Valid);
        Assert.Equal("false", evidence.Facts["tun_enable"]);
        Assert.Equal("9000", evidence.Facts["tun_mtu"]);
        Assert.Equal("198.18.0.1/16", evidence.Facts["dns_fake_ip_range"]);
        Assert.Equal("7890", evidence.Facts["mixed_port"]);
        Assert.Equal(5, evidence.Facts.Count);
    }

    [Theory]
    [InlineData("tun:\n  - name: child\n    enable: true\n    mtu: 9000\n")]
    [InlineData("unknown:\n  tun:\n    enable: true\n    mtu: 9000\n")]
    [InlineData("tun.enable: true\ntun.mtu: 9000\n")]
    public async Task Nice_lists_and_qualified_literal_keys_do_not_create_tun_facts(string subtree)
    {
        var evidence = await ReadNice("mode: rule\n" + subtree);
        Assert.True(evidence.Valid);
        Assert.False(evidence.Facts.ContainsKey("tun_enable"));
        Assert.False(evidence.Facts.ContainsKey("tun_mtu"));
        Assert.Contains(evidence.Gaps, gap => gap.Contains("tun_enable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nice_an_indentless_list_under_an_unknown_child_does_not_hide_a_real_direct_sibling()
    {
        var evidence = await ReadNice("mode: rule\ntun:\n  unknown:\n  - name: child\n    enable: true\n    mtu: 1500\n  enable: false\n  mtu: 9000\n");
        Assert.True(evidence.Valid);
        Assert.Equal("false", evidence.Facts["tun_enable"]);
        Assert.Equal("9000", evidence.Facts["tun_mtu"]);
    }

    [Fact]
    public async Task Nice_genuinely_conflicting_root_fields_are_still_invalid()
    {
        var evidence = await ReadNice("mixed-port: 7890\nmixed-port: 443\nmode: rule\n");
        Assert.False(evidence.Valid);
        Assert.Contains("多个取值", evidence.ValidityReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nice_http_port_is_not_an_alias_for_the_mixed_port()
    {
        var both = await ReadNice("mixed-port: 7890\nport: 7891\nmode: rule\n");
        Assert.True(both.Valid);
        Assert.Equal("7890", both.Facts["mixed_port"]);
        var onlyHttp = await ReadNice("port: 7891\nmode: rule\n");
        Assert.True(onlyHttp.Valid);
        Assert.False(onlyHttp.Facts.ContainsKey("mixed_port"));
    }

    [Fact]
    public async Task Nice_a_document_with_only_unknown_nested_values_has_no_facts()
    {
        var evidence = await ReadNice("proxies:\n  - name: node\n    port: 443\n    password: " + Marker + "\n");
        Assert.False(evidence.Valid);
        Assert.Empty(evidence.Facts);
        Assert.NotEmpty(evidence.Gaps);
        Assert.DoesNotContain(Marker, EvidenceText.Describe(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_partial_config_gaps_are_exposed_on_ClientStatus_and_prevent_completeness()
    {
        var files = new SyntheticConfigFileSource().AddFile(SyntheticPath, Encoding.UTF8.GetBytes("mode: rule\n"));
        var provider = AdapterFactory.Create(configAdapters: new[] { new NiceAdapter(files, SyntheticPath) });
        var status = Assert.Single(await provider.ReadAsync(CancellationToken.None), item => item.ClientId == KnownClientIds.Nice);
        var evidence = Assert.Single(status.Evidence);
        Assert.True(evidence.Valid);
        Assert.NotEmpty(evidence.Gaps);
        Assert.All(evidence.Gaps, gap => Assert.Contains(gap, status.Gaps));
        Assert.False(status.IsComplete);
    }

    [Fact]
    public async Task Fully_known_whitelist_stays_valid_without_inventing_a_gap()
    {
        var evidence = await ReadNice("mixed-port: 7890\nmode: rule\nallow-lan: false\ntun:\n  enable: true\n  mtu: 9000\ndns:\n  fake-ip-range: 198.18.0.1/16\n");
        Assert.True(evidence.Valid);
        Assert.Equal(6, evidence.Facts.Count);
        Assert.Empty(evidence.Gaps);
        var complete = new ClientStatus
        {
            ClientId = KnownClientIds.Nice, DisplayName = "synthetic", Evidence = new[] { evidence },
        };
        Assert.True(complete.IsComplete);
        Assert.False((complete with { Evidence = new[] { evidence with { Gaps = new[] { "unknown field" } } } }).IsComplete);
        Assert.False((complete with { Evidence = new[] { evidence with { Valid = false } } }).IsComplete);
        Assert.False((complete with { Gaps = new[] { "observation gap" } }).IsComplete);
    }

    [Fact]
    public void Oversized_file_is_rejected_before_reading_any_content_or_mtime()
    {
        var stream = new SyntheticStream(Limit + 1L);
        var metadataReads = 0;
        var source = new SystemConfigFileSource(_ => stream, _ => { metadataReads++; return ObservedAt; });
        var result = source.Read(SyntheticPath);
        AssertRejected(result, "file_too_large");
        Assert.Equal(0, stream.ReadCalls);
        Assert.Equal(0, metadataReads);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2097152)]
    public void Stable_files_up_to_and_including_the_limit_return_complete_content(int length)
    {
        var stream = new SyntheticStream(length);
        var source = new SystemConfigFileSource(_ => stream, _ => ObservedAt);
        var result = source.Read(SyntheticPath);
        Assert.True(result.Exists);
        Assert.Null(result.Error);
        Assert.NotNull(result.Content);
        Assert.Equal(length, result.Content.Length);
        Assert.Equal(ObservedAt, result.LastModifiedAt);
        Assert.Equal((long)length, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public void A_stale_small_length_cannot_bypass_the_limit_or_return_a_partial_prefix()
    {
        var stream = new SyntheticStream(Limit + 100L, _ => 1);
        var result = new SystemConfigFileSource(_ => stream, _ => ObservedAt).Read(SyntheticPath);
        AssertRejected(result, "file_too_large");
        Assert.Equal(Limit + 1L, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public void Growth_or_truncation_below_the_limit_is_not_returned_as_a_complete_file(long initialLength, long actualLength)
    {
        var stream = new SyntheticStream(actualLength, read => read == 1 ? initialLength : actualLength);
        var result = new SystemConfigFileSource(_ => stream, _ => ObservedAt).Read(SyntheticPath);
        AssertRejected(result, "file_changed_during_read");
    }

    [Fact]
    public void Growth_after_EOF_and_mtime_changes_also_discard_the_content()
    {
        var grown = new SyntheticStream(3, read => read == 1 ? 3 : Limit + 1L);
        AssertRejected(new SystemConfigFileSource(_ => grown, _ => ObservedAt).Read(SyntheticPath), "file_too_large");
        var changed = new SyntheticStream(3);
        var metadataReads = 0;
        AssertRejected(new SystemConfigFileSource(_ => changed,
            _ => ObservedAt.AddSeconds(metadataReads++)).Read(SyntheticPath), "file_changed_during_read");
    }

    [Fact]
    public void Path_replacement_cannot_reopen_the_file_between_size_content_and_metadata()
    {
        var original = new SyntheticStream(3);
        var replacement = new SyntheticStream(Limit + 1L);
        Stream currentPathTarget = original;
        var opens = 0;
        var source = new SystemConfigFileSource(_ => { opens++; return currentPathTarget; }, stream =>
        {
            Assert.Same(original, stream);
            currentPathTarget = replacement;
            return ObservedAt;
        });
        var result = source.Read(SyntheticPath);
        Assert.True(result.Exists);
        Assert.Equal(3, result.Content!.Length);
        Assert.Equal(1, opens);
        Assert.Equal(0, replacement.ReadCalls);
        replacement.Dispose();
    }

    [Fact]
    public void Read_errors_are_fixed_codes_without_content_prefix_or_exception_text()
    {
        var stream = new SyntheticStream(3) { ThrowOnRead = true };
        var result = new SystemConfigFileSource(_ => stream, _ => ObservedAt).Read(SyntheticPath);
        AssertRejected(result, "io_error");
        Assert.DoesNotContain(Marker, result.Error!, StringComparison.Ordinal);
        Assert.True(stream.Disposed);
        var interrupted = new SyntheticStream(10_000) { ThrowAfterBytes = 8192 };
        AssertRejected(new SystemConfigFileSource(_ => interrupted, _ => ObservedAt).Read(SyntheticPath), "io_error");
        Assert.Equal(8192L, interrupted.BytesRead);
        var inaccessible = new SystemConfigFileSource(_ => throw new UnauthorizedAccessException(Marker), _ => ObservedAt);
        AssertRejected(inaccessible.Read(SyntheticPath), "access_denied");
        var missing = new SystemConfigFileSource(_ => throw new FileNotFoundException(Marker), _ => ObservedAt).Read(SyntheticPath);
        Assert.False(missing.Exists);
        Assert.Null(missing.Content);
        Assert.Null(missing.Error);
    }

    [Fact]
    public void Unseekable_or_empty_paths_do_not_start_an_unbounded_read()
    {
        var stream = new SyntheticStream(3) { Seekable = false };
        var opens = 0;
        var source = new SystemConfigFileSource(_ => { opens++; return stream; }, _ => ObservedAt);
        AssertRejected(source.Read(" "), "empty_path");
        Assert.Equal(0, opens);
        AssertRejected(source.Read(SyntheticPath), "unsupported_file_stream");
        Assert.Equal(0, stream.ReadCalls);
    }

    private static Task<ClientConfigEvidence> ReadNice(string text) => new NiceAdapter(
        new SyntheticConfigFileSource().AddFile(SyntheticPath, Encoding.UTF8.GetBytes(text)), SyntheticPath)
        .ReadAsync(ObservedAt, CancellationToken.None);

    private static void AssertRejected(ConfigFileReadResult result, string reason)
    {
        Assert.False(result.Exists);
        Assert.Equal(reason, result.Error);
        Assert.Null(result.Content);
        Assert.Null(result.LastModifiedAt);
    }

    private sealed class SyntheticStream(long actualLength, Func<int, long>? reportedLength = null) : Stream
    {
        private long _position;
        private int _lengthReads;
        public int ReadCalls { get; private set; }
        public long BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnRead { get; init; }
        public long? ThrowAfterBytes { get; init; }
        public bool Seekable { get; init; } = true;
        public override bool CanRead => true;
        public override bool CanSeek => Seekable;
        public override bool CanWrite => false;
        public override long Length => reportedLength?.Invoke(++_lengthReads) ?? actualLength;
        public override long Position { get => _position; set => _position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            if (ThrowOnRead || ThrowAfterBytes is long threshold && BytesRead >= threshold) throw new IOException(Marker);
            var read = (int)Math.Min(count, Math.Max(0, actualLength - _position));
            Array.Fill(buffer, (byte)'A', offset, read);
            _position += read;
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
