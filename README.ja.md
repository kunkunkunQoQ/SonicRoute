<!-- Generated from docs/readme/shared.json and docs/readme/locales/ja-JP.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | **日本語** | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md)

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE) [![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian で支援](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**アプリごとに、音声の出力先デバイスを選べます。**

SonicRoute は Windows の通知領域に常駐するオーディオツールです。アプリごとに出力・入力デバイスを選び、音楽はスピーカーで、ゲームや通話はヘッドセットで再生できます。ホットキー、通知領域のホイール、パネルで音声を操作し、日常の操作を自動化ルールにまとめられます。

📥 [最新リリースをダウンロード](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [ユーザーガイド](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（実験的） · 9 種類の UI 言語 · MIT ライセンス

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="ユーザーガイド（簡体字中国語）"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="マスコットをクリックしてユーザーガイドを開く"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute クイックパネル：上部の出力デバイスとアプリごとの音量操作"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute v1.23 自動化ルールエディター：アプリのトリガーと音声操作の選択"></a>
</div>

<sub>画面例：クイックパネルでアプリごとの音量とデバイスを操作し、自動化エディターでトリガーと操作手順を組み合わせられます。</sub>

## ✨ SonicRoute の主な機能

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="機能"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="マスコットをクリックして SonicRoute の機能を見る"></a>

- **アプリ単位の双方向ルーティング（再生＋録音）：** アプリごとに再生・録音デバイスを個別に設定。ゲームや通話はヘッドセットへ、音楽はスピーカーへ振り分けられます。設定は Windows にネイティブ保存され、アプリ終了後も維持されます。
- **フォアグラウンド自動検出と直感的な操作：** システムイベントにより音声を再生中のフォアグラウンドアプリを瞬時に検出。タスクバーのトレイ領域でホイールをスクロールするだけで音量を調整でき、OSD 表示やマイク一括ミュートも利用可能です。
- **2つのパネルスタイルとリアルタイム音声アクティビティ：** クラシックな一覧パネルとモダンなカードパネル（Modern）を提供。350〜800px の高さ調整に対応し、各アプリの音量調整や音声メーターによる動作確認が可能です。
- **37種類のアクションを備えた自動化と CLI 連携：** アプリ起動・切り替え・終了、スケジュール、ショートカットなど 6 つのトリガーに対応。デバイス切り替え、音量、ミュート、PowerShell などを自由に組み合わせ可能。名前付きパイプによる sr コマンドで外部連携も容易です。
- **軽量設計とイベント駆動アーキテクチャ：** システムイベント駆動により常駐ポーリングタイマーを排除し、アイドル時の CPU 使用率はほぼゼロ。依存関係不要の Legacy 版（.NET 4.8）、単一ファイル Lite 版（x64 / ARM64 実験版）、Store 版を用意し、9 言語に対応。

## 🎮 日常での使い方

味方の声が大きすぎるときは、通話アプリのスライダーだけ下げれば、ゲームの音量は変わりません。

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="自動化"><img src="docs/images/mascot-automation-jp.png" width="48" align="middle" alt="ロボットをクリックして自動化ルールを確認する"></a> 例えば、ゲーム起動時に「ゲームの音声をヘッドホンへ → ゲームの音量を 50% に設定 → 通話アプリを開く」を順に実行するルールを設定し、有効にします。 [自動化を設定](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

## 🚀 ダウンロードと使い始め方

**現在のリリース：[v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [リリースノート（簡体字中国語）](docs/release-v1.24.md)

Windows x64 をお使いの方には、インストールが簡単で自動更新にも対応する Microsoft Store 版がおすすめです。単体の実行ファイルなら Lite x64、.NET 8 のインストールが難しい場合は Legacy x64 を選んでください。上のダウンロードリンクは常に最新リリースを示し、下の直接リンクは v1.24 用です。

| エディション | ダウンロード | 要件 |
|---|---|---|
| Lite x64 | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | x64 .NET 8 Desktop Runtime |
| Lite ARM64（実験的） | [単体 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | ARM64 .NET 8 Desktop Runtime |
| Legacy x64 | [完全版 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | .NET Framework 4.8。実行前に ZIP 全体を展開してください |
| Microsoft Store | [Store からインストール](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64；自動更新；Store に現在提供中のバージョンが表示されます |

Lite には対応する [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。Legacy に .NET 8 は必要ありません。

1. SonicRoute を起動し、Windows の通知領域（隠れているアイコンの中にある場合があります）でアイコンを見つけて、ダブルクリックでメイン画面を開きます。
2. 設定で使いたい出力・入力デバイスを選びます。
3. 通知領域のアイコンをクリックしてパネルを開くか、ホットキーを設定します。複数の操作を組み合わせる場合は、自動化ルールを作成します。

`sr` を初めて使う前に、SonicRoute を通常起動してコマンドを登録し、ターミナルを開き直してください。デバイス切り替えが想定どおりに動作しない場合は [よくある質問](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) をご覧ください。

1.24 では、自動化のひな形、最近の実行履歴（初期設定はオフ、保存容量を設定可能）、アプリごとのアイコン読み込みを追加しました。アイコンがない場合や汎用アイコンの場合は、アプリ名の先頭文字を表示します。設定のバックアップには読み込んだアイコンも含まれます。過去の変更は [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) を参照してください。

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

- **v1.25 · 大型アップデート：** 詳細は今後のリリースノートでお知らせします。
- **今後の段階：** フィードバックをもとに既存機能を継続的に改善します。計画はフィードバックに応じて変更される場合があります。

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
