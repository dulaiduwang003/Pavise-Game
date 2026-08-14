// @author bdth 2074055628@qq.com
// 文件用途 系统体检结论分部 汇总事实生成带依据的处理建议

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        private static void BuildVerdicts(AuditReport report, Facts facts, double worstIrq, int hzCur, int hzBest,
            InterruptAttributionResult culprits)
        {
            if (hzCur > 0 && hzBest > 0 && !RefreshRateIsBest(hzCur, hzBest))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "主屏刷新率",
                    Value = "强烈建议处理",
                    Note = "当前 " + hzCur + "Hz 可用 " + hzBest + "Hz 这一项比本页任何调度优化的收益都直接 "
                        + "帧数上限是翻倍级别的差距 而且没有任何代价 做法 系统设置 显示 高级显示 里把刷新率改到 "
                        + hzBest + "Hz 这是持久设置 改一次就一直生效 Pavise 不代改",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            if (facts.Access != null && facts.Access.FilterKeysSwallowing)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "筛选键",
                    Value = "强烈建议处理",
                    Note = "筛选键在输入路径上主动插入了 " + facts.Access.DelayBeforeAcceptanceMs
                        + " 毫秒的接受延迟 键鼠这条链上外设段总共才 1 到 5 毫秒 这一项一个人就顶掉了好几倍 "
                        + "多数人是玩游戏时长按 Shift 被弹窗误开的 做法 系统环境页拨开 辅助功能拦截 开关 不需要管理员也不用重启",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }
            else if (facts.Access != null && facts.Access.AnyNeedsFix)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "辅助功能热键",
                    Value = "建议关闭",
                    Note = "当前不吞击键 但开关或热键还留着 连按五次 Shift 或长按右 Shift 八秒会在全屏游戏里弹窗 "
                        + "打断一次团战的代价比任何毫秒级优化都大 做法 系统环境页拨开 辅助功能拦截 开关",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            bool btInput = false;
            try
            {
                btInput = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true)
                    || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
            }
            catch { }
            if (btInput)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "蓝牙键鼠",
                    Value = "建议换连接方式",
                    Note = "台架实测同一只鼠标蓝牙约 10.4 毫秒 2.4G 接收器约 3.6 毫秒 而 2.4G 与有线基本无差 "
                        + "无线比有线慢是过时说法 但蓝牙确实慢 而且这七毫秒比本页所有软件优化加起来都多 "
                        + "做法 插 2.4G 接收器或换有线 没有软件解法",
                    Evidence = EvMeasuredBench,
                    Warn = true
                });
            }

            if (facts.HidPowerSave)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "键鼠设备省电",
                    Value = "建议关闭",
                    Note = "治的是空闲一段时间后第一下操作的顿挫 不是稳态延迟 台式机没有代价直接关 "
                        + "笔记本要权衡续航 做法 系统环境页拨开 键鼠设备省电 开关",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.QueueTampered)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "键鼠队列长度",
                    Value = "建议改回默认",
                    Note = "有工具把它改过 这个值决定能缓存多少条输入 不决定输入被处理的快慢 所以调它不降延迟 "
                        + "调低了还会在高回报率鼠标上丢输入 点右侧一键修复改回默认 100 重启生效 可还原",
                    Evidence = EvMechanism,
                    Warn = true,
                    FixKey = "inputq"
                });
            }

            report.Verdicts.Add(new AuditRow
            {
                Name = "输入延迟的大头在哪",
                Value = "看清楚再动手",
                Note = "端到端延迟三段 外设 1 到 5 毫秒 渲染管线在 GPU 打满时 20 到 60 毫秒 显示器 3 到 20 毫秒 "
                    + "所以键鼠注册表偏方是在错的量级上花力气 真正的大头是渲染队列堆积 "
                    + "做法 游戏支持 NVIDIA Reflex 或 AMD Anti-Lag 就在游戏里开 没有就把帧率上限压到 GPU 占用 90 到 95 百分比 "
                    + "别顶着刷新率上限跑 那会隐式触发垂直同步 反而多加半帧到一帧 显卡页的帧率上限和低延迟就是干这个的",
                Evidence = EvMeasuredBench,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "后台压制",
                Value = facts.SuppressOn ? "已开启 不用处理" : "建议开启",
                Note = "合成台架六轮配对实测 1% 最差帧改善中位 90.7% 而且免疫随时间累积的恶化",
                Evidence = EvMeasuredBench,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "Windows 游戏模式",
                Value = facts.GameMode ? "已开启 不用处理" : "建议开启",
                Note = "系统原生的游戏时段后台抑制 没有兼容风险",
                Evidence = EvMechanism,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = "NVIDIA 深度调优",
                Value = facts.Nv ? "可以尝试" : "本机不适用",
                Note = facts.Nv ? "显卡页按游戏开启 写入连续失败会自动关闭对应开关"
                    : facts.IntegratedOnly
                        ? "核显没有对应的调优接口 这一项跳过 收益从压制 绑核和电源那边拿"
                        : "没有 NVIDIA 驱动接口",
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            if (report.MeasureOk)
            {
                int tier = InterruptTier(worstIrq);
                string culprit = culprits != null && culprits.Ok && culprits.TopDpc != null
                    ? culprits.TopDpc : null;
                report.Verdicts.Add(new AuditRow
                {
                    Name = "中断负载",
                    Value = tier == 2 ? "建议处理" : "不用处理",
                    Note = tier == 2
                        ? (culprit != null
                            ? "有个核心 " + PercentText(worstIrq) + " 的时间在处理硬件中断 头号来源是 " + culprit
                                + " 做法 它属于显卡就开 GPU 中断亲和 硬盘和网卡类的中断路由属于驱动层 本软件的避让开关够不到 只能等驱动更新"
                            : "有个核心 " + PercentText(worstIrq) + " 的时间在处理硬件中断 做法 系统环境页开两个避让开关 重启后再体检对比 数值降了就是它们 降不下来是硬盘 网卡或主板设备在响 那层属于驱动和硬件 本软件不碰")
                        : "当前量级 " + PercentText(worstIrq) + " 很小 感觉不出来 不用管",
                    Evidence = EvMeasuredLocal,
                    Warn = tier == 2
                });
            }

            if (facts.Dvr)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "Game DVR 后台录制",
                    Value = "建议关闭",
                    Note = "后台录制一直开着就一直有开销 多数人根本不用 Xbox Game Bar 录制",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.MemOk && facts.UsedRatio >= 0.85)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "内存余量",
                    Value = "建议处理",
                    Note = "只剩 " + facts.AvailGb.ToString("F1") + " GB 可用 内存不够时系统会拿硬盘顶内存 那是毫秒级的等待 "
                        + "比任何调度问题都更容易造成明显卡顿 做法 退掉吃内存的后台程序 长期紧张就加内存条 这个没有软件解法",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            if (facts.Vbs.WmiOk && facts.Vbs.VbsRunning)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "VBS",
                    Value = "可以尝试关闭",
                    Note = "有代价 影响内存完整性 WSL2 Docker 和沙盒 用这些功能就别关 做法 系统环境页找 VBS 卡片 拨开开关 重启生效",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.ClockStale != null && facts.ClockStale.Count > 0)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "平台时钟",
                    Value = "建议校正",
                    Note = "强制 HPET 是十年前的教程遗产 TSC 计时比它快几个数量级 点右侧一键修复即可清掉 重启生效 可还原",
                    Evidence = EvMechanism,
                    Warn = true,
                    FixKey = "clock"
                });
            }

            if (facts.PageOk && PageFileLooksDisabled(facts.PageFileGb))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "页面文件",
                    Value = "建议恢复",
                    Note = "关页面文件不提帧 只把内存吃紧时的变慢换成直接崩溃 部分游戏和反作弊还要求它存在",
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            bool coreUnparked;
            if (PowerPlan.TryCurrentUnparked(out coreUnparked) && !coreUnparked)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "核心停泊",
                    Value = "对局中自动解除",
                    Note = "当前电源方案在低负载时会让部分 CPU 核心进入停泊 从停泊态唤醒有额外延迟 表现为偶发顿挫和 1% 最差帧变差 "
                        + "Pavise 在对局激活时会把当前方案的核心停泊临时解除 保持全部核心唤醒 退出自动还原 无需你处理 "
                        + "想常态解除可在策略页改用 Pavise 托管电源方案",
                    Evidence = EvMechanism,
                    Warn = false
                });
            }
        }
    }
}
