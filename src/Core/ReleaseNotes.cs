// @author bdth 2074055628@qq.com
// 文件用途 维护内置的三语版本说明并记录已读版本

using System;
using System.Collections.Generic;

namespace AegisApp
{
    internal sealed class ReleaseNote
    {
        public readonly string Version;
        public readonly string Date;
        private readonly string[][] items;

        public ReleaseNote(string version, string date, string[][] entries)
        {
            Version = version; Date = date; items = entries;
        }

        public string Tag { get { return "v" + Version; } }

        public int Count { get { return items == null ? 0 : items.Length; } }

        public string Item(int index)
        {
            if (items == null || index < 0 || index >= items.Length) return "";
            string[] row = items[index];
            if (row == null || row.Length == 0) return "";
            int lang = Lang.Cur;
            if (lang < 0 || lang >= row.Length || string.IsNullOrEmpty(row[lang])) return row[0];
            return row[lang];
        }
    }

    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new[]
        {
            new ReleaseNote("1.5.1", "2026-07-29", new[]
            {
                new[]{ "修复后台压制的写入判定：EcoQoS 由内核异步生效，写入后立即回读有概率读到旧值，导致大批进程被误报「写入未完全生效」。现在判定失败前会做有界重试，并在日志中给出具体失败环节。" },
                new[]{ "修复残留识别误伤自愿后台进程：Idle + 低 IO + 低页面优先级同样是进程自己进入后台模式的特征，此前会被当作上次遗留的压制，还原时反而把它提升到普通优先级并清掉它自己的 EcoQoS。现在要求核心摆位也吻合才判定为残留。" },
                new[]{ "修复隔离舱「丢弃记录」可能删除唯一副本：此前只检查原位置是否存在同名路径，客户端重新下载出一个空目录即可满足；现在按清单记录的文件数与字节数做等价校验，不等价一律拒绝丢弃。" },
                new[]{ "修复提优在存在陈旧条目时反复拆装运行中的游戏：只还原不匹配的条目，当前画面进程保持原状；核心分区在本机确实不支持时记为已处理，不再每轮无退避重写。" },
                new[]{ "修复压制恢复日志中进程名解码失败被静默当作空串，导致该进程被永久遗弃且无人还原；现在整行作废并原样保留，同时记录日志。" },
                new[]{ "LCU 凭据的日志来源改为校验端口属主进程，与 WMI、lockfile 两条来源一致，避免端口被本机其它进程占用时把凭据发出去。" },
                new[]{ "WeGame 根目录拒绝卷根，避免整个磁盘被当作清理范围而误伤同名的通用进程。看门狗就绪判定不再仅凭命名对象，需确认确有本程序进程存在。" },
                new[]{ "报告页改为分行展示：等宽字体下中文占双倍宽，整条记录此前会被折行截断；磁盘格式保持不变。" },
                new[]{ "界面仅保留中文，移除英文与日文文案，安装体积由约 610 KB 降至约 507 KB。多语言机制与全部文案键保留，补回译文即可恢复。" },
            }),
            new ReleaseNote("1.5.0", "2026-07-28", new[]
            {
                new[]{ "新增「英雄联盟专栏」：允许 WeGame 正常完成认证与启动，LCU 确认大厅可用后再按安装路径精确退出 WeGame、Cross、AI 教练、录制、反馈和网络助手等国服附加进程。" },
                new[]{ "新增「对局真无头」：进入 InProgress 后调用客户端原生接口关闭完整 CEF/UX，保留 LeagueClient 后端和游戏；赛后自动预热并回显大厅，独立看门狗在 Aegis 退出后仍负责恢复。" },
                new[]{ "Cross、诊断助手、反馈、网络助手、TQM 和 TenioDL 统一使用版本化可逆隔离：先写清单再移动、同卷校验、恢复绝不覆盖。设置页原有的 Cross 永久删除入口已移除，专栏不提供任何不可逆的文件删除。" },
                new[]{ "隔离批次新增「丢弃记录」出口：当客户端更新重新下载了组件、导致恢复无法覆盖时，可在确认每一项都已回到原位置后丢弃记录并重新隔离，不再卡死。" },
                new[]{ "专栏不重复竞技模式的优先级、CPU、EcoQoS 或 ACE 策略；运行时不注入、不修改内存，不替换游戏核心文件。LCU 凭据只保存在内存中且不会写入日志或子进程参数。" },
                new[]{ "新增 LoL 专用 ROG 指挥舱界面，实时显示安装、LCU、Gameflow、WeGame、Cross、CEF/UX 和本次释放内存，并集中提供启动、立即净化、客户端恢复与附加层隔离、恢复、丢弃。" },
                new[]{ "运行时开销与权限修正：专栏停用时不再扫描磁盘或轮询客户端，安装未找到时改为指数退避；精准净化改为两段式，仅对确认目标申请终止权限；WeGame 以登录用户而非管理员身份启动，避免游戏与反作弊被连带提权。" },
                new[]{ "对局无头状态改为落盘记录：只有确实由 Aegis 收起的界面才会被自动回显，客户端自身启动或用户主动关闭时不再被干预；独立恢复器不再自行拉起 WeGame，并在客户端后端消失或超时后退出。" },
                new[]{ "后台压制修正：采样窗口不足一秒时不再前移基线，也不再回报「无压制」。进程频繁启停的机器上热度以前永远攒不起来，自适应隔离在默认预设下等于没生效，而且一次亚秒采样会把已经隔离的进程放回全部核心。" },
                new[]{ "还原记录修正：抑制日志现在保存进程原本的 EcoQoS 状态，崩溃恢复不再把自愿省电的程序改成系统托管；崩溃后保留待重试的记录会在启动时重新载入，不再被本次会话的第一次写入抹掉。" },
                new[]{ "退出与关机修正：解除时先还原进程状态再还原耗时的环境项，退出预算被截断时不再丢掉最要紧的部分；新增注销/关机处理，以前系统关机完全不做任何还原。" },
                new[]{ "系统接口修正：单处理器组机器上 L3 缓存记录以前全部被丢弃，非对称缓存处理器（7950X3D 一类）从未被识别；Windows 10 上 EcoQoS 读取接口不受支持导致精确还原一直失败，现改用底层接口。" },
                new[]{ "配置写入修正：临时文件写到一半失败、或上次崩溃留下半截临时文件时，以前会把它覆盖到正式文件上并回报成功——所有配置与还原记录都走这条路径。现在只有确认临时文件完整才允许回退覆盖。" },
                new[]{ "界面修正：英文与日文下有九处卡片和标签说明被截断（中文放得下，所以一直没被发现），卡片高度改为按文字实际测量结果自适应；游戏库页面不再每 1.2 秒在界面线程上逐个枚举系统进程。" },
            }),
            new ReleaseNote("1.4.4", "2026-07-25", new[]
            {
                new[]{ "完成原有通用优化功能线；后续英雄联盟专项能力从 1.5.0 开始独立演进。" },
                new[]{ "安全修复：后台压制的反作弊豁免此前用的是精确名单，而检测器用的是含子串的宽判定。两者宽度不一致时，程序自己认定为反作弊的进程仍可能被压制——被压的反作弊心跳超时可能导致掉线。现已统一为同一套判定，并在冻结路径上加了独立的第二道拦截。" },
                new[]{ "安全修复：向 PowerShell 传递目录与策略名时使用字符串拼接。PowerShell 也把排版引号（U+2019 等）当作定界符，因此转义 ASCII 单引号并不足够；本程序以管理员身份运行，构造特定名称的目录可导致以管理员权限执行任意命令。现改为经环境变量传参、脚本以 -EncodedCommand 传入且不再落临时文件。" },
                new[]{ "修复：游戏档案文件读取失败时会被当成「档案为空」，界面显示空游戏库，此时新增任何一个游戏都会把原档案整份覆盖。现在读取失败会被识别并拒绝保存，原文件受到保护。" },
                new[]{ "修复：主界面部分说明文字因换行条件从不成立而被截断，被截掉的往往正是风险提示；托盘菜单文字偏上；游戏库列表与标题栏在重复绘制时可能因共享字体被释放而反复报错。" },
                new[]{ "还原可靠性：进程原有的 EcoQoS 设置、逐游戏图形选项中由 Windows 写入的其它字段、中断亲和改动过的设备名单，现在都会被完整保留并还原，不再被覆盖或遗漏。" },
                new[]{ "本次共修复 26 个问题，自测用例从 39 项扩充到 59 项。" },
            }),
            new ReleaseNote("1.4.3", "2026-07-25", new[]
            {
                new[]{ "GPU 中断亲和优化：把显卡中断引导到游戏所在的核心附近，减少跨核/跨缓存开销。多适配器或多处理器组系统只应用较轻的邻近策略。需重启显卡设备或电脑后生效。" },
                new[]{ "游戏网络优先：同样把网卡中断引导到游戏核心附近，并给游戏目录中已添加的可执行文件的流量打上 QoS 优先级标记，减少排队和抖动。" },
                new[]{ "压制豁免不再写死平台名：改为沿当前游戏渲染进程的父进程链向上识别启动器宿主，任何平台、任何游戏都自动适用；进程树够不到的常驻启动器外壳则按通用启动器类别在对局期间兜底豁免。" },
                new[]{ "新增开场动画：主窗口打开时淡入并轻微上浮，不再生硬地直接弹出。" },
                new[]{ "新增「检测到游戏后自动收起窗口」开关（默认关闭）：检测到游戏 10 秒后把主窗口收回托盘，每局只收一次，之后手动打开不会再被收走。" },
                new[]{ "新增版本说明：程序内置三语更新说明，离线可查看；升级到新版本后第一次打开会有未读标记。" },
                new[]{ "修复：托盘右键菜单文字整体偏上约 6~7 像素，现已垂直居中。" },
                new[]{ "修复：注册表恢复机制此前无法正确处理二进制类型的值，比较时会退化成类型名比较而非内容比较。" },
            }),
            new ReleaseNote("1.4.2", "2026-07-24", new[]
            {
                new[]{ "任意进程加速：不再局限于游戏，你手动添加的任何程序都能被识别、保护并提速，进程一跑起来就生效，不需要它有可见窗口或处于前台。" },
                new[]{ "套壳启动器识别：把真正的主程序解压到临时目录再运行的启动器，现在也能被正确认出（app → app64 / app_x64 / app-v2 这类位数或版本后缀），同时拒绝 app → app_updater 这种会误伤第三方进程的匹配。" },
                new[]{ "粘性会话保护：一局确认之后，alt-tab 切到桌面或别的程序都不会再被误判成目标已退出，保护和加速会一直保持。" },
                new[]{ "竞技级压制范围：竞技模式（以及自定义模式下新增的同名开关）不再豁免前台和可见窗口，除真正的 Windows 核心服务外一律压制。" },
                new[]{ "核心系统服务豁免收紧：从原来的「C:\\Windows 下一律不动」改成按进程名加安装路径双重匹配的精确白名单，只在竞技级强度下生效。" },
                new[]{ "修复：竞技模式下 alt-tab 离开游戏，曾会导致游戏进程自己被当成后台压制，在有反作弊的游戏里可能掉线。" },
                new[]{ "修复：位数/版本后缀匹配过去允许任意后续内容，可能让名字前缀撞车的无关第三方进程被当成你选定的目标而获得完全信任。" },
                new[]{ "安全：反作弊进程无条件豁免压制，且改为按进程名加已知子串双重匹配，堵上「内置目录里没收录的反作弊变体可能被压制」这个缺口。" },
            }),
            new ReleaseNote("1.0", "2026-07-24", new[]
            {
                new[]{ "本仓库下的首个公开发布版本。" },
            }),
        };

        public static ReleaseNote Current
        {
            get
            {
                foreach (ReleaseNote n in All)
                    if (string.Equals(n.Version, App.Version, StringComparison.OrdinalIgnoreCase)) return n;
                return null;
            }
        }

        public static bool HasUnseen
        {
            get { return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase); }
        }

        public static void MarkSeen() { Settings.SaveStr(SeenKey, App.Version); }

#if AEGIS_SELFTEST
        internal static List<string> MissingTranslations()
        {
            var bad = new List<string>();
            foreach (ReleaseNote n in All)
                for (int i = 0; i < n.Count; i++)
                {
                    int prev = Lang.Cur;
                    try
                    {
                        for (int lang = 0; lang < 3; lang++)
                        {
                            Lang.Cur = lang;
                            if (string.IsNullOrEmpty(n.Item(i))) bad.Add(n.Version + " #" + i + " lang" + lang);
                        }
                    }
                    finally { Lang.Cur = prev; }
                }
            return bad;
        }
#endif
    }
}
