// 文件用途 纯决策逻辑检查 不激活 COM 不碰音频设备 不写任何东西
// 音频低延迟回归 只验证收益判定与周期换算 不碰真实音频设备
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int audioLatChecks;

        internal static int RunAudioLowLatencyRegressionTests()
        {
            Action[] tests =
            {
                AudioLatWorthApplyingRequiresSmallerPeriod,
                AudioLatPeriodTextFormatsMilliseconds,
                AudioLatBluetoothEndpointsAreSkipped
            };
            audioLatChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS audio-low-latency assertions=" + audioLatChecks
                + " com=untouched devices=untouched windows_shown=false");
            return tests.Length;
        }

        private static void AudioLatCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Audio low latency regression: " + message);
            audioLatChecks++;
        }

        private static void AudioLatBluetoothEndpointsAreSkipped()
        {
            AudioLatCheck(AudioLowLatency.IsBluetoothEnumerator("BTHENUM")
                && AudioLowLatency.IsBluetoothEnumerator("bthhfenum"),
                "Bluetooth enumerators are recognized regardless of case");
            AudioLatCheck(!AudioLowLatency.IsBluetoothEnumerator("USB")
                && !AudioLowLatency.IsBluetoothEnumerator("HDAUDIO")
                && !AudioLowLatency.IsBluetoothEnumerator(null),
                "wired enumerators and a missing property keep the stream path");
        }

        private static void AudioLatWorthApplyingRequiresSmallerPeriod()
        {
            AudioLatCheck(AudioLowLatency.WorthApplying(480, 128),
                "a device minimum below the default period is the whole point");
            AudioLatCheck(!AudioLowLatency.WorthApplying(480, 480),
                "minimum equal to default means zero gain and must be rejected");
            AudioLatCheck(!AudioLowLatency.WorthApplying(480, 0),
                "a zero minimum is a driver lie, not a valid stream period");
            AudioLatCheck(!AudioLowLatency.WorthApplying(128, 480),
                "minimum above default must never open a stream");
        }

        private static void AudioLatPeriodTextFormatsMilliseconds()
        {
            AudioLatCheck(AudioLowLatency.FramesMs(480, 48000) == "10",
                "480 frames at 48kHz is exactly 10ms");
            AudioLatCheck(AudioLowLatency.FramesMs(128, 48000) == "2.67",
                "128 frames at 48kHz rounds to 2.67ms with an invariant decimal point");
            AudioLatCheck(AudioLowLatency.PeriodText(480, 128, 48000) == "10ms->2.67ms",
                "the log line carries default and minimum in order");
            AudioLatCheck(AudioLowLatency.PeriodText(480, 128, 0) == "480->128",
                "a missing sample rate falls back to raw frame counts");
        }
    }
}
#endif
