<!-- Generated from docs/readme/shared.json and docs/readme/locales/ja-JP.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | **日本語** | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md)

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

- **アプリ単位の双方向ルーティング（再生＋録音）：** アプリごとに再生・録音デバイスを個別に設定。ゲームや通話はヘッドセットへ、音楽はスピーカーへ振り分けられます。設定は Windows にネイティブ保存され、アプリ終了後も維持されます。
- **フォアグラウンド自動検出と直感的な操作：** システムイベントにより音声を再生中のフォアグラウンドアプリを瞬時に検出。タスクバーのトレイ領域でホイールをスクロールするだけで音量を調整でき、OSD 表示やマイク一括ミュートも利用可能です。
- **2つのパネルスタイルとリアルタイム音声アクティビティ：** クラシックな一覧パネルとモダンなカードパネル（Modern）を提供。350〜800px の高さ調整に対応し、各アプリの音量調整や音声メーターによる動作確認が可能です。
- **37種類のアクションを備えた自動化と CLI 連携：** アプリ起動・切り替え・終了、スケジュール、ショートカットなど 6 つのトリガーに対応。デバイス切り替え、音量、ミュート、PowerShell などを自由に組み合わせ可能。名前付きパイプによる sr コマンドで外部連携も容易です。
- **軽量設計とイベント駆動アーキテクチャ：** システムイベント駆動により常駐ポーリングタイマーを排除し、アイドル時の CPU 使用率はほぼゼロ。依存関係不要の Legacy 版（.NET 4.8）、単一ファイル Lite 版（x64 / ARM64 実験版）、Store 版を用意し、9 言語に対応。

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

**現在のリリース：[v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [リリースノート（簡体字中国語）](docs/release-v1.24.md)

Windows x64 をお使いの方には、インストールが簡単で自動更新にも対応する Microsoft Store 版がおすすめです。単体の実行ファイルなら Lite x64、.NET 8 のインストールが難しい場合は Legacy x64 を選んでください。上のダウンロードリンクは常に最新リリースを示し、下の直接リンクは v1.24 用です。

| エディション | ダウンロード | 要件 |
|---|---|---|
| ⭐ Lite x64（おすすめ・標準） | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | 単一ファイルでインストール不要。システムに適合する x64 .NET 8 Desktop Runtime が必要です |
| ⚡ Lite ARM64（実験的） | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | Surface Pro や Snapdragon 搭載 ARM64 PC でネイティブ動作。ARM64 .NET 8 Desktop Runtime が必要です |
| 🛡️ Legacy x64（.NET 8 不要） | [完全版 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | 追加ランタイム不要。Windows 標準の .NET Framework 4.8 で動作し、ZIP を解凍するだけですぐに使えます |
| 🔄 Microsoft Store（自動更新） | [Store からインストール](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | ワンクリック導入とストア経由の自動バックグラウンド更新に対応（64ビット Windows 10/11 対象） |

Lite には対応する [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。Legacy に .NET 8 は必要ありません。

1. **起動と常駐**：ダブルクリックで起動するとタスクバーのトレイに常駐します。トレイアイコンをダブルクリックしてメイン画面を開きます。
2. **デバイスの選択と振り分け**：「設定 → 保持するデバイス」でよく使うデバイスにチェックを入れます。トレイアイコンをクリックしてクイックパネルを開き、スライダーでアプリごとに音声を振り分けます。
3. **手元でのブラインド調整**：トレイアイコン上にマウスを置いてホイールを回すだけで音量を調整できます。ショートカットキーや自動化ルールと組み合わせれば、ゲーム画面から切り替えることなく操作できます。

`sr` を初めて使う前に、SonicRoute を通常起動してコマンドを登録し、ターミナルを開き直してください。デバイス切り替えが想定どおりに動作しない場合は [よくある質問](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) をご覧ください。

```bash
# 自動化ルールを実行（SonicRoute が起動中である必要があります）
sr "ルール名または ID"
```

1.24 では、自動化のひな形、最近の実行履歴（初期設定はオフ、保存容量を設定可能）、アプリごとのアイコン読み込みを追加しました。アイコンがない場合や汎用アイコンの場合は、アプリ名の先頭文字を表示します。設定のバックアップには読み込んだアイコンも含まれます。過去の変更は [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) を参照してください。

### ❓ よくある質問（FAQ）

**Q：SonicRoute を終了してもアプリのルーティングや音量設定は維持されますか？**

A：はい、維持されます。アプリのルーティングは Windows 側のオーディオエンジンによってネイティブに保存されるため、終了後も有効です。ホットキーや自動化ルールのみ常駐が必要です。

**Q：起動時に .NET 8 Desktop Runtime が不足していると表示された場合はどうすればよいですか？**

A：上記テーブル内のランタイムリンクから公式の .NET 8 Desktop Runtime をインストールするか、追加環境が不要な Legacy 版をご利用ください。

**Q：バックグラウンド常駐中に CPU やバッテリーを消費しますか？**

A：ほぼゼロです。SonicRoute は完全な Windows システムイベント駆動型アーキテクチャを採用しており、待機時の CPU 使用率は 0%、無駄な定期ポーリングも一切ありません。

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

- **v1.25 · 大型アップデート（開発中）**：自動化ルールを 6 種のトリガーと 37 種の高度なアクションに拡張（録音デバイス詳細、アプリ個別マイク制御、全アプリ一括音量調整、ワンクリックルーティングリセット対応）。
- **今後の展開**：コミュニティのフィードバックに基づき、低遅延イベント監視とゼロポーリング設計を継続的に洗練。

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
