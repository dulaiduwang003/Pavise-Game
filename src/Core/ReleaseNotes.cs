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
            new ReleaseNote("1.6.5", "2026-08-05", new[]
            {
                new[]{ "新增「扫描已安装游戏」，读取各平台安装记录，勾选批量加入游戏库。" },
                new[]{ "加速器不再被列为游戏。" },
                new[]{ "修复回滚旧版本会清空游戏库。" },
                new[]{ "修复安装目录识别过窄。" },
                new[]{ "修复启动器换壳认错后无法纠正。" },
                new[]{ "竞技档下前台程序的子进程一并豁免。" },
                new[]{ "移除报告模式，只留日志；每局成效改记在日志里。" },
                new[]{ "系统体检若干修正。" },
                new[]{ "界面功能说明全部重写。" },
            }),
            new ReleaseNote("1.6.4", "2026-08-05", new[]
            {
                new[]{ "系统体检新增六项：刷新率、内存余量、供电方式、后台占用前三、Game DVR、系统版本。" },
                new[]{ "新增启动反馈弹窗，可关闭。" },
                new[]{ "新版本启动会接管在跑的旧版本，旧实例完整还原后再退出。" },
                new[]{ "修复体检页滚动后重新体检顶部留白。" },
                new[]{ "效率模式判定细分为不支持 / 接口可用 / 支持。" },
            }),
            new ReleaseNote("1.6.3", "2026-08-05", new[]
            {
                new[]{ "新增「系统体检」页，只读检查本机能力与设置，输出带依据的结论清单。" },
                new[]{ "体检页支持 NVIDIA 写入实测。" },
                new[]{ "移除 v1.6.2 的中断核规避，收益不抵让出一个物理核的代价。" },
                new[]{ "修复 Xbox / 微软商店版游戏加不进目标库。" },
            }),
            new ReleaseNote("1.6.2", "2026-08-05", new[]
            {
                new[]{ "新增「中断核规避」（默认关，8 核以上生效）。" },
                new[]{ "规避目标改为开局实测，不再固定屏蔽 CPU 0。" },
                new[]{ "中断测量改用 30 秒窗口，精度提高十倍。" },
                new[]{ "许可协议改为 Pavise 许可协议：源码公开、可自由使用与修改，禁止收费分发。" },
            }),
            new ReleaseNote("1.6.1", "2026-08-04", new[]
            {
                new[]{ "修复跑在临时目录的程序主体被当后台压制。" },
                new[]{ "新增写入失败熔断，连续失败 2 次自动关闭对应开关。" },
                new[]{ "识别拒绝一切修改的自保护程序，后续对局直接跳过。" },
                new[]{ "游戏退出增加 15 秒宽限期，启动器换壳不再触发整套还原。" },
                new[]{ "竞技电源策略补齐隐藏调速参数。" },
                new[]{ "游戏提速新增退出效率模式的回读验证。" },
                new[]{ "移除 NVIDIA 低延迟中驱动不接受的一项设置。" },
                new[]{ "证据模式开启前增加确认弹窗。" },
            }),
            new ReleaseNote("1.6.0", "2026-08-02", new[]
            {
                new[]{ "仓库迁移至 github.com/dulaiduwang003/Pavise-Game。" },
                new[]{ "新增「后台冻结」（默认关，仅竞技 / 激进自定义档）：挂起已隔离、30 秒无动静且无窗口的后台。" },
                new[]{ "新增「渲染主权域」（默认关）：单独抬高决定帧数的主线程。" },
                new[]{ "新增「NVIDIA 低延迟」（默认关）：渲染队列压到 1 帧。" },
                new[]{ "新增「后备提优」（默认关）：打不开句柄时由系统在进程创建时给高优先级。" },
                new[]{ "新增「Windows 游戏模式守护」与「TCP 低延迟」（均默认关）。" },
                new[]{ "修正「前台调度加权」，原写入值等同系统默认，实为空操作；改名「前台调度稳定」。" },
                new[]{ "「后台 GPU 让位」与「后台冻结」接入优化策略页，此前无法开启。" },
            }),
            new ReleaseNote("1.5.1", "2026-07-31", new[]
            {
                new[]{ "新增「后台 GPU 让位」（默认关）：被重压的后台连显卡优先级一并降低。" },
                new[]{ "帧率统计剔除失焦帧。" },
                new[]{ "竞技模式不再把库中另一个正在运行的游戏当后台压制。" },
                new[]{ "英雄联盟安装扫描改为按需触发。" },
                new[]{ "预设白名单收敛为系统核心进程，早前并入的 11 条第三方豁免会自动移除。" },
                new[]{ "游戏库拒绝盘符根目录。" },
            }),
            new ReleaseNote("1.5", "2026-07-29", new[]
            {
                new[]{ "新增「英雄联盟专栏」：精准退出国服附加进程，对局中收起大厅，赛后自动恢复。" },
                new[]{ "新增附加层删除，需单独确认。" },
                new[]{ "新增「竞技画质」，可一键还原。" },
                new[]{ "界面只保留简体中文，体积由约 610 KB 降至约 500 KB。" },
                new[]{ "修复一批压制与提速的稳定性问题。" },
                new[]{ "修正退出、关机与崩溃恢复的若干问题。" },
            }),
            new ReleaseNote("1.4.4", "2026-07-25", new[]
            {
                new[]{ "安全修复：反作弊豁免与识别的判定宽度不一致，可能压到反作弊导致掉线。" },
                new[]{ "安全修复：向 PowerShell 传目录名使用字符串拼接，可被构造目录名借管理员权限执行任意命令。" },
                new[]{ "修复游戏档案读取失败被当成空档案，此时新增游戏会覆盖原文件。" },
                new[]{ "修复主界面部分说明文字被截断、托盘菜单文字偏上。" },
                new[]{ "还原更完整：效率模式原值、逐游戏图形选项其它字段、中断亲和设备名单。" },
            }),
            new ReleaseNote("1.4.3", "2026-07-25", new[]
            {
                new[]{ "新增显卡中断亲和与游戏网络优先，均需重启生效。" },
                new[]{ "压制豁免改为沿父进程链识别启动器，不再写死平台名。" },
                new[]{ "新增开场动画与「检测到游戏后自动收起窗口」（默认关）。" },
                new[]{ "新增内置版本说明。" },
                new[]{ "修复注册表恢复无法处理二进制类型的值。" },
            }),
            new ReleaseNote("1.4.2", "2026-07-24", new[]
            {
                new[]{ "不限于游戏：手动添加的任何程序都能被识别、保护并提速。" },
                new[]{ "新增套壳启动器识别，支持位数与版本后缀。" },
                new[]{ "新增会话粘性保护，切到桌面不再被判定为游戏已退出。" },
                new[]{ "新增竞技级压制范围。" },
                new[]{ "系统核心豁免收紧为进程名加路径的精确名单。" },
                new[]{ "修复竞技模式下切出游戏时，游戏进程本身被当后台压制。" },
                new[]{ "安全：反作弊无条件豁免压制。" },
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
