// 文件用途 中断观测状态和台账读取诊断的纯测试 不建窗口 不起 ETW
using System;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestIrqObservationStatusReasons()
        {
            bool warning;
            Eq(Lang.T("irq.capture.waiting"), IrqPageStatus.ResolveData(
                0, 0, 0, 0, "", false, "", "", "", out warning));
            Eq(false, warning);

            // 开关打开不等于正在采样 真正的采集状态不能被请先打一局盖掉
            string capturing = Lang.T("irq.capture.system");
            Eq(capturing, IrqPageStatus.ResolveData(
                0, 0, 0, 0, capturing, false, "", "", "", out warning));
            Eq(false, warning);

            string lost = Lang.F("irq.capture.lost", "events=12, buffers=1");
            Eq(lost, IrqPageStatus.ResolveData(
                0, 0, 0, 0, lost, true, "", "", "", out warning));
            Eq(true, warning);
            Eq(lost, IrqPageStatus.ResolveData(
                3, 3, 3, 1, lost, true, "", "", "", out warning));
            Eq(true, warning);

            string saveFailed = Lang.T("irq.capture.savefailed");
            Eq(saveFailed, IrqPageStatus.ResolveData(
                0, 0, 0, 0, saveFailed, true, "", "", "", out warning));
            Eq(true, warning);

            string readFailed = Lang.T("irq.ledger.readfailed");
            Eq(readFailed, IrqPageStatus.ResolveData(
                0, 0, 0, 0, capturing, false, readFailed, "", "", out warning));
            Eq(true, warning);
            string devicesFailed = Lang.T("irq.state.devicesfailed");
            Eq(devicesFailed, IrqPageStatus.ResolveData(
                1, 1, 1, 0, capturing, false, "", devicesFailed, "", out warning));
            Eq(true, warning);

            // 第一局哪怕一条 Worth 建议都没有 也得把有效局数显示出来 不能给空白页
            Eq(Lang.F("irq.state.fewsessions", 1, IrqSessionLedger.MinSessionsForVerdict),
                IrqPageStatus.ResolveData(1, 1, 1, 1,
                    Lang.F("irq.capture.saved.system", 1020), false, "", "", "", out warning));
            Eq(false, warning);

            string previousBoot = IrqPageStatus.ExclusionText(IrqSessionExclusion.DifferentBoot);
            Eq(Lang.F("irq.state.unusable", 2, previousBoot), IrqPageStatus.ResolveData(
                2, 0, 0, 0, "", false, "", "", previousBoot, out warning));
            Eq(true, warning);
            Eq(Lang.F("irq.state.unusable", 1, Lang.T("irq.record.unavailable")),
                IrqPageStatus.ResolveData(1, 0, 0, 0, "", false, "", "", "", out warning));
            Eq(true, warning);

            Eq(Lang.T("irq.state.nomapped"), IrqPageStatus.ResolveData(
                3, 3, 3, 0, "", false, "", "", "", out warning));
            Eq(false, warning);
            Eq(Lang.F("irq.state.done", 2, 3), IrqPageStatus.ResolveData(
                3, 3, 3, 2, "", false, "", "", "", out warning));
            Eq(false, warning);

            string shortData = Lang.F("irq.state.rawshort", IrqSessionRecord.MinUsableSeconds,
                IrqSessionLedger.MinSessionsForVerdict);
            Eq(shortData, IrqPageStatus.ResolveData(
                1, 1, 0, 1, "", false, "", "", "", out warning));
            Eq(false, warning);
            Eq(Lang.F("irq.state.counts", 1, 0) + " · " + shortData,
                IrqPageStatus.CountedText(shortData, 1, 0, ""));
            Eq(Lang.F("irq.state.counts", 4, 1), IrqPageStatus.CountedText("", 4, 1, ""));
            Eq(readFailed, IrqPageStatus.CountedText(readFailed, 0, 0, readFailed));

            string noDuration = IrqPageStatus.ExclusionText(IrqSessionExclusion.NoDuration);
            string zeroSeconds = IrqPageStatus.ResolveData(
                1, 0, 0, 0, "", false, "", "", noDuration, out warning);
            Eq(Lang.F("irq.state.unusable", 1, noDuration), zeroSeconds);
            Eq(true, warning);
            Eq(Lang.F("irq.state.counts", 1, 0) + " · " + zeroSeconds,
                IrqPageStatus.CountedText(zeroSeconds, 1, 0, ""));

            foreach (IrqSessionExclusion issue in new[]
            {
                IrqSessionExclusion.TooShort, IrqSessionExclusion.MissingSystemMask,
                IrqSessionExclusion.LostEvents, IrqSessionExclusion.NoDrivers,
                IrqSessionExclusion.DifferentBoot, IrqSessionExclusion.DifferentTopology,
                IrqSessionExclusion.NoDuration
            })
            {
                string explanation = IrqPageStatus.ExclusionText(issue);
                Eq(true, !string.IsNullOrEmpty(explanation));
                Eq(false, explanation == Lang.T("irq.state.nosessions"));
            }
        }

        private static IrqSessionRecord IrqStatusRecord()
        {
            var rec = new IrqSessionRecord
            {
                StartUtcTicks = 123456789,
                DurationSeconds = 120,
                GameName = "状态测试",
                BootStamp = "1000000",
                TopologyStamp = "status-test-topology",
                GameMask = 0,
                SystemMask = 0xFUL
            };
            rec.Drivers.Add(new IrqDriverRecord
            {
                Driver = "status-test.sys", DriverVersion = "1.0",
                Dpc = 10, DpcTotalNs = 20000, DpcMaxNs = 5000, CpuMask = 0x1UL
            });
            return rec;
        }

        private static void TestIrqSessionExclusionReasons()
        {
            const string boot = "1000000";
            const string topology = "status-test-topology";
            IrqSessionRecord rec = IrqStatusRecord();
            // 系统观测 GameMask=0 可以展示原始数据 但挪核建议的门槛不因此放低
            Eq(IrqSessionExclusion.None, rec.VerdictExclusion(boot, topology));
            rec.GameMask = rec.SystemMask;
            Eq(IrqSessionExclusion.None, rec.VerdictExclusion(boot, topology));

            rec.DurationSeconds = 59;
            Eq(IrqSessionExclusion.TooShort, rec.VerdictExclusion(boot, topology));
            rec.DurationSeconds = 60;
            Eq(IrqSessionExclusion.None, rec.VerdictExclusion(boot, topology));
            rec.SystemMask = 0;
            Eq(IrqSessionExclusion.MissingSystemMask, rec.VerdictExclusion(boot, topology));
            rec.SystemMask = 0xFUL;
            rec.EventsLost = 1;
            Eq(IrqSessionExclusion.LostEvents, rec.VerdictExclusion(boot, topology));
            rec.EventsLost = 0;

            Eq(IrqSessionExclusion.DifferentBoot, rec.VerdictExclusion("1000010", topology));
            Eq(IrqSessionExclusion.None, rec.VerdictExclusion("1000005", topology));
            Eq(IrqSessionExclusion.DifferentTopology, rec.VerdictExclusion(boot, "different-topology"));
            rec.TopologyStamp = "";
            Eq(IrqSessionExclusion.None, rec.VerdictExclusion(boot, "different-topology"));
            rec.Drivers.Clear();
            Eq(IrqSessionExclusion.NoDrivers, rec.VerdictExclusion(boot, topology));
        }

        private static void TestIrqLedgerReadStatus(string dir)
        {
            // 只写专用的测试子目录 绝不绑定或者清理用户真实的 IRQ 历史
            string work = Path.Combine(Path.GetFullPath(dir), "irq-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string path = Path.Combine(work, IrqSessionLedger.FileName);
            try
            {
                string issue;
                IrqSessionLedger.Bind(null);
                Eq(0, IrqSessionLedger.Load(out issue).Count);
                Eq(Lang.T("irq.ledger.unbound"), issue);
                Eq(false, IrqSessionLedger.Append(IrqStatusRecord()));

                IrqSessionLedger.Bind(work);
                Eq(0, IrqSessionLedger.Load(out issue).Count);
                Eq("", issue);
                Eq(true, IrqSessionLedger.Append(IrqStatusRecord()));
                Eq(1, IrqSessionLedger.Load(out issue).Count);
                Eq("", issue);
                Eq(1, IrqSessionLedger.Load().Count);

                string intact = File.ReadAllText(path);
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Eq(0, IrqSessionLedger.Load(out issue).Count);
                    Eq(Lang.T("irq.ledger.readfailed"), issue);
                    Eq(false, IrqSessionLedger.Append(IrqStatusRecord()));
                }
                Eq(intact, File.ReadAllText(path));
                Eq(1, IrqSessionLedger.Load(out issue).Count);
                Eq("", issue);

                string malformed = intact + "S|broken\r\n";
                File.WriteAllText(path, malformed, Encoding.UTF8);
                Eq(0, IrqSessionLedger.Load(out issue).Count);
                Eq(Lang.T("irq.ledger.corrupt"), issue);
                Eq(0, IrqSessionLedger.Load().Count);
                Eq(false, IrqSessionLedger.Append(IrqStatusRecord()));
                Eq(malformed, File.ReadAllText(path));

                const string unknown = "PAVISE_IRQ_SESSIONS_V9\r\nS|future\r\n";
                File.WriteAllText(path, unknown, Encoding.UTF8);
                Eq(0, IrqSessionLedger.Load(out issue).Count);
                Eq(Lang.T("irq.ledger.format"), issue);
                Eq(false, IrqSessionLedger.Append(IrqStatusRecord()));
                Eq(unknown, File.ReadAllText(path));

                // 文件被删掉之后 旧的 readOnlyFormat 不能一直挡着同目录的新观测
                File.Delete(path);
                Eq(true, IrqSessionLedger.Append(IrqStatusRecord()));
                Eq(1, IrqSessionLedger.Load(out issue).Count);
                Eq("", issue);

                File.WriteAllText(path, "", Encoding.UTF8);
                Eq(0, IrqSessionLedger.Load(out issue).Count);
                Eq(Lang.T("irq.ledger.corrupt"), issue);
                Eq(false, IrqSessionLedger.Append(IrqStatusRecord()));

                File.WriteAllBytes(path, new byte[] { 0xFF });
                Eq(0, IrqSessionLedger.Load(out issue).Count);
                Eq(Lang.T("irq.ledger.corrupt"), issue);
                Eq(false, IrqSessionLedger.Append(IrqStatusRecord()));
                byte[] preserved = File.ReadAllBytes(path);
                Eq(1, preserved.Length);
                Eq((byte)0xFF, preserved[0]);

                File.Delete(path);
                Eq(0, IrqSessionLedger.Load(out issue).Count);
                Eq("", issue);
                Eq(true, IrqSessionLedger.Append(IrqStatusRecord()));
                Eq(1, IrqSessionLedger.Load(out issue).Count);
                Eq("", issue);
                Eq(0, Directory.GetFiles(work, IrqSessionLedger.FileName + ".*.tmp").Length);
            }
            finally
            {
                IrqSessionLedger.Bind(dir);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { Directory.Delete(work); } catch { }
            }
        }
    }
}
