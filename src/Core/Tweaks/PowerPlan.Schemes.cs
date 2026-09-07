// @author bdth 2074055628@qq.com
// 文件用途 电源方案的 powrprof 机械层 枚举 名称读写 创建删除 与托管方案的逐项写入
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        private const uint AccessScheme = 16;

        [DllImport("powrprof.dll")] private static extern uint PowerReadValueMin(IntPtr root, ref Guid sub, ref Guid setting, out uint min);
        [DllImport("powrprof.dll")] private static extern uint PowerReadValueMax(IntPtr root, ref Guid sub, ref Guid setting, out uint max);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, ref IntPtr dest);
        [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(
            IntPtr root, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);
        [DllImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
        private static extern uint PowerReadFriendlyNameBuf(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDescription(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        private static readonly Guid SubVideo     = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        private static readonly Guid VideoBrightness    = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");
        private static readonly Guid VideoDimBrightness = new Guid("f1fbfde2-a960-4165-9f88-50667911ce96");
        private static readonly Guid VideoAdaptive      = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

        private static readonly Guid SubNone      = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
        private static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid SubPcie      = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        private static readonly Guid SubUsb       = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        private static readonly Guid SubDisk      = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
        private static readonly Guid SubWireless  = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");

        private static readonly Guid Personality       = new Guid("245d8541-3943-4422-b025-13a784f679b7");
        private static readonly Guid ProcThrottleMin   = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        private static readonly Guid ProcThrottleMax   = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        private static readonly Guid CpMinCores        = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        private static readonly Guid CpMaxCores        = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");
        private static readonly Guid PerfBoostMode     = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        private static readonly Guid Throttling        = new Guid("3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb");
        private static readonly Guid SysCoolPol        = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f");
        private static readonly Guid PcieAspm          = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
        private static readonly Guid UsbSelSuspend     = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        private static readonly Guid DiskIdle          = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
        // NVMe 非操作性电源态的唤醒是毫秒级尾延迟 抑制降档的正规杠杆是延迟容忍不是超时
        //   超时到期后驱动只选 ENLAT+EXLAT 不超过容忍值的状态 容忍 0 等于没有状态够格
        //   微软性能方案自己就是这么写的 超时 0 的语义没有文档定义 不碰
        //   AHCI LPM 唤醒卡顿有 Event 129 实锤 对局保持链路 Active
        private static readonly Guid NvmeLatTolPrimary   = new Guid("fc95af4d-40e7-4b6d-835a-56d131dbc80e");
        private static readonly Guid NvmeLatTolSecondary = new Guid("dbc9e238-6de9-49e3-92cd-8c2b4946b472");
        private static readonly Guid AhciLpm             = new Guid("0b2d69d7-a2a1-449c-9680-f91c70521c60");

        // USB3 链路的 U1/U2 低功耗态退出是微秒到毫秒级 和选择性暂停同一路 对局关掉
        private static readonly Guid Usb3Lpm           = new Guid("d4e98f31-5ffe-4ce1-be31-1b38b384c009");
        // 厂商注入到方案里的显卡子组 只有装了对应驱动的机器才有 SettingPresent 读不到整项跳过
        //   Intel 核显 0 最长续航 1 平衡 2 最高性能 掌机上它划的是核显那份预算
        //   切换显卡 0 强制省电卡 1 优化省电 2 优化性能 3 最大性能
        private static readonly Guid SubIntelGfx       = new Guid("44f3beca-a7c0-460e-9df2-bb8b99e0cba6");
        private static readonly Guid IntelGfxPlan      = new Guid("3619c3f2-afb2-4afc-b0e9-e7fef372de36");
        private static readonly Guid SubSwitchGfx      = new Guid("e276e160-7cb0-43c6-b20b-73f5dce39954");
        private static readonly Guid SwitchGfxPolicy   = new Guid("a1662ab2-9d34-4e53-ba8b-2639b9e20857");

        // 极限档专属 空闲状态选择策略 不等同于硬件唤醒延迟或禁止空闲
        //   这几项在多数方案上未暴露 SettingPresent 读不到就整项跳过 不写坏方案
        private static readonly Guid IdlePromote      = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c");
        private static readonly Guid IdleDemote       = new Guid("4b92d758-5a24-4851-a470-815d78aee119");
        private static readonly Guid IdleScaling      = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");

        private static readonly Guid PerfEpp           = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        private static readonly Guid PerfAutonomous    = new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537");
        private static readonly Guid PerfBoostPol      = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b59");
        private static readonly Guid PerfIncPol        = new Guid("465e1f50-b610-473a-ab58-00d1077dc418");
        private static readonly Guid PerfDecPol        = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac6");
        private static readonly Guid PerfIncTime       = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa");
        private static readonly Guid PerfDecTime       = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8");
        private static readonly Guid PerfIncThreshold  = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d");
        private static readonly Guid PerfDecThreshold  = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6");
        private static readonly Guid LatencyHintPerf   = new Guid("619b7505-003b-4e82-b7a6-4dd29c300971");
        private static readonly Guid LatencyHintUnpark = new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd88");
        private static readonly Guid PerfDutyCycling   = new Guid("4e4450b3-6179-4e91-b8f1-5bb9938f81a1");
        private static readonly Guid ProcFreqMax       = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        private static readonly Guid WirelessPowerSave = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");

        private static readonly Guid ProcThrottleMin1  = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d");
        private static readonly Guid ProcThrottleMax1  = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed");
        private static readonly Guid CpMinCores1       = new Guid("0cc5b647-c1df-4637-891a-dec35c318584");
        private static readonly Guid CpMaxCores1       = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029");
        private static readonly Guid PerfEpp1          = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
        private static readonly Guid SchedPolicy       = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
        private static readonly Guid ShortSchedPolicy  = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");
        // 效率等级 1 那半边的升降频与延迟敏感项 GUID 末位比等级 0 大一
        //   P 核写激进 E 核却留系统默认 游戏辅助线程落到 E 核上爬频走的就是平衡档口径
        //   HWP 自主模式下升降频策略 时间 阈值六项是空操作 真增量是睿频策略和两项延迟敏感
        private static readonly Guid PerfBoostPol1      = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b5a");
        private static readonly Guid PerfIncPol1        = new Guid("465e1f50-b610-473a-ab58-00d1077dc419");
        private static readonly Guid PerfDecPol1        = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac7");
        private static readonly Guid PerfIncTime1       = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5ab");
        private static readonly Guid PerfDecTime1       = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c9");
        private static readonly Guid PerfIncThreshold1  = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35e");
        private static readonly Guid PerfDecThreshold1  = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a7");
        private static readonly Guid LatencyHintPerf1   = new Guid("619b7505-003b-4e82-b7a6-4dd29c300972");
        private static readonly Guid LatencyHintUnpark1 = new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd89");

        private static readonly Guid IdleDisableSet    = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");

        private struct Knob
        {
            public readonly Guid Sub;
            public readonly Guid Setting;
            public readonly uint ArenaAc, ArenaDc, CalmAc, CalmDc;
            private readonly string labelKey;
            public string Label { get { return Lang.T(labelKey); } }
            public Knob(Guid sub, Guid setting, uint arenaAc, uint arenaDc, uint calmAc, uint calmDc, string label)
            {
                Sub = sub; Setting = setting;
                ArenaAc = arenaAc; ArenaDc = arenaDc; CalmAc = calmAc; CalmDc = calmDc;
                labelKey = label;
            }
        }

        private static readonly Knob[] CoreKnobs = new Knob[]
        {
            new Knob(SubProcessor, ProcThrottleMin, 100, 100,  20, 10, "t.powerplanschemes.1"),
            new Knob(SubProcessor, ProcThrottleMax, 100, 100, 100, 100, "t.powerplanschemes.2"),
            new Knob(SubProcessor, CpMinCores,      100, 100,  50, 20, "t.powerplanschemes.3"),
            new Knob(SubProcessor, CpMaxCores,      100, 100, 100, 100, "t.powerplanschemes.4"),
            new Knob(SubProcessor, PerfBoostMode,     2,   2,   2,  3, "t.powerplanschemes.5"),
            new Knob(SubProcessor, Throttling,        0,   0,   2,  2, "t.powerplanschemes.6"),
            new Knob(SubProcessor, SysCoolPol,        1,   1,   1,  1, "t.powerplanschemes.7"),
            new Knob(SubPcie,      PcieAspm,          0,   0,   1,  2, "t.powerplanschemes.8"),
            new Knob(SubUsb,       UsbSelSuspend,     0,   0,   0,  0, "t.powerplanschemes.9"),
            new Knob(SubDisk,      DiskIdle,          0,   0, 1200, 600, "t.powerplanschemes.10"),
            new Knob(SubNone,      Personality,       1,   1,   1,  1, "t.powerplanschemes.11"),
        };

        private static readonly Knob[] OptionalKnobs = new Knob[]
        {
            new Knob(SubProcessor, PerfEpp,           0,   0,  32, 70, "t.powerplanschemes.12"),
            new Knob(SubProcessor, PerfBoostPol,    100, 100,  60, 40, "t.powerplanschemes.13"),
            new Knob(SubProcessor, PerfIncPol,        2,   2,   1,  1, "t.powerplanschemes.14"),
            new Knob(SubProcessor, PerfDecPol,        1,   1,   2,  2, "t.powerplanschemes.15"),
            new Knob(SubProcessor, PerfIncTime,       1,   1,   3,  3, "t.powerplanschemes.16"),
            new Knob(SubProcessor, PerfDecTime,      10,  10,   5,  5, "t.powerplanschemes.17"),
            new Knob(SubProcessor, PerfIncThreshold, 10,  10,  30, 40, "t.powerplanschemes.18"),
            new Knob(SubProcessor, PerfDecThreshold,  8,   8,  20, 30, "t.powerplanschemes.19"),
            new Knob(SubProcessor, LatencyHintPerf, 100, 100,  75, 50, "t.powerplanschemes.20"),
            new Knob(SubProcessor, LatencyHintUnpark,100,100,  50, 50, "t.powerplanschemes.21"),
            new Knob(SubProcessor, PerfDutyCycling,   0,   0,   0,  1, "t.powerplanschemes.22"),
            new Knob(SubProcessor, ProcFreqMax,       0,   0,   0,  0, "t.powerplanschemes.23"),
            new Knob(SubWireless,  WirelessPowerSave, 0,   0,   1,  2, "t.powerplanschemes.24"),
            new Knob(SubDisk,      NvmeLatTolPrimary,   0, 0,  15,  50, "t.powerplanschemes.35"),
            new Knob(SubDisk,      NvmeLatTolSecondary, 0, 0, 100, 100, "t.powerplanschemes.36"),
            new Knob(SubDisk,      AhciLpm,             0, 0,   0,   1, "t.powerplanschemes.37"),
            new Knob(SubUsb,       Usb3Lpm,             0, 0,   2,   3, "t.powerplanschemes.43"),
            new Knob(SubIntelGfx,  IntelGfxPlan,        2, 2,   1,   1, "t.powerplanschemes.44"),
            new Knob(SubSwitchGfx, SwitchGfxPolicy,     3, 3,   1,   1, "t.powerplanschemes.54"),
        };

        private static readonly Knob[] HybridKnobs = new Knob[]
        {
            new Knob(SubProcessor, ProcThrottleMin1, 100, 100,  20, 10, "t.powerplanschemes.25"),
            new Knob(SubProcessor, ProcThrottleMax1, 100, 100, 100, 100, "t.powerplanschemes.26"),
            new Knob(SubProcessor, CpMinCores1,      100, 100,  50, 20, "t.powerplanschemes.27"),
            new Knob(SubProcessor, CpMaxCores1,      100, 100, 100, 100, "t.powerplanschemes.28"),
            new Knob(SubProcessor, PerfEpp1,           0,   0,  32, 70, "t.powerplanschemes.29"),
            new Knob(SubProcessor, SchedPolicy,        2,   2,   5,  5, "t.powerplanschemes.30"),
            new Knob(SubProcessor, ShortSchedPolicy,   2,   2,   5,  5, "t.powerplanschemes.31"),
            new Knob(SubProcessor, PerfBoostPol1,    100, 100,  60, 40, "t.powerplanschemes.45"),
            new Knob(SubProcessor, PerfIncPol1,        2,   2,   1,  1, "t.powerplanschemes.46"),
            new Knob(SubProcessor, PerfDecPol1,        1,   1,   2,  2, "t.powerplanschemes.47"),
            new Knob(SubProcessor, PerfIncTime1,       1,   1,   3,  3, "t.powerplanschemes.48"),
            new Knob(SubProcessor, PerfDecTime1,      10,  10,   5,  5, "t.powerplanschemes.49"),
            new Knob(SubProcessor, PerfIncThreshold1, 10,  10,  30, 40, "t.powerplanschemes.50"),
            new Knob(SubProcessor, PerfDecThreshold1,  8,   8,  20, 30, "t.powerplanschemes.51"),
            new Knob(SubProcessor, LatencyHintPerf1, 100, 100,  75, 50, "t.powerplanschemes.52"),
            new Knob(SubProcessor, LatencyHintUnpark1,100,100,  50, 50, "t.powerplanschemes.53"),
        };

        // 极限档在电竞列之上再加的一组 只在极限档写 其余档位一律不碰
        //   分两类 一类是把已有旋钮推到量程尽头 一类是电竞列没碰过的空闲行为
        //   计量单位由系统定义 写入前一律经 Clamp 夹到本机允许区间
        private static readonly Knob[] ExtremeKnobs = new Knob[]
        {
            // 提高选择更深空闲态的门槛；保留系统原来的降级门槛。
            // IdleDemote 按空闲比例低于阈值触发降级，0 不能解释为“更快退出”。
            // 不把它直接改成另一个未经实测的极端值；旧写入由快照迁移还原。
            new Knob(SubProcessor, IdlePromote,     100, 100, 100, 100, "t.powerplanschemes.39"),
            // 空闲检查周期 c4581c31 曾在这里写 0 想靠 Clamp 夹到下限 本机量程 1~200000 微秒
            //   写入从没成功过 每局固定报一项失败 内核检查周期跟定时器节拍走 写 1 和默认 50000 分不出差别
            //   没有依据支撑它 整项撤掉 旧快照里若有它 RestoreExtremeKnobs 照样按 GUID 写回
            // 关闭按当前性能状态缩放空闲门槛，不改变硬件的 C-state 退出时延。
            new Knob(SubProcessor, IdleScaling,       0,   0,   0,   0, "t.powerplanschemes.42"),
        };

#if PAVISE_SELFTEST
        // 极限组的成员与取值 供回归核对 不触发任何写入
        internal static int ExtremeKnobCountForTest { get { return ExtremeKnobs.Length; } }

        internal static bool ExtremeOnlyGuidForTest(Guid setting)
        {
            foreach (Knob k in CoreKnobs) if (k.Setting == setting) return false;
            foreach (Knob k in OptionalKnobs) if (k.Setting == setting) return false;
            foreach (Knob k in HybridKnobs) if (k.Setting == setting) return false;
            foreach (Knob k in ExtremeKnobs) if (k.Setting == setting) return true;
            return false;
        }

        internal static Guid[] ExtremeKnobGuidsForTest()
        {
            var list = new List<Guid>();
            foreach (Knob k in ExtremeKnobs) list.Add(k.Setting);
            return list.ToArray();
        }
        internal static List<string> DescribeKnobs()
        {
            var lines = new List<string>();
            foreach (Knob k in CoreKnobs) lines.Add(DescribeKnob(k));
            foreach (Knob k in OptionalKnobs) lines.Add(DescribeKnob(k));
            foreach (Knob k in HybridKnobs) lines.Add(DescribeKnob(k));
            foreach (Knob k in ExtremeKnobs) lines.Add(DescribeKnob(k));
            return lines;
        }

        private static string DescribeKnob(Knob k)
        {
            return k.Label + " arenaAc=" + k.ArenaAc + " arenaDc=" + k.ArenaDc
                + " calmAc=" + k.CalmAc + " calmDc=" + k.CalmDc;
        }
#endif

        private static PowerPlanProfile cachedProfile;

        private static PowerPlanProfile CurrentProfile()
        {
            if (cachedProfile != null) return cachedProfile;
            bool amd = false;
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    if (k != null)
                        amd = ((k.GetValue("ProcessorNameString") as string) ?? "")
                            .IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            cachedProfile = PowerPlanProfile.Resolve(amd, CpuTopology.Hybrid,
                CpuTopology.AsymCache, CpuTopology.PartitionTag);
            return cachedProfile;
        }

        private static bool TuneTarget(Guid g, bool aggressive, bool handheld, bool extreme)
        {
            try
            {
                PowerPlanProfile profile = CurrentProfile();
                bool? autonomousAc, autonomousDc;
                ReadAutonomousScaling(g, out autonomousAc, out autonomousDc);
                int written = 0;
                int failed = 0;
                var skipped = new List<string>();

                foreach (Knob k in CoreKnobs)
                {
                    if (aggressive && IsProcessorMinimum(k.Setting)
                        && !autonomousAc.HasValue && !autonomousDc.HasValue) { skipped.Add(k.Label); continue; }
                    if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                    if (WriteKnob(g, k, aggressive, handheld, profile,
                        autonomousAc, autonomousDc)) written++;
                    else { failed++; LogKnobFailure(k); }
                }
                foreach (Knob k in OptionalKnobs)
                {
                    if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                    // 核显只做合成的机器 独显渲染 核显和 CPU 同一块封装共享功耗预算
                    //   把核显钉在最高性能等于先划走 CPU 的睿频份 这类机器一律写平衡
                    Knob effective = k.Setting == IntelGfxPlan && IntelGfxSharesPackageWithDiscrete()
                        ? new Knob(k.Sub, k.Setting, k.CalmAc, k.CalmDc, k.CalmAc, k.CalmDc, "t.powerplanschemes.44")
                        : k;
                    if (WriteKnob(g, effective, aggressive, handheld, profile,
                        autonomousAc, autonomousDc)) written++; else failed++;
                }
                // 极限档专属组 未暴露的项照常跳过 不影响其余旋钮的写入结果
                //   写入前先快照现值 退出极限档重写方案时按快照写回
                //   否则空闲策略留在托管方案上 电竞档会白用极限的空闲策略
                // 旧版撤回项在极限档内也必须恢复；失败保留收据，下次配置重试。
                if (!RestoreExtremeKnobs(g, extreme))
                { Logger.Warn(Lang.T("log.powerplanschemes.extremePending")); return false; }
                if (extreme)
                {
                    if (!SnapshotExtremeKnobs(g))
                    { Logger.Warn(Lang.T("log.powerplanschemes.extremePending")); return false; }
                    List<ExtremeSavedValue> snapshot;
                    if (!ReadExtremeSnapshot(g, out snapshot)) return false;
                    foreach (Knob k in ExtremeKnobs)
                    {
                        // 一次瞬时读失败后 SettingPresent 可能又成功；仍不能写未备份项。
                        if (!snapshot.Exists(delegate(ExtremeSavedValue v) { return v.Setting == k.Setting; }))
                        { skipped.Add(k.Label); continue; }
                        if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                        if (WriteKnob(g, k, aggressive, handheld, profile,
                            autonomousAc, autonomousDc)) written++; else failed++;
                    }
                }
                if (CpuTopology.Hybrid && profile.WriteHetero)
                {
                    foreach (Knob k in HybridKnobs)
                    {
                        if (aggressive && IsProcessorMinimum(k.Setting)
                            && !autonomousAc.HasValue && !autonomousDc.HasValue) { skipped.Add(k.Label); continue; }
                        if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                        Knob eff = k;
                        if (k.Setting == SchedPolicy || k.Setting == ShortSchedPolicy)
                            eff = new Knob(k.Sub, k.Setting, profile.HeteroSched, profile.HeteroSched,
                                k.CalmAc, k.CalmDc, k.Label);
                        if (WriteKnob(g, eff, aggressive, handheld, profile,
                            autonomousAc, autonomousDc)) written++; else failed++;
                    }
                }

                if (failed > 0)
                {
                    if (written > 0)
                        Logger.Warn(Lang.T("log.powerplanschemes.35") + failed + Lang.T("log.powerplanschemes.45"));
                    else
                    {
                        Logger.Log(Lang.T("log.powerplanschemes.35") + failed + Lang.T("log.powerplanschemes.36"));
                        return false;
                    }
                }

                Logger.Log(Lang.T("log.powerplanschemes.37")
                    + (!aggressive ? Lang.T("log.powerplanschemes.39")
                        : handheld ? Lang.T("log.powerplanschemes.46") : Lang.T("log.powerplanschemes.38"))
                    + " " + ManagedPlanTitle + Lang.T("log.powerplanschemes.40") + profile.Tag + Lang.T("log.powerplanschemes.41") + written + Lang.T("t.gamemodeenv.30")
                    + (profile.PreserveCoreParking ? Lang.T("log.powerplanschemes.42") : "")
                    + (skipped.Count > 0 ? Lang.T("log.powerplanschemes.43") + skipped.Count + Lang.T("log.powerplanschemes.44") + string.Join(" ", skipped.ToArray()) : ""));
                return true;
            }
            catch { return false; }
        }

        private static void LogKnobFailure(Knob k)
        {
            // 日志不能重新写一遍参数，否则会绕开未知平台的保留分支。
            Logger.Warn(Lang.T("log.powerplanschemes.32") + Lang.T(k.Label)
                + Lang.T("log.powerplanschemes.33"));
        }

        private static int intelGfxSharesPackage = -1;

        private static bool IntelGfxSharesPackageWithDiscrete()
        {
            int cached = intelGfxSharesPackage;
            if (cached >= 0) return cached == 1;
            bool intelIntegrated = false, otherDiscrete = false;
            try
            {
                GpuAdapter[] all = GpuInventory.Adapters();
                if (all != null)
                    foreach (GpuAdapter a in all)
                    {
                        if (a.Vendor == GpuVendor.Intel && a.Integrated) intelIntegrated = true;
                        else if (a.Vendor != GpuVendor.Intel && !a.Integrated) otherDiscrete = true;
                    }
            }
            catch { }
            bool shares = intelIntegrated && otherDiscrete;
            if (shares) Logger.Log(Lang.T("log.powerplanschemes.igpu"));
            intelGfxSharesPackage = shares ? 1 : 0;
            return shares;
        }

        private static bool WriteKnob(Guid scheme, Knob k, bool aggressive,
            bool handheld, PowerPlanProfile profile, bool? autonomousAc, bool? autonomousDc)
        {
            bool coreParking = k.Setting == CpMinCores || k.Setting == CpMaxCores;
            bool useArena = coreParking ? profile.UseArenaCoreParking(aggressive) : aggressive;
            uint ac = useArena ? ArenaAcFor(k, k.ArenaAc, handheld, autonomousAc)
                : CalmAcFor(k, k.CalmAc, profile);
            uint dc = useArena ? ArenaDcFor(k, k.ArenaDc, autonomousDc) : k.CalmDc;
            if (useArena && IsProcessorMinimum(k.Setting))
            {
                // AC/DC 独立判定。某一侧无法确认时保持那一侧原值，读失败则整项不写。
                if (!ProcessorPowerPlatform.TryResolveMinimumIndices(autonomousAc, autonomousDc, ac, dc,
                    delegate(bool onAc)
                    {
                        uint original;
                        return (onAc ? ReadAc(scheme, k.Sub, k.Setting, out original)
                            : ReadDc(scheme, k.Sub, k.Setting, out original)) ? (uint?)original : null;
                    }, out ac, out dc)) return false;
            }
            return WritePair(scheme, k.Sub, k.Setting, ac, dc);
        }

        private static void ReadAutonomousScaling(Guid scheme, out bool? ac, out bool? dc)
        {
            ac = null; dc = null;
            try
            {
                ProcessorPowerPlatform.Interface platform = ProcessorPowerPlatform.Current;
                uint value;
                uint? requestedAc = ReadAc(scheme, SubProcessor, PerfAutonomous, out value) ? (uint?)value : null;
                uint? requestedDc = ReadDc(scheme, SubProcessor, PerfAutonomous, out value) ? (uint?)value : null;
                ac = ProcessorPowerPlatform.AutonomousMinimum(platform, requestedAc);
                dc = ProcessorPowerPlatform.AutonomousMinimum(platform, requestedDc);
            }
            catch { }
        }

        private static bool IsProcessorMinimum(Guid setting)
        {
            return setting == ProcThrottleMin || setting == ProcThrottleMin1;
        }

        // 自主调频确认开着时最低处理器状态放到温和值 由硬件自己定频
        //   09-06 在 i7-9750H 笔记本上 A/B 过 锁 100 反而 230 到 250 帧 放开 270 到 280 帧
        //   六核笔记本功耗和散热是一份预算 全核钉最高频 忙的核反而拿不到睿频
        private static uint AutonomousArenaValue(Knob k, uint value, bool ac, bool? autonomous)
        {
            return autonomous == true && IsProcessorMinimum(k.Setting)
                ? (ac ? k.CalmAc : k.CalmDc) : value;
        }

        // 笔记本的专注档 电池那一侧放开纯省电项 跟 CalmArenaAcOnDesktop 对称 方向相反
        //   长期以来专注档 31 个旋钮插电和电池写的是同一套值 台式机分流只服务台式机
        //   拔了电还照着插电的口径写 最低性能状态 100 不停泊核心 一切省电全关 没人受益
        //
        // 只放开纯省电项 不碰会影响帧和输入的
        //   不动 ProcThrottleMax PerfEpp PerfBoostPol 这些负载中决定频率的
        //     实测 EPP 全量程扫描频率纹丝不动 而 PL1 才是笔记本上的真天花板
        //     另外这块归 Dynamic Boost 和 Intel DTT 管 抢方向盘只会更糟
        //   不动 PcieAspm 它会独立掐显卡带宽 是少数几个确实影响游戏的电源项
        //   不动 UsbSelSuspend 那条治的是键鼠空闲后第一下发飘
        // 放开的取值直接借智能档电池那一列 免得再引一套魔数
        // NVMe 延迟容忍随 DiskIdle 一起放开 AHCI LPM 不放 它的唤醒直接打到帧上 跟 PcieAspm 一路
        private static readonly Guid[] ArenaDcRelaxOnLaptop =
        {
            ProcThrottleMin, ProcThrottleMin1, CpMinCores, CpMinCores1,
            PerfDutyCycling, DiskIdle, WirelessPowerSave,
            NvmeLatTolPrimary, NvmeLatTolSecondary,
        };

        // 笔记本插电时也不该强制一个核都不停泊
        //   本机台架 12 逻辑核 3 线程稳态负载 同一台机器两轮独立测量
        //   不停泊最小核心% 100 50 20 5
        //   第一轮实际频率% 153.8 157.8 164.0 157.8
        //   第二轮实际频率% 153.8 162.1 156.3 163.6
        //   两次 100 都恰好 153.8 六个放开的臂全在 156.3~164.0 零重叠
        //   也就是说强制不停泊反而让干活的核跑得更慢 封装那份预算被摊到更多活跃核上
        //   跟专注档的意图正好相反
        // 台式机同样按这张表放开 功耗墙紧的小机箱和风冷高核数 CPU 上机理相同
        //   "台式机不受约束"没有数据 而唯一一组实测指向相反方向 没有依据就不写 100
        //   放开后写的是智能档那一列 与非对称缓存机器保留停泊是两条独立的路
        // 功耗那条没结论 两轮基线自己就漂了 6W 噪声大于效应 别拿它当依据
        private static readonly Guid[] ArenaAcRelaxOnLaptop =
        {
            CpMinCores, CpMinCores1,
        };

        internal static bool ArenaAcRelaxed(Guid setting)
        {
            for (int i = 0; i < ArenaAcRelaxOnLaptop.Length; i++)
                if (setting == ArenaAcRelaxOnLaptop[i]) return true;
            return false;
        }

        // 掌机档插电时按电池那一列的口径放开 借的就是 ArenaDcRelaxOnLaptop 那张表
        //   笔记本插电只放开核心停泊 是因为那份预算还够 CPU 和独显各拿各的
        //   掌机整机十几瓦 CPU 和集显抢的是同一份 最低性能状态锁 100 等于先把预算划给 CPU
        //   放开的仍然只是纯省电项 EPP PerfBoostPol ProcThrottleMax 照写激进值 不碰帧和输入
        private static uint ArenaAcFor(Knob k, uint ac, bool handheld, bool? autonomous)
        {
            ac = AutonomousArenaValue(k, ac, true, autonomous);
            return ResolveArenaAc(Native.HasSystemBattery(), handheld,
                ArenaAcRelaxed(k.Setting), ArenaDcRelaxed(k.Setting), ac, k.CalmAc);
        }

        // 纯决策 插电那一列最终写什么
        //   台式机和笔记本插电都只放开核心停泊 掌机插电按电池那张表放开纯省电项
        internal static uint ResolveArenaAc(bool hasBattery, bool handheld,
            bool relaxedOnAc, bool relaxedOnDc, uint arenaAc, uint calmAc)
        {
            if (!hasBattery) return relaxedOnAc ? calmAc : arenaAc;
            if (handheld && relaxedOnDc) return calmAc;
            return relaxedOnAc ? calmAc : arenaAc;
        }

        internal static bool ArenaDcRelaxed(Guid setting)
        {
            for (int i = 0; i < ArenaDcRelaxOnLaptop.Length; i++)
                if (setting == ArenaDcRelaxOnLaptop[i]) return true;
            return false;
        }

        private static uint ArenaDcFor(Knob k, uint dc, bool? autonomous)
        {
            dc = AutonomousArenaValue(k, dc, false, autonomous);
            if (!Native.HasSystemBattery()) return dc;   // 台式机根本用不到电池那一列
            return ArenaDcRelaxed(k.Setting) ? k.CalmDc : dc;
        }

#if PAVISE_SELFTEST
        internal static uint AutonomousMinimumForTest(bool secondary, bool ac, bool autonomous)
        {
            Knob k = secondary ? HybridKnobs[0] : CoreKnobs[0];
            return AutonomousArenaValue(k, ac ? k.ArenaAc : k.ArenaDc, ac, autonomous);
        }
#endif

        // 下架前写进去的 1 清一次 不看接管状态 清成功记个标记不再重复跑
        //   没有托管方案或方案里没这一项都算清完 拿不到写权限就留着标记下次再试
        internal static bool ClearLegacyIdleDisableOnce()
        {
            lock (lk)
            {
                if (!RestoreCpuIdle() || CpuIdleHasResidue) return false;
                return ClearLegacyIdleDisableOnceCore();
            }
        }

        private static bool ClearLegacyIdleDisableOnceCore()
        {
            if (Settings.Load(IdleDisableClearedKey, false)) return true;
            try
            {
                Guid g = ManagedPlanGuid();
                if (g == Guid.Empty || !SettingPresent(g, SubProcessor, IdleDisableSet))
                {
                    Settings.Save(IdleDisableClearedKey, true);
                    return true;
                }
                if (!WritePair(g, SubProcessor, IdleDisableSet, 0u, 0u)) return false;
                // 托管方案正是当前活动方案时 得重新激活一次内核才会重读这个值
                //   激活没成就别记标记 下次启动再清一遍 记死了就再也没有第二次机会
                Guid? cur = Current();
                if (cur.HasValue && cur.Value == g && !Set(g)) return false;
                Settings.Save(IdleDisableClearedKey, true);
                Logger.Log(Lang.T("log.powerplanschemes.47"));
                return true;
            }
            catch { return false; }
        }

        private static readonly Guid[] CalmArenaAcOnDesktop =
        {
            ProcThrottleMin, ProcThrottleMin1, CpMinCores, CpMinCores1,
            PerfEpp, PerfEpp1, PerfBoostPol, PerfIncPol, PerfIncTime,
            PerfIncThreshold, LatencyHintPerf, LatencyHintUnpark,
        };

        private static uint CalmAcFor(Knob k, uint ac, PowerPlanProfile profile)
        {
            if (Native.HasSystemBattery()) return ac;
            if (profile.PreserveCoreParking
                && (k.Setting == CpMinCores || k.Setting == CpMinCores1)) return ac;
            for (int i = 0; i < CalmArenaAcOnDesktop.Length; i++)
                if (k.Setting == CalmArenaAcOnDesktop[i]) return k.ArenaAc;
            return ac;
        }

        // 对局中把能效偏好临时抬高 让出共享功耗预算 只动托管方案的 AC 值 退场必还原
        //   EPP 才是 HWP 平台上真正控制功耗与响应折中的旋钮
        //   ProcThrottleMin 只决定"能不能降" 地板放开了 EPP 仍为 0 的话照样不会降
        //   混合架构上 E 核那份 PerfEpp1 必须一起动 否则只改到一个能效等级
        // 让路的写入来自采样线程 还原可能同时来自采样线程和对局退出那条路
        //   Stop 里是先 Join 再查 EppYielded 正常不会撞上 但 Join 超时就会
        //   撞上的后果是重复写或读到写了一半的快照 加把锁比推理便宜
        private static readonly object eppLk = new object();
        private static bool eppYielded;
        private static bool eppApplied;
        private static Guid eppSavedScheme;
        private static uint eppSavedAc, eppSavedAc1;
        private static bool eppSaved, eppSaved1;

        internal static bool EppYielded { get { lock (eppLk) return eppYielded; } }

        internal static bool TryYieldEpp(uint epp)
        {
            lock (eppLk)
            {
                // 待恢复不算让出成功 它的原始值也不能被
                // 第二次尝试的快照顶掉
                if (eppYielded) return eppApplied;
                try
                {
                    Guid g = EppManagedScheme();
                    if (g == Guid.Empty) return false;
                    uint savedAc, savedAc1;
                    if (!EppReadAc(g, false, out savedAc)) return false;
                    // 有些系统没有第二个能效等级 取不到原始值的设置项
                    // 一律不写
                    bool hasSecondary = EppReadAc(g, true, out savedAc1);
                    eppSavedScheme = g;
                    eppSavedAc = savedAc;
                    eppSavedAc1 = savedAc1;
                    eppSaved = true;
                    eppSaved1 = false;
                    eppApplied = false;
                    eppYielded = true;
                    bool ok = EppWriteVerified(g, false, epp);
                    if (ok && hasSecondary)
                    {
                        // 进原生代码之前先把这次要写的记下来
                        // 因为它可能在改了一半设置之后失败
                        eppSaved1 = true;
                        ok = EppWriteVerified(g, true, epp);
                    }
                    if (ok) ok = EppReapplyVerified(g);
                    if (!ok) { RestoreEpp(); return false; }
                    eppApplied = true;
                    return true;
                }
                catch
                {
                    if (eppYielded) RestoreEpp();
                    return false;
                }
            }
        }

        internal static bool RestoreEpp()
        {
            lock (eppLk)
            {
                if (!eppYielded) return true;
                eppApplied = false;
                // 托管方案的引用从写入到现在可能已经变了
                // 这些原始值只属于当时捕获的那套方案
                Guid g = eppSavedScheme;
                if (g == Guid.Empty || !eppSaved) return false;
                try
                {
                    bool ok = EppWriteVerified(g, false, eppSavedAc);
                    if (eppSaved1) ok = EppWriteVerified(g, true, eppSavedAc1) && ok;
                    if (!ok || !EppReapplyVerified(g)) return false;
                    ClearEppSnapshot();
                    return true;
                }
                catch { return false; }
            }
        }

        // 方案正在生效时改值要重新 SetActive 一次 否则内核不会重新读
        private static bool EppReapplyVerified(Guid scheme)
        {
            Guid? cur = EppCurrentScheme();
            if (!cur.HasValue || cur.Value == Guid.Empty) return false;
            if (cur.Value != scheme) return true;
            if (!EppSetActive(scheme)) return false;
            Guid? after = EppCurrentScheme();
            return after.HasValue && after.Value == scheme;
        }

        private static bool EppWriteVerified(Guid scheme, bool secondary, uint value)
        {
            uint actual;
            return EppWriteAc(scheme, secondary, value)
                && EppReadAc(scheme, secondary, out actual) && actual == value;
        }

        private static void ClearEppSnapshot()
        {
            eppYielded = eppApplied = eppSaved = eppSaved1 = false;
            eppSavedScheme = Guid.Empty;
            eppSavedAc = eppSavedAc1 = 0;
        }

        private static Guid EppManagedScheme()
        {
#if PAVISE_SELFTEST
            if (EppManagedSchemeForTest != null) return EppManagedSchemeForTest();
#endif
            return ManagedPlanGuid();
        }

        private static bool EppReadAc(Guid scheme, bool secondary, out uint value)
        {
#if PAVISE_SELFTEST
            if (EppReadAcForTest != null) return EppReadAcForTest(scheme, secondary, out value);
#endif
            return ReadAc(scheme, SubProcessor, secondary ? PerfEpp1 : PerfEpp, out value);
        }

        private static bool EppWriteAc(Guid scheme, bool secondary, uint value)
        {
#if PAVISE_SELFTEST
            if (EppWriteAcForTest != null) return EppWriteAcForTest(scheme, secondary, value);
#endif
            return WriteAc(scheme, SubProcessor, secondary ? PerfEpp1 : PerfEpp, value);
        }

        private static Guid? EppCurrentScheme()
        {
#if PAVISE_SELFTEST
            if (EppCurrentSchemeForTest != null) return EppCurrentSchemeForTest();
#endif
            return Current();
        }

        private static bool EppSetActive(Guid scheme)
        {
#if PAVISE_SELFTEST
            if (EppSetActiveForTest != null) return EppSetActiveForTest(scheme);
#endif
            return Set(scheme);
        }

#if PAVISE_SELFTEST
        internal delegate bool EppReadAcProbe(Guid scheme, bool secondary, out uint value);
        internal static Func<Guid> EppManagedSchemeForTest;
        internal static EppReadAcProbe EppReadAcForTest;
        internal static Func<Guid, bool, uint, bool> EppWriteAcForTest;
        internal static Func<Guid?> EppCurrentSchemeForTest;
        internal static Func<Guid, bool> EppSetActiveForTest;

        internal static void ResetEppForTest()
        {
            lock (eppLk)
            {
                ClearEppSnapshot();
                EppManagedSchemeForTest = null;
                EppReadAcForTest = null;
                EppWriteAcForTest = null;
                EppCurrentSchemeForTest = null;
                EppSetActiveForTest = null;
            }
        }
#endif

        internal static bool ReadAc(Guid scheme, Guid sub, Guid setting, out uint value)
        {
            Guid sb = sub, st = setting;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, out value) == 0;
        }

        internal static bool ReadDc(Guid scheme, Guid sub, Guid setting, out uint value)
        {
            Guid sb = sub, st = setting;
            return PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, out value) == 0;
        }

        internal static bool WriteDc(Guid scheme, Guid sub, Guid setting, uint value)
        {
            Guid sb = sub, st = setting;
            return PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, value) == 0;
        }

        internal static bool WriteAc(Guid scheme, Guid sub, Guid setting, uint value)
        {
            Guid sb = sub, st = setting;
            return PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref st, value) == 0;
        }

        internal static bool ManagedPlanIsActive
        {
            get
            {
                try
                {
                    Guid g = ManagedPlanGuid();
                    if (g == Guid.Empty) return false;
                    Guid? cur = Current();
                    return cur.HasValue && cur.Value == g;
                }
                catch { return false; }
            }
        }

        private static bool SettingPresent(Guid scheme, Guid sub, Guid setting)
        {
            Guid sb = sub, set = setting;
            uint value;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, out value) == 0;
        }

        private static uint Clamp(Guid sub, Guid setting, uint v)
        {
            Guid sb = sub, set = setting;
            uint lo, hi;
            if (PowerReadValueMin(IntPtr.Zero, ref sb, ref set, out lo) != 0) return v;
            sb = sub; set = setting;
            if (PowerReadValueMax(IntPtr.Zero, ref sb, ref set, out hi) != 0) return v;
            if (lo > hi) return v;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc)
        {
            uint code;
            return WritePair(scheme, sub, setting, ac, dc, out code);
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc, out uint code)
        {
            Guid sb = sub, set = setting;
            code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, ac);
            if (code == ErrorInvalidParameter)
            {
                uint fixedAc = Clamp(sub, setting, ac);
                if (fixedAc != ac)
                {
                    sb = sub; set = setting;
                    code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, fixedAc);
                }
            }
            if (code != 0) return false;

            sb = sub; set = setting;
            code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, dc);
            if (code == ErrorInvalidParameter)
            {
                uint fixedDc = Clamp(sub, setting, dc);
                if (fixedDc != dc)
                {
                    sb = sub; set = setting;
                    code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, fixedDc);
                }
            }
            return code == 0;
        }

        private const uint ErrorInvalidParameter = 87;

        // 卸载时按名字清掉历史版本留下的方案 收据里那份 RemoveManagedPlan 已经删过 这里只兜旧账
        internal static bool IsManagedPlanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string prefix in new[] { "PG ", "由软件调度", "Scheduled by Pavise", "Aegis", "Pavise" })
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static int DeleteManagedPlansByName()
        {
            int deleted = 0;
            lock (lk)
            {
                Guid? cur = Current();
                foreach (Guid g in EnumerateSchemes())
                {
                    if (!IsManagedPlanName(ReadName(g))) continue;
                    if (cur.HasValue && cur.Value == g)
                    {
                        if (!SwitchAwayFrom(g)) continue;
                        cur = Current();
                    }
                    Guid t = g;
                    if (PowerDeleteScheme(IntPtr.Zero, ref t) == 0) deleted++;
                }
            }
            return deleted;
        }

        private static Guid? Current()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return null;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            catch { return null; }
            finally { LocalFree(p); }
        }

        private static bool Set(Guid g)
        {
            if (PowerSetActiveScheme(IntPtr.Zero, ref g) != 0) return false;
            Guid? actual = Current();
            return actual.HasValue && actual.Value == g;
        }

        private static bool SchemeUsable(Guid g)
        {
            uint size = 0;
            uint r = PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            return r == 0 || r == 234;
        }

        private static string ReadName(Guid g)
        {
            try
            {
                uint size = 0;
                uint probe = PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if ((probe != 0 && probe != 234) || size == 0 || size > 4096) return "";
                byte[] buf = new byte[size];
                if (PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "";
                return Encoding.Unicode.GetString(buf).TrimEnd('\0');
            }
            catch { return ""; }
        }

        private static bool WriteName(Guid g, string title, string note)
        {
            try
            {
                byte[] nameBuf = Encoding.Unicode.GetBytes(title + "\0");
                Guid s = g;
                if (PowerWriteFriendlyName(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, nameBuf, (uint)nameBuf.Length) != 0)
                    return false;
                byte[] noteBuf = Encoding.Unicode.GetBytes(note + "\0");
                s = g;
                PowerWriteDescription(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, noteBuf, (uint)noteBuf.Length);
                return true;
            }
            catch { return false; }
        }

        private static List<Guid> EnumerateSchemes()
        {
            var list = new List<Guid>();
            try
            {
                for (uint i = 0; i < 128; i++)
                {
                    uint size = 16;
                    byte[] buf = new byte[16];
                    if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buf, ref size) != 0) break;
                    list.Add(new Guid(buf));
                }
            }
            catch { }
            return list;
        }

        internal static void SyncDisplayFeel(Guid src, Guid dst)
        {
            Guid[] settings = { VideoBrightness, VideoDimBrightness, VideoAdaptive };
            foreach (Guid setting in settings)
            {
                Guid s = src, d = dst, sub = SubVideo, st = setting;
                uint value;
                if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0)
                    PowerWriteACValueIndex(IntPtr.Zero, ref d, ref sub, ref st, value);
                if (PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0)
                    PowerWriteDCValueIndex(IntPtr.Zero, ref d, ref sub, ref st, value);
            }
        }

        private static bool Duplicate(Guid src, out Guid created)
        {
            created = Guid.Empty;
            IntPtr dest = IntPtr.Zero;
            Guid s = src;
            uint r = PowerDuplicateScheme(IntPtr.Zero, ref s, ref dest);
            if (r != 0 || dest == IntPtr.Zero) return false;
            try { created = (Guid)Marshal.PtrToStructure(dest, typeof(Guid)); }
            catch { return false; }
            finally { LocalFree(dest); }
            return created != Guid.Empty;
        }

#if PAVISE_SELFTEST
        internal static Guid SelfTestCurrent()
        {
            Guid? cur = Current();
            return cur.HasValue ? cur.Value : Guid.Empty;
        }

        internal static bool SelfTestSetActive(Guid g) { return Set(g); }

        internal static bool SelfTestDuplicate(out Guid created) { return Duplicate(HighPerf, out created); }

        internal static bool SelfTestEppRange(Guid scheme, out uint lo, out uint hi)
        {
            Guid sb = SubProcessor, st = PerfEpp;
            lo = 0; hi = 0;
            return PowerReadValueMin(IntPtr.Zero, ref sb, ref st, out lo) == 0
                && PowerReadValueMax(IntPtr.Zero, ref sb, ref st, out hi) == 0;
        }

        internal static Guid SelfTestProcSub { get { return SubProcessor; } }
        internal static Guid SelfTestMinState { get { return ProcThrottleMin; } }
        internal static Guid SelfTestParkMin { get { return CpMinCores; } }
        internal static Guid SelfTestMaxState { get { return ProcThrottleMax; } }
        internal static Guid SelfTestBoostPol { get { return PerfBoostPol; } }
        internal static Guid SelfTestPcieAspm { get { return PcieAspm; } }
        internal static Guid SelfTestUsbSuspend { get { return UsbSelSuspend; } }
        internal static Guid SelfTestDutyCycling { get { return PerfDutyCycling; } }
        internal static Guid SelfTestWirelessSave { get { return WirelessPowerSave; } }
        internal static Guid SelfTestEppSetting { get { return PerfEpp; } }

        internal static bool SelfTestReadBrightnessAc(Guid scheme, out uint value)
        {
            Guid s = scheme, sub = SubVideo, st = VideoBrightness;
            return PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0;
        }

        internal static bool SelfTestWriteBrightnessAc(Guid scheme, uint value)
        {
            Guid s = scheme, sub = SubVideo, st = VideoBrightness;
            return PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, value) == 0;
        }

        internal static int SelfTestSchemeCount() { return EnumerateSchemes().Count; }

        internal static bool SelfTestDelete(Guid scheme)
        {
            Guid t = scheme;
            return PowerDeleteScheme(IntPtr.Zero, ref t) == 0;
        }

        internal static string SelfTestName(Guid scheme) { return ReadName(scheme); }

        internal static bool SelfTestWriteName(Guid scheme, string title) { return WriteName(scheme, title, "selftest"); }

        internal static bool SelfTestReadProcMinAc(Guid scheme, out uint value)
        {
            Guid s = scheme;
            Guid sub = new Guid("54533251-82be-4824-96c1-47b60b740d00");
            Guid st = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
            return PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref st, out value) == 0;
        }
#endif
    }
}
