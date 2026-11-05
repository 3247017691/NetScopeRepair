using System.IO.Compression;
using System.Text;
using NetScope.Storage.Export;

namespace NetScope.Export.Tests;

public sealed class PackageAdmissionTests
{
    [Fact]
    public void Default_limits_match_the_design_contract()
    {
        var limits = new PackageAdmissionLimits();

        Assert.Equal(1000, limits.MaxEntries);
        Assert.Equal(100L * 1024 * 1024, limits.MaxTotalUncompressedBytes);
        Assert.Equal(20L * 1024 * 1024, limits.MaxEntryUncompressedBytes);
        Assert.Equal(8, limits.MaxPathDepth);
    }

    [Fact]
    public async Task A_package_produced_by_the_exporter_is_admitted()
    {
        using var workspace = new TempWorkspace();
        var session = await ExportSession.CreateAsync(workspace.File("good.ndjson"));
        var result = await new IncidentExporter().ExportAsync(session,
            workspace.Subdirectory("packages"), CancellationToken.None);

        var admission = UntrustedPackageValidator.Validate(result.PackagePath);

        Assert.True(admission.IsAccepted, string.Join("; ", admission.Problems.Select(problem => problem.Code)));
        Assert.Equal(7, admission.EntryCount);
        Assert.True(admission.TotalUncompressedBytes > 0);
        Assert.True(admission.MaxObservedPathDepth >= 2);
    }

    [Theory]
    [InlineData("/etc/passwd", "absolute_path")]
    [InlineData(@"\Windows\evil.txt", "absolute_path")]
    [InlineData(@"\\server\share\evil.txt", "absolute_path")]
    [InlineData(@"C:\Windows\win.ini", "drive_letter_path")]
    [InlineData("C:/Windows/win.ini", "drive_letter_path")]
    [InlineData("../outside.txt", "parent_traversal")]
    [InlineData(@"..\outside.txt", "parent_traversal")]
    [InlineData("safe/../../outside.txt", "parent_traversal")]
    [InlineData("./relative.txt", "dot_segment")]
    [InlineData("safe/./relative.txt", "dot_segment")]
    public void Dangerous_entry_names_are_rejected(string name, string expectedCode)
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("evil.zip"),
            archive => ZipFactory.AddText(archive, name, "synthetic"));

        var admission = UntrustedPackageValidator.Validate(package);

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == expectedCode);
        Assert.Contains(admission.Problems, problem => problem.EntryName == name);
    }

    [Fact]
    public void Symbolic_link_entries_are_rejected_from_unix_external_attributes()
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("symlink.zip"), archive =>
        {
            var entry = archive.CreateEntry("innocent.txt");
            // Unix external attributes: S_IFLNK | 0777.
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var stream = entry.Open();
            stream.Write("target"u8);
        });

        var admission = UntrustedPackageValidator.Validate(package);

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "symbolic_link_entry");
    }

    [Fact]
    public void Duplicate_names_are_rejected_case_insensitively()
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("duplicate.zip"), archive =>
        {
            ZipFactory.AddText(archive, "manifest.json", "{}");
            ZipFactory.AddText(archive, "MANIFEST.JSON", "{}");
        });

        var admission = UntrustedPackageValidator.Validate(package);

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "duplicate_entry_name");
    }

    [Fact]
    public void Path_depth_is_capped_at_eight_segments()
    {
        using var workspace = new TempWorkspace();
        var accepted = ZipFactory.Create(workspace.File("depth-ok.zip"),
            archive => ZipFactory.AddText(archive, "a/b/c/d/e/f/g/h.txt", "ok"));
        var rejected = ZipFactory.Create(workspace.File("depth-bad.zip"),
            archive => ZipFactory.AddText(archive, "a/b/c/d/e/f/g/h/i.txt", "too deep"));

        Assert.True(UntrustedPackageValidator.Validate(accepted).IsAccepted);
        var admission = UntrustedPackageValidator.Validate(rejected);
        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "path_too_deep");
    }

    [Fact]
    public void Entry_count_is_bounded()
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("many.zip"), archive =>
        {
            for (var index = 0; index < 3; index++) ZipFactory.AddText(archive, $"file-{index}.txt", "synthetic");
        });

        var admission = UntrustedPackageValidator.Validate(package,
            new PackageAdmissionLimits { MaxEntries = 2 });

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "too_many_entries");
    }

    [Fact]
    public void One_entry_larger_than_the_limit_is_rejected()
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("large-entry.zip"), archive =>
        {
            ZipFactory.AddBytes(archive, "large.bin", new byte[256]);
        });

        var admission = UntrustedPackageValidator.Validate(package,
            new PackageAdmissionLimits { MaxEntryUncompressedBytes = 128, MaxTotalUncompressedBytes = 1024 });

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "entry_too_large");
    }

    [Fact]
    public void Total_uncompressed_size_is_bounded_across_entries()
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("large-total.zip"), archive =>
        {
            ZipFactory.AddBytes(archive, "first.bin", new byte[60]);
            ZipFactory.AddBytes(archive, "second.bin", new byte[60]);
        });

        var admission = UntrustedPackageValidator.Validate(package,
            new PackageAdmissionLimits { MaxEntryUncompressedBytes = 80, MaxTotalUncompressedBytes = 100 });

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "total_too_large");
    }

    [Fact]
    public void A_single_entry_over_the_default_twenty_megabyte_limit_is_rejected_without_extraction()
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("oversized.zip"), archive =>
        {
            var entry = archive.CreateEntry("oversized.bin", CompressionLevel.Fastest);
            using var stream = entry.Open();
            var block = new byte[1024 * 1024];
            for (var index = 0; index <= 20; index++) stream.Write(block, 0, block.Length);
        });

        var admission = UntrustedPackageValidator.Validate(package);

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "entry_too_large");
    }

    [Fact]
    public void Arbitrary_non_zip_bytes_are_rejected_as_invalid()
    {
        using var workspace = new TempWorkspace();
        var package = workspace.File("not-a-zip.bin");
        File.WriteAllText(package, "this is not a zip archive", Encoding.UTF8);

        var admission = UntrustedPackageValidator.Validate(package);

        Assert.False(admission.IsAccepted);
        Assert.Contains(admission.Problems, problem => problem.Code == "invalid_zip");
    }

    [Fact]
    public void A_non_seekable_stream_can_be_checked_without_extracting_files()
    {
        using var workspace = new TempWorkspace();
        var package = ZipFactory.Create(workspace.File("stream.zip"),
            archive => ZipFactory.AddText(archive, "manifest.json", "{}"));
        using var file = File.OpenRead(package);
        using var nonSeekable = new ForwardOnlyStream(file);

        var admission = UntrustedPackageValidator.Validate(nonSeekable);

        Assert.True(admission.IsAccepted);
        Assert.False(nonSeekable.CanSeek);
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly Stream _inner;

        public ForwardOnlyStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
