using System;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class GameLibraryTile
    {
        private System.Windows.Forms.Timer addonNoticeTimer;
        private string addonNoticeKey = "";
        private DateTime nextAddonNoticeUtc;
        private int addonNoticeSerial;
        private int addonNoticeJob;
        private bool addonNoticePending;
        private bool addonNoticeAvailable;
        private static readonly ProcessActionFailureBudget addonNoticeFailures = new ProcessActionFailureBudget();

        private void InitializeAddonNotice()
        {
            addonNoticeTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            addonNoticeTimer.Tick += delegate { QueueAddonNoticeInspection(false); };
        }

        private static string AddonNoticeKey(GameProfile profile)
        {
            if (profile == null || !(LolGameExtension.MentionsLol(profile.Name) || LolGameExtension.MentionsLol(profile.Root)
                || LolGameExtension.MentionsLol(profile.ExecutablePath)))
            {
                return "";
            }
            return profile.Id + "\n" + profile.Name + "\n" + profile.ExecutablePath + "\n" + profile.Root;
        }

        private void RefreshAddonNotice()
        {
            string key = AddonNoticeKey(Item == null ? null : Item.Profile);
            if (!string.Equals(key, addonNoticeKey, StringComparison.OrdinalIgnoreCase))
            {
                addonNoticeKey = key;
                addonNoticeAvailable = false;
                addonNoticeJob = 0;
                addonNoticePending = false;
                nextAddonNoticeUtc = DateTime.MinValue;
            }
            if (addonNoticeTimer != null)
            {
                addonNoticeTimer.Enabled = Visible && IsHandleCreated && key.Length > 0;
                QueueAddonNoticeInspection(false);
            }
        }

        // 只解析此游戏库条目的路径；展示提示不能扫描安装目录或启动 LOL 服务。
        internal static bool InspectAddonNotice(GameProfile profile)
        {
            bool failed;
            return InspectAddonNoticeResult(profile, out failed);
        }

        private static bool InspectAddonNoticeResult(GameProfile profile, out bool failed)
        {
            failed = false;
            if (AddonNoticeKey(profile).Length == 0) return false;
            string root;
            if (!LolGameExtension.TryResolveProfileRoot(profile, out root)) return false;
            var inspection = LolAddonCleaner.InspectAvailability(root);
            failed = !string.IsNullOrEmpty(inspection.Error);
            return !failed && inspection.CanDelete;
        }

        private void QueueAddonNoticeInspection(bool force)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || !Visible || addonNoticeKey.Length == 0) return;
            if (addonNoticeJob != 0)
            {
                addonNoticePending |= force;
                return;
            }
            if (!force && DateTime.UtcNow < nextAddonNoticeUtc) return;
            long session = PerformanceTuning.RecoveryCycleSerial;
            if (!addonNoticeFailures.Allowed(0, session, addonNoticeKey, "notice")) return;
            int job = ++addonNoticeSerial;
            addonNoticeJob = job;
            string key = addonNoticeKey;
            GameProfile profile = Item.Profile.Clone();
            try
            {
            if (!ThreadPool.QueueUserWorkItem(delegate
            {
                bool available = false;
                bool failed = false;
                try { available = InspectAddonNoticeResult(profile, out failed); }
                catch { failed = true; }
                if (failed) RecordAddonNoticeFailure(session, key);
                else addonNoticeFailures.Succeeded(0, session, key, "notice");
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed || Disposing || addonNoticeJob != job) return;
                        addonNoticeJob = 0;
                        bool repeat = addonNoticePending;
                        addonNoticePending = false;
                        if (string.Equals(key, AddonNoticeKey(Item == null ? null : Item.Profile), StringComparison.OrdinalIgnoreCase) && !repeat)
                        {
                            addonNoticeAvailable = available;
                            nextAddonNoticeUtc = DateTime.UtcNow.AddSeconds(30);
                        }
                        else nextAddonNoticeUtc = DateTime.MinValue;
                        RefreshModel();
                    });
                }
                catch (InvalidOperationException) { }
            })) { addonNoticeJob = 0; RecordAddonNoticeFailure(session, key); }
            }
            catch { addonNoticeJob = 0; RecordAddonNoticeFailure(session, key); }
        }

        private static void RecordAddonNoticeFailure(long session, string key)
        {
            int failures = addonNoticeFailures.Failed(0, session, key, "notice");
            Logger.Warn("LOL 附加内容提示观测失败=" + failures + "/3");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RefreshAddonNotice();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            addonNoticeJob = 0;
            nextAddonNoticeUtc = DateTime.MinValue;
            if (addonNoticeTimer != null) addonNoticeTimer.Stop();
            base.OnHandleDestroyed(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) nextAddonNoticeUtc = DateTime.MinValue;
            RefreshAddonNotice();
        }

        private void DisposeAddonNotice()
        {
            addonNoticeJob = 0;
            if (addonNoticeTimer == null) return;
            addonNoticeTimer.Dispose();
            addonNoticeTimer = null;
        }
    }
}
