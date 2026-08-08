// @author bdth 2074055628@qq.com
// 文件用途 把统一弹窗渲染成图片 用于人工核对绘制结果

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RunDialogShot(string outDir)
        {
            Directory.CreateDirectory(outDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var cases = new[]
            {
                new object[] { "info", DlgKind.Info, "重启后生效", "显卡中断亲和已写入。该设置需要重启这块设备或重启电脑之后才会生效。", false },
                new object[] { "warn", DlgKind.Warn, "游戏正在运行", "此时清理会把游戏自己的内存也踢出去，立刻造成卡顿和重新读盘。请退出游戏后再清理。", false },
                new object[] { "danger", DlgKind.Danger, "这是拿安全换性能", "被排除的目录不再被实时扫描，其中的恶意文件不会被拦截。游戏目录常混有破解版、第三方 mod 和修改器，风险高于普通目录。", true },
                new object[] { "success", DlgKind.Success, "清理完成", "耗时 1.4 秒。\r\n\r\n可用内存 +382 MB\r\n系统缓存 -18894 MB\r\n\r\n可用内存增加的那部分，大多来自被丢弃的缓存。", false }
            };

            Type t = typeof(PaviseDialog);
            ConstructorInfo ctor = t.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string), typeof(DlgKind), typeof(string), typeof(string) }, null);
            if (ctor == null) { Console.Write("找不到 PaviseDialog 构造函数"); return; }

            foreach (object[] c in cases)
            {
                string name = (string)c[0];
                var kind = (DlgKind)c[1];
                string title = (string)c[2];
                string body = (string)c[3];
                bool confirm = (bool)c[4];
                var dlg = (Form)ctor.Invoke(new object[]
                {
                    title, body, kind,
                    confirm ? Lang.T("dlg.confirm") : Lang.T("dlg.ok"),
                    confirm ? Lang.T("dlg.cancel") : null
                });
                using (dlg)
                {
                    dlg.StartPosition = FormStartPosition.Manual;
                    dlg.Location = new Point(-20000, -20000);
                    dlg.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(120);
                    Application.DoEvents();
                    using (var bmp = new Bitmap(dlg.ClientSize.Width, dlg.ClientSize.Height))
                    {
                        dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        string path = Path.Combine(outDir, "dlg-" + name + ".png");
                        bmp.Save(path, ImageFormat.Png);
                        Console.Write(path + "  " + bmp.Width + "x" + bmp.Height + Environment.NewLine);
                    }
                    dlg.Hide();
                }
            }
        }
    }
}
