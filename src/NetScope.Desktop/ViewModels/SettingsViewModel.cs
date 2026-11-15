using System.Windows.Input;
using NetScope.Desktop.Services;

namespace NetScope.Desktop.ViewModels;

/// <summary>
/// Local preferences. Saving writes only the app's own settings file; it never applies a network
/// setting, starts a probe or changes a client.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private readonly Action<AppSettings> _applied;
    private string _intervalSeconds = "10";
    private string _targetId = "target-user-configured";
    private string _targetEndpoint = "";
    private string _expectedStatus = "204";
    private string _explicitProxy = "";
    private int _familyIndex;
    private bool? _hideOnClose;
    private bool _loading;

    public SettingsViewModel(SettingsStore store, AppSettings initial, Action<AppSettings> applied)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applied = applied ?? throw new ArgumentNullException(nameof(applied));
        Load(initial);
        SaveCommand = new AsyncRelayCommand(() =>
        {
            Save();
            return Task.CompletedTask;
        });
    }

    public ICommand SaveCommand { get; }

    public string IntervalSeconds
    {
        get => _intervalSeconds;
        set => Set(ref _intervalSeconds, value);
    }

    /// <summary>Editable target profile id. Saving it never contacts the endpoint.</summary>
    public string TargetId
    {
        get => _targetId;
        set => Set(ref _targetId, value);
    }

    public string TargetEndpoint
    {
        get => _targetEndpoint;
        set => Set(ref _targetEndpoint, value);
    }

    public string MethodText => "GET（固定；三路径共用同一方法）";

    public string TargetProfileCaption =>
        "目标档案只在本机保存：目标 ID、URL、期望状态码、地址族与显式代理。保存或打开设置都不会发起任何请求；"
        + "只有回到首页手动点击“立即诊断”才会按该档案发起外部验证。";

    public string ExpectedStatus
    {
        get => _expectedStatus;
        set => Set(ref _expectedStatus, value);
    }

    public string ExplicitProxy
    {
        get => _explicitProxy;
        set => Set(ref _explicitProxy, value);
    }

    public int FamilyIndex
    {
        get => _familyIndex;
        set
        {
            if (!Set(ref _familyIndex, value)) return;
            Raise(nameof(FamilyNotice));
        }
    }

    /// <summary>
    /// Only the no-explicit-proxy path can force and prove a family. The proxy and system paths
    /// are reported as an Unsupported/unknown gap with the T04-R2 reason codes, never as success.
    /// </summary>
    public string FamilyNotice => FamilyIndex switch
    {
        0 => "跟随系统：未指定地址族，三条路径都不产生族能力结论；只有“无显式代理”路径可以强制族，"
             + "代理/系统默认路径会以 Unsupported/未知缺口表达，且生产方不会确认三路径时间可比。",
        1 => "IPv4：无显式代理路径会强制 IPv4（真实 socket）；显式 HTTP 代理返回 Unsupported 缺口"
             + "（address_family_not_enforced_through_proxy）；系统默认路径记为未知"
             + "（address_family_unknown_for_system_default）。",
        _ => "IPv6：无显式代理路径会强制 IPv6（真实 socket）；显式 HTTP 代理返回 Unsupported 缺口"
             + "（address_family_not_enforced_through_proxy）；系统默认路径记为未知"
             + "（address_family_unknown_for_system_default）。",
    };

    /// <summary>
    /// 0 = 每次关闭窗口时询问 (null), 1 = 隐藏到托盘继续观察 (true), 2 = 直接退出并停止采集 (false)
    /// </summary>
    public int CloseBehaviorIndex
    {
        get => _hideOnClose switch
        {
            true => 1,
            false => 2,
            _ => 0,
        };
        set
        {
            bool? next = value switch
            {
                1 => true,
                2 => false,
                _ => null,
            };
            if (_hideOnClose == next) return;
            _hideOnClose = next;
            Raise();
            Raise(nameof(HideOnCloseText));
            if (!_loading)
            {
                SaveCloseBehaviorImmediate();
            }
        }
    }

    public string HideOnCloseText => _hideOnClose switch
    {
        true => "关闭窗口时隐藏到托盘",
        false => "关闭窗口时退出程序",
        _ => "关闭窗口时询问",
    };

    public void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            IntervalSeconds = ((int)settings.ObservationInterval.TotalSeconds).ToString();
            TargetId = string.IsNullOrWhiteSpace(settings.ProbeTargetId) ? "target-user-configured" : settings.ProbeTargetId;
            TargetEndpoint = settings.ProbeTargetEndpoint;
            ExpectedStatus = settings.ExpectedStatus.ToString();
            ExplicitProxy = settings.ExplicitProxyEndpoint ?? "";
            FamilyIndex = settings.AddressFamily switch
            {
                "ipv4" => 1,
                "ipv6" => 2,
                _ => 0,
            };
            _hideOnClose = settings.HideOnClose;
            Raise(nameof(CloseBehaviorIndex));
            Raise(nameof(HideOnCloseText));
            StatusMessage = null;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveCloseBehaviorImmediate()
    {
        try
        {
            var current = _store.Load();
            var updated = current with { HideOnClose = _hideOnClose };
            _store.Save(updated);
            _applied(updated);
            StatusMessage = "已更新关闭窗口行为：" + HideOnCloseText + "。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "关闭窗口行为保存失败：" + exception.GetType().Name + "。";
        }
    }

    public bool TryBuild(out AppSettings settings, out string? error)
    {
        settings = new AppSettings();
        error = null;
        if (!int.TryParse(IntervalSeconds, out var seconds) || seconds is < 1 or > 600)
        {
            error = "观察间隔需为 1—600 秒。";
            return false;
        }

        if (!int.TryParse(ExpectedStatus, out var status) || status is < 100 or > 599)
        {
            error = "期望状态码需为 100—599。";
            return false;
        }

        var target = TargetEndpoint.Trim();
        if (target.Length > 0)
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "探测目标必须是 http(s) URL，且不能包含用户名或密码。";
                return false;
            }
        }

        var targetId = TargetId.Trim();
        if (targetId.Length == 0)
        {
            error = "目标档案 ID 不能为空；它只用于本地区分观察结论。";
            return false;
        }

        if (targetId.Length > 80 || targetId.Any(char.IsControl))
        {
            error = "目标档案 ID 需为 1—80 个字符，且不能包含控制字符。";
            return false;
        }

        var proxy = ExplicitProxy.Trim();
        if (proxy.Length > 0
            && (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri)
                || proxyUri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(proxyUri.UserInfo)
                || !string.IsNullOrEmpty(proxyUri.AbsolutePath.Trim('/'))
                || !string.IsNullOrEmpty(proxyUri.Query)))
        {
            error = "显式代理需为 http(s) 的 host:port，不能含凭据、路径或查询。";
            return false;
        }

        settings = new AppSettings
        {
            ObservationInterval = TimeSpan.FromSeconds(seconds),
            ProbeTargetId = targetId,
            ProbeTargetEndpoint = target,
            ExpectedStatus = status,
            ExplicitProxyEndpoint = proxy.Length == 0 ? null : proxy,
            AddressFamily = FamilyIndex switch
            {
                1 => "ipv4",
                2 => "ipv6",
                _ => null,
            },
            HideOnClose = _hideOnClose,
        };
        return true;
    }

    public void Save()
    {
        if (!TryBuild(out var settings, out var error))
        {
            StatusMessage = error;
            return;
        }

        try
        {
            _store.Save(settings);
            _applied(settings);
            StatusMessage = "设置已保存；观察间隔将在下次开始观察时生效。保存设置没有发起任何请求，"
                + "也没有改动任何网络配置。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "设置保存失败：" + exception.GetType().Name + "。";
        }
    }
}
