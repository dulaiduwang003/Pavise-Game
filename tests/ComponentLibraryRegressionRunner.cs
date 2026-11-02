#if PAVISE_SELFTEST && PAVISE_COMPONENT_LIBRARY
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Component library checks cannot start the application runtime."); }
    }

    // 组件库回归入口：内存配置、离屏宿主，按 DPI、主题与语言循环执行全部检查。
    internal static class ComponentLibraryRegressionRunner
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            IntPtr desktop = CreateDesktop("PaviseComponents-" + Guid.NewGuid().ToString("N"),
                IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
            if (desktop == IntPtr.Zero) { Console.Error.WriteLine("Private fixture desktop unavailable"); return 1; }
            int result = 1;
            try
            {
                var thread = new Thread(delegate() {
                    if (!SetThreadDesktop(desktop))
                    { Console.Error.WriteLine("Fixture desktop assignment failed: " + Marshal.GetLastWin32Error()); return; }
                    result = RunChecks(args);
                    Console.Out.Flush(); Console.Error.Flush();
                    // This is a dedicated fixture process. Finish on its private UI thread;
                    // do not marshal STA teardown back to a console thread without a pump.
                    Environment.Exit(result);
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start(); thread.Join();
                return result;
            }
            finally { CloseDesktop(desktop); }
        }
        private static int RunChecks(string[] args)
        {
            try
            {
                if (args.Length != 1) return 2;
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Control.CheckForIllegalCrossThreadCalls = true;
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                Logger.WritesEnabled = false;
                Dpi.Init(); Lang.Init();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                int assertions = 0, runs = 0;
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                foreach (bool light in new[] { false, true })
                foreach (int language in new[] { 0, 1 })
                {
                    Dpi.Scale = scale; Theme.DropFontCache(); Theme.SetLight(light); Lang.Cur = language;
                    assertions += ComponentLibraryChecks.Run(output, scale, light, language);
                    runs++;
                    Console.WriteLine("PASS combination dpi=" + scale + " light=" + light + " lang=" + language + " runs=" + runs);
                }
                Console.WriteLine("PASS component library assertions=" + assertions + " runs=" + runs
                    + " dpi=100,125,150,200,300 themes=dark,light languages=zh,en"
                    + " fixture_desktop=private fixture_windows=offscreen application_started=false input_desktop_unchanged=true cursor_input_sent=false settings=transient");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }

    // 离屏宿主窗体：不激活、不进任务栏、位于可见屏幕之外。
    internal sealed class ComponentLibraryHost : Form
    {
        public ComponentLibraryHost()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1000, 800);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
        }
    }
}
#endif
