// @author bdth 2074055628@qq.com
// 文件用途 维护内置的三语版本说明并记录已读版本

using System;
using System.Collections.Generic;

namespace PaviseApp
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
            new ReleaseNote("1.5.1", "2026-07-31", new[]
            {
                new[]{ "新增「后台 GPU 让位」开关（默认关闭，自定义策略内）：重压 / 隔离档的后台进程连 GPU 调度优先级一并压低，GPU 吃紧时后台渲染与转码让位给游戏；原值随压制快照记录，恢复与崩溃自愈走同一条路。直播推流等需要后台继续工作的软件请先加入白名单。" },
                new[]{ "帧率证据剔除失焦帧：游戏在后台主动降帧的部分不再污染平均帧率与 1% / 0.1% Low，证据行单独报告剔除的失焦时长与占比；对焦帧不足时明示本局无法给出可信统计。切屏前后跨越边界的帧一并剔除。" },
                new[]{ "游戏库互保：竞技模式不再把游戏库里另一个正在运行的游戏当作后台进程压制，双开挂机不再被误伤；提优与逐游戏设置仍只服务当前检测到的会话。" },
                new[]{ "英雄联盟安装扫描改为按需：安装位置未知或失效（卸载、移动）时，启动不再自动扫描——进入专栏页、打开专栏开关、执行专栏操作或相关进程出现时才会扫；扫描遮罩只覆盖专栏页、可手动取消，扫描期间锁定模式切换；已定位的安装后台校验完全静默，不再做全盘探测。" },
                new[]{ "预设白名单收敛为系统核心进程，不再预置任何第三方软件；例外由用户在白名单页自行添加。" },
                new[]{ "一次性白名单清理：本版本起预设白名单收敛为系统核心；早前构建并入的 11 条第三方豁免会在下次启动时自动移除，用户此后自行添加的例外不受影响。游戏库互保同时拒绝盘符根档案路径，避免整个磁盘被豁免压制。开机自启迁移拒绝临时/微信缓存等易失目录。" },
                new[]{ "修复压制自测的一个竞速误报，并新增 GPU 让位、失焦剔除与游戏库互保的自测；自测用例扩充到 82 项。" },
            }),
            new ReleaseNote("1.5", "2026-07-29", new[]
            {
                new[]{ "新增「英雄联盟专栏」：WeGame 只负责认证与拉起，LCU 确认大厅可用后按安装路径精确退出国服附加进程；对局中通过客户端原生接口关闭 CEF/UX，赛后自动回显，独立看门狗在 Pavise 退出后仍负责恢复。" },
                new[]{ "新增附加层直接删除：单独确认后删除 Cross 附加层（AI 教练、iCreate 录制），客户端更新会重新下载这些组件；客户端、WeGame 或反作弊运行时拒绝操作，删除范围不含游戏本体、登录链路与更新器。" },
                new[]{ "新增「竞技画质」：一组可逆的 game.cfg 设置（阴影、抗锯齿、光束、装饰效果、画质档位、独占全屏），原值连同安装路径一并记录，可一键还原。" },
                new[]{ "界面仅保留简体中文，构建体积由约 610 KB 降至约 500 KB；多语言机制与全部文案键保留，补回译文即可恢复。" },
                new[]{ "后台压制与提优的一批稳定性修复：EcoQoS 异步生效导致的误报、把自愿进入后台的进程误判为残留并反向提权、陈旧条目导致运行中的游戏被反复拆装、恢复日志中进程名解码失败被静默丢弃。" },
                new[]{ "英雄联盟运行时加固：LCU 凭据的日志来源改为校验端口属主进程；WeGame 根拒绝卷根；看门狗就绪判定不再仅凭命名对象；净化判定权归路径，此前两个已声明的清理目录从未生效。" },
                new[]{ "退出、关机与崩溃恢复修正，以及单处理器组 L3、非对称缓存处理器、Windows 10 EcoQoS 读取等系统接口修正。" },
                new[]{ "会话报告改为分行展示，解决等宽字体下中文双倍宽导致的折行截断；磁盘格式不变。" },
            }),
            new ReleaseNote("1.4.4", "2026-07-25", new[]
            {
                new[]{ "完成原有通用优化功能线；后续英雄联盟专项能力从 1.5 开始独立演进。" },
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

#if PAVISE_SELFTEST
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
