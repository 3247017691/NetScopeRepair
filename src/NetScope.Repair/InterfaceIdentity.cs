using System.Text.Json.Serialization;
using NetScope.Domain;

namespace NetScope.Repair;

/// <summary>
/// What an interface is. Only <see cref="Tun"/> is ever eligible for the three V1 actions, and
/// even a TUN interface still needs a trusted core-to-TUN attribution before a plan may exist.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<InterfaceKind>))]
public enum InterfaceKind
{
    [JsonStringEnumMemberName("tun")]
    Tun,

    [JsonStringEnumMemberName("ethernet")]
    Ethernet,

    [JsonStringEnumMemberName("wifi")]
    Wifi,

    [JsonStringEnumMemberName("rndis")]
    Rndis,

    [JsonStringEnumMemberName("pppoe")]
    Pppoe,

    [JsonStringEnumMemberName("loopback")]
    Loopback,

    [JsonStringEnumMemberName("virtual")]
    Virtual,

    [JsonStringEnumMemberName("other")]
    Other,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

public static class InterfaceKinds
{
    /// <summary>Real or shared uplinks that must never be written, even if misidentified as TUN.</summary>
    public static bool IsProtectedUplinkKind(InterfaceKind kind) => kind
        is InterfaceKind.Ethernet
        or InterfaceKind.Wifi
        or InterfaceKind.Rndis
        or InterfaceKind.Pppoe;
}

/// <summary>
/// Stable identity for one interface/family. GUID is the persistent identity, LUID locates the
/// object during this boot, and ifIndex is only a value freshly resolved during revalidation.
/// Name and adapter description are display text and never part of the key.
/// </summary>
public sealed record InterfaceIdentity
{
    public required Guid InterfaceGuid { get; init; }

    public required ulong? Luid { get; init; }

    public required int? IfIndex { get; init; }

    public required RepairAddressFamily Family { get; init; }

    /// <summary>Network compartment (routing context); unknown is not the same as compartment 0.</summary>
    public required int? CompartmentId { get; init; }

    /// <summary>PnP device instance, used to detect a re-created adapter with a recycled GUID.</summary>
    public required string? DeviceInstanceId { get; init; }

    public required InterfaceKind Kind { get; init; }

    /// <summary>Display only. A name token is never an attribution proof.</summary>
    public string? Name { get; init; }

    /// <summary>Display only, for example "Meta Tunnel".</summary>
    public string? AdapterDescription { get; init; }

    public string? DriverKey { get; init; }

    public string? DriverVersion { get; init; }

    public required string BootId { get; init; }

    /// <summary>Identity fields without which a write must not even be planned.</summary>
    public IReadOnlyList<string> MissingCriticalFields
    {
        get
        {
            var missing = new List<string>();
            if (Luid is null) missing.Add("luid");
            if (IfIndex is null) missing.Add("if_index");
            if (Family == RepairAddressFamily.Unknown) missing.Add("family");
            if (CompartmentId is null) missing.Add("compartment_id");
            if (string.IsNullOrWhiteSpace(DeviceInstanceId)) missing.Add("device_instance_id");
            if (Kind == InterfaceKind.Unknown) missing.Add("kind");
            return missing;
        }
    }

    public bool IsComplete => MissingCriticalFields.Count == 0;

    /// <summary>Persistent target key: GUID + family + compartment. Excludes ifIndex and LUID.</summary>
    public string TargetFingerprint =>
        InterfaceGuid.ToString("N") + "|" + WireNames.Of(Family) + "|" + (CompartmentId?.ToString() ?? "unknown");

    /// <summary>Current-boot locator used for revalidation, not for persistence.</summary>
    public string CurrentBootLocator =>
        TargetFingerprint + "|" + WireNames.Of(Kind) + "|" + (Luid?.ToString("x16") ?? "unknown")
        + "|" + (IfIndex?.ToString() ?? "unknown") + "|" + (DeviceInstanceId ?? "unknown") + "|" + BootId;
}

/// <summary>Which observations may establish "this TUN belongs to this client core".</summary>
[JsonConverter(typeof(WireEnumJsonConverter<TunAttributionBasis>))]
public enum TunAttributionBasis
{
    /// <summary>A trusted client runtime API reported its own TUN adapter.</summary>
    [JsonStringEnumMemberName("client_runtime_api")]
    ClientRuntimeApi,

    /// <summary>A trusted platform source proved the device owner/session.</summary>
    [JsonStringEnumMemberName("driver_device_owner")]
    DriverDeviceOwner,

    /// <summary>The platform owns the interface in a session we can verify.</summary>
    [JsonStringEnumMemberName("platform_owned_session")]
    PlatformOwnedSession,

    /// <summary>Untrusted: the interface name merely contains a client token.</summary>
    [JsonStringEnumMemberName("name_token")]
    NameToken,

    /// <summary>Untrusted: an address in the fake-ip range only says the adapter is a TUN candidate.</summary>
    [JsonStringEnumMemberName("fake_ip_address_range")]
    FakeIpAddressRange,

    /// <summary>Untrusted: a configuration file says tun=true.</summary>
    [JsonStringEnumMemberName("disk_config_enabled")]
    DiskConfigEnabled,

    /// <summary>Untrusted: the user clicked a row in the UI.</summary>
    [JsonStringEnumMemberName("user_selection")]
    UserSelection,

    /// <summary>Untrusted: the adapter description looked like a tunnel.</summary>
    [JsonStringEnumMemberName("adapter_description")]
    AdapterDescription,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(WireEnumJsonConverter<TunAttributionVerdict>))]
public enum TunAttributionVerdict
{
    [JsonStringEnumMemberName("verified")]
    Verified,

    [JsonStringEnumMemberName("candidate")]
    Candidate,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>
/// One attribution statement and where it came from. A <see cref="TunAttributionVerdict.Verified"/>
/// claim is only usable when the basis is trusted, a source id exists and evidence is cited.
/// </summary>
public sealed record TunAttribution
{
    public required TunAttributionVerdict Verdict { get; init; }

    public required TunAttributionBasis Basis { get; init; }

    public string? SourceId { get; init; }

    public string? ClientId { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();
}

public static class TunAttributionPolicy
{
    public static bool IsTrustedBasis(TunAttributionBasis basis) => basis
        is TunAttributionBasis.ClientRuntimeApi
        or TunAttributionBasis.DriverDeviceOwner
        or TunAttributionBasis.PlatformOwnedSession;

    /// <summary>
    /// Downgrades an over-claimed verification. Name token, 198.18/15 address, a disk
    /// <c>tun=true</c> value and a user click are all recorded as unknown, never as proof.
    /// </summary>
    public static TunAttribution Normalize(TunAttribution attribution)
    {
        if (attribution.Verdict != TunAttributionVerdict.Verified)
        {
            return attribution;
        }

        if (!IsTrustedBasis(attribution.Basis))
        {
            return attribution with
            {
                Verdict = TunAttributionVerdict.Unknown,
                Reason = "归属依据不可信（" + BasisText(attribution.Basis) + "）：名称、地址、磁盘配置或用户点选不能证明核心→TUN 归属。",
            };
        }

        if (string.IsNullOrWhiteSpace(attribution.SourceId) || attribution.EvidenceIds.Count == 0)
        {
            return attribution with
            {
                Verdict = TunAttributionVerdict.Unknown,
                Reason = "归属声称 verified，但缺少来源 ID 或证据引用。",
            };
        }

        return attribution;
    }

    public static string BasisText(TunAttributionBasis basis) => basis switch
    {
        TunAttributionBasis.ClientRuntimeApi => "客户端运行时 API",
        TunAttributionBasis.DriverDeviceOwner => "驱动/设备所有者",
        TunAttributionBasis.PlatformOwnedSession => "平台会话所有权",
        TunAttributionBasis.NameToken => "接口名称包含客户端 token",
        TunAttributionBasis.FakeIpAddressRange => "198.18/15 fake-ip 地址",
        TunAttributionBasis.DiskConfigEnabled => "磁盘配置 tun=true",
        TunAttributionBasis.UserSelection => "用户点选",
        TunAttributionBasis.AdapterDescription => "适配器描述",
        _ => "未知来源",
    };
}

/// <summary>
/// The protected uplink and default-route context. Unknown protected uplink or an unknown
/// default-route owner blocks a plan because the target could be the real exit.
/// </summary>
public sealed record UplinkAssessment
{
    public required bool ProtectedUplinkKnown { get; init; }

    public Guid? ProtectedInterfaceGuid { get; init; }

    public InterfaceKind? ProtectedKind { get; init; }

    public bool? TargetOwnsDefaultRoute { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();
}
