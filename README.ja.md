<div align="center">

<a href="https://pavise.club/en/">
  <img src="docs/icon.png" width="88" height="88" alt="Pavise ロゴ">
</a>

# PAVISE

[English](README.md) · [简体中文](README.zh-CN.md) · **日本語**

### リソースを、ゲームへ。

**無料・オープンソースの Windows ゲーム実行時リソース管理ツール。**<br>
ゲーム中のインテリジェントなバックグラウンド抑制を中心に、既存のハードウェアの性能をゲームに活かします。

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)

**[公式サイト](https://pavise.club/en/) · [ダウンロード](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) · [ユーザーガイド](https://pavise.club/en/docs/v3/) · [問題の報告](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

> このリポジトリではソースコード、ビルド方法、実行可能な回帰テストを提供しています。正式版と更新記録は[リリースページ](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)をご覧ください。

## Pavise の目的

Pavise はゲーム実行時のリソース配分を管理します。バックグラウンド抑制、CPU 配置、重要スレッド、電源などを連携させ、競合を減らし、ゲーム終了後は記録済みの一時変更を復元します。

すべてのプレイヤーが、専門的な Windows ゲーム実行時リソース管理を無料で利用し、より滑らかで安定したゲーム体験を得られることを目指しています。自分のハードウェアを活かすために、高価な最適化ソフトウェアを購入する必要がないように。実装を読めて、技術を検証できることが、Pavise が無料・オープンソースを続ける理由です。

## 現行版の機能

3.3.0.8 の全機能名と子機能は[完全な機能一覧](README.md#complete-feature-list)をご覧ください。各機能は OS、ハードウェア、ドライバー、ゲームの対応状況に応じて提供されます。

| 領域 | 主な機能名 |
| --- | --- |
| **ゲームとバックグラウンド** | ガード、スマート／競技モード、オフ／通常／積極の抑制、CPU・I/O・メモリ・GPU 優先度、EcoQoS、配信の最適化のダウンロード譲渡、依存プロセス保護、ホワイトリスト。 |
| **ライブラリとゲーム別設定** | EXE／実行中のゲーム追加、プラットフォームスキャン、自動登録、無視リスト、検索、名前／カバー変更、強制トリガー、ゲーム一族の制御、全画面最適化、NVIDIA ReBAR。 |
| **CPU とスレッド** | ゲーム用 CPU 範囲、P コア／E コア／CCD／キャッシュ領域、SMT 選択、バックグラウンド分離、描画スレッド自動／手動 CPU 選択、物理コア回避、ゲーム優先度、スレッド優先度。 |
| **電源とタイマー** | 管理電源／既存プラン選択、AC／DC 分岐、ノート PC メーカー連携、携帯ゲーム機互換性、スリープ抑止、タイマー周期、グローバルタイマー分解能、マルチメディアスケジューリング。 |
| **メモリとキャッシュ** | ゲームキャッシュ予熱、待機リスト整理、閾値設定、手動ディープクリーン、ゲーム中の Pavise 自身のメモリ回収、NTFS キャッシュ拡張、シェーダーキャッシュ整理。 |
| **グラフィックス** | 高性能 GPU の事前選択、HAGS、ウィンドウゲーム最適化、Windows／NVIDIA VRR、AMD SAM、NVIDIA 低遅延／最大性能／キャッシュ容量、OpenGL スレッド最適化、Intel Endurance Gaming 制限解除、バックグラウンド録画停止。 |
| **システムと実験室** | システム性能セット、VBS／CPU ソフトウェア緩和、NTFS、前面タイムスライス、ストレージ／音声省電力、EPP 譲渡、GPU クロック／電力上限、低バッファー音声、CPU アイドル方針。 |
| **アンチチートと LoL** | ACE／EasyAntiCheat／BattlEye 独立グループ、3 段階の強度、補助コア配置、互換リスト、付属プロセス整理／復元、ヘッドレスクライアント、追加コンポーネント検査／削除。 |
| **システム診断** | CPU／メモリ／表示／電源／入力／ストレージ／ネットワーク診断、GPU 割り込み観測／配置／再起動後検証／復元、MSI 修復／復元、ドライバー遅延、メモリ圧縮・ネットワーク・有線優先・FTH の修復／復元。 |
| **設定と保守** | 作者設定／極限設定、ログイン時起動、ゲーム開始時に隠す、終了猶予、明暗テーマ、色、16 種類の動作エフェクト、設定リセット、復元してアンインストール。 |
| **ログと更新** | 動作／診断ログ、コピー／出力、AI 調整プロンプト、最新版の更新説明、署名検証付き更新、進捗／取消、失敗時ロールバック、手動ダウンロード。 |

## モードと復元

スマートモードは負荷に応じてバックグラウンドの制御範囲を調整し、ゲームから切り替えると制御を解除します。競技モードは対局中の制御を維持し、切り替えても継続します。ゲーム、ホワイトリスト、必要な依存プロセスは両方で保護されます。電源パラメーターは両モードで共通です。

作者設定／極限設定は一括設定で、スマート／競技とは別に選びます。対応する携帯ゲーム機は自動識別され、電源を Windows とメーカーソフトウェアに任せます。

記録済みのセッション変更はゲーム終了後に復元します。永続設定は個別の復元入口を使い、一部は再起動が必要です。英語入力への切り替えはゲーム終了時に元のレイアウトへ戻すことを試みます。削除したファイルやキャッシュは、設定の復元では再作成できません。

## クイックスタート

**動作要件：** Windows 10 2004（19041）以降、.NET Framework 4.7.2 以降、管理者権限。アプリの UI は英語と簡体字中国語に対応します。

1. [正式版](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)を固定のフォルダーへ展開します。
2. 管理者として起動し、実際のゲーム EXE を追加してホワイトリストとゲーム別設定を確認します。
3. 必要な機能、スマート／競技、バックグラウンド抑制の強度を選び、ガードを有効にします。
4. ゲーム終了後に復元状態を確認します。問題があれば動作／診断ログを出力します。

**他の優先度・CPU アフィニティ制御ソフトウェアとの併用は避けてください。** 互いに設定を上書きする可能性があります。

Pavise は選択された機能に応じてプロセス、サービス、電源、レジストリなどを操作し、一部は PowerShell を使うため、セキュリティソフトウェアが検出・遮断する場合があります。公式配布元を確認したうえで、必要に応じて Pavise を許可リストへ追加してください。安全性に懸念がある場合は、公開ソースを確認するか自分でビルドし、実行するか判断できます。

## ソースからビルドとテスト

Windows 標準の .NET Framework C# コンパイラーと PowerShell を使用します。Visual Studio は不要です。

```bat
build.cmd -b prod
```

出力は `build\Pavise.exe` です。内蔵の更新説明には現行バージョンのみを収録し、過去の更新履歴は[リリースページ](https://github.com/dulaiduwang003/Pavise-Game/releases)に保存します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Run-PublicTests.ps1
```

[テストと台架の説明](tests/README.md) · [ソースガイド](docs/SOURCE-GUIDE.md) · [現行版のユーザーガイド](https://pavise.club/en/docs/v3/)

## コントリビュート

バグ報告、修正、ドキュメントの改善、翻訳を歓迎します。

1. バグは [Issue を作成](https://github.com/dulaiduwang003/Pavise-Game/issues)し、Pavise のバージョン、Windows のビルド、ハードウェア、影響を受けるゲーム、関連する設定、再現手順を添えてください。診断出力を共有する前に個人情報が含まれていないか確認してください。
2. リポジトリを Fork し、**`pavise3x`** からブランチを作成して、変更は焦点を絞ってください。大きな機能は先に Issue で方針を相談してください。
3. 何を変更し、どうテストしたかを説明してください。UI の変更にはスクリーンショットを、動作の修正には関連ログや回帰テストを添えてください。
4. **`pavise3x`** に向けて Pull Request を作成してください。メンテナーがレビューとテストを行ってからマージします。

コードを使用、改変、再配布する前に [GNU GPLv3](LICENSE) をお読みください。

## コントリビューター

コード、テスト、バグ報告、翻訳で Pavise を良くしてくださる皆さんに感謝します。

<a href="https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors">
  <img src="docs/contributors.svg" alt="Pavise のコントリビューター：完全な一覧は GitHub で">
</a>

[コントリビューター一覧](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors) · [参加する](https://github.com/dulaiduwang003/Pavise-Game/issues)

## サポートとコミュニティ

**[bdth](https://github.com/dulaiduwang003)** が作成し、保守しています。

- **公式サイト：** [pavise.club](https://pavise.club/en/)
- **バグと提案：** [GitHub Issues](https://github.com/dulaiduwang003/Pavise-Game/issues)
- **メール：** [2074055628@qq.com](mailto:2074055628@qq.com)
- **QQ コミュニティ：** グループ 4 `166255062` · グループ 5 `1109874913`
- **開発を支援：** [寄付](https://pavise.club/en/support/)。寄付は任意で、Pavise とその機能は無料で入手できます。

## ライセンス

Pavise は **[GNU General Public License バージョン 3 のみ](LICENSE)**（`GPL-3.0-only`）で公開されています。Copyright (C) 2026 bdth。

GPLv3 に従い、利用、学習、改変、再配布ができ、商用利用や有償配布も可能です。対象となる作品を配布する場合は、著作権表示とライセンス表示を保持し、変更内容と日付を明示したうえで、GPLv3 で提供してください。バイナリを配布する場合は、ライセンスの定めに従って受領者に対応するソースコードを提供する必要があります。

本ソフトウェアは、商品性や特定目的への適合性を含め、**一切の保証なく**提供されます。全文は [LICENSE](LICENSE)、プロジェクトの表示は [NOTICE](NOTICE) を参照してください。

公式版は引き続き[公式サイト](https://pavise.club/en/changelog/#latest)で無料配布します。寄付は任意です。
