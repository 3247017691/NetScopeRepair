using System.Text.Json;
using NetScope.Collectors.Windows;

namespace NetScope.Windows.Tests;

public sealed class SecretRedactionTests
{
    [Theory]
    [InlineData("http://user:synthetic-password@127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("socks5://name:synthetic-password@10.0.0.1:1080", "socks5://10.0.0.1:1080")]
    [InlineData("http://127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("user:synthetic-password@proxy.example:8080", "proxy.example:8080")]
    [InlineData("http://%75ser:%70assword@proxy.example:8080/a%2Fb?token=synthetic-query#synthetic-fragment", "http://proxy.example:8080")]
    [InlineData("https://proxy.example/secret-path?access_token=synthetic-query#synthetic-fragment", "https://proxy.example")]
    [InlineData("http://[::1]:7890/secret-path?token=synthetic-query", "http://[::1]:7890")]
    [InlineData("http://proxy.example/path-secret;semicolon-secret", "http://proxy.example")]
    [InlineData("", "")]
    public void Proxy_output_is_limited_to_scheme_host_and_port(string input, string expected)
    {
        Assert.Equal(expected, SecretRedaction.RedactUrl(input));
        Assert.False(SecretRedaction.LooksUnredacted(expected));
    }

    [Fact]
    public void Every_protocol_map_entry_is_sanitized_independently()
    {
        const string raw = "http=http://alice:SYNTHETIC_PASSWORD_A@proxy-a.example:8080;"
            + "https=http://bob:SYNTHETIC_PASSWORD_B@proxy-b.example:8443;"
            + "socks=name:SYNTHETIC_PASSWORD_C@127.0.0.1:1080";

        var result = SecretRedaction.RedactUrl(raw);

        Assert.Equal("http=http://proxy-a.example:8080;https=http://proxy-b.example:8443;socks=127.0.0.1:1080", result);
        Assert.DoesNotContain("SYNTHETIC_", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", result, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", result, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_map_fragment_cannot_be_reinterpreted_as_a_hostname_and_leak_a_path_secret()
    {
        const string raw = "http=http://proxy.example/private;SYNTHETIC_PATH_SECRET;https=http://other.example/?token=SYNTHETIC_QUERY";

        Assert.Equal(SecretRedaction.Replacement, SecretRedaction.RedactUrl(raw));
    }

    [Theory]
    [InlineData("http=http://proxy.example/path?token=private;https=SecretThatLooksLikeAHost")]
    [InlineData("http=http://proxy.example/private;https=SecretThatLooksLikeAHost")]
    [InlineData("http=http://proxy.example/#private;https=SecretThatLooksLikeAHost")]
    [InlineData("http=http://proxy.example/%2Fprivate;https=SecretThatLooksLikeAHost")]
    public void Ambiguous_URL_components_in_a_map_fail_closed_before_reinterpreting_secret_text(string raw)
    {
        Assert.Equal(SecretRedaction.Replacement, SecretRedaction.RedactUrl(raw));
    }

    [Theory]
    [InlineData("http://user:SYNTHETIC_PASSWORD@")]
    [InlineData("http://proxy.example:invalid/SYNTHETIC_PATH")]
    [InlineData("http://[::1/SYNTHETIC_PATH")]
    [InlineData("http%3A%2F%2Fuser%3ASYNTHETIC_PASSWORD%40proxy.example")]
    [InlineData("http://%75ser%3ASYNTHETIC_PASSWORD%40proxy.example")]
    [InlineData("unknown://user:SYNTHETIC_PASSWORD@proxy.example/private")]
    [InlineData("http://proxy.example\\SYNTHETIC_PATH")]
    [InlineData("http://proxy.example/\r\nSYNTHETIC_INJECTION")]
    [InlineData("opaque SYNTHETIC_SECRET")]
    public void Malformed_ambiguous_or_unsupported_values_fail_closed(string raw)
    {
        Assert.Equal(SecretRedaction.Replacement, SecretRedaction.RedactUrl(raw));
    }

    [Theory]
    [InlineData("https://user:SYNTHETIC_PASSWORD@pac.example:8443/private/SYNTHETIC_PATH.pac?token=SYNTHETIC_QUERY#SYNTHETIC_FRAGMENT", "https://pac.example:8443")]
    [InlineData("http://pac.example/%53%59%4e%54%48%45%54%49%43?secret=%41%42%43", "http://pac.example")]
    [InlineData("file:///C:/private/SYNTHETIC_PATH.pac", "***")]
    [InlineData("data:text/plain,SYNTHETIC_SECRET", "***")]
    [InlineData("pac.example/SYNTHETIC_PATH", "***")]
    [InlineData("http://[invalid/SYNTHETIC_PATH", "***")]
    public void PAC_output_preserves_only_a_verified_HTTP_origin(string raw, string expected)
    {
        Assert.Equal(expected, SecretRedaction.RedactPacUrl(raw));
    }

    [Fact]
    public void Public_WinINET_layer_contains_neither_map_credentials_nor_PAC_secrets()
    {
        var layer = WindowsProxySource.CreateWinInetLayer(1,
            "http=user:SYNTHETIC_PASSWORD_A@proxy-a.example:80;https=user:SYNTHETIC_PASSWORD_B@proxy-b.example:8080",
            "https://pac.example/SYNTHETIC_PATH?token=SYNTHETIC_QUERY#SYNTHETIC_FRAGMENT", 1);

        var serialized = JsonSerializer.Serialize(layer);

        Assert.True(layer.Enabled);
        Assert.True(layer.AutoDetect);
        Assert.Equal("http=proxy-a.example;https=proxy-b.example:8080", layer.ProxyServer);
        Assert.Equal("https://pac.example", layer.AutoConfigUrl);
        Assert.DoesNotContain("SYNTHETIC_", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Environment_collection_reads_only_allowed_names_and_never_returns_URL_secrets()
    {
        var requested = new List<string>();
        var variables = WindowsProxySource.ReadProxyVariables(name =>
        {
            requested.Add(name);
            return name switch
            {
                "HTTPS_PROXY" => "http://user:SYNTHETIC_PASSWORD@127.0.0.1:7890/SYNTHETIC_PATH?token=SYNTHETIC_QUERY#SYNTHETIC_FRAGMENT",
                "ALL_PROXY" => "socks5://user:%53%59%4e%54%48%45%54%49%43@127.0.0.1:1080/private",
                _ => null,
            };
        });

        Assert.Contains("HTTPS_PROXY=http://127.0.0.1:7890", variables);
        Assert.Contains("ALL_PROXY=socks5://127.0.0.1:1080", variables);
        var serialized = JsonSerializer.Serialize(variables);
        Assert.DoesNotContain("SYNTHETIC_", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("%53%59", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", serialized, StringComparison.Ordinal);
        Assert.All(requested, name => Assert.Contains(name.ToUpperInvariant(),
            new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }));
    }

    [Theory]
    [InlineData("localhost,127.0.0.1,::1,.example.com,*.corp.example,10.0.0.0/8,2001:db8::/32,[::1]:8080,example.com:443,*")]
    [InlineData(" .example.com , localhost,127.0.0.0/8, <local>,")]
    public void Valid_NO_PROXY_bypass_semantics_and_formatting_are_preserved(string bypass)
    {
        var variables = WindowsProxySource.ReadProxyVariables(name => name == "NO_PROXY" ? bypass : null);

        Assert.Equal("NO_PROXY=" + bypass, Assert.Single(variables));
        Assert.Equal(bypass, SecretRedaction.RedactBypassList(bypass));
    }

    [Fact]
    public void Malformed_NO_PROXY_URL_entries_do_not_leak_secrets_or_destroy_other_bypass_entries()
    {
        const string raw = ".corp.example,http://user:SYNTHETIC_PASSWORD@host/private?token=SYNTHETIC_QUERY,10.0.0.0/8";

        Assert.Equal(".corp.example,***,10.0.0.0/8", SecretRedaction.RedactBypassList(raw));
    }

    [Fact]
    public void No_variables_are_reported_when_none_are_set()
    {
        Assert.Empty(WindowsProxySource.ReadProxyVariables(_ => null));
        Assert.Empty(WindowsProxySource.ReadProxyVariables(_ => "   "));
    }
}
