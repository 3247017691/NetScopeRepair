using NetScope.Application;

namespace NetScope.Storage.Export;

/// <summary>The stable identifiers used by the preview and the redaction report.</summary>
internal static class RedactionCategories
{
    public const string UserName = "user_name";
    public const string DeviceName = "device_name";
    public const string LocalPath = "local_path";
    public const string PrivateHost = "private_host";
    public const string PublicTarget = "public_target";
    public const string FakeIp = "fake_ip";
    public const string PrivateIp = "private_ip";
    public const string PublicIp = "public_ip";
    public const string Loopback = "loopback";
    public const string LinkLocal = "link_local";
    public const string Multicast = "multicast";
    public const string UnspecifiedAddress = "unspecified_address";
    public const string ProxyCredential = "proxy_credential";
    public const string Authorization = "authorization";
    public const string Cookie = "cookie";
    public const string Token = "token";
    public const string Password = "password";
    public const string SubscriptionUrl = "subscription_url";
    public const string QuerySecret = "query_secret";
    public const string UrlPath = "url_path";
    public const string UnparseableUrl = "unparseable_url";

    public static string Strategy(string category) => category switch
    {
        UserName => "stable per-package alias (user-XX, salt-keyed)",
        DeviceName => "stable per-package alias (device-XX, salt-keyed)",
        LocalPath => "stable per-package alias (path-XX, salt-keyed)",
        PrivateHost => "stable per-package alias (private-host-XX, salt-keyed)",
        PublicTarget => "stable per-package alias (target-A, target-B, ...)",
        FakeIp => "class-preserving address alias (198.18.0.0/15)",
        PrivateIp => "class-preserving address alias (private ranges)",
        PublicIp => "class-preserving address alias (public ranges)",
        Loopback => "class-preserving address alias (loopback)",
        LinkLocal => "class-preserving address alias (link-local)",
        Multicast => "class-preserving address alias (multicast)",
        UnspecifiedAddress => "class-preserving address alias (unspecified)",
        ProxyCredential => "credential userinfo removed",
        Authorization => "header value replaced",
        Cookie => "header value replaced",
        Token => "secret value replaced",
        Password => "secret value replaced",
        SubscriptionUrl => "subscription URL replaced",
        QuerySecret => "query value or whole query removed",
        UrlPath => "business URL path removed",
        UnparseableUrl => "whole unparseable URL replaced",
        _ => "replaced",
    };
}

/// <summary>
/// Counts replaced values by category. Only category names and counts leave the anonymizer;
/// original values and the alias map never do.
/// </summary>
internal sealed class RedactionLedger
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Record(string category)
    {
        if (string.IsNullOrEmpty(category)) return;
        _counts[category] = _counts.GetValueOrDefault(category) + 1;
    }

    public IReadOnlyList<ExportRedactionSummary> Snapshot() => _counts
        .OrderBy(item => item.Key, StringComparer.Ordinal)
        .Select(item => new ExportRedactionSummary
        {
            Category = item.Key,
            ReplacedFields = item.Value,
            Strategy = RedactionCategories.Strategy(item.Key),
        })
        .ToArray();

    public IReadOnlyList<string> Categories() => _counts.Keys.Order(StringComparer.Ordinal).ToArray();
}
