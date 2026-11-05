using System.Runtime.InteropServices;
using NetScope.Collectors.Windows;
using NetScope.Domain;

namespace NetScope.Windows.Tests;

public sealed class NativeNetworkLayoutTests
{
    [Fact]
    public void Interface_binding_matches_the_verified_Windows_x64_SDK_layout()
    {
        Assert.Equal(168, Marshal.SizeOf<NativeNetworkApi.IpInterfaceRow>());
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.InterfaceLuid), 8);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.InterfaceIndex), 16);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.UseAutomaticMetric), 44);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.ZoneIndices), 80);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.Metric), 148);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.ReachableTime), 160);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.TransmitOffload), 164);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.ReceiveOffload), 165);
        AssertOffset(nameof(NativeNetworkApi.IpInterfaceRow.DisableDefaultRoutes), 166);
    }

    [Theory]
    [InlineData(false, true, true, 0x5A)]
    [InlineData(true, false, false, 0)]
    public void Synthetic_rows_read_the_real_flag_independently_of_offload_and_out_of_row_bytes(
        bool disableDefaultRoutes, bool automaticMetric, bool offload, byte oldOffsetSentinel)
    {
        // Build an independent SDK byte fixture, not a managed row with the same field assignments
        // as the implementation. The extra tail reproduces the old, invalid read at offset 172.
        var bytes = new byte[176];
        BitConverter.GetBytes((ushort)2).CopyTo(bytes, 0);
        BitConverter.GetBytes(0x0102030405060708UL).CopyTo(bytes, 8);
        BitConverter.GetBytes(12U).CopyTo(bytes, 16);
        bytes[44] = automaticMetric ? (byte)1 : (byte)0;
        BitConverter.GetBytes(37U).CopyTo(bytes, 148);
        bytes[164] = bytes[165] = offload ? (byte)0xFF : (byte)0;
        bytes[166] = disableDefaultRoutes ? (byte)1 : (byte)0;
        bytes[172] = oldOffsetSentinel;

        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var row = Marshal.PtrToStructure<NativeNetworkApi.IpInterfaceRow>(memory);
            var result = NativeNetworkApi.MetricsFromRow(row);

            Assert.Equal(disableDefaultRoutes, result.DisableDefaultRoutes);
            Assert.Equal(automaticMetric, result.AutomaticMetric);
            Assert.Equal(37U, result.Metric);
            Assert.Equal(0x0102030405060708UL, result.Luid);
            Assert.Equal(oldOffsetSentinel, Marshal.ReadByte(memory, 172));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Arm64)]
    public void Unverified_architectures_report_unsupported_without_calling_native_code(Architecture architecture)
    {
        var native = new NativeNetworkApi(architecture);

        Assert.False(NativeNetworkApi.HasVerifiedLayout(architecture));
        var metrics = native.ReadInterfaceMetrics(12, NativeNetworkApi.AfInet);
        var routes = native.ReadRoutes(NativeNetworkApi.AfUnspec);
        Assert.Equal(EventStatus.Unsupported, metrics.Status);
        Assert.Equal(EventStatus.Unsupported, routes.Status);
        Assert.Null(metrics.Value);
        Assert.Null(routes.Value);
        Assert.NotNull(metrics.Reason);
        Assert.NotNull(routes.Reason);
    }

    private static void AssertOffset(string field, int expected) =>
        Assert.Equal(expected, Marshal.OffsetOf<NativeNetworkApi.IpInterfaceRow>(field).ToInt32());
}
