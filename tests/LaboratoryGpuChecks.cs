#if PAVISE_LAB_GPU_CHECKS
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class LaboratoryGpuChecks
    {
        private static readonly Dictionary<string, string> receipts = new Dictionary<string, string>();
        private static LaboratoryGpu.Sample live;
        private static DateTime now;
        private static bool readable, denySave, denyClear, failWrite, failReset, rejectWithoutChange;
        private static int writes, probes, resets, assertions;
        private static void Check(bool ok, string message)
        { assertions++; if (!ok) throw new InvalidOperationException(message); }
        private static LaboratoryGpu.Sample Copy(LaboratoryGpu.Sample s)
        { return new LaboratoryGpu.Sample { Vendor = s.Vendor, Id = s.Id, Current = s.Current, Maximum = s.Maximum, Minimum = s.Minimum, Context = s.Context }; }
        private static string Stored(string key) { string s; return receipts.TryGetValue(key, out s) ? s : ""; }
        private static bool Probe(string key, LaboratoryGpu.Sample wanted, out LaboratoryGpu.Sample result, out string reason)
        {
            probes++; result = null; reason = "mock unsupported";
            if (!readable || wanted != null && (wanted.Id != live.Id || wanted.Vendor != live.Vendor)) return false;
            result = Copy(live); return true;
        }
        private static void Fresh(string vendor, bool clocks)
        {
            receipts.Clear(); writes = probes = resets = 0;
            readable = true; denySave = denyClear = failWrite = failReset = rejectWithoutChange = false;
            now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
            live = new LaboratoryGpu.Sample { Vendor = vendor, Id = vendor == "nv" ? "GPU-fixture-uuid" : @"PCI\VEN_1002&DEV_FFFF\instance", Current = clocks ? (vendor == "nv" ? -1 : 500) : 300000, Maximum = clocks ? 1900 : 350000, Context = clocks && vendor == "amd" ? 1900 : 0 };
            LaboratoryGpu.ResetForTest();
            LaboratoryGpu.NowForTest = delegate { return now; };
            LaboratoryGpu.LoadForTest = Stored;
            LaboratoryGpu.SaveForTest = delegate(string key, string value)
            {
                if (denySave || denyClear && value.Length == 0) return false;
                receipts[key] = value; return true;
            };
            LaboratoryGpu.ProbeForTest = Probe;
            LaboratoryGpu.WriteForTest = delegate(string key, LaboratoryGpu.Sample device, long value)
            {
                Check(Stored(key == LaboratoryGpu.PowerKey ? LaboratoryGpu.PowerReceipt : LaboratoryGpu.ClockReceipt).Length > 0, "native write preceded durable receipt");
                Check(device.Id == live.Id, "write selected a different GPU");
                writes++;
                if (rejectWithoutChange) { device.RejectedWithoutChange = true; return false; }
                live.Current = value; return !failWrite;
            };
            LaboratoryGpu.ResetClockForTest = delegate(LaboratoryGpu.Sample device)
            {
                Check(Stored(LaboratoryGpu.ClockReceipt).Length > 0, "reset lost recovery debt");
                Check(device.Id == live.Id, "clock reset targeted a different GPU");
                resets++; return !failReset;
            };
        }
        private static void PowerLifecycle()
        {
            Fresh("nv", false);
            LaboratoryGpu.Update(false, false);
            Check(probes == 0 && writes == 0, "disabled feature scanned or wrote GPU");
            LaboratoryGpu.Update(true, false);
            Check(live.Current == live.Maximum && writes == 1, "power cap did not apply");
            int before = probes;
            LaboratoryGpu.Update(true, false);
            Check(probes == before && writes == 1, "cached update rescanned or rewrote");
            Check(LaboratoryGpu.Restore(), "power restore failed");
            Check(live.Current == 300000 && Stored(LaboratoryGpu.PowerReceipt) == "", "original cap or receipt not restored");
        }
        private static void ExternalOwner()
        {
            Fresh("nv", false); LaboratoryGpu.Update(true, false);
            live.Current = 325000; now = now.AddSeconds(16);
            LaboratoryGpu.Update(true, false);
            Check(writes == 1 && Stored(LaboratoryGpu.PowerReceipt) == "", "external cap overwritten");
            now = now.AddMinutes(1); LaboratoryGpu.Update(true, false);
            Check(writes == 1, "yielded feature reacquired during session");
            Check(LaboratoryGpu.Restore() && live.Current == 325000, "restore changed external setting");
            Fresh("amd", true); LaboratoryGpu.Update(false, true);
            live.Context = live.Maximum = 2000;
            Check(LaboratoryGpu.Restore() && writes == 1, "AMD external maximum was ignored by restore");
        }
        private static void ReceiptFailures()
        {
            Fresh("nv", false); denySave = true; LaboratoryGpu.Update(true, false);
            Check(writes == 0 && live.Current == 300000, "failed receipt save still wrote");
            Fresh("nv", false); LaboratoryGpu.Update(true, false); readable = false;
            Check(!LaboratoryGpu.Restore() && Stored(LaboratoryGpu.PowerReceipt).Length > 0, "unreadable device discarded debt");
            readable = true; denyClear = true;
            Check(!LaboratoryGpu.Restore() && live.Current == 300000 && Stored(LaboratoryGpu.PowerReceipt).Length > 0, "clear failure discarded debt");
            denyClear = false; int before = writes;
            Check(LaboratoryGpu.Restore() && writes == before, "cleanup retried original write unnecessarily");
            Fresh("nv", false); failWrite = true; LaboratoryGpu.Update(true, false);
            Check(Stored(LaboratoryGpu.PowerReceipt).Length > 0, "restore failure forgot receipt");
            failWrite = false; Check(LaboratoryGpu.Restore(), "retry did not recover failed write");
            Fresh("nv", false); receipts[LaboratoryGpu.PowerReceipt] = "damaged";
            Check(!LaboratoryGpu.Restore() && writes == 0 && Stored(LaboratoryGpu.PowerReceipt) == "damaged", "malformed receipt was applied or discarded");
            Fresh("nv", false); LaboratoryGpu.Update(true, false); live.Id = "GPU-replacement";
            Check(!LaboratoryGpu.Restore() && writes == 1, "replacement GPU was written");
            Fresh("nv", true); rejectWithoutChange = true; LaboratoryGpu.Update(false, true);
            Check(resets == 0 && Stored(LaboratoryGpu.ClockReceipt) == "", "known unsupported clock request reset external state or left false debt");
            Fresh("nv", true); LaboratoryGpu.Update(false, true);
            receipts[LaboratoryGpu.PowerReceipt] = Stored(LaboratoryGpu.ClockReceipt);
            Check(!LaboratoryGpu.Restore() && Stored(LaboratoryGpu.PowerReceipt).Length > 0, "clock receipt accepted as power recovery instruction");
        }
        private static void ClockLifecycle()
        {
            Fresh("nv", true); LaboratoryGpu.Update(false, true);
            Check(writes == 1 && probes == 1, "NVIDIA clock request missing or invented readback");
            Check(LaboratoryGpu.State(LaboratoryGpu.ClockKey).Contains("1900"), "accepted clock request lacks target");
            now = now.AddMinutes(1); LaboratoryGpu.Update(false, true);
            Check(writes == 1 && probes == 1, "NVIDIA clock was continuously reapplied");
            failReset = true;
            Check(!LaboratoryGpu.Restore() && Stored(LaboratoryGpu.ClockReceipt).Length > 0, "failed reset forgot debt");
            failReset = false;
            Check(LaboratoryGpu.Restore() && Stored(LaboratoryGpu.ClockReceipt) == "" && resets == 2, "NVIDIA reset recovery failed");
            Fresh("amd", true); LaboratoryGpu.Update(false, true);
            Check(live.Current == 1900 && probes == 2, "AMD maximum floor not verified");
            Check(LaboratoryGpu.Restore() && live.Current == 500, "AMD minimum original not restored");
            Fresh("nv", true); LaboratoryGpu.Update(false, true); failReset = true;
            LaboratoryGpu.Update(false, false); int before = resets;
            LaboratoryGpu.Update(false, false);
            Check(resets == before, "disabled recovery ignored failure backoff");
            now = now.AddSeconds(31); LaboratoryGpu.Update(false, false);
            Check(resets == before + 1, "disabled recovery never retried");
        }
        [STAThread]
        private static int Main()
        {
            try
            {
                PowerLifecycle(); ExternalOwner(); ReceiptFailures(); ClockLifecycle();
                Console.WriteLine("PASS laboratory GPU assertions=" + assertions + " backend=mocked settings=in-memory application_started=false native_writes=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
