<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise logo">
</a>

# PAVISE

### Give your games room to run.

A Windows game resource manager.<br>Choose how background tasks, scheduling and power behave; restore recorded session changes after play.

**Per-game profiles · Smart / Competitive · Recorded recovery**

[简体中文](README.md) · **English** · [日本語](README.ja.md)

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[Website](https://pavise.club/en/) · [Latest download](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) · [User guide](https://pavise.club/en/docs/) · [Report an issue](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

> **The source code and documentation are being organized and will be uploaded to this repository when ready.** The code currently here is historical; use the published application and its release notes for current behavior.

## What Pavise does

Games share a PC with browsers, launchers and update tasks. Pavise applies your saved choices to game and background resource allocation during a session, then restores the session changes it recorded.

You choose which features to enable. Guard applies them when it identifies a game. Each game can have its own configuration; protection rules and your whitelist determine which background applications remain unaffected.

| Area | Main capabilities |
| --- | --- |
| **Library and detection** | Add games, scan installations or running programs, and manage per-game settings. Program selection includes windowed, minimized and tray applications. |
| **Background control** | Adjust eligible background CPU, I/O, memory-page and GPU scheduling; manage exclusions; pause updates, downloads and maintenance during play. |
| **Game and core scheduling** | Game priorities, thread boosting, logical-core selection, CCD / SMT shortcuts, core isolation and background core avoidance. |
| **Managed power** | Match the CPU, device type and Smart / Competitive scene. Preserve everyday display, sleep, lid and battery settings, or select an existing plan or Don't switch. |
| **Graphics and memory** | Supported GPU performance preferences, graphics controls, cache warming and standby-memory cleanup, configured independently. |
| **Laboratory** | CPU idle policies, EPP power sharing, GPU clock / power controls and low-buffer audio, subject to hardware and driver support. |
| **System checkup** | Inspect system and device state; observe and adjust GPU interrupts, verify after reboot and restore. MSI repair and restoration have separate actions. |
| **Recovery and updates** | Original-value records, pending recovery retries, logs, diagnostic summaries, an AI tuning export, and in-app updates with progress and signature verification. |

The overview shows **confirmed suppressed-process and paused-service counts** for the current session. Availability depends on Windows, hardware and drivers. Assess results with your own games and background workload.

## Scenes and configuration presets

- **Smart** balances game resource control with everyday application interaction.
- **Competitive** uses a more active session policy while respecting foreground protection, the whitelist and your feature choices.
- **Default / Extreme presets** apply groups of feature settings independently of the scene. Hardware support and current state are still checked.

## Download and start

1. Download the complete package from the [website](https://pavise.club/en/changelog/#latest) or [GitHub Releases](https://github.com/dulaiduwang003/Pavise-Game/releases/latest).
2. Extract it to a stable folder. Keep `Pavise.exe`, its `.exe.config` and the accompanying files together, then run as administrator.
3. Add a game, select a scene, review feature choices and the whitelist, then enable Guard.
4. Check recovery after play. Persistent settings follow their own restoration instructions; restart when a setting requires it.

Requires **Windows 10 2004 / build 19041 or newer and .NET Framework 4.7.2+**. Windows 11 is supported; individual controls depend on system and device capabilities.

### Updates and release changes

Starting with **3.2.0.2**, About → Check & update can download and verify an update before replacing the application, keeping your library, whitelist and preferences. **Versions 3.2.0.1 and earlier need one manual upgrade first.** Exit the game before updating.

Frame-rate detection, the floating OSD, game-session reports and RTSS integration have been removed. See the [release history](https://github.com/dulaiduwang003/Pavise-Game/releases) for full changes.

## Documentation and feedback

[User guide](https://pavise.club/en/docs/) · [Changelog](https://pavise.club/en/changelog/#latest) · [Issues](https://github.com/dulaiduwang003/Pavise-Game/issues)

For a report, include the Pavise and Windows versions, CPU / GPU, game, relevant settings and reproduction steps. Add logs or a diagnostic summary when useful.

## Author and support

Created and maintained by **[bdth](https://github.com/dulaiduwang003)**. Pavise is free to obtain; **donations are entirely optional**. Thank you for using it, reporting issues and supporting development.

[Support development](https://pavise.club/support/) · [Author profile](https://github.com/dulaiduwang003)

<p align="center">
  <img src="docs/wechat.png" width="220" alt="Author's WeChat donation QR code">
  <img src="docs/alipay.png" width="220" alt="Author's Alipay donation QR code">
</p>

See [LICENSE](LICENSE) and [NOTICE](NOTICE) for the project license and notices.
