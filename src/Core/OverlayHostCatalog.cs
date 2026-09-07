// 文件用途 识别需要免于后台压制的录制与覆盖层宿主 不读取游戏模块或产品文件
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class OverlayHostCatalog
    {
        // 这是兼容性名单 不是签名认证 也不说明覆盖层正在当前游戏中运行
        // 只匹配具体 EXE 不按安装目录 子进程树或产品名前缀传播豁免
        // Steam 的通信 覆盖层 UI 与浏览器渲染是一组 只保其中一个不能保证功能
        // 该组由调用方的现有家族策略控制 不影响下方独立工具的保护
        private static readonly HashSet<string> GamePlatformProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "steam", "gameoverlayui", "steamwebhelper"
            };

        private static readonly HashSet<string> ProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment",
                "DiscordHookHelper", "DiscordHookHelper64",

                // 新 NVIDIA App 与旧 GeForce Experience 的录制/覆盖层宿主
                "NVIDIA Share", "NVIDIA Overlay", "nvsphelper", "nvsphelper64",

                // 32 位游戏由 32 位装载器注入 rtsshooks EncoderServer 是
                // Afterburner 录制链的编码宿主 压住任何一个都会拖累渲染路径
                "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64",
                "MSIAfterburner", "EncoderServer", "EncoderServer64",

                // 浏览器源和封装输出同样属于录制链 不能只保护主窗口
                "obs32", "obs64", "obs-browser-page", "obs-ffmpeg-mux",

                "Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64",

                // 旧模块扫描的 nahimicosd/a-volute 对应同一音频/OSD 产品链
                "NahimicService", "NahimicSvc32", "NahimicSvc64",

                "fraps",

                // 纯语音软件 收进托盘后没有可见窗口 不在这里点名就会按普通后台隔离
                //   隔离档是 IDLE 优先级加极低磁盘 IO 加小核限频 语音会断续
                //   这些 EXE 的文件描述都不含外设词表里的词 只能按名字点
                //   QQ TIM 微信不进名单 它们平时就是重后台 该压 通话时由 VoiceSessionRoster 按采集会话放行
                "KOOK", "YY", "Oopz",
                "ts3client_win64", "ts3client_win32", "TeamSpeak", "mumble"
            };

        // 调用方仍须核对会话及 PID/Creation 并且只解除 Background 原因
        // HardwareControlCatalog / PeripheralCatalog 的既有保护独立保留
        // 未知或重命名的宿主可用精确白名单补充 不放宽到 chrome/updater 等通用进程
        internal static bool ShouldProtectProcess(
            string name, string imagePath, bool protectGamePlatformHosts)
        {
            string normalizedName = WhitelistRule.NormalizeName(name);
            if (!ProcessNames.Contains(normalizedName)
                && !(protectGamePlatformHosts && GamePlatformProcessNames.Contains(normalizedName)))
                return false;

            // 此规范化器先要求绝对路径 再调用 GetFullPath 不会把相对路径当作身份
            string path = WhitelistRule.NormalizeImagePath(imagePath);
            if (path.Length == 0) return false;
            try
            {
                // \\server\obs64.exe 只是 UNC 共享根 不是共享目录内的可执行文件
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || path.Length <= root.Length) return false;
                string leaf = Path.GetFileName(path);
                if (!leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
                return string.Equals(normalizedName, leaf.Substring(0, leaf.Length - 4),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
