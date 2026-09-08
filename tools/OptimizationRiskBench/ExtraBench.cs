// 文件用途 走生产的音频和存储路径 资源严格自有 不声称任何游戏 FPS 收益
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class OptimizationRiskBench
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private static void StoreLockProbe()
        {
            string root = Path.Combine(Path.GetTempPath(), "Pavise-OwnedStore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, GameProfileStore.FileName);
            try
            {
                string executable = Process.GetCurrentProcess().MainModule.FileName;
                GameProfile profile = GameProfileStore.NewProfile("owned-lock-probe", Path.GetDirectoryName(executable), executable);
                var store = new GameProfileStore(root);
                Require(store.Save(new[] { profile }), "seed owned profile file");
                string before = File.ReadAllText(file);
                GameProfile changed = profile.Clone(); changed.Name = "owned-lock-probe-changed";
                bool deniedSave;
                // 故意攥着自己的读句柄 不给删除共享 这是一个确定性的反例
                // 不能拿来证明现实里是哪个进程锁着文件
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    deniedSave = store.Save(new[] { changed });
                bool intactAfterFailure = File.ReadAllText(file) == before;
                bool latched = store.SaveFailed;
                bool sameInstanceRetry = store.Save(new[] { changed });
                bool freshInstanceRetry = new GameProfileStore(root).Save(new[] { changed });
                Require(!deniedSave && intactAfterFailure && !latched && sameInstanceRetry && freshInstanceRetry,
                    "store-lock recovery regression failed");
                Console.WriteLine("save_while_owned_lock,original_intact,failure_latched,retry_after_unlock_same_instance,retry_after_unlock_fresh_instance");
                Console.WriteLine(deniedSave + "," + intactAfterFailure + "," + latched + "," + sameInstanceRetry + "," + freshInstanceRetry);
            }
            finally
            {
                Require(Path.GetFullPath(file).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "owned store cleanup boundary");
                if (File.Exists(file)) File.Delete(file);
                Directory.Delete(root, false);
            }
        }

        private static object AudioCall(object target, string method, object[] arguments)
        {
            Type contract = typeof(AudioLowLatency).GetNestedType("IAudioClient3", BindingFlags.NonPublic);
            return contract.GetMethod(method).Invoke(target, arguments);
        }
        private static uint CurrentAudioPeriod(object client, out int rate, out int status)
        {
            object[] values = { IntPtr.Zero, (uint)0 };
            status = (int)AudioCall(client, "GetCurrentSharedModeEnginePeriod", values);
            IntPtr format = (IntPtr)values[0]; rate = 0;
            try { if (status == 0 && format != IntPtr.Zero) rate = Marshal.ReadInt32(format, 4); }
            finally { if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format); }
            return (uint)values[1];
        }
        private static void AudioProbe(bool enable)
        {
            object device = null, probe = null;
            IntPtr format = IntPtr.Zero;
            int rateBefore = 0, rateDuring = 0, rateAfter = 0, hrBefore = -1, hrDuring = -1, hrAfter = -1;
            uint before = 0, during = 0, after = 0, defaultFrames = 0, minFrames = 0;
            bool applied = false, active = false, restored = false;
            double cpu = double.NaN;
            try
            {
                object[] endpointArgs = { null };
                device = typeof(AudioLowLatency).GetMethod("DefaultRenderDevice", PrivateStatic).Invoke(null, endpointArgs);
                if (device == null) { Console.WriteLine("status\nSKIP_no_default_render_device"); return; }
                Type deviceType = typeof(AudioLowLatency).GetNestedType("IMMDevice", BindingFlags.NonPublic);
                Type clientType = typeof(AudioLowLatency).GetNestedType("IAudioClient3", BindingFlags.NonPublic);
                object[] activation = { clientType.GUID, 0x17, IntPtr.Zero, null };
                int activationStatus = (int)deviceType.GetMethod("Activate").Invoke(device, activation);
                if (activationStatus != 0) { Console.WriteLine("status,hresult\nSKIP_interface_unavailable," + activationStatus); return; }
                probe = activation[3];
                object[] mix = { IntPtr.Zero };
                Require((int)AudioCall(probe, "GetMixFormat", mix) == 0, "audio mix format");
                format = (IntPtr)mix[0];
                object[] periods = { format, (uint)0, (uint)0, (uint)0, (uint)0 };
                Require((int)AudioCall(probe, "GetSharedModeEnginePeriod", periods) == 0, "supported audio periods");
                defaultFrames = (uint)periods[1]; minFrames = (uint)periods[3];
                before = CurrentAudioPeriod(probe, out rateBefore, out hrBefore);
                if (enable) applied = AudioLowLatency.Activate();
                active = (bool)typeof(AudioLowLatency).GetField("active", PrivateStatic).GetValue(null);
                Thread.Sleep(300);
                during = CurrentAudioPeriod(probe, out rateDuring, out hrDuring);
                long idle0, kernel0, user0, idle1, kernel1, user1;
                Require(GetSystemTimes(out idle0, out kernel0, out user0), "audio CPU before");
                Thread.Sleep(3000);
                Require(GetSystemTimes(out idle1, out kernel1, out user1), "audio CPU after");
                long total = kernel1 - kernel0 + user1 - user0;
                cpu = 100.0 * (total - (idle1 - idle0)) / total;
            }
            finally
            {
                // 只释放本进程的流 不去重置别的音频客户端和服务
                restored = AudioLowLatency.Restore()
                    && !(bool)typeof(AudioLowLatency).GetField("active", PrivateStatic).GetValue(null);
                Thread.Sleep(300);
                if (probe != null)
                {
                    try { after = CurrentAudioPeriod(probe, out rateAfter, out hrAfter); }
                    finally { Marshal.ReleaseComObject(probe); }
                }
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                if (device != null) Marshal.ReleaseComObject(device);
            }
            Require(restored, "owned audio stream released");
            Console.WriteLine("arm,default_frames,min_frames,before_frames,during_frames,after_frames,before_rate,during_rate,after_rate,before_hr,during_hr,after_hr,activate_return,production_active,system_cpu_percent,stream_released");
            Console.WriteLine(string.Join(",", new[] { enable ? "active" : "baseline", defaultFrames.ToString(), minFrames.ToString(),
                before.ToString(), during.ToString(), after.ToString(), rateBefore.ToString(), rateDuring.ToString(), rateAfter.ToString(),
                hrBefore.ToString(), hrDuring.ToString(), hrAfter.ToString(), applied.ToString(), active.ToString(), cpu.ToString("F4", Inv), restored.ToString() }));
        }

    }
}
