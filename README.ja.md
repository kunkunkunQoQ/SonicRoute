<!-- Generated from docs/readme/shared.json and docs/readme/locales/ja-JP.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | **日本語** | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian で支援](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**アプリごとに、音声の出力先デバイスを選べます。**

SonicRoute は Windows の通知領域に常駐するオーディオツールです。アプリごとに出力・入力デバイスを選び、音楽はスピーカーで、ゲームや通話はヘッドセットで再生できます。ホットキー、通知領域のホイール、パネルで音声を操作し、日常の操作を自動化ルールにまとめられます。

📥 [最新リリースをダウンロード](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [ユーザーガイド](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（実験的） · 9 種類の UI 言語 · MIT ライセンス

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="ユーザーガイド（簡体字中国語）"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="マスコットをクリックしてユーザーガイドを開く"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute クイックパネル：上部の出力デバイスとアプリごとの音量操作"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute 自動化ルールエディター：アプリトリガーとオーディオアクションの手順選択"></a>
</div>

<sub>UIプレビュー：音量とルーティングを即座に操作できるクイックパネル（可変高さ・アクリル半透明対応）と、トリガーとアクションを自在に組める自動化エディター。</sub>

## ✨ SonicRoute の主な機能

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="機能"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="マスコットをクリックして SonicRoute の機能を見る"></a>

- **アプリごとの音声分離と双方向ルーティング（再生＋録音）：** アプリごとに再生・録音デバイスを個別に設定。ゲームや通話はヘッドセットへ、音楽はスピーカーへ振り分けられます。設定は Windows にネイティブ保存され、アプリ終了後も維持されます。
- **フォアグラウンド追従とトレイホイール直感操作：** システムイベントにより音声を再生中のフォアグラウンドアプリを瞬時に検出。タスクバーのトレイ領域でホイールをスクロールするだけで音量を調整でき、OSD 表示やマイク一括ミュートも利用可能です。
- **2つのパネルとリアルタイム音声レベル表示：** クラシックな一覧パネルとモダンなカードパネル（Modern）を提供。350〜850px の高さ調整に対応し、各アプリの音量調整や音声メーターによる動作確認が可能です。
- **自動化コンボエンジンと高速 CLI（6種のトリガー × 37種のアクション）：** アプリ起動・切り替え・終了、スケジュール、ショートカットなど 6 つのトリガーに対応。デバイス切り替え、音量、ミュート、PowerShell などを自由に組み合わせ可能。名前付きパイプによる sr コマンドで外部連携も容易です。
- **イベント優先の検出：** システム通知を利用し、必要な場合は1秒間隔のスナップショット確認で補います。不要なデータは必要に応じて整理します。Legacy（.NET 4.8）、.NET 10 / NET8 の Lite、Store 版を9言語で提供します。

## 🎮 日常での使い方

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="自動化"><img src="docs/images/mascot-automation-jp.png" width="48" align="middle" alt="ロボットをクリックして自動化ルールを確認する"></a> 面倒な手動設定を、一度設定すればずっと快適な自動化体験へ。 [自動化を設定](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 日常のシチュエーション | SonicRoute による解決策 |
|---|---|
| 🎮 **ボイスチャットがうるさい、またはゲーム音で声が聞こえない** | Discord などの通話音量を個別に調整し、ゲーム音はそのまま維持。ヘッドセットと通話を分離可能 |
| 🎵 **ゲームをプレイしながら裏で音楽や動画を流したい** | 音楽はスピーカー、ゲームはヘッドホンへ。全画面ゲームを終了せずタスクバーのホイール操作で音量調節 |
| ⚡ **ゲーム起動のたびにヘッドセットを繋ぎ直したり設定を変えている** | ゲーム起動時に「ヘッドセットへ切り替え＋音量40%＋通話起動」、終了時にスピーカーへ自動復帰 |
| 🎙️ **席を外す時やプライベートな通話への切り替え** | Ctrl+Shift+N で全マイクを一括ミュート。画面上の OSD バナーで消音状態を一目で確認 |
| 📹 **OBS でのゲーム配信や画面録画チュートリアル** | アプリごとに独立した音声トラックを割り当て、プライベートな会話や通知音が録画に入るのを防止 |

## 🚀 ダウンロードと使い始め方

**現在のリリース：[v1.26](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.26)** · 2026-10-23 · [リリースノート（簡体字中国語）](docs/release-v1.26.md)

Windows x64 では、.NET 10 の Lite、NET8 互換 Lite、.NET Framework 4.8 の Legacy から選べます。Store ページには現在配信中のバージョンが表示され、更新は Store から提供されます。下の直接ダウンロードリンクは v1.26 用です。

| エディション | ダウンロード | 要件 |
|---|---|---|
| ⭐ Lite x64（.NET 10・標準） | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-x64.exe) | ポータブル単一ファイル。x64 .NET 10 Desktop Runtime が必要です |
| ⚡ Lite ARM64（.NET 10）（実験的） | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-arm64.exe) | ポータブル ARM64 ファイル。ARM64 .NET 10 Desktop Runtime が必要です |
| ☕ Lite NET8 x64 | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-x64.exe) | NET8 互換 Lite。x64 .NET 8 Desktop Runtime が必要です |
| ☕ Lite NET8 ARM64（実験的） | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-arm64.exe) | NET8 互換 Lite。ARM64 .NET 8 Desktop Runtime が必要です |
| 🛡️ Legacy x64（.NET 8 不要） | [完全版 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Legacy-x64.zip) | 追加ランタイム不要。Windows 標準の .NET Framework 4.8 で動作し、ZIP を解凍するだけですぐに使えます |
| 🔄 Microsoft Store（Store 経由で更新） | [Store からインストール](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | .NET 10 の Store パッケージには x64 .NET 10 Desktop Runtime が必要です。現在配信中のバージョンは Store ページで確認してください（64 ビット Windows 10/11 対象）。 |

標準 Lite には [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)、NET8 互換 Lite には [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。NET8 版の提供と保守は 2026 年 11 月以降終了します。その後、x64 は .NET 10 または Legacy（.NET Framework 4.8）を手動で選択し、ARM64 は .NET 10 を選択してください。

1. **起動と常駐**：ダブルクリックで起動するとタスクバーのトレイに常駐します。トレイアイコンをダブルクリックしてメイン画面を開きます。
2. **デバイスの選択と振り分け**：「設定 → 保持するデバイス」でよく使うデバイスにチェックを入れます。トレイアイコンをクリックしてクイックパネルを開き、スライダーでアプリごとに音声を振り分けます。
3. **手元でのブラインド調整**：トレイアイコン上にマウスを置いてホイールを回すだけで音量を調整できます。ショートカットキーや自動化ルールと組み合わせれば、ゲーム画面から切り替えることなく操作できます。

`sr` を初めて使う前に、SonicRoute を通常起動してコマンドを登録し、ターミナルを開き直してください。デバイス切り替えが想定どおりに動作しない場合は [よくある質問](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) をご覧ください。

```bash
# 自動化ルールを実行（SonicRoute が起動中である必要があります）
sr "ルール名または ID"
```

バージョン 1.26 では、一部の高い権限で動作するアプリのアイコン表示を修正し、必要時の音声読み取りと .NET 10／Legacy 版の永続化を改善しました。また、ミュート状態の読み戻しと終了時のクリーンアップを改善しています。過去の変更は [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) を参照してください。

### ❓ よくある質問（FAQ）

**Q：SonicRoute を終了してもアプリのルーティングや音量設定は維持されますか？**

A：はい、維持されます。アプリのルーティングは Windows 側のオーディオエンジンによってネイティブに保存されるため、終了後も有効です。ホットキーや自動化ルールのみ常駐が必要です。

**Q：各エディションに必要な Runtime は何ですか？**

A：標準 Lite には対応する .NET 10 Desktop Runtime、Lite NET8 には .NET 8 が必要です。Legacy は追加の .NET Desktop Runtime が不要で、.NET Framework 4.8 で動作します。現在の Store パッケージには x64 .NET 10 Desktop Runtime が必要です。

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

## 📌 ロードマップ

- **今後の計画はありません：** 主要機能はすでに完成しており、現時点で新機能の追加予定はありません。今後は必要に応じた互換性と安定性の保守に専念します。

[アイデアの共有・問題の報告](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 🧩 その他のプロジェクトと組み合わせ方

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**：Windows 向けオーディオプレーヤーです。「プログラムを起動」アクションと組み合わせると、ホットキーで効果音を再生できます。 [設定ガイド（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="XiaoYue Comics"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="卵の殻から顔を出し、漫画を抱えるシャオユエをクリックして連載を読む"></a>

**[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

卵から飛び出した魔法少女と、手伝うたびに事態が悪化するハチャメチャな日常を描くギャグ漫画。

<br clear="all">

---

<a href="EASTER_EGGS.md" title="昨日と明日のイースターエッグ"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="休んでいるマスコットをクリックしてイースターエッグを見つける"></a>

SonicRoute をご利用いただきありがとうございます。 [ガイドを読む（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [問題を報告](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://www.ifdian.net/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
