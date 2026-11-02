#if PAVISE_SELFTEST && PAVISE_AUTO_IGNORE_REGRESSION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class AutoIgnoreRegressionRunner
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions, suites, failed;
        private static string output;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            Dpi.Init(); Dpi.NoFit = true; Lang.Init(); Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false); PanelForm.SuppressUiWorkersForTest = true;
            PerformanceActionBackend.ReadForTest = delegate { throw new InvalidOperationException("Unexpected tuning read"); };
            PerformanceActionBackend.WriteForTest = delegate { throw new InvalidOperationException("Unexpected tuning write"); };
            using (var watchdog = new System.Threading.Timer(delegate { Environment.Exit(124); }, null, 45000, Timeout.Infinite))
            {
                Run("single removal preserves the other ignore and library bytes across reload", SingleRemove);
                Run("removing an ignore clears its retry and allows real automatic admission with fake evidence", SamplingEligibility);
                Run("manual deletion during sampling and the strict commit cannot revive an ignored game", DeleteDuringAutomaticAdmission);
                Run("failed and locked ignore saves retain disk memory and retry state", SaveFailures);
                Run("shutdown during the final ignore commit cancels publication", ShutdownCancel);
                Run("normal paired library-ignore commit and canceled compensation remain intact", PairedTransaction);
                Run("dialog duplicate clicks and save failure echo the actual committed model", DialogState);
                Run("closing the dialog rejects its delayed UI completion", DialogDisposed);
                Run("library button opens the real localized dialog and DPI content remains reachable", LibraryEntryAndLayout);
                Run("high DPI short work area keeps dialog actions visible and scrolls ordered content", ShortWorkArea);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failed + " assertions=" + assertions
                    + " settings=transient runtime_started=false gpu=mocked system_writes=0");
                return failed == 0 ? 0 : 1;
            }
        }
        private static void Run(string title, Action test)
        {
            suites++;
            try { test(); Console.WriteLine("PASS " + title); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failed++; Console.Error.WriteLine("FAIL " + title + ": " + error);
            }
        }
        private static void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        private static object Get(object host, string name) { return host.GetType().GetField(name, Hidden).GetValue(host); }
        private static void Set(object host, string name, object value) { host.GetType().GetField(name, Hidden).SetValue(host, value); }
        private static void SingleRemove()
        {
            using (var f = new Fixture())
            {
                List<string> snapshot = f.Mode.GetAutoAddIgnoredPaths(); snapshot.Clear();
                Check(f.Mode.GetAutoAddIgnoredPaths().Count == 2, "Caller mutation changed the live ignore table");
                byte[] profiles = File.ReadAllBytes(f.ProfilesFile); f.SeedRetry(); int changes = 0;
                f.Mode.LibraryChanged += delegate { changes++; };
                Check(f.Mode.RemoveAutoAddIgnoredPath(f.First.ToUpperInvariant()), "Single ignored path could not be removed");
                Check(f.Mode.GetAutoAddIgnoredPaths().SequenceEqual(new[] { f.Second }) && f.Mode.GetProfiles().Count == 1,
                    "Removing one ignore changed another ignore or the game library");
                Check(File.ReadAllBytes(f.ProfilesFile).SequenceEqual(profiles), "Ignore-only commit rewrote the unchanged primary library");
                Check(f.Retry.Count == 1 && !f.Retry.ContainsKey(f.First) && f.Retry.ContainsKey(f.Second)
                    && (long)Get(f.Mode, "autoAddGateTicks") == 0 && changes == 1, "Single removal failed to clear exactly its retry/gate or notify the library");
                byte[] ignored = File.ReadAllBytes(f.IgnoreFile);
                Check(!f.Mode.RemoveAutoAddIgnoredPath(f.First) && !f.Mode.RemoveAutoAddIgnoredPath(" ")
                    && changes == 1 && File.ReadAllBytes(f.IgnoreFile).SequenceEqual(ignored), "A stale or blank removal wrote data or emitted a false event");
                GameMode reloaded = f.Reload();
                Check(reloaded.GetAutoAddIgnoredPaths().SequenceEqual(new[] { f.Second }) && reloaded.GetProfiles().Count == 1,
                    "The exact remaining original path did not survive restart");
                Set(reloaded, "stopping", true); f.CheckNoStaging();
            }
        }
        private static void SamplingEligibility()
        {
            using (var f = new Fixture())
            using (var sampled = new ManualResetEvent(false))
            {
                int samples = 0; long now = DateTime.UtcNow.Ticks;
                f.Mode.AutoAddFullscreen = true; Set(f.Mode, "enabled", true);
                f.Mode.AutoAddTestNow = delegate { return now; };
                f.Mode.AutoAddTestForeground = delegate { return 741234; };
                f.Mode.AutoAddTestIdentity = delegate { return new GameProcessSnapshot { Pid = 741234, Creation = 54321,
                    Name = "GameOne", Path = f.First, Foreground = true, FullscreenLike = true, Visible = true }; };
                f.Mode.AutoAddTestGpu = delegate { samples++; sampled.Set(); return new Dictionary<int, double> { { 741234, 40 } }; };
                InvokeAutoAdd(f.Mode);
                Check(samples == 0 && f.Mode.GetProfiles().Count == 1, "Permanent ignore was bypassed before its explicit removal");
                f.SeedRetry();
                Check(f.Mode.RemoveAutoAddIgnoredPath(f.First) && f.Mode.AutoAddFullscreen, "Removing an ignore toggled automatic adding or failed");
                InvokeAutoAdd(f.Mode); Check(sampled.WaitOne(2000), "Cleared retry/gate did not permit the next automatic sample");
                var clock = Stopwatch.StartNew();
                while ((int)Get(f.Mode, "rendererGpuSamplingBusy") != 0 && clock.ElapsedMilliseconds < 3000) Thread.Sleep(10);
                Check((int)Get(f.Mode, "rendererGpuSamplingBusy") == 0 && samples == 1 && f.Mode.GetProfiles().Count == 2,
                    "Automatic completion did not admit the formerly ignored executable");
                Check(f.Mode.GetProfiles().Any(p => p.ExecutablePath == f.First) && f.Mode.GetAutoAddIgnoredPaths().SequenceEqual(new[] { f.Second }),
                    "Automatic addition changed another ignore or missed the selected game");
                f.CheckNoStaging();
            }
        }
        private static void InvokeAutoAdd(GameMode mode) { typeof(GameMode).GetMethod("TryAutoAddForegroundGame", Hidden).Invoke(mode, null); }
        private static void DeleteDuringAutomaticAdmission()
        {
            foreach (bool strictBoundary in new[] { false, true })
            using (var f = new Fixture())
            {
                Check(f.Mode.RemoveAutoAddIgnoredPath(f.First), "Could not release the initial fixture ignore");
                f.Mode.AutoAddFullscreen = true; Set(f.Mode, "enabled", true);
                int samples = 0, deletes = 0; long now = DateTime.UtcNow.Ticks;
                Action remove = delegate
                {
                    Check(f.Mode.AddGameExecutable("User selection", f.First), "User addition during sampling failed");
                    f.Mode.RemoveProfile(f.Mode.GetProfiles().Single(p => p.ExecutablePath == f.First).Id); deletes++;
                };
                f.Mode.AutoAddTestNow = delegate { return now; };
                f.Mode.AutoAddTestForeground = delegate { return 741235; };
                f.Mode.AutoAddTestIdentity = delegate { return new GameProcessSnapshot { Pid = 741235, Creation = 54322,
                    Name = "GameOne", Path = f.First, Foreground = true, FullscreenLike = true, Visible = true }; };
                f.Mode.AutoAddTestGpu = delegate
                {
                    samples++; if (!strictBoundary && deletes == 0) remove();
                    return new Dictionary<int, double> { { 741235, 40 } };
                };
                if (strictBoundary) f.Store.BeforeReplaceForTest = delegate { f.Store.BeforeReplaceForTest = null; remove(); };
                InvokeAutoAdd(f.Mode);
                var clock = Stopwatch.StartNew();
                while ((int)Get(f.Mode, "rendererGpuSamplingBusy") != 0 && clock.ElapsedMilliseconds < 3000) Thread.Sleep(10);
                Check((int)Get(f.Mode, "rendererGpuSamplingBusy") == 0 && samples == 1 && deletes == 1,
                    "Fake sampling did not reach the selected actual user-deletion boundary");
                Check(f.Mode.GetProfiles().Count == 1 && f.Mode.GetAutoAddIgnoredPaths().Contains(f.First)
                    && new GameProfileStore(f.Data).LoadProfiles().Count == 1, "Old sampler revived a user-deleted game or cleared its permanent ignore");
                Check(!f.Mode.ProfileStoreSaveFailed, "A concurrent user deletion incorrectly tripped the library failure circuit breaker");
                Check(f.Mode.RemoveAutoAddIgnoredPath(f.First), "Explicitly releasing a new deletion could not be saved");
                InvokeAutoAdd(f.Mode); clock.Restart();
                while ((int)Get(f.Mode, "rendererGpuSamplingBusy") != 0 && clock.ElapsedMilliseconds < 3000) Thread.Sleep(10);
                Check((int)Get(f.Mode, "rendererGpuSamplingBusy") == 0 && samples == 2 && f.Mode.GetProfiles().Count == 2,
                    "A later automatic attempt could not admit the explicitly released game");
                f.CheckNoStaging();
            }
        }
        private static void SaveFailures()
        {
            foreach (bool locked in new[] { false, true })
            using (var f = new Fixture())
            {
                f.SeedRetry(); byte[] old = File.ReadAllBytes(f.IgnoreFile); int changes = 0;
                f.Mode.LibraryChanged += delegate { changes++; };
                FileStream hold = null;
                try
                {
                    if (locked) hold = new FileStream(f.IgnoreFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    else f.Transaction.BeforeWriteForTest = delegate { throw new IOException("Isolated write denial"); };
                    Check(!f.Mode.RemoveAutoAddIgnoredPath(f.First), "Failed or locked save reported a removed entry");
                    Check(File.ReadAllBytes(f.IgnoreFile).SequenceEqual(old) && f.Mode.GetAutoAddIgnoredPaths().Count == 2
                        && f.Retry.Count == 2 && (long)Get(f.Mode, "autoAddGateTicks") != 0 && changes == 0,
                        "Save failure published memory, cleared retry state or lost the original file");
                    Check(!f.Mode.ProfileStoreSaveFailed, "An ignore-only save failure incorrectly disabled the whole game library");
                }
                finally { if (hold != null) hold.Dispose(); f.Transaction.BeforeWriteForTest = null; }
                Check(f.Mode.RemoveAutoAddIgnoredPath(f.First), "A released ignore-file failure could not be retried"); f.CheckNoStaging();
            }
        }
        private static void ShutdownCancel()
        {
            using (var f = new Fixture())
            {
                byte[] old = File.ReadAllBytes(f.IgnoreFile); f.SeedRetry();
                f.Transaction.BeforeWriteForTest = delegate(string phase) { if (phase == "apply-ignore-only") Set(f.Mode, "stopping", true); };
                Check(!f.Mode.RemoveAutoAddIgnoredPath(f.First), "Shutdown admitted an ignore-only replacement");
                Check(File.ReadAllBytes(f.IgnoreFile).SequenceEqual(old) && f.Mode.GetAutoAddIgnoredPaths().Count == 2 && f.Retry.Count == 2,
                    "Canceled final commit published disk or memory state");
                f.Transaction.BeforeWriteForTest = null;
                Check(!f.Mode.RemoveAutoAddIgnoredPath(f.First), "A stopped library accepted a later removal"); f.CheckNoStaging();
                Set(f.Mode, "stopping", false); f.Store.MarkSaveFailed(new IOException("Isolated fatal library fault"));
                Check(!f.Mode.RemoveAutoAddIgnoredPath(f.First) && File.ReadAllBytes(f.IgnoreFile).SequenceEqual(old),
                    "A faulted library admitted a removal and lost its original ignore record");
            }
        }
        private static void PairedTransaction()
        {
            using (var f = new Fixture())
            {
                byte[] profiles = File.ReadAllBytes(f.ProfilesFile), ignored = File.ReadAllBytes(f.IgnoreFile);
                f.Store.BeforeReplaceForTest = delegate { Set(f.Mode, "stopping", true); };
                Check(!f.Mode.AddGameExecutable("Canceled paired add", f.First), "Canceled primary commit published a paired addition");
                Check(File.ReadAllBytes(f.ProfilesFile).SequenceEqual(profiles) && File.ReadAllBytes(f.IgnoreFile).SequenceEqual(ignored)
                    && f.Mode.GetAutoAddIgnoredPaths().Count == 2 && f.Mode.GetProfiles().Count == 1,
                    "Existing paired transaction failed to compensate the ignore table after primary cancellation");
                f.Store.BeforeReplaceForTest = null; Set(f.Mode, "stopping", false);
                Check(f.Mode.AddGameExecutable("Manual paired add", f.First) && f.Mode.GetAutoAddIgnoredPaths().SequenceEqual(new[] { f.Second }),
                    "Unchanged paired library/ignore commit was broken by the ignore-only branch");
                GameMode loaded = f.Reload();
                Check(loaded.GetProfiles().Count == 2 && loaded.GetAutoAddIgnoredPaths().SequenceEqual(new[] { f.Second }), "Paired success was not durable");
                Set(loaded, "stopping", true); f.CheckNoStaging();
            }
        }
        private static void DialogState()
        {
            using (var f = new Fixture())
            using (var dialog = new AutoIgnoreDialog(f.Mode))
            {
                var work = new Queue<Action>(); var posts = new Queue<Action>();
                dialog.WorkQueueForTest = work.Enqueue; dialog.PostForTest = posts.Enqueue;
                ShowHidden(dialog);
                var list = Find<TechListBox>(dialog, "autoIgnoreList"); var remove = Find<Button>(dialog, "autoIgnoreResume");
                list.SelectedIndex = list.Items.IndexOf(f.First); remove.PerformClick(); remove.PerformClick();
                Check(work.Count == 1 && !remove.Enabled && list.Items.Count == 2, "Repeated click queued duplicate work or optimistically removed the row");
                work.Dequeue()(); Check(posts.Count == 1 && f.Mode.GetAutoAddIgnoredPaths().Count == 1 && list.Items.Count == 2 && !remove.Enabled,
                    "Worker published controls or released busy before UI consumption");
                posts.Dequeue()(); Check(list.Items.Count == 1 && remove.Enabled && Find<TextBox>(dialog, "autoIgnorePath").Text == f.Second,
                    "UI completion did not echo the committed remaining entry");
                f.Transaction.BeforeWriteForTest = delegate { throw new IOException("Denied"); };
                remove.PerformClick(); work.Dequeue()(); posts.Dequeue()();
                Check(list.Items.Count == 1 && remove.Enabled && Find<Label>(dialog, "autoIgnoreStatus").Text == Lang.T("autoignore.failed"),
                    "Failed save disappeared from the dialog or stranded its button");
                f.Transaction.BeforeWriteForTest = null; remove.PerformClick(); work.Dequeue()(); posts.Dequeue()();
                Check(list.Items.Count == 0 && !remove.Enabled && Find<Label>(dialog, "autoIgnoreEmpty").Visible,
                    "Empty list retained an active remove button or lost its empty state");
                dialog.Close();
            }
            using (var f = new Fixture())
            using (var dialog = new AutoIgnoreDialog(f.Mode))
            {
                ShowHidden(dialog); dialog.WorkQueueForTest = delegate { throw new InvalidOperationException("Queue rejected"); };
                Find<Button>(dialog, "autoIgnoreResume").PerformClick();
                Check(Find<Button>(dialog, "autoIgnoreResume").Enabled && f.Mode.GetAutoAddIgnoredPaths().Count == 2,
                    "Rejected UI work queue held the button or mutated the ignore table"); dialog.Close();
            }
        }
        private static void DialogDisposed()
        {
            using (var f = new Fixture())
            {
                var work = new Queue<Action>(); var posts = new Queue<Action>();
                var dialog = new AutoIgnoreDialog(f.Mode); dialog.WorkQueueForTest = work.Enqueue; dialog.PostForTest = posts.Enqueue;
                ShowHidden(dialog); Find<Button>(dialog, "autoIgnoreResume").PerformClick(); dialog.Close(); dialog.Dispose();
                work.Dequeue()(); posts.Dequeue()();
                Check(f.Mode.GetAutoAddIgnoredPaths().Count == 1 && (int)Get(dialog, "changeBusy") == 0,
                    "Disposed dialog lost the authorized commit, touched dead controls or retained its completion gate");
            }
        }
        private static void LibraryEntryAndLayout()
        {
            foreach (int dpi in new[] { 96, 144 })
            foreach (int language in new[] { 0, 1, 2 })
            using (var f = new Fixture())
            {
                Dpi.Scale = dpi / 96f; Lang.Cur = language; Theme.DropFontCache();
                f.Mode.InitializeAutomation();
                using (var form = new IgnoreOffscreenPanel(new Tamer(f.Core), f.Mode, SystemIcons.Application))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show();
                    form.ShowPageForShot(PageId.Library); typeof(PanelForm).GetMethod("StopPageReveal", Hidden).Invoke(form, null); Application.DoEvents();
                    Control page = typeof(PanelForm).GetField("pageLibrary", Hidden).GetValue(form) as Control;
                    var button = Find<PillButton>(page, "libraryAutoIgnore"); var actions = Find<WorkspacePanel>(page, "libraryActions");
                    // 自 a58b2ba 起入口是图标按钮：本地化名称与说明放在无障碍名称和提示中，没有可见文字。
                    Check(button is SettingsButton && button.AccessibleName == Lang.T("v15.library.autoadd.settings")
                        && button.AccessibleDescription == Lang.T("autoignore.title") && !button.AccessibleName.StartsWith("v15.")
                        && actions.AutoScroll && !actions.HorizontalScroll.Visible,
                        "Localized library ignore entry was missing or created horizontal scrolling: name=" + button.AccessibleName
                        + " expected=" + Lang.T("v15.library.autoadd.settings") + " horizontal=" + actions.HorizontalScroll.Visible
                        + " display=" + actions.DisplayRectangle + " client=" + actions.ClientRectangle);
                    actions.ScrollControlIntoView(button); Application.DoEvents();
                    Check(actions.ClientRectangle.Contains(button.Bounds), "Ignore-list button was not reachable in the library action viewport");
                    if (language == 0) Save(page, "library-ignore-" + dpi + ".png");
                    bool opened = false; Exception dialogError = null;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
                    {
                        timer.Tick += delegate
                        {
                            var dialog = Application.OpenForms.Cast<Form>().OfType<AutoIgnoreDialog>().FirstOrDefault();
                            if (dialog == null) return;
                            timer.Stop();
                            try
                            {
                                opened = true; dialog.Location = new Point(-20000, -20000);
                                Check(dialog.Text == Lang.T("autoignore.title") && Find<TechListBox>(dialog, "autoIgnoreList").Items.Count == 2,
                                    "Library entry opened an unrelated or untranslated dialog");
                                Check(Find<Button>(dialog, "autoIgnoreClose").Text == Lang.T("autoignore.close")
                                    && !Find<Button>(dialog, "autoIgnoreClose").Text.StartsWith("autoignore."), "Dialog close action was not localized");
                                foreach (Control control in dialog.Controls) Check(dialog.ClientRectangle.Contains(control.Bounds), "Dialog control clipped at DPI/language: " + control.Name);
                                Check(Find<RoundPanel>(dialog, "autoIgnoreListWrap").Bottom < Find<Label>(dialog, "autoIgnorePathTitle").Top,
                                    "Dialog list overlapped the selected path details");
                                Save(dialog, "ignore-dialog-" + dpi + "-" + language + ".png");
                                if (language == 0) Save(dialog, "ignore-dialog-" + dpi + ".png");
                                dialog.ClientSize = new Size(Theme.S(440), dialog.ClientSize.Height);
                                typeof(AutoIgnoreDialog).GetMethod("LayoutContent", Hidden).Invoke(dialog, null);
                                foreach (Control control in dialog.Controls) Check(dialog.ClientRectangle.Contains(control.Bounds), "Narrow dialog control clipped: " + control.Name);
                                Check(TextRenderer.MeasureText(Find<Button>(dialog, "autoIgnoreResume").Text,
                                    Find<Button>(dialog, "autoIgnoreResume").Font).Width <= Find<Button>(dialog, "autoIgnoreResume").Width,
                                    "Narrow dialog resume action was truncated");
                                if (language == 0) Save(dialog, "ignore-dialog-narrow-" + dpi + ".png");
                            }
                            catch (Exception error) { dialogError = error; }
                            finally { dialog.Close(); }
                        };
                        timer.Start(); button.PerformClick();
                    }
                    Check(opened && dialogError == null && !(bool)typeof(PanelForm).GetField("autoIgnoreDialogOpen", Hidden).GetValue(form),
                        "Actual library click did not open/close the dialog cleanly: " + dialogError);
                    form.RealExit = true; form.Close();
                }
            }
            Dpi.Scale = 1; Lang.Cur = 0; Theme.DropFontCache();
        }
        private static T Find<T>(Control parent, string name) where T : Control
        { return (T)parent.Controls.Find(name, true).Single(); }
        private static void ShortWorkArea()
        {
            foreach (int language in new[] { 0, 1, 2 })
            using (var f = new Fixture())
            {
                Dpi.Scale = 2; Lang.Cur = language; Theme.DropFontCache();
                using (var dialog = new AutoIgnoreDialog(f.Mode))
                {
                    dialog.ClientSize = new Size(880, 688); ShowHidden(dialog);
                    var content = Find<WorkspacePanel>(dialog, "autoIgnoreContent");
                    var list = Find<RoundPanel>(dialog, "autoIgnoreListWrap"); var pathTitle = Find<Label>(dialog, "autoIgnorePathTitle");
                    var details = Find<TextBox>(dialog, "autoIgnorePath"); var resume = Find<Button>(dialog, "autoIgnoreResume");
                    Check(list.Bottom < pathTitle.Top && pathTitle.Bottom <= details.Top, "Short high-DPI viewport overlapped list and path controls");
                    Check(dialog.ClientRectangle.Contains(resume.Bounds) && content.Bottom < Find<Label>(dialog, "autoIgnoreStatus").Top
                        && content.AutoScroll && !content.HorizontalScroll.Visible, "Short viewport clipped its fixed action buttons or introduced horizontal scrolling");
                    content.ScrollControlIntoView(details); Application.DoEvents();
                    Check(content.ClientRectangle.Contains(details.Bounds), "Selected path could not be reached by scrolling the short viewport");
                    if (language == 0) Save(dialog, "ignore-dialog-short-192.png");
                    dialog.Close();
                }
            }
            Dpi.Scale = 1; Lang.Cur = 0; Theme.DropFontCache();
        }
        private static void ShowHidden(Form form)
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
            IntPtr handle = form.Handle; SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
            form.Show(); Application.DoEvents();
        }
        private static void Save(Control control, string name)
        { using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, control.ClientRectangle); bitmap.Save(Path.Combine(output, name)); } }
        private sealed class Fixture : IDisposable
        {
            internal readonly SuppressionCore Core = new SuppressionCore();
            internal readonly GameMode Mode;
            internal readonly string Data, First, Second;
            internal string IgnoreFile { get { return Path.Combine(Data, LibraryIgnoreTransaction.IgnoreFileName); } }
            internal string ProfilesFile { get { return Path.Combine(Data, GameProfileStore.FileName); } }
            internal Dictionary<string, long> Retry { get { return (Dictionary<string, long>)Get(Mode, "autoAddRejectUntil"); } }
            internal LibraryIgnoreTransaction Transaction { get { return (LibraryIgnoreTransaction)Get(Mode, "libraryIgnoreTransaction"); } }
            internal GameProfileStore Store { get { return (GameProfileStore)Get(Mode, "profileStore"); } }
            internal Fixture()
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                Data = Path.Combine(output, "case-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Data);
                string games = Path.Combine(Data, "游戏 & Games"); Directory.CreateDirectory(games);
                First = Path.Combine(games, "One", "GameOne.exe"); Second = Path.Combine(games, "Two", "GameTwo.exe");
                string remaining = Path.Combine(games, "Remaining", "StillInLibrary.exe");
                foreach (string target in new[] { First, Second, remaining }) Directory.CreateDirectory(Path.GetDirectoryName(target));
                foreach (string target in new[] { First, Second, remaining }) File.Copy(Assembly.GetExecutingAssembly().Location, target);
                Mode = new GameMode(Data, Core);
                Check(Mode.AddGameExecutable("One", First) && Mode.AddGameExecutable("Two", Second) && Mode.AddGameExecutable("Remaining", remaining), "Could not prepare actual temporary profiles");
                foreach (GameProfile profile in Mode.GetProfiles()) if (profile.ExecutablePath != remaining) Mode.RemoveProfile(profile.Id);
                Check(Mode.GetAutoAddIgnoredPaths().Count == 2 && Mode.GetProfiles().Count == 1 && Get(Mode, "worker") == null,
                    "Actual manual removal did not establish the fixture ignore list or started runtime");
            }
            internal void SeedRetry()
            { Retry[First] = DateTime.UtcNow.AddMinutes(10).Ticks; Retry[Second] = DateTime.UtcNow.AddMinutes(10).Ticks; Set(Mode, "autoAddGateTicks", DateTime.UtcNow.AddSeconds(30).Ticks); }
            internal GameMode Reload() { return new GameMode(Data, new SuppressionCore()); }
            internal void CheckNoStaging()
            {
                var observations = (RendererObservationStore)Get(Mode, "rendererObservations");
                if (observations != null) Check(observations.Close(2000), "Fixture observation file writer did not drain");
                string[] staged = Directory.GetFiles(Data, "*.tmp");
                Check(!File.Exists(Path.Combine(Data, LibraryIgnoreTransaction.FileName)) && staged.Length == 0,
                    "Ignore operation left a transaction or staging file: " + Data + " temp=" + string.Join(",", staged));
            }
            public void Dispose()
            {
                Set(Mode, "stopping", true); Transaction.BeforeWriteForTest = null; Store.BeforeReplaceForTest = null;
                var observations = (RendererObservationStore)Get(Mode, "rendererObservations"); if (observations != null) observations.Close(2000);
                Check(Get(Mode, "worker") == null, "Ignore fixture started the optimizer main loop");
            }
        }
    }
    internal sealed class IgnoreOffscreenPanel : PanelForm
    {
        internal IgnoreOffscreenPanel(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Ignore tests cannot start optimization runtime"); }
    }
}
#endif
