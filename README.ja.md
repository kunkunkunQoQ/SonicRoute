<!-- Generated from docs/readme/shared.json and docs/readme/locales/ja-JP.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | **日本語** | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian で支援](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**アプリごとに、音声の出力先デバイスを選べます。**

SonicRoute は Windows の通知領域に常駐するオーディオツールです。アプリごとに出力・入力デバイスを選び、音楽はスピーカーで、ゲームや通話はヘッドセットで再生できます。ホットキー、通知領域のホイール、パネルで音声を操作し、日常の操作を自動化ルールにまとめられます。

📥 [最新リリースをダウンロード](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [ユーザーガイド](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（実験的） · 9 種類の UI 言語 · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="SonicRoute ライブデモ：モバイルアプリの音声を PC クイックパネルへドラッグして出力先を切り替え"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="SonicRoute 自動化ルールエディター：アプリトリガーとオーディオアクションの手順選択"></a>
</div>

<sub>UI プレビュー：左側はスマホ音声のドラッグ＆ドロップ分流と出力切り替えデモ、右側はトリガーとアクションを自在に組める自動化エディター。</sub>

<details>
<summary><sub>🖼️ 他のスクリーンショットを表示（クイックパネル・スマホ管理）</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute クイックパネル：上部の出力デバイスとアプリごとの音量操作"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="SonicRoute スマートフォン連携管理：Android デバイス接続、ピン留めアプリ、仮想ディスプレイ音声設定"></a>
</div>

</details>

## ✨ SonicRoute の主な機能

- **アプリごとの音声分離と双方向ルーティング（再生＋録音）：** アプリごとに再生・録音デバイスを個別に設定。ゲームや通話はヘッドセットへ、音楽はスピーカーへ振り分けられます。設定は Windows にネイティブ保存され、アプリ終了後も維持されます。
- **フォアグラウンド追従とトレイホイール直感操作：** システムイベントにより音声アプリを瞬時に特定。タスクバーのトレイ領域でホイール操作による音量調整やAlt+ホイールでの出力先切替が可能。画面OSD表示とマイクミュートも備え、ウィンドウを切替えずに操作できます。
- **クイックパネルとリアルタイム音声レベル表示：** カード型パネルは350〜850pxの高さ調整と位置の保存に対応。各アプリの音量スライダーとリアルタイム音声メーターで直感的に操作できます。
- **Android 連携＆独自 AirPlay 受信（Full 版）**：Android の音声を PC に取り込んで個別調整・仮想画面操作（H.265・フォアグラウンド時ゲームパッド対応）でき、PC 音声のスマホ送信も可能。独自 C# 製 AirPlay 音声・等比ミラーリング受信を追加し、クイックパネルから PC 出力や接続済み Android スピーカー／Bluetooth へ切り替え、SmartVolume で音量を均一化できます。
- **自動化コンボエンジンと高速 CLI（6種のトリガー × 39種のアクション）：** アプリ起動・切り替え・終了、スケジュール、ショートカットなど 6 つのトリガーに対応。デバイス切り替え、音量、ミュート、PowerShell などを自由に組み合わせ可能。名前付きパイプによる sr コマンドで外部連携も容易です。
- **イベント優先の検出**：システム通知を活用し、必要に応じて1秒ごとのスナップショットで補完。不要データはオンデマンドで解放。Android連携・仮想ディスプレイ・AirPlay機能はFullのみで、LiteとLegacyはPC機能のみです。9言語に対応。

## 🎮 日常での使い方

面倒な手動設定を、一度設定すればずっと快適な自動化体験へ。 [自動化を設定](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 日常のシチュエーション | SonicRoute による解決策 |
|---|---|
| 🎮 **ボイスチャットがうるさい、またはゲーム音で声が聞こえない** | Discord などの通話音量を個別に調整し、ゲーム音はそのまま維持。ヘッドセットと通話を分離可能 |
| 🎵 **ゲームをプレイしながら裏で音楽や動画を流したい** | 音楽はスピーカー、ゲームはヘッドホンへ。ゲームを中断せず、トレイ領域でホイール操作して音楽の音量調整やAlt+ホイールで出力デバイスを切替 |
| ⚡ **ゲーム起動のたびにヘッドセットを繋ぎ直したり設定を変えている** | ゲーム起動時に「ヘッドセットへ切り替え＋音量40%＋通話起動」、終了時にスピーカーへ自動復帰 |
| 🎙️ **席を外す時やプライベートな通話への切り替え** | Ctrl+Shift+N で全マイクを一括ミュート。画面上の OSD バナーで消音状態を一目で確認 |
| 📹 **OBS でのゲーム配信や画面録画チュートリアル** | アプリごとに独立した音声トラックを割り当て、プライベートな会話や通知音が録画に入るのを防止 |
| 📱 **スマホの音声を PC で再生、または PC 上でスマホアプリを操作** | Android の音声を PC で個別調整し仮想ウィンドウでアプリ操作できるほか、Apple 端末の AirPlay 音画ミラーリングも受信可能。クイックパネルから PC 出力や Android スピーカー／Bluetooth も直接切り替えられます。 |

## 🚀 ダウンロードと使い始め方

**現在のリリース：[v1.33](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.33)** · 2026-11-18 · [リリースノート（簡体字中国語）](docs/release-v1.33.md)

Androidスマートフォン連携・仮想ディスプレイ・AirPlay機能を使う場合は **Full x64**、PCの音声機能のみなら **Lite x64**、.NET Framework 4.8環境では **Legacy x64** を選択してください。ARM64版は実験的です。スマートフォン・AirPlay連携はFullのみに含まれます。Microsoft StoreではFullとLiteを提供しており、現在の配信状況はStoreページでご確認ください。以下は v1.33 のリンクです。

| エディション | ダウンロード | 要件 |
|---|---|---|
| ⭐ Full (.NET 10、スマホ連携対応) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-x64.exe) / [ARM64（実験的）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-arm64.exe) | 単一 EXE ポータブル版。アーキテクチャに適合した .NET 10 Desktop Runtime が必要。オプションの Android 連携・仮想ディスプレイ・AirPlay 機能を含む。 |
| ⭐ Lite (.NET 10、PC 音声のみ) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-x64.exe) / [ARM64（実験的）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-arm64.exe) | 単一ファイルポータブル版。アーキテクチャに適合した .NET 10 Desktop Runtime が必要。PC 音声機能のみ。 |
| 🛡️ Legacy x64 (PC 音声のみ、.NET Framework 4.8) | [完全版 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Legacy-x64.zip) | 追加ランタイム不要。制限された環境向け。標準の .NET Framework 4.8 で解凍後すぐ利用可能。 |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full / Lite 版を提供。x64 .NET 10 Desktop Runtime 必須。提供状況は Store に準拠。 |

<details>
<summary><sub>📦 過去の NET8 互換版（保守終了・過去のダウンロードあり・クリックで展開）</sub></summary>

| エディション | ダウンロード | 要件 |
|---|---|---|
| Lite NET8 (保守終了) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64（実験的）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | 更新は終了しています。過去のダウンロードは引き続き利用できます。.NET 10 または Legacy への移行を推奨します。 |
</details>

> [!NOTE]
> FullとLiteには [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) が必要です。LegacyはWindows標準の.NET Framework 4.8で動作し、追加導入は不要です（旧 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 互換版は保守終了・過去のダウンロードは継続）。

### 🏁 3ステップで今すぐ始める

1. **起動してタスクトレイに常駐**：解凍またはダブルクリックで起動するとタスクトレイに常驻します（いつでも確認できるようオーバーフローからドラッグして固定を推奨）。トレイアイコンをダブルクリックでメイン画面を開きます。
2. **デバイス選択と個別ルーティング**：「設定 → 保持するデバイス」でお手持ちのヘッドホンやスピーカーにチェックを入れます。トレイアイコンをクリックしてクイックパネルを開き、スライダーでアプリごとに音量調整と出力先切り替えを行います。
3. **トレイ上でのブラインド操作**：通知領域にカーソルを合わせてホイール操作するだけでアプリ音量を即座に微調整、Alt+ホイールで出力先を即時切り替え、Shift+ホイールでシステムマスター音量を調整できます。ゲーム画面を閉じることなく快適に操作できます。

### ⚡ コマンドラインと自動化連携（上級者向け）

`sr` コマンドは名前付きパイプを使用して、起動中の SonicRoute インスタンスとミリ秒単位で高速連携します（未起動時には起動しません）。初回起動時に自動登録されるため、ターミナルを再起動すればスクリプト、AutoHotkey、Stream Deck から直接呼び出せます。不明点は [よくある質問](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) をご確認ください。

```bash
# 自動化ルールを実行（SonicRoute が起動中である必要があります）
sr "ルール名または ID"
```

### 🆕 v1.33 リリースの主な改善点

v1.33ではFullに独自C#製AirPlay音声・等比画面ミラーリング受信（PCM/ALAC/AAC-LC/AAC-ELD、任意PIN、最前面/F11全画面、PCやAndroidスピーカー/Bluetoothへのドラッグ転送）、H.265 (HEVC) と音声/映像デコーダー選択、表示中のみ実行するエンコーダー取得、フォアグラウンド限定ゲームパッド制御、QR（2分自動更新）と手動ペアリング統合カード、保存済み無線デバイスのLAN検索・再接続（候補ポート検証による無効ポート除外、保存済み識別子での新IP/ポート再接続、未検出・失敗時の手動入力保持）と設定復元を追加しました。全エディション（Full/Lite/Legacy）で独立した2つのマイク常駐OSDスイッチ、1行構成の設定管理カード、20行超アプリリスト仮想化、OSD統合最適化、PNG元バイト・未使用テクスチャ解放、Fullでのウィンドウ閉鎖時明示的GC要求の廃止（ランタイムの自然回収へ委任）、config .bak保護、エラー時診断ログ、Store Lite対応およびGPL-3.0-onlyオフライン閲覧に対応しています。詳細は [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

### ❓ よくある質問（FAQ）

**Q：SonicRoute を終了してもアプリのルーティングや音量設定は維持されますか？**

A：SonicRouteを終了しても、Windowsのアプリ別出力先と既存の音声セッションは維持されます。ホットキー、トレイ操作、自動化、スマートフォン音声、画面ミラーリング、クリップボード同期にはSonicRouteの実行が必要です。

**Q：各エディションに必要な Runtime は何ですか？**

A：FullとLiteには対応する.NET 10 Desktop Runtimeが必要です。Legacyは.NET Framework 4.8を使用し、Store版にはx64の.NET 10 Desktop Runtimeが必要です。旧NET8版の保守は終了していますが、過去のファイルは引き続きダウンロードできます。

**Q：バックグラウンド常駐中に CPU やバッテリーを消費しますか？**

A：検出はシステムイベントを優先し、必要な場合に限り1秒間隔のスナップショット確認で補います。不要なデータは必要に応じて整理します。実際の CPU 使用量は環境と利用状況によって異なります。

## 📚 ドキュメント

詳細な Wiki は現在、簡体字中国語で公開されています。

| トピック | ガイド |
|---|---|
| インストール、パネル、日常の操作 | [ユーザーガイド](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [機能](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 既定のキー、割り当て、操作対象 | [ホットキー](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| トリガー、アクション、スクリプト連携 | [自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [コマンドライン](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| インストールとトラブルシューティング | [よくある質問](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| ソース、ビルド、リリース | [ソースコードとビルド](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [リリース情報](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **AIによる高速要約と省トークンでのトラブルシューティング**：Cursor、Copilot、ChatGPT、Claude などの AI アシスタントで不具合の調査、設計の把握、スクリプト作成を行う際は、専用の機械仕様書 [README.ai.md](README.ai.md) を直接参照することをお勧めします。LLM 向けの高密度構造化 DSL で整理されており、冗長な文章を排除してコンテキストのトークン消費を大幅に削減し、素早く原因を特定できます。

## 📌 ロードマップ

- **音声ルーティング機能の向上（Route Enhancement）**：コア・ルーティング・エンジンのさらなる進化を計画中。デバイスの抜き差し時の自動復旧や動的ストリーム切り替え、マルチセッションアプリでのルーティング安定性向上を進めます。

[アイデアの共有・問題の報告](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 📜 ライセンス

- **GPL-3.0 オープンソース**：SonicRoute は GNU General Public License 第 3 版のみ（GPL-3.0-only）で提供されます。[LICENSE](LICENSE) を参照してください。GPL の条件に従う商用利用、販売、有償配布が可能です。
- **再配布**：必要な著作権表示とライセンス表記を保持し、変更を明記し、対象となる改変作品を GPL 第 3 版で配布してください。対象のバイナリを配布する場合は GPL 第 6 条に従い完全な対応ソースを提供してください。詳細は [LICENSE](LICENSE) を参照してください。
- **適用範囲と従来の許諾**：2026-11-17 の GPL 移行から、著作権者が許諾できる範囲で、本リビジョンの対象プロジェクト全体（以前のコードを含む）を GPL-3.0-only で提供します。既存の MIT および旧独自ライセンスによる許諾は有効で、既存タグや公開済みバイナリの表示は変更しません。第三者コンポーネントは元のライセンスを保持します（[サードパーティライセンス表記](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)）。

---

<a href="EASTER_EGGS.md" title="昨日と明日のイースターエッグ"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="休んでいるマスコットをクリックしてイースターエッグを見つける"></a>

SonicRoute をご利用いただきありがとうございます。 [ガイドを読む（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [問題を報告](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
