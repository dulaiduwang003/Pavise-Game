using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class Lang
    {
        private static void AddGpuSpoofMessages(Dictionary<string, string[]> messages)
        {
            messages.Add("env.gpu.spoof.toggle", new[] { "显卡伪装", "GPU spoofing" });
            messages.Add("env.gpu.spoof.choose", new[] { "选择显卡型号", "Choose a GPU model" });
            messages.Add("env.gpu.spoof.tier.none", new[] { "不伪装", "No spoofing" });
            messages.Add("env.gpu.spoof.tier.mixed", new[] { "当前为不同档位", "Different tiers in use" });
            messages.Add("env.gpu.spoof.tier.restore", new[] { "恢复独显原名", "Restore original GPU names" });
            messages.Add("env.gpu.spoof.retry", new[] { "重试", "Retry" });
            messages.Add("env.gpu.spoof.state.reboot", new[] { "待重启电脑", "PC restart required" });
            messages.Add("env.gpu.spoof.state.recoverable", new[] { "有原名记录，可恢复", "Original name saved. Restore available." });
            messages.Add("env.gpu.spoof.gpu.enable.title", new[] { "启用显卡伪装", "Enable GPU spoofing" });
            messages.Add("env.gpu.spoof.gpu.restore.title", new[] { "恢复显卡原名", "Restore original GPU names" });
            messages.Add("env.gpu.spoof.state.gamebusy", new[] { "游戏准备或运行中，退出后可操作", "A game is starting or running. Exit the game to continue." });
            messages.Add("env.gpu.spoof.gpu.tip", new[] { "修改 Windows 中的独显名称并重启显卡驱动。所有程序都能看到新名称。", "Changes the discrete GPU name in Windows and restarts its driver. The new name is visible to all apps." });
            messages.Add("env.gpu.spoof.state.reading", new[] { "正在读取显卡信息", "Reading GPU information" });
            messages.Add("env.gpu.spoof.state.busy", new[] { "正在处理显卡名称", "Working on the GPU name" });
            messages.Add("env.gpu.spoof.state.error", new[] { "读取显卡信息失败", "Could not read GPU information" });
            messages.Add("env.gpu.spoof.state.none", new[] { "没有找到可伪装的独立显卡", "No discrete GPU to rename" });
            messages.Add("env.gpu.spoof.state.uncertain", new[] { "无法确认是独立显卡，已禁用", "Could not confirm a discrete GPU. Spoofing disabled." });
            messages.Add("env.gpu.spoof.state.partial", new[] { "只有部分独立显卡完成伪装", "Only some discrete GPUs are renamed" });
            messages.Add("env.gpu.spoof.state.active", new[] { "显卡伪装已生效", "GPU spoofing is active" });
            messages.Add("env.gpu.spoof.state.pending", new[] { "伪装名称已写入，等待生效", "New name written; pending" });
            messages.Add("env.gpu.spoof.state.restorepending", new[] { "原名已写回，等待生效", "Original name written back; pending" });
            messages.Add("env.gpu.spoof.state.off", new[] { "显卡伪装未开启", "GPU spoofing is off" });
            messages.Add("env.gpu.spoof.gpu.confirm", new[] { "屏幕会黑几秒。", "The screen will go black for a few seconds." });
            messages.Add("env.gpu.spoof.gpu.done.pending", new[] { "名称已写入，尚未确认生效。\n\n{0}", "Names written. Waiting for verification.\n\n{0}" });
            messages.Add("env.gpu.spoof.gpu.done.active", new[] { "显卡伪装已生效。\n\n{0}", "GPU spoofing is active.\n\n{0}" });
            messages.Add("env.gpu.spoof.gpu.restored", new[] { "已恢复显卡原名。\n\n{0}", "Original GPU names restored.\n\n{0}" });
            messages.Add("env.gpu.spoof.gpu.restored.pending", new[] { "原名已写回，尚未确认生效。\n\n{0}", "Original names written. Waiting for verification.\n\n{0}" });
            messages.Add("env.gpu.spoof.gpu.now.done", new[] { "驱动已重启，程序可读取以下名称。\n\n{0}", "Drivers restarted. Apps now read these names.\n\n{0}" });
            messages.Add("env.gpu.spoof.gpu.now.reboot", new[] { "Windows 要求重启电脑后生效。\n\n{0}", "Windows needs a PC restart to apply these names.\n\n{0}" });
            messages.Add("env.gpu.spoof.gpu.now.unchanged", new[] { "尚未确认名称生效，重启电脑后可再次查看。\n\n{0}", "Could not verify the names. Check again after restarting the PC.\n\n{0}" });
            messages.Add("env.gpu.spoof.gpu.failed", new[] { "操作没有完成：{0}", "The action did not finish: {0}" });
            messages.Add("env.gpu.spoof.gpu.err.none", new[] { "没有找到可伪装的独立显卡", "No discrete GPU to rename" });
            messages.Add("env.gpu.spoof.gpu.err.same", new[] { "此档与显卡原名相同", "This tier matches the original GPU name" });
            messages.Add("env.gpu.spoof.gpu.err.uncertain", new[] { "无法确认是独立显卡，已禁用", "Could not confirm a discrete GPU. Spoofing disabled." });
            messages.Add("env.gpu.spoof.gpu.err.read", new[] { "读不到显卡设备名称", "Cannot read the GPU device name" });
            messages.Add("env.gpu.spoof.gpu.err.kind", new[] { "显卡设备名称的格式不支持", "Unsupported GPU device name format" });
            messages.Add("env.gpu.spoof.gpu.err.verify", new[] { "写入后复查名称不一致", "The name did not match after writing" });
            messages.Add("env.gpu.spoof.gpu.err.restore", new[] { "显卡名称写回失败", "Could not write the GPU name back" });
            messages.Add("env.gpu.spoof.gpu.err.running", new[] { "游戏正在准备或运行。请退出游戏后再操作。", "A game is preparing to launch or running. Exit the game before continuing." });
            messages.Add("env.gpu.spoof.name.unknown", new[] { "未知", "Unknown" });
            messages.Add("env.gpu.spoof.effect", new[] { "重启显卡驱动", "Restarts the GPU driver" });
            messages.Add("env.gpu.spoof.shader.tip", new[] {
                "部分游戏会按识别到的显卡型号选择着色器配置，渲染开销可能随配置变化。更改型号后，如果游戏提供「重新编译着色器」选项，可用它重建缓存。能否切换配置取决于游戏的硬件识别方式。",
                "Some games choose shader settings based on the detected GPU model. Rendering cost can vary with those settings. After changing the model, use the game's shader recompilation option, if available, to rebuild its cache. Whether the settings change depends on how the game identifies the GPU." });
            messages.Add("env.gpu.spoof.platform.desktop", new[] { "台式机和未知机型使用台式型号。", "Desktops and unknown device types use desktop models." });
            messages.Add("env.gpu.spoof.platform.mobile", new[] { "笔记本和掌机使用笔记本型号。", "Laptops and handhelds use laptop models." });
            messages.Add("env.gpu.spoof.ai.purpose", new[] { "修改已确认的 PCI 独显名称，写入前保存原名，恢复时按记录写回。各档型号按厂商匹配，Intel 独显使用 NVIDIA 型号。", "Renames confirmed discrete PCI GPUs, saves their original names, and restores them from the saved records. Each tier uses vendor-specific models. Intel discrete GPUs use NVIDIA models." });
            messages.Add("env.gpu.spoof.ai.scene", new[] { "新名称对所有程序可见。重启显卡驱动时屏幕会黑几秒。Windows 可能要求重启电脑，更新驱动可能恢复原名", "All apps can see the new names. The screen goes black for a few seconds while the GPU driver restarts. Windows may require a PC restart. Driver updates may restore the original names" });
            messages.Add("env.gpu.spoof.ai.operation", new[] { "在环境页的显卡列表中直接选择型号。选择「不伪装」恢复原名。确认后重启显卡驱动，待生效时可重试。游戏准备或运行时禁用选择。已生效的名称会保留，直到重新选择", "Choose a GPU model directly from the list in Environment. Choose No spoofing to restore the original names. Confirm to restart the GPU drivers. Retry if a change is pending. The selector is disabled while a game is preparing or running. Active names stay in effect until you make a selection" });
        }
    }
}
