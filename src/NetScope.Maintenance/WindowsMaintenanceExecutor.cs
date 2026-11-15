using System.Globalization;
using Microsoft.Win32;

namespace NetScope.Maintenance;

public interface IMaintenanceExecutor
{
    Task<ActionResult> ApplyAsync(RepairAction action, CancellationToken cancellationToken);

    Task<(bool Ok, string Message)> RestoreAsync(JournalEntry entry, CancellationToken cancellationToken);

    Task BroadcastEnvironmentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The only place in this assembly that writes to the machine. Each action is a fixed command;
/// values are passed through quoted literals, never concatenated into an executable string.
/// </summary>
public sealed class WindowsMaintenanceExecutor : IMaintenanceExecutor
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly WindowsCommandRunner _runner;

    public WindowsMaintenanceExecutor(WindowsCommandRunner? runner = null)
        => _runner = runner ?? new WindowsCommandRunner();

    public async Task<ActionResult> ApplyAsync(RepairAction action, CancellationToken cancellationToken)
    {
        try
        {
            return action.Kind switch
            {
                "ClearProxyEnv" or "AlignProxyEnv" => SetEnvironmentVariable(action),
                "DisableSystemProxy" => DisableSystemProxy(action),
                "SetSystemProxyServer" => SetSystemProxyValue(action, "ProxyServer", action.Desired ?? ""),
                "EnableSystemProxy" => SetSystemProxyValue(action, "ProxyEnable", "1"),
                "ResetWinHttpProxy" or "SetWinHttpProxy" => await SetWinHttpAsync(action, cancellationToken).ConfigureAwait(false),
                "RaiseInterfaceMetric" => await RaiseMetricAsync(action, cancellationToken).ConfigureAwait(false),
                "ResetInterfaceDns" => await ResetDnsAsync(action, cancellationToken).ConfigureAwait(false),
                "RemoveGhostDevice" => await RemoveDeviceAsync(action, cancellationToken).ConfigureAwait(false),
                "FlushDnsCache" => await FlushDnsAsync(action, cancellationToken).ConfigureAwait(false),
                _ => new ActionResult(action.Id, action.Title, false, action.Current, null, "未知动作类型 " + action.Kind),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return new ActionResult(action.Id, action.Title, false, action.Current, null,
                exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static ActionResult SetEnvironmentVariable(RepairAction action)
    {
        var scope = action.Parameters.TryGetValue("Scope", out var value) && value == "Machine"
            ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
        var name = action.Parameters["Name"];
        var before = Environment.GetEnvironmentVariable(name, scope);
        var after = action.Parameters.TryGetValue("Value", out var desired) && desired.Length > 0 ? desired : null;
        Environment.SetEnvironmentVariable(name, after, scope);
        return new ActionResult(action.Id, action.Title, true, before, after, null);
    }

    private static ActionResult DisableSystemProxy(RepairAction action)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new IOException("HKCU Internet Settings 不可写");
        var before = Convert.ToInt32(key.GetValue("ProxyEnable", 0), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        return new ActionResult(action.Id, action.Title, true, before, "0", null);
    }

    private static ActionResult SetSystemProxyValue(RepairAction action, string name, string desired)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new IOException("HKCU Internet Settings 不可写");
        var before = key.GetValue(name, "")?.ToString() ?? "";
        if (name == "ProxyEnable")
            key.SetValue(name, int.Parse(desired, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else
            key.SetValue(name, desired, RegistryValueKind.String);
        return new ActionResult(action.Id, action.Title, true, before, desired, null);
    }

    private async Task<ActionResult> SetWinHttpAsync(RepairAction action, CancellationToken cancellationToken)
    {
        var before = action.Current ?? "";
        var desired = action.Parameters.TryGetValue("Port", out var portText) ? portText : "0";
        var reset = action.Kind == "ResetWinHttpProxy" || desired == "0";
        var result = reset
            ? await _runner.RunAsync("netsh", new[] { "winhttp", "reset", "proxy" }, cancellationToken).ConfigureAwait(false)
            : await _runner.RunAsync("netsh", new[] { "winhttp", "set", "proxy", "127.0.0.1:" + desired }, cancellationToken).ConfigureAwait(false);
        return result.Ok
            ? new ActionResult(action.Id, action.Title, true, before, reset ? "direct" : "127.0.0.1:" + desired, null)
            : new ActionResult(action.Id, action.Title, false, before, null,
                "netsh 失败（可能需要管理员）：" + result.StandardError);
    }

    private async Task<ActionResult> RaiseMetricAsync(RepairAction action, CancellationToken cancellationToken)
    {
        var index = int.Parse(action.Parameters["Index"], CultureInfo.InvariantCulture);
        var script = "Set-NetIPInterface -InterfaceIndex " + index.ToString(CultureInfo.InvariantCulture)
            + " -AddressFamily IPv4 -InterfaceMetric " + MaintenancePlanner.HighInterfaceMetric.ToString(CultureInfo.InvariantCulture)
            + " -AutomaticMetric Disabled -ErrorAction Stop; 'OK'";
        var result = await _runner.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return result.Ok && result.StandardOutput.Contains("OK", StringComparison.Ordinal)
            ? new ActionResult(action.Id, action.Title, true, action.Current,
                MaintenancePlanner.HighInterfaceMetric.ToString(CultureInfo.InvariantCulture) + "/Disabled", null)
            : new ActionResult(action.Id, action.Title, false, action.Current, null,
                "Set-NetIPInterface 失败（通常需要管理员）：" + result.StandardError);
    }

    private async Task<ActionResult> ResetDnsAsync(RepairAction action, CancellationToken cancellationToken)
    {
        var index = int.Parse(action.Parameters["Index"], CultureInfo.InvariantCulture);
        var script = "Set-DnsClientServerAddress -InterfaceIndex " + index.ToString(CultureInfo.InvariantCulture)
            + " -ResetServerAddresses -ErrorAction Stop; 'OK'";
        var result = await _runner.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return result.Ok && result.StandardOutput.Contains("OK", StringComparison.Ordinal)
            ? new ActionResult(action.Id, action.Title, true, action.Parameters["PreDns"], "auto", null)
            : new ActionResult(action.Id, action.Title, false, action.Parameters["PreDns"], null,
                "Set-DnsClientServerAddress 失败：" + result.StandardError);
    }

    private async Task<ActionResult> RemoveDeviceAsync(RepairAction action, CancellationToken cancellationToken)
    {
        var id = action.Parameters["InstanceId"];
        var result = await _runner.RunAsync("pnputil", new[] { "/remove-device", id }, cancellationToken)
            .ConfigureAwait(false);
        return result.Ok
            ? new ActionResult(action.Id, action.Title, true, id, "removed", null)
            : new ActionResult(action.Id, action.Title, false, id, null, "pnputil 失败：" + result.StandardError);
    }

    private async Task<ActionResult> FlushDnsAsync(RepairAction action, CancellationToken cancellationToken)
    {
        var cleared = await _runner.RunPowerShellAsync(
            "try { Clear-DnsClientCache -ErrorAction Stop } catch {}; 'OK'", cancellationToken).ConfigureAwait(false);
        await _runner.RunAsync("ipconfig", new[] { "/flushdns" }, cancellationToken).ConfigureAwait(false);
        return new ActionResult(action.Id, action.Title, cleared.Ok, "", "flushed",
            cleared.Ok ? null : "Clear-DnsClientCache 不可用，已尝试 ipconfig /flushdns");
    }

    public async Task<(bool Ok, string Message)> RestoreAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            switch (entry.Kind)
            {
                case "ClearProxyEnv":
                case "AlignProxyEnv":
                {
                    var scope = entry.Scope == "Machine" ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
                    Environment.SetEnvironmentVariable(entry.Name,
                        string.IsNullOrEmpty(entry.Old) ? null : entry.Old, scope);
                    return (true, "已还原 " + entry.Scope + "\\" + entry.Name);
                }
                case "DisableSystemProxy":
                case "EnableSystemProxy":
                case "SetSystemProxyServer":
                {
                    using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
                        ?? throw new IOException("HKCU Internet Settings 不可写");
                    var name = entry.Kind == "SetSystemProxyServer" ? "ProxyServer" : "ProxyEnable";
                    if (name == "ProxyEnable")
                        key.SetValue(name, int.TryParse(entry.Old, out var old) ? old : 0, RegistryValueKind.DWord);
                    else
                        key.SetValue(name, entry.Old ?? "", RegistryValueKind.String);
                    return (true, "已还原 " + name + "=" + entry.Old);
                }
                case "RaiseInterfaceMetric":
                {
                    var parts = (entry.Old ?? "").Split('/');
                    var index = int.Parse(entry.Extra ?? entry.Name, CultureInfo.InvariantCulture);
                    var metric = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
                    var auto = parts.Length > 1 ? parts[1] : "Enabled";
                    var script = auto == "Enabled"
                        ? "Set-NetIPInterface -InterfaceIndex " + index + " -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop; 'OK'"
                        : "Set-NetIPInterface -InterfaceIndex " + index + " -AddressFamily IPv4 -InterfaceMetric " + metric
                            + " -AutomaticMetric Disabled -ErrorAction Stop; 'OK'";
                    var result = await _runner.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
                    return result.Ok && result.StandardOutput.Contains("OK", StringComparison.Ordinal)
                        ? (true, "已还原 if=" + index + " 接口度量")
                        : (false, "还原接口度量失败：" + result.StandardError);
                }
                case "ResetInterfaceDns":
                {
                    var index = int.Parse(entry.Extra ?? entry.Name, CultureInfo.InvariantCulture);
                    var servers = (entry.Old ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (servers.Length == 0) return (true, "无静态 DNS 需要还原");
                    var list = string.Join(",", servers.Select(value => WindowsCommandRunner.PsQuote(value.Trim())));
                    var script = "Set-DnsClientServerAddress -InterfaceIndex " + index
                        + " -ServerAddresses @(" + list + ") -ErrorAction Stop; 'OK'";
                    var result = await _runner.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
                    return result.Ok ? (true, "已还原 if=" + index + " DNS") : (false, "还原 DNS 失败：" + result.StandardError);
                }
                case "ResetWinHttpProxy":
                case "SetWinHttpProxy":
                {
                    var reset = string.IsNullOrWhiteSpace(entry.Old) || entry.Old == "direct";
                    var arguments = reset
                        ? new[] { "winhttp", "reset", "proxy" }
                        : new[] { "winhttp", "set", "proxy", entry.Old!.Replace("http://", "", StringComparison.Ordinal) };
                    var result = await _runner.RunAsync("netsh", arguments, cancellationToken).ConfigureAwait(false);
                    return result.Ok ? (true, "已还原 WinHTTP") : (false, "还原 WinHTTP 失败：" + result.StandardError);
                }
                case "RemoveGhostDevice":
                    return (true, "设备 " + entry.Name + " 曾被移除；插回设备或重启会自动重新枚举（不自动恢复驱动）");
                case "FlushDnsCache":
                    return (true, "缓存刷新无需还原");
                default:
                    return (false, "未知回滚类型 " + entry.Kind);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or FormatException)
        {
            return (false, "还原失败：" + exception.Message);
        }
    }

    public async Task BroadcastEnvironmentAsync(CancellationToken cancellationToken)
    {
        const string script = @"
$sig = '[DllImport(""user32.dll"", SetLastError = true, CharSet = CharSet.Auto)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);'
try { $t = Add-Type -MemberDefinition $sig -Name 'Win32EnvMaintenance' -Namespace 'NetScope' -PassThru -ErrorAction Stop; $r = [UIntPtr]::Zero; [void]$t::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 3000, [ref]$r); 'OK' } catch { 'SKIP' }
";
        await _runner.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
    }
}
