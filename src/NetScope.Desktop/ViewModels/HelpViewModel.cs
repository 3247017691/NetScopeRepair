using System.Collections.ObjectModel;

namespace NetScope.Desktop.ViewModels;

public sealed record HelpSection(string Title, string Body);

/// <summary>Offline help: the same text works with no network, no API key and no repair capability.</summary>
public sealed class HelpViewModel : ViewModelBase
{
    public HelpViewModel(string sessionPath, string dataRoot)
    {
        SessionPath = sessionPath;
        DataRoot = dataRoot;
        Sections = new ObservableCollection<HelpSection>
        {
            new("M1 能做什么",
                "只读采集本机网卡、路由、DNS 与代理分层配置；手动运行一组明确目标的探测；保存证据、时间线与诊断；"
                + "导出脱敏 ZIP 交给你自己分析。所有结论都会标出证据等级、引用 ID、反证与缺口。"),
            new("M1 不做什么",
                "不修改路由、度量、DNS、代理环境变量、TUN 或客户端配置；不停止/切换/重启任何客户端；"
                + "不注册服务、计划任务或开机启动；不上传日志、不调用 DeepSeek、不需要 API Key。"
                + "界面里没有“执行修复”入口，修复能力属于 M3，且必须先有证据和用户授权。"),
            new("原来的 TUN 优先级修复在哪里？",
                "当前 M1 不能替代桌面 TUN 修复脚本。读取度量、自动度量和 IgnoreDefaultRoutes 属于诊断事实；"
                + "修改这三个字段、修后验证与精确撤销尚未接入，计划在 M3 实现。原脚本仍保留，软件不会调用它。"),
            new("状态标签怎么读",
                "“当前样本正常”只表示本次采集的数据域完整且没有产生异常结论，不代表全网正常；"
                + "“采集不足”表示某个数据域没读到或规则执行失败；“发现异常”只在有观察/候选/支持等级时出现。"
                + "未知状态永远不会被画成正常。"),
            new("托盘与退出",
                "关闭窗口默认隐藏到托盘，当前观察继续；显式“退出”才停止采集并尝试 flush 日志。"
                + "隐藏期间不会自动开始此前未启动的探测。退出期间的故障不会留下前窗记录（M2 才实现故障窗口）。"),
            new("数据与隐私",
                "会话保存在本机应用数据目录，导出包使用包内稳定别名并移除用户名、设备名、完整路径和凭据；"
                + "D3 类数据（密钥、订阅、Cookie、聊天正文）不采集也不导出。匿名化不是数学匿名，分享前请看预览。"),
            new("排障入口",
                "若界面显示“日志写入失败”，说明磁盘或权限有问题：先检查数据目录是否可写，再继续采集。"
                + "若时间线出现“观察缺口/重复事件”，这些事件不会被计入成功；可把脱敏包交给人工分析。"),
            new("边界",
                "M1 的真实探测可能受系统默认代理栈或 TUN 影响；“不使用显式代理”不证明物理直连。"
                + "强制 IPv4/IPv6、外部握手/认证、浏览器实际代理链仍未被验证。"),
        };
    }

    public ObservableCollection<HelpSection> Sections { get; }

    public string SessionPath { get; }

    public string DataRoot { get; }
}
