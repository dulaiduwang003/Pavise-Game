<div align="center">

<a href="https://pavise.club/en/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise logo">
</a>

# PAVISE

### Give your games room to run.

A Windows game resource manager with background process control,<br>
per-game profiles, and automatic recovery after play.

**English** · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://pavise.club/en/changelog/#latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[Website](https://pavise.club/en/) &nbsp; / &nbsp; [Download & release notes](https://pavise.club/en/changelog/#latest) &nbsp; / &nbsp; [User guide](https://pavise.club/en/docs/)**

<br>

<img src="docs/screenshots/en/overview.png" width="100%" alt="Pavise overview showing the active game, session status and resource controls">

</div>

<p align="center">
  <a href="#why-pavise">Why Pavise</a> ·
  <a href="#features">Features</a> ·
  <a href="#modes">Modes</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="#contributing">Contribute</a> ·
  <a href="#contributors">Contributors</a>
</p>

## Why Pavise

Games share your machine with browsers, launchers, update services and other background work. Pavise manages that competition: it gives the game more scheduling priority and reduces the resources claimed by eligible background processes while you play.

**When the game ends, Pavise automatically restores the session changes it recorded.** If Pavise exits unexpectedly, the next launch retries recovery and keeps records of anything it could not restore. Persistent settings and file cleanup have different recovery rules; see [What gets restored](#what-gets-restored).

| Made for your game | Back to your desktop | See what is happening |
| :--- | :--- | :--- |
| Automatic game detection, configurable background suppression and per-game profiles. | Recorded session changes are restored after play, without resetting your everyday setup. | Session reports, hardware checks, structured logs and a copyable diagnostic summary. |

Pavise runs locally, uploads no local machine data, and does not inject into game processes or modify game memory. It checks the official update source at startup.

It does not create extra CPU or GPU performance. The benefit depends on background contention, hardware and the game. Compare the same scene with and without Pavise; a clean system or a fully GPU-bound game may see little change.

## Features

| Area | What you can control |
| :--- | :--- |
| **Game library** | Add an EXE, shortcut or folder; scan Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net, Xbox and Microsoft Store. Keep a separate profile for each game. |
| **Background processes** | Coordinate CPU, disk I/O, paging and GPU priorities, EcoQoS and timer policy. Built-in protection rules and your whitelist define what stays untouched. |
| **CPU cores** | Choose game cores, use CCD and SMT shortcuts, reserve system cores, and optionally keep ordinary background work outside the selected game range. |
| **Graphics** | Access supported NVIDIA, AMD and Intel driver controls, GPU preferences, power limits and optional VRAM policies, with recorded original values. |
| **Memory & power** | Use a managed power plan, session power policies and optional memory controls. Memory cleanup and other advanced policies are individually configurable. |
| **Device interrupts** | Observe DPC activity, compare sessions, inspect device/core candidates and verify manual changes after a reboot. |
| **Diagnostics** | Inspect hardware capabilities, scheduling settings, power and thermal limits, session reports and warnings. Copy a diagnostic summary when reporting a problem. |
| **Your workflow** | Switch between English and Chinese, choose light or dark themes, use feature search, minimize to the tray and manage a whitelist. |

**[Explore every module, switch and limitation →](https://pavise.club/en/docs/)**

Ordinary background suppression exempts anti-cheat processes, Windows core services, the input/audio/peripheral chain, hardware tools and other signed-in accounts. Separate anti-cheat controls are off by default and cover selected user-mode processes only; they do not control kernel drivers. Game-family exemption is on by default.

## Modes

| Mode | Background scope | Foreground apps after alt-tab | Power & hardware policy |
| :--- | :--- | :--- | :--- |
| **Smart** | Isolates eligible background processes when the session starts. | The app you are using and its family are exempt. | Optional adaptive escalation is off by default. |
| **Esports** | Widens suppression to eligible non-game processes, including windowed apps. | Remain eligible for suppression; whitelist and built-in protection still apply. | Additional policies remain configurable. |
| **Handheld** | Uses the Esports background scope. | Same as Esports. | Leaves power control to vendor tools; requires a battery and excludes several desktop-oriented policies. |
| **Custom** | Choose background, core, graphics, memory, power and environment policies individually. | Depends on the selected policies. | Tune globally or override settings per game. |

Modes that your machine cannot use are hidden. The former Extreme mode was removed in v2.2.2; its additional controls are now separate switches, off by default. [Full mode details](https://pavise.club/en/docs/modes/).

## Quick start

**Requirements:** Windows 10 version 2004 (build 19041) or later, with Windows 11 24H2 preferred. Resource management requires administrator privileges. The application interface supports **English and Simplified Chinese**; Japanese documentation is also available.

1. **Get Pavise** from the [official download page](https://pavise.club/en/changelog/#latest). Read the release notes and choose GitHub or Quark. Downloads are free.
2. **Open Pavise** and add your game's EXE, shortcut or folder to the library, or scan your installed games.
3. **Choose a mode** and review its settings. Add applications you want to keep unaffected to the whitelist. Use a per-game profile where needed.
4. **Enable the guard and play.** Detection and session management run automatically; minimizing the game does not end the session.
5. **Exit the game.** Pavise restores recorded session changes. Check the session report or diagnostic summary to see what happened.

The executable is currently unsigned. For detailed setup and screenshots, see the [illustrated user guide](https://pavise.club/en/docs/).

## What gets restored

| Change | Recovery behavior |
| :--- | :--- |
| **Game-session changes** | Restored from recorded original values when play ends. Recovery is retried at the next launch after an unexpected exit. |
| **Persistent settings** | System Environment settings, application GPU preferences and per-EXE compatibility settings remain until separately restored. Some changes require a reboot. |
| **Input language** | A one-time request to switch to an English layout is not rolled back. |
| **Deleted files or purged caches** | Settings recovery cannot recreate them. Optional League add-on deletion is a separate, explicitly confirmed operation. |

Turning off the guard stops general session management and attempts to restore session changes. Standby policies, persistent settings and game extensions follow their own controls.

<details>
<summary><strong>Recovery tools and local data</strong></summary>

The Settings page provides restoration and uninstall controls. For a broken installation that cannot open, the repository includes [Pavise-Rescue.cmd](Pavise-Rescue.cmd). It exports diagnostics before attempting recovery, resets power plans and **deletes the game library, whitelist and settings**. A restart is required afterward; read the [recovery description](https://pavise.club/en/docs/recovery/) before using it.

Data normally lives in `%AppData%\Pavise`, with interface and feature settings under `HKCU\Software\Pavise`. An empty `Pavise.portable` file beside the executable enables portable storage in the program directory.

</details>

## A closer look

<table>
<tr>
<td width="50%"><img src="docs/screenshots/en/library.png" alt="Pavise game library"><br><strong>Game library</strong><br>Games, recognition and individual profiles.</td>
<td width="50%"><img src="docs/screenshots/en/policy.png" alt="Pavise optimization policies"><br><strong>Optimization policies</strong><br>Control the policies used during play.</td>
</tr>
<tr>
<td width="50%"><img src="docs/screenshots/en/graphics.png" alt="Pavise graphics controls"><br><strong>Graphics</strong><br>Driver controls and GPU policies.</td>
<td width="50%"><img src="docs/screenshots/en/interrupt.png" alt="Pavise device interrupt page"><br><strong>Device interrupts</strong><br>Observe, adjust and compare results.</td>
</tr>
</table>

## Documentation

| Start here | Go deeper |
| :--- | :--- |
| [Illustrated user guide](https://pavise.club/en/docs/) | [Modes in depth](https://pavise.club/en/docs/modes/) |
| [Release notes & downloads](https://pavise.club/en/changelog/#latest) | [Per-game configuration](https://pavise.club/en/docs/profiles/) |
| [Chinese documentation](README.zh-CN.md) | [How scheduling works](https://pavise.club/en/docs/mechanisms/) |
| [Japanese documentation](README.ja.md) | [Memory and power policies](https://pavise.club/en/docs/memory-power/) |

## Build from source

Build on Windows with the .NET Framework 4.x compiler. The build script uses the system compiler; Visual Studio is not required.

```bat
git clone --branch pavise2x https://github.com/dulaiduwang003/Pavise-Game.git
cd Pavise-Game
build.cmd -b dev
```

The output is `build\Pavise.exe`. To build and run the separate regression-test executable:

```bat
build.cmd -b dev build\Pavise.selftest.exe --selftest
build\Pavise.selftest.exe
```

## Contributing

Bug reports, fixes, documentation improvements and translations are welcome.

1. For a bug, [open an issue](https://github.com/dulaiduwang003/Pavise-Game/issues) with your Pavise version, Windows build, hardware, affected game, relevant settings and steps to reproduce. Review diagnostic output for personal information before sharing it.
2. Fork the repository, create a branch from **`pavise2x`**, and keep the change focused. For a larger feature, discuss the approach in an issue first.
3. Explain what changed and how you tested it. Include screenshots for UI changes and relevant logs or regression tests for behavioral fixes.
4. Open a pull request targeting **`pavise2x`**. The maintainer reviews and tests changes before merging.

Please read the [GNU GPLv3](LICENSE) before using, modifying or redistributing the code.

## Contributors

Thank you to everyone who improves Pavise through code, testing, bug reports and translations.

<a href="https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors">
  <img src="https://raw.githubusercontent.com/dulaiduwang003/Pavise-Game/codex/readme-assets/contributors.svg" alt="Pavise contributors — view the complete contributor list on GitHub">
</a>

Contributor avatars refresh automatically after changes reach the default branch and once a day via [GitHub Actions](https://github.com/dulaiduwang003/Pavise-Game/actions/workflows/contributors.yml). The image shows up to 100 contributors from GitHub commit history. [View all contributors](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors) · [Get involved](https://github.com/dulaiduwang003/Pavise-Game/issues)

## Support & community

Created and maintained by **[bdth](https://github.com/dulaiduwang003)**.

- **Website:** [pavise.club](https://pavise.club/en/)
- **Bugs & ideas:** [GitHub Issues](https://github.com/dulaiduwang003/Pavise-Game/issues)
- **Email:** [2074055628@qq.com](mailto:2074055628@qq.com)
- **QQ community:** Group 4 — `166255062` · Group 5 — `1109874913`
- **Support development:** [Donate](https://pavise.club/en/support/). Donations are voluntary; Pavise and its features are free to obtain.

## Licence

Pavise is free software licensed under the **[GNU General Public License v3.0 only](LICENSE)** (`GPL-3.0-only`). Copyright (C) 2026 bdth.

You may use, study, modify and redistribute Pavise, including commercially and for a fee, under GPLv3. When distributing covered works, preserve the copyright and licence notices, identify modifications and their dates, and license the covered work under GPLv3. When distributing binaries, provide recipients with the Corresponding Source as required by the licence.

Pavise is provided **without any warranty**, including merchantability or fitness for a particular purpose. See [LICENSE](LICENSE) for the full terms and [NOTICE](NOTICE) for the project notice.

Official downloads remain free on the [Pavise website](https://pavise.club/en/changelog/#latest). Donations are optional.
