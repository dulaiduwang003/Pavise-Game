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
            new ReleaseNote("1.7.1", "2026-08-13", new[]
            {
                new[]{ "新增关闭指针精度增强 默认关 关掉系统那条鼠标加速曲线 立即生效 可还原" },
                new[]{ "新增辅助功能按键拦截 清掉筛选键 粘滞键 切换键 键鼠里唯一能救回两位数毫秒的软件项 可还原" },
                new[]{ "新增禁止键鼠设备选择性暂停 治空闲一会儿之后第一下操作发飘 只碰键鼠 不碰 U 盘声卡 可还原" },
                new[]{ "新增键鼠队列校正 把被第三方工具改过的队列长度改回系统默认" },
                new[]{ "USB 中断避让改成开启前先弹窗说明代价 只有 4K 以上回报率的鼠标值得开 1000Hz 及以下基本没收益" },
                new[]{ "USB 中断避让新增开机自检 换过 CPU 或在 BIOS 改过核心数 失效的掩码会自动还原" },
                new[]{ "体检新增键鼠链路整段 鼠标回报率实测 延迟量级说明 蓝牙键鼠会被点名 只读不改" },
                new[]{ "新增重压后台绑核收缩 默认开 治后台抢内存带宽 台架实测 33 帧到 95 帧" },
                new[]{ "收缩落点跟着你划的游戏核实时算 多 CCD 会整个躲开游戏核那块 L3 这项不需要你设置" },
                new[]{ "新增对局稳定后回收后台工作集 默认关 代价是切回那些程序头一下会顿" },
                new[]{ "新增待机列表超阈值时清空 默认关 用过 ISLC 的开这个" },
                new[]{ "对局前清理低优先级待机内存重新上线 默认关 内存三项不再被极限模式强制" },
                new[]{ "对局电源计划默认改成卓越性能 本机没有会自动建一份 有就直接用你那份" },
                new[]{ "托管方案降成下拉最后一项 改名 PG 竞技 带本机签名 一键还原会连它删掉" },
                new[]{ "升级时旧的 Pavise 竞技方案自动删除 正在用它会先切走再删" },
                new[]{ "检查更新改成九条线路并发赛跑 直连不通自动落到 ghproxy 和 jsDelivr 镜像" },
                new[]{ "下载地址跟着通的那条线路走 新增官方网盘入口 只允许打开可信域名" },
                new[]{ "游戏库新增强制接管 模拟器 云游戏这类识别不到的 进程一起来就进对局 开启前会说明代价" },
                new[]{ "添加游戏改成实时 每 3 秒自动补上新开的程序 扫描和 GPU 采样各阶段都有进度提示" },
                new[]{ "核心分配手动档补了操作说明 已划核数挪到矩阵上方 新增一行重压后台落点" },
                new[]{ "策略页被预设锁住的开关写明是强制开还是强制关 暂停索引和预取的说明原来写反了" },
                new[]{ "打开主界面不再自动弹窗 版本说明和反馈交流都改成到关于页手动打开" },
                new[]{ "修复大小核机型的后台压制等于没做 现在挤核叠在核心分区之上" },
                new[]{ "修复挤核会挤到你划给游戏的核上 现在只在后台能待的区域里收缩" },
                new[]{ "修复后台档位在门槛上反复抖 一局 135 次降到 4 次" },
                new[]{ "修复开局最要紧的十秒被停服务占住 停服务挪到对局稳定 20 秒后" },
                new[]{ "修复服务让路在核心不多的机器上从来没生效 暂停索引预取与它互相抵消" },
                new[]{ "修复常规档在电池上会打开 USB 选择性暂停 现在四档都不打开它" },
                new[]{ "修复核心矩阵三处看不清 后台落点标不出来 无超线程的大小核不分带 快捷选核出现重复按钮" },
                new[]{ "修复版本说明弹窗打开时卡顿 改成整卡自绘 控件从 293 个降到 17 个" },
                new[]{ "对局中托盘提示的刷新从 1.5 秒放宽到 6 秒" },
                new[]{ "移除窗口化游戏优化 交回系统设置里由你自己决定 升级后自动还原" },
                new[]{ "移除后台硬限帧 升级后自动还原" },
                new[]{ "移除游戏文件预热" },
            }),
            new ReleaseNote("1.7.0.6", "2026-08-11", new[]
            {
                new[]{ "新增核心分配 全核 只用大核 手动三选一 手动可以逐个逻辑核挑 只在对局中下发 退出自动还原" },
                new[]{ "修复上传让位一直没生效 写的是旧版策略格式 现在的系统不认 升级后才真的开始限速" },
                new[]{ "修复 Steam 覆盖层被当成普通后台压制 平台豁免改成按安装目录识别" },
                new[]{ "移除对局内存驻留" },
                new[]{ "移除对局前清理待机内存" },
                new[]{ "移除游戏时降级桌面视觉效果 升级后自动还原" },
                new[]{ "日志改成全中文 不再输出内部代号和十六进制掩码" },
            }),
            new ReleaseNote("1.7.0.5", "2026-08-11", new[]
            {
                new[]{ "移除后台赶去集显 它会把驱动录屏 视频播放这类后台程序也永久改走集显 而且对已在运行的程序无法生效 升级后自动还原写过的显卡偏好" },
                new[]{ "移除禁用 MPO 排查开关 只有极少数驱动与显示器组合用得上 关掉它画面全部改走合成 抓屏类程序也可能受影响 升级后自动还原 Pavise 写过的值 你自己改的不动" },
            }),
            new ReleaseNote("1.7.0.4", "2026-08-11", new[]
            {
                new[]{ "游戏核心范围可以选绑哪块了 双 CCD 机型两块任选 X3D 多出高频 CCD 给少数吃频率的游戏 切换即时生效 对局中会等对局结束" },
                new[]{ "电源计划改成一个下拉选完 不切换 用 Pavise 托管方案 或者用你自己调好的计划 选了自己的以后 Pavise 只负责切过去 一个设置都不改 笔记本嫌托管方案太热的选平衡就行" },
                new[]{ "优化策略页按 核心控制 预设细节 会话附加 分成三个标签 不再一列滚到底 设置搜索直达时自动切到对应标签" },
                new[]{ "设置页新增清除全部配置 升级后遇到卡顿掉帧可以试试 把配置环境全部删除" },
                new[]{ "移除后台限速" },
                new[]{ "移除全屏游戏自动入库" },
                new[]{ "移除刷新率守护" },
                new[]{ "移除自定义重压时清空后台内存" },
            }),
            new ReleaseNote("1.7.0.3", "2026-08-10", new[]
            {
                new[]{ "标题栏新增设置搜索 跨页面匹配 点击直达" },
                new[]{ "移除禁用 CPU 空闲状态 升级后电源计划自动写回空闲允许" },
                new[]{ "移除硬盘中断避让 升级后自动还原写入的值" },
                new[]{ "收到反馈 极限模式 不如 竞技模式 此版本进行了紧急修正" },
            }),
            new ReleaseNote("1.7.0.2", "2026-08-08", new[]
            {
                new[]{ "修复对局中和挂机后按键鼠标可能变卡 键盘 鼠标 耳机 音频 输入法 手柄类程序 还遇到变卡的 把后台的按键宏类工具加白名单" },
                new[]{ "游戏退出后如果有设置没还原成功 不再每 20 秒刷一条日志 改成写明卡在哪一项 静默重试" },
                new[]{ "新增极限模式 在竞技之上把 GPU 让位和后台策略一次拉到位 模式页里被强制的开关会显示为锁定 游戏核心范围和电源计划仍由你手动选择 键鼠耳机输入法照常不碰" },
                new[]{ "新增后台限速 被强力压制的后台整体限到 5% CPU 运行时间 台架实测 1% 最差帧更稳 吞吐也更高 极限模式强制开启 自定义档可选 Pavise 意外退出由看门狗自动解除" },
                new[]{ "竞技电源计划按 CPU 类别分方案 Intel 大小核线程优先性能核 AMD 密集核铺开全部核心 X3D 不插手调度交给大缓存偏好 日志写明用的哪套" },
                new[]{ "新增后台赶去集显 双显卡本把被压制后台里用显卡的程序改走集显 给独显腾显存和引擎时间 显卡页开启 默认关 对正在运行的程序下次启动生效" },
                new[]{ "核心分区改成独立手动选择 策略页可随时在全核和单 CCD 间切换 极限模式不再强制；7945HX 7950X 5900X 会选择完整处理器 Die，Zen2 不再把单个 CCX 误当 CCD，X3D 全核时继续把调度交给 AMD 驱动" },
                new[]{ "新增游戏文件预热 默认关闭 对局开始后把游戏文件悄悄读进缓存 加载和切图更顺 内存不足自动放弃" },
                new[]{ "新增对局内存驻留 默认关闭 内存紧张时把游戏内存钉住不被换出 内存充裕时不动作 退出解除" },
                new[]{ "新增后台上传让位 默认关闭 对局时限制被压制后台的上行带宽 网盘同步和 P2P 做种不再拖高延迟" },
                new[]{ "系统环境页新增前台时间片校正 被老优化教程改过的时间片值修回系统默认 正常的不动" },
                new[]{ "PUBG 塔科夫这类被反作弊挡住提优的游戏 概览页现在直接指路开 后备提优 不再只写进日志" },
                new[]{ "大改体验页 自行查看" },
                new[]{ "添加游戏支持直接从正在运行的程序里选 和 白名单一样" },
            }),
            new ReleaseNote("1.7.0", "2026-08-08", new[]
            {
                new[]{ "智能保帧上线 默认开启 单独抬高决定帧数的那个线程 游戏吃满 CPU 时 1% 最差帧改善 77% 到 96% 识别不出时提优自动降档 负载退去 1 秒内恢复" },
                new[]{ "后台 GPU 让位重新上线 后台吃满显卡时中位帧时间减半 尾部帧改善约四成" },
                new[]{ "移除 AMD 驱动调优 部分 A 卡有副作用 升级后自动还原相关驱动设置" },
                new[]{ "新增 QQ 交流二群 1101249532" },
            }),
            new ReleaseNote("1.6.8.1", "2026-08-07", new[]
            {
                new[]{ "保帧线程重新上线 补充测试发现 6 核 12 线程这类机型提升明显 游戏吃满 CPU 时 1% 最差帧改善 77% 到 96% 开关在策略页 沿用你之前的设置" },
                new[]{ "竞技模式禁用 CPU 空闲状态重新上线 新增平台识别 Intel K 系配 Z 板 AMD 桌面 Ryzen 配 B 板或 X 板可用 其余平台禁 C-State 会连带关掉睿频 开关自动置灰 之前升级把它重置成了关 要用的重新打开" },
                new[]{ "暂停索引和预取服务重新上线 机械盘建议开 SSD 不用" },
                new[]{ "这三项启动时不再被自动清理" },
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
