<!-- Generated from docs/readme/shared.json and docs/readme/locales/ja-JP.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | **日本語** | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian で支援](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**アプリごとに、音声の出力先デバイスを選べます。**

SonicRoute は Windows の通知領域に常駐するオーディオツールです。アプリごとに出力・入力デバイスを選び、音楽はスピーカーで、ゲームや通話はヘッドセットで再生できます。ホットキー、通知領域のホイール、パネルで音声を操作し、日常の操作を自動化ルールにまとめられます。

📥 [最新リリースをダウンロード](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [ユーザーガイド](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（実験的） · 9 種類の UI 言語 · MIT ライセンス

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
- **Android スマホ連携＆独立仮想ディスプレイ (Full 版)**：スマホ音声を PC に取り込んでアプリ別音量調整・出力、PC 音声をスマホへ送信可能。スマホアプリを PC 上の独立した仮想画面ウィンドウで適応表示し、クイックパネルからスマホスピーカーや Bluetooth を操作・スマート音量で均一化。
- **自動化コンボエンジンと高速 CLI（6種のトリガー × 39種のアクション）：** アプリ起動・切り替え・終了、スケジュール、ショートカットなど 6 つのトリガーに対応。デバイス切り替え、音量、ミュート、PowerShell などを自由に組み合わせ可能。名前付きパイプによる sr コマンドで外部連携も容易です。
- **イベント優先の検出**：システム通知を活用し、必要に応じて1秒ごとのスナップショットで補完。不要データはオンデマンドで解放。スマートフォン連携はFullのみで、LiteとLegacyはPC機能のみです。9言語に対応。

## 🎮 日常での使い方

面倒な手動設定を、一度設定すればずっと快適な自動化体験へ。 [自動化を設定](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 日常のシチュエーション | SonicRoute による解決策 |
|---|---|
| 🎮 **ボイスチャットがうるさい、またはゲーム音で声が聞こえない** | Discord などの通話音量を個別に調整し、ゲーム音はそのまま維持。ヘッドセットと通話を分離可能 |
| 🎵 **ゲームをプレイしながら裏で音楽や動画を流したい** | 音楽はスピーカー、ゲームはヘッドホンへ。ゲームを中断せず、トレイ領域でホイール操作して音楽の音量調整やAlt+ホイールで出力デバイスを切替 |
| ⚡ **ゲーム起動のたびにヘッドセットを繋ぎ直したり設定を変えている** | ゲーム起動時に「ヘッドセットへ切り替え＋音量40%＋通話起動」、終了時にスピーカーへ自動復帰 |
| 🎙️ **席を外す時やプライベートな通話への切り替え** | Ctrl+Shift+N で全マイクを一括ミュート。画面上の OSD バナーで消音状態を一目で確認 |
| 📹 **OBS でのゲーム配信や画面録画チュートリアル** | アプリごとに独立した音声トラックを割り当て、プライベートな会話や通知音が録画に入るのを防止 |
| 📱 **スマホの音声を PC で再生、または PC 上でスマホアプリを操作** | スマホの音楽・動画・ゲーム音声を PC ヘッドホンやスピーカーで個別調整。スマホ画面を独立した仮想ウィンドウで操作し、スマホのスピーカー／Bluetooth もクイックパネルから直接制御可能。 |

## 🚀 ダウンロードと使い始め方

**現在のリリース：[v1.32](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.32)** · 2026-11-14 · [リリースノート（簡体字中国語）](docs/release-v1.32.md)

Androidスマートフォンの音声・仮想ディスプレイ機能を使う場合は **Full x64**、PCの音声機能のみなら **Lite x64**、.NET Framework 4.8を利用する環境では **Legacy x64** を選択してください。ARM64版は実験的です。スマートフォン連携はFullのみです。Store版はFullを基に構築されますが、現在配信中の版はStoreページでご確認ください。以下は v1.32 のリンクです。

| エディション | ダウンロード | 要件 |
|---|---|---|
| ⭐ Full (.NET 10、スマホ連携対応) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-x64.exe) / [ARM64（実験的）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-arm64.exe) | 単一 EXE ポータブル版。アーキテクチャに適合した .NET 10 Desktop Runtime が必要。オプションの Android 音声・仮想ディスプレイ連携を含む。 |
| ⭐ Lite (.NET 10、PC 音声のみ) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Lite-x64.exe) / [ARM64（実験的）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Lite-arm64.exe) | 単一ファイルポータブル版。アーキテクチャに適合した .NET 10 Desktop Runtime が必要。PC 音声機能のみ。 |
| 🛡️ Legacy x64 (PC 音声のみ、.NET Framework 4.8) | [完全版 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Legacy-x64.zip) | 追加ランタイム不要。制限された環境向け。標準の .NET Framework 4.8 で解凍後すぐ利用可能。 |
| 🔄 Microsoft Store (Full ベース) | [Store からインストール](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | Full をベースにビルド。x64 .NET 10 Desktop Runtime が必要。提供バージョンは Store ページに準拠。 |

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

### 🆕 v1.32 リリースの主な改善点

v1.32ではFullに純粋C#製Androidバックエンドを統合し、外部adb/scrcpy/FFmpeg/SDL2バイナリを廃止して単一EXEを大幅に軽量化しました。仮想ディスプレイのゲームパッド対応、スマホパネル間隔の調整（6 DIP）、スマホ「システム出力」のWindows既定再生デバイス自動追従や6件の不具合修正を追加し、全エディション（Full/Lite/Legacy）でトレイの長い名前によるサブメニュー重なり修正と待機時メモリ・リソース最適化を実施しています。詳細は [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

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

## 🧩 その他のプロジェクトと組み合わせ方

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**：Windows 向けオーディオプレーヤーです。「プログラムを起動」アクションと組み合わせると、ホットキーで効果音を再生できます。 [設定ガイド（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)
- **[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**：卵から飛び出した魔法少女と、手伝うたびに事態が悪化するハチャメチャな日常を描くギャグ漫画。

---

<a href="EASTER_EGGS.md" title="昨日と明日のイースターエッグ"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="休んでいるマスコットをクリックしてイースターエッグを見つける"></a>

SonicRoute をご利用いただきありがとうございます。 [ガイドを読む（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [問題を報告](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
