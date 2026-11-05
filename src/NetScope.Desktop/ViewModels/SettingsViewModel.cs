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

    public string TargetEndpoint
    {
        get => _targetEndpoint;
        set => Set(ref _targetEndpoint, value);
    }

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

    public string FamilyNotice => FamilyIndex == 0
        ? "跟随系统默认栈；不强制地址族。"
        : "M1 的真实传输不强制地址族：选择后会诚实返回“不支持”，不会伪造 IPv4/IPv6 结果。";

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
            StatusMessage = "设置已保存；观察间隔将在下次开始观察时生效。没有改动任何网络配置。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "设置保存失败：" + exception.GetType().Name + "。";
        }
    }
}
