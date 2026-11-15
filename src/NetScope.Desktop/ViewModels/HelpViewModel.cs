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
            new("能做什么",
                "只读采集本机网卡、路由、DNS 与代理分层配置；手动运行一组明确目标的探测；保存证据、时间线与诊断；"
                + "导出脱敏 ZIP 交给你自己分析。所有结论都会标出证据等级、引用 ID、反证与缺口。"
                + "「修复」页对本机做真实体检与受控修复：勾选并二次确认后才写入，全部改动可回滚。"),
            new("不做什么",
                "观察与诊断不修改路由、度量、DNS、代理环境变量、TUN 或客户端配置；不停止/切换/重启任何客户端；"
                + "不注册服务、计划任务或开机启动；不上传日志、不调用 DeepSeek、不需要 API Key。"
                + "修复只发生在「修复」页：先只读体检，再勾选动作并二次确认才写入；不会自动申请管理员权限。"),
            new("原来的 TUN 优先级修复在哪里？",
                "已内置到「修复」页：体检读取 TUN 网卡的接口度量、自动度量与 IgnoreDefaultRoutes 三个字段，异常时给出 TUN 优先级修复动作。"
                + "执行需要管理员；修后自动做 A~E 自检，度量改动未生效（原因 A/B）会自动还原 TUN 度量；也可一键回滚全部改动。旧桌面脚本已删除，零脚本依赖。"),
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
