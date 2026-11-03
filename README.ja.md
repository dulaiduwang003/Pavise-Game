<div align="center">

<a href="https://pavise.club/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise logo">
</a>

# PAVISE

### ゲームに使えるリソースを。

Windows 向けゲームリソース管理ツール。<br>バックグラウンド、スケジューリング、電源の動作を選び、対局終了後に記録済みの変更を復元します。

**ゲーム別設定 · スマート／競技 · 記録に基づく復元**

[简体中文](README.md) · [English](README.en.md) · **日本語**

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[公式サイト](https://pavise.club/en/) · [最新版](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) · [使い方](https://pavise.club/en/docs/) · [問題を報告](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

> **ソースコードとドキュメントは整理中です。準備が整い次第、このリポジトリにアップロードします。** 現在のコードは過去のバージョンです。現行の動作は公開済みアプリとリリースノートを参照してください。

## Pavise でできること

ゲームは、ブラウザー、ランチャー、更新タスクと PC のリソースを共有します。Pavise は保存した設定に従って対局中のリソース配分を調整し、終了後に記録済みの対局変更を復元します。

有効にする機能は自分で選びます。守護はゲームを識別すると設定を適用します。ゲームごとに設定を保存でき、保護ルールとホワイトリストで対象外のアプリを管理できます。

| 項目 | 主な機能 |
| --- | --- |
| **ゲームライブラリと識別** | ゲームの追加、インストール先や実行中プログラムの検索、ゲーム別設定。最小化・トレイ常駐アプリも選択できます。 |
| **バックグラウンド制御** | 対象プロセスの CPU、I/O、メモリページ、GPU スケジューリングを調整。除外設定と、対局中の更新・ダウンロード・保守タスクの一時停止。 |
| **ゲームとコアの制御** | 優先度、スレッド優先度、論理コア選択、CCD／SMT 一括選択、コア分離とバックグラウンドのコア回避。 |
| **電源管理** | CPU、端末種別、スマート／競技の選択に応じてプランを適用。画面、スリープ、蓋、バッテリーの設定を保持し、既存プランや「切り替えない」も選択可能。 |
| **グラフィックスとメモリ** | 対応 GPU の性能設定、グラフィックス設定、キャッシュの事前読み込み、スタンバイメモリ整理を個別に選択。 |
| **実験室** | CPU アイドル、EPP 電力配分、GPU クロック／電力、低バッファー音声。利用可否はハードウェアとドライバーによります。 |
| **システム診断** | 設定とデバイス状態の確認。GPU 割り込みの観測・調整・再起動後の検証・復元。MSI の修復と復元は別操作。 |
| **復元と更新** | 元の値の記録、未完了復元の再試行、ログ、診断概要、AI 調整用説明の出力、進捗と署名検証に対応したアプリ内更新。 |

概要には、その対局で**適用を確認したプロセス抑制数とサービス停止数**を表示します。機能の利用可否は Windows、ハードウェア、ドライバーによります。効果は実際のゲームとバックグラウンド負荷で判断してください。

## シーンと設定プリセット

- **スマート**：ゲームと普段のアプリ操作の両立を考慮した制御。
- **競技**：前面アプリの保護、ホワイトリスト、機能選択を守りながら、対局向けの制御を強めます。
- **デフォルト／極限プリセット**：機能設定を一括適用します。シーンとは別に選び、ハードウェア対応と現在の状態を確認して実行します。

## ダウンロードと使い始め方

1. [公式サイト](https://pavise.club/en/changelog/#latest)または [GitHub Releases](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) から配布パッケージをダウンロード。
2. 固定のフォルダーに展開し、`Pavise.exe`、同名の `.exe.config`、同梱ファイルをまとめて保持し、管理者として実行。
3. ゲームを追加し、シーン、機能、ホワイトリストを確認して守護を有効化。
4. 終了後に復元状態を確認。永続設定は各項目の復元手順に従い、必要な場合は再起動。

必要環境：**Windows 10 2004（19041）以降、.NET Framework 4.7.2 以降**。Windows 11 に対応し、各機能はシステムとデバイスの能力に応じて提供します。

### 更新とバージョン情報

**3.2.0.2** 以降は「バージョン情報 → 更新を確認」から進捗を見ながらダウンロードし、検証後にアプリを置き換えられます。ゲームライブラリ、ホワイトリスト、設定を保持します。**3.2.0.1 以前は一度手動で更新してください。** 更新前にゲームを終了してください。

フレームレート検出、フローティング OSD、対局レポート、RTSS 連携は削除されました。詳細は[リリース履歴](https://github.com/dulaiduwang003/Pavise-Game/releases)を参照してください。

## ドキュメントとフィードバック

[使い方](https://pavise.club/en/docs/) · [更新履歴](https://pavise.club/en/changelog/#latest) · [問題を報告](https://github.com/dulaiduwang003/Pavise-Game/issues)

報告には Pavise と Windows のバージョン、CPU／GPU、ゲーム、関連設定、再現手順を添えてください。必要に応じてログや診断概要も追加してください。

## 作者と支援

**[bdth](https://github.com/dulaiduwang003)** が作成・保守しています。Pavise は無料で、**寄付は任意**です。利用、フィードバック、開発支援に感謝します。

[開発を支援](https://pavise.club/support/) · [作者プロフィール](https://github.com/dulaiduwang003)

<p align="center">
  <img src="docs/wechat.png" width="220" alt="作者の WeChat 寄付用 QR コード">
  <img src="docs/alipay.png" width="220" alt="作者の Alipay 寄付用 QR コード">
</p>

ライセンスと表示事項は [LICENSE](LICENSE)、[NOTICE](NOTICE) を参照してください。
