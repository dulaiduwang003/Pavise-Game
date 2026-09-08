using System.Collections.Generic;
namespace PaviseApp
{
    internal static class ScalingLang
    {
        internal static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]> {
            { "scale.title", new[] { "全屏缩放", "Fullscreen scaling" } },
            { "scale.sharp", new[] { "锐化", "Sharpen" } },
            { "scale.mouse", new[] { "窗口鼠标映射", "Mouse mapping" } },
            { "scale.off", new[] { "未启用", "Off" } },
            { "scale.waiting", new[] { "等待游戏窗口", "Waiting for a game window" } },
            { "scale.starting", new[] { "正在加载…", "Loading…" } },
            { "scale.running", new[] { "正在缩放", "Scaling" } },
            { "scale.paused", new[] { "切出后暂停", "Paused while unfocused" } },
            { "scale.windowed", new[] { "请先降低游戏窗口分辨率", "Use a lower window resolution" } },
            { "scale.stopped", new[] { "本次已退出 · 重新开启可继续", "Stopped · toggle to restart" } },
            { "scale.error", new[] { "启动失败 · 重新开启可重试", "Failed · toggle to retry" } },
            { "scale.unavailable", new[] { "此构建缺少缩放组件", "Scaling component unavailable" } },
            { "scale.hint", new[] { "低分辨率窗口模式 · Ctrl+Alt+U 退出缩放", "Lower-resolution window · Ctrl+Alt+U to exit" } },
            { "scale.tip", new[] { "开启守护并运行本游戏后，把游戏窗口等比放大到所在显示器，保留黑边并可锐化。请在游戏内设置较低分辨率，Pavise 不修改游戏画质设置。切出时暂停，游戏退出时停止。缩放会增加 GPU 开销，实际收益取决于游戏瓶颈。", "With protection on, fits the running game's window to its monitor with optional sharpening and letterboxing. Set a lower resolution inside the game. Pauses when unfocused and stops when the game exits. Scaling adds GPU work; performance depends on the bottleneck." } },
            { "scale.mouse.tip", new[] { "默认保留游戏原生鼠标输入，适合使用原始输入并自行锁定光标的 3D 游戏。普通窗口可开启坐标映射；使用原始输入的游戏可能不接受此模式的点击，请关闭映射。", "Off preserves native mouse input, for 3D games that use raw input and confine their cursor. Enable coordinate mapping for ordinary windows. Raw-input games may ignore mapped clicks; leave this off for those games." } },
            { "scale.savefailed", new[] { "缩放设置保存失败", "Could not save scaling settings" } }
        };
    }
}
