<div align="center">

<a href="https://pavise.club/"><img src="docs/icon.png" width="76" height="76" alt="Pavise logo"></a>

# PAVISE

### Resource orchestration, bounded by the game session.

A Windows resource-control system built around game identity,<br>
explicit user intent, and recorded recovery.

**English** · [简体中文](README.md) · [日本語](README.ja.md)

**The 3.1.0.6 source is being organized and will be uploaded in a few days.** Historical source in the repository does not yet represent this release.

[![GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-bf2949?style=flat-square&labelColor=17242c)](LICENSE)
[![Release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=17242c&color=bf2949)](https://pavise.club/changelog/#latest)

**[Website](https://pavise.club/) · [Download](https://pavise.club/changelog/#latest) · [User guide](https://pavise.club/docs/)**

</div>

## System design

<a href="docs/pavise-architecture.svg"><img src="docs/pavise-architecture.png" width="100%" alt="Pavise component architecture: implementation modules, dependencies, native interfaces, and session state transitions"></a>

*Figure 1. Component dependencies and session lifecycle. [Vector original](docs/pavise-architecture.svg).*

Pavise treats resource control as a bounded game session. It identifies the game and related processes, resolves saved choices into an effective policy, applies supported controls through Windows native interfaces, and restores recorded changes when the session ends.

**Scope before strength.** Process identity, foreground state, and CPU topology establish the target. Game execution and background contention follow separate paths, subject to protection rules and runtime support.

**Intent separate from observation.** Saved configuration defines what is requested; current conditions determine what can be applied. Telemetry supports observation and comparison, while performance choices remain with the user.

**Recovery as part of control.** Applicable changes retain original values and recovery records. Exit triggers reconciliation; unfinished recovery remains pending. Persistent settings and cache operations follow their own lifecycles.

## Interface

<img src="docs/overview-3.1.0.6.en.png" width="100%" alt="Current Pavise overview with ROG-inspired styling and compact bottom navigation">

*3.1.0.6 UI. Screenshot uses demonstration data; it is not a benchmark. *

<details>
<summary>Light theme</summary>

<img src="docs/overview-light-3.1.0.6.en.png" width="100%" alt="Pavise light theme">

</details>

## Run

Windows 10 2004 / build 19041 or newer, .NET Framework 4.7.2+, and administrator rights for resource control.

Download and extract the complete package. Keep `Pavise.exe`, `Pavise.exe.config`, `LICENSE`, and `NOTICE` together. Add a game to the library, review the saved policy, then enable Guard. See the [user guide](https://pavise.club/docs/) for configuration and recovery details.

Effects depend on the game, hardware, and selected settings. Compare under the same workload; enabling more controls does not guarantee better performance.

## Project

[Issues](https://github.com/dulaiduwang003/Pavise-Game/issues) · [Contributors](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors) · [Author: bdth](https://github.com/dulaiduwang003) · [Support development](https://pavise.club/support/)

For the current release, use the verified download package. Source publication will follow; report issues with your version, environment and reproduction steps.

Pavise is free to obtain; donations are optional. QQ communities: `166255062` / `1109874913`.

<details>
<summary>Support the author</summary>

<img src="docs/wechat.png" width="180" alt="Author's WeChat donation QR code"> <img src="docs/alipay.png" width="180" alt="Author's Alipay donation QR code">

</details>

**License:** [GPL-3.0-only](LICENSE). Copyright (C) 2026 bdth. See [NOTICE](NOTICE).
