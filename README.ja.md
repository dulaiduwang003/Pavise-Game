<div align="center">

<a href="https://pavise.club/en/">
  <img src="docs/icon.png" width="96" height="96" alt="Pavise ロゴ">
</a>

# PAVISE

### ゲームに、走る余地を。

バックグラウンドプロセスの制御、ゲームごとのプロファイル、<br>
プレイ後の自動復元を備えた Windows 向けゲームリソースマネージャー。

[English](README.md) · [简体中文](README.zh-CN.md) · **日本語**

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://pavise.club/en/changelog/#latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/stargazers)
[![Contributors](https://img.shields.io/github/contributors/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors)

**[公式サイト](https://pavise.club/en/) &nbsp; / &nbsp; [ダウンロードと更新履歴](https://pavise.club/en/changelog/#latest) &nbsp; / &nbsp; [ユーザーガイド](https://pavise.club/en/docs/)**

このページは日本語の説明書です。アプリの UI は英語と中国語のみのため、以下の画面は英語 UI です。

<br>

<img src="docs/screenshots/en/overview.png" width="100%" alt="Pavise の概要画面：実行中のゲーム、セッション状態、リソース制御">

</div>

<p align="center">
  <a href="#pavise-を選ぶ理由">Pavise を選ぶ理由</a> ·
  <a href="#機能">機能</a> ·
  <a href="#モード">モード</a> ·
  <a href="#クイックスタート">クイックスタート</a> ·
  <a href="#コントリビュート">コントリビュート</a> ·
  <a href="#コントリビューター">コントリビューター</a>
</p>

## Pavise を選ぶ理由

ゲームは、ブラウザー、ランチャー、更新サービス、その他のバックグラウンド処理と 1 台のマシンを共有しています。Pavise はその奪い合いを管理します。プレイ中はゲームのスケジューリング優先度を上げ、対象となるバックグラウンドプロセスが占有するリソースを抑えます。

**ゲームが終了すると、Pavise は記録しておいたセッション変更を自動的に復元します。** Pavise が異常終了した場合は、次回起動時に復元を再試行し、復元できなかった項目の記録を保持します。永続設定とファイル削除には別の復元規則があります。[復元されるもの](#復元されるもの)を参照してください。

| ゲームのために | デスクトップはそのまま | 何が起きたか分かる |
| :--- | :--- | :--- |
| ゲームの自動検出、設定可能なバックグラウンド抑制、ゲームごとのプロファイル。 | 記録したセッション変更はプレイ後に復元され、普段のシステム設定は変わりません。 | セッションレポート、ハードウェア診断、構造化ログ、コピーできる診断サマリー。 |

Pavise はローカルで動作し、本機のデータを送信せず、ゲームプロセスへの注入やゲームメモリの書き換えも行いません。起動時に公式の更新元を 1 回確認します。

CPU や GPU の性能を新たに生み出すものではありません。効果はバックグラウンドの競合状況、ハードウェア、ゲームによって異なります。同じ場面で Pavise のオン・オフを比較してください。もともとクリーンな環境や完全に GPU 律速のゲームでは変化が小さい場合があります。

## 機能

| 領域 | 制御できること |
| :--- | :--- |
| **ゲームライブラリ** | EXE、ショートカット、フォルダーを追加。Steam、Epic、GOG、Ubisoft、Riot、WeGame、Battle.net、Xbox、Microsoft Store をスキャン。ゲームごとに個別のプロファイルを保持。 |
| **バックグラウンドプロセス** | CPU、ディスク IO、ページング、GPU の優先度、EcoQoS、タイマーポリシーをまとめて制御。組み込みの保護規則とホワイトリストで、触れないものを決めます。 |
| **CPU コア** | ゲーム用コアの選択、CCD と SMT のショートカット、システム用コアの確保、通常のバックグラウンドをゲーム用コアの外に留めるオプション。 |
| **グラフィックス** | 対応する NVIDIA、AMD、Intel のドライバー制御、GPU 設定、電力上限、任意の VRAM ポリシー。元の値は記録されます。 |
| **メモリと電源** | 管理電源プラン、セッション中の電源ポリシー、任意のメモリ制御。メモリ整理などの高度なポリシーは個別に設定できます。 |
| **デバイス割り込み** | DPC 活動の観測、セッション間の比較、デバイスとコア候補の確認、再起動後の手動変更の検証。 |
| **診断** | ハードウェア能力、スケジューリング設定、電力・温度制限、セッションレポート、警告の確認。問題を報告する際は診断サマリーをコピーできます。 |
| **ワークフロー** | 英語と中国語の切り替え、ライトとダークのテーマ、機能検索、トレイへの最小化、ホワイトリストの管理。 |

**[すべてのモジュール、スイッチ、制限事項を見る →](https://pavise.club/en/docs/)**

通常のバックグラウンド抑制は、アンチチートプロセス、Windows コアサービス、入力・音声・周辺機器のチェーン、ハードウェアツール、他のログインアカウントを除外します。アンチチートの制御は別のスイッチで既定はオフ、対象は指定されたユーザーモードプロセスのみで、カーネルドライバーは制御しません。ゲーム一族の除外は既定で有効です。

## モード

| モード | バックグラウンドの範囲 | 切り替え後に使うアプリ | 電源とハードウェアのポリシー |
| :--- | :--- | :--- | :--- |
| **スマート** | セッション開始時に対象のバックグラウンドプロセスを隔離します。 | 使用中のアプリとその一族は除外されます。 | 任意の自動引き上げは既定でオフです。 |
| **eスポーツ** | 抑制範囲を対象のゲーム以外のプロセスへ広げます。ウィンドウのあるアプリも含みます。 | 抑制対象のままです。ホワイトリストと組み込みの保護は有効です。 | 追加ポリシーは引き続き設定できます。 |
| **携帯機** | eスポーツと同じバックグラウンド範囲です。 | eスポーツと同じです。 | 電力制御はベンダーツールに任せます。バッテリーが必要で、デスクトップ向けのポリシーはいくつか提供しません。 |
| **カスタム** | バックグラウンド、コア、グラフィックス、メモリ、電源、環境のポリシーを個別に選びます。 | 選んだポリシーによります。 | 全体で調整するか、ゲームごとに上書きします。 |

本機で使えないモードは表示されません。以前の極限モードは v2.2.2 で削除され、その追加項目はそれぞれ独立したスイッチになりました（既定はオフ）。[モードの詳細](https://pavise.club/en/docs/modes/)。

## クイックスタート

**動作要件：** Windows 10 バージョン 2004（ビルド 19041）以降、Windows 11 24H2 を推奨。リソース管理には管理者権限が必要です。アプリの UI は**英語と簡体字中国語**に対応し、日本語はドキュメントのみです。

1. **Pavise を入手**：[公式ダウンロードページ](https://pavise.club/en/changelog/#latest)で更新履歴を読み、GitHub または Quark を選びます。ダウンロードは無料です。
2. **Pavise を開き**、ゲームの EXE、ショートカット、フォルダーをライブラリに追加するか、インストール済みのゲームをスキャンします。
3. **モードを選び**、その設定を確認します。影響を受けたくないアプリはホワイトリストに追加します。必要ならゲームごとのプロファイルを使います。
4. **ガードを有効にしてプレイします。** 検出とセッション管理は自動で行われ、ゲームを最小化してもセッションは終わりません。
5. **ゲームを終了します。** Pavise は記録したセッション変更を復元します。セッションレポートや診断サマリーで何が起きたかを確認できます。

実行ファイルは現在署名されていません。詳しい設定手順とスクリーンショットは[図解ユーザーガイド](https://pavise.club/en/docs/)を参照してください。

## 復元されるもの

| 変更 | 復元の動作 |
| :--- | :--- |
| **ゲームセッションの変更** | プレイ終了時に記録した元の値から復元します。異常終了後は次回起動時に再試行します。 |
| **永続設定** | システム環境の設定、アプリの GPU 設定、EXE ごとの互換設定は個別に復元するまで残ります。一部は再起動が必要です。 |
| **入力言語** | 英語レイアウトへの一度きりの切り替え要求は戻しません。 |
| **削除したファイルや消去したキャッシュ** | 設定の復元では再作成できません。任意の LOL 追加コンポーネント削除は、明示的に確認する別操作です。 |

ガードをオフにすると汎用のセッション管理を停止し、セッション変更の復元を試みます。待機ポリシー、永続設定、ゲーム拡張はそれぞれの制御に従います。

<details>
<summary><strong>復元ツールとローカルデータ</strong></summary>

設定ページに復元とアンインストールの操作があります。開けなくなったインストールには、リポジトリの [Pavise-Rescue.cmd](Pavise-Rescue.cmd) を使います。復元を試みる前に診断情報を書き出し、電源プランをリセットし、**ゲームライブラリ、ホワイトリスト、設定を削除します**。実行後は再起動が必要です。使う前に[復元の説明](https://pavise.club/en/docs/recovery/)を読んでください。

データは通常 `%AppData%\Pavise` に保存され、UI と機能の設定はレジストリの `HKCU\Software\Pavise` にあります。実行ファイルの隣に空の `Pavise.portable` ファイルを置くと、プログラムのディレクトリに保存するポータブルモードになります。

</details>

## 画面

<table>
<tr>
<td width="50%"><img src="docs/screenshots/en/library.png" alt="Pavise のゲームライブラリ"><br><strong>ゲームライブラリ</strong><br>ゲーム、認識、個別プロファイル。</td>
<td width="50%"><img src="docs/screenshots/en/policy.png" alt="Pavise の最適化ポリシー"><br><strong>最適化ポリシー</strong><br>プレイ中に使うポリシーを制御します。</td>
</tr>
<tr>
<td width="50%"><img src="docs/screenshots/en/graphics.png" alt="Pavise のグラフィックス制御"><br><strong>グラフィックス</strong><br>ドライバー制御と GPU ポリシー。</td>
<td width="50%"><img src="docs/screenshots/en/interrupt.png" alt="Pavise のデバイス割り込みページ"><br><strong>デバイス割り込み</strong><br>観測し、調整し、結果を比較します。</td>
</tr>
</table>

## ドキュメント

| まずはここから | さらに詳しく |
| :--- | :--- |
| [図解ユーザーガイド](https://pavise.club/en/docs/) | [モードの詳細解説](https://pavise.club/en/docs/modes/) |
| [更新履歴とダウンロード](https://pavise.club/en/changelog/#latest) | [ゲームごとの設定](https://pavise.club/en/docs/profiles/) |
| [英語ドキュメント](README.md) | [スケジューリングの仕組み](https://pavise.club/en/docs/mechanisms/) |
| [中国語ドキュメント](README.zh-CN.md) | [メモリと電源のポリシー](https://pavise.club/en/docs/memory-power/) |

## ソースからビルド

Windows 上で .NET Framework 4.x のコンパイラーを使ってビルドします。ビルドスクリプトはシステムのコンパイラーを使うため、Visual Studio は不要です。

```bat
git clone --branch pavise2x https://github.com/dulaiduwang003/Pavise-Game.git
cd Pavise-Game
build.cmd -b dev
```

出力は `build\Pavise.exe` です。回帰テスト用の実行ファイルを別にビルドして実行するには次のようにします。

```bat
build.cmd -b dev build\Pavise.selftest.exe --selftest
build\Pavise.selftest.exe
```

## コントリビュート

バグ報告、修正、ドキュメントの改善、翻訳を歓迎します。

1. バグは [Issue を作成](https://github.com/dulaiduwang003/Pavise-Game/issues)し、Pavise のバージョン、Windows のビルド、ハードウェア、影響を受けるゲーム、関連する設定、再現手順を添えてください。診断出力を共有する前に個人情報が含まれていないか確認してください。
2. リポジトリを Fork し、**`pavise2x`** からブランチを作成して、変更は焦点を絞ってください。大きな機能は先に Issue で方針を相談してください。
3. 何を変更し、どうテストしたかを説明してください。UI の変更にはスクリーンショットを、動作の修正には関連ログや回帰テストを添えてください。
4. **`pavise2x`** に向けて Pull Request を作成してください。メンテナーがレビューとテストを行ってからマージします。

コードを使用、改変、再配布する前に [GNU GPLv3](LICENSE) をお読みください。

## コントリビューター

コード、テスト、バグ報告、翻訳で Pavise を良くしてくださる皆さんに感謝します。

<a href="https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors">
  <img src="https://raw.githubusercontent.com/dulaiduwang003/Pavise-Game/codex/readme-assets/contributors.svg" alt="Pavise のコントリビューター：完全な一覧は GitHub で">
</a>

コントリビューターのアバターは、変更が既定ブランチに入ったときと 1 日 1 回、[GitHub Actions](https://github.com/dulaiduwang003/Pavise-Game/actions/workflows/contributors.yml) で自動更新されます。画像には GitHub のコミット履歴から最大 100 名を表示します。[コントリビューター一覧](https://github.com/dulaiduwang003/Pavise-Game/graphs/contributors) · [参加する](https://github.com/dulaiduwang003/Pavise-Game/issues)

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
