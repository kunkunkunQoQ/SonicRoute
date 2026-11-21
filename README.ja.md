<!-- Generated from docs/readme/shared.json, docs/readme/locales/ja-JP.json, and docs/readme/releases/v1.34.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | **日本語** | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![最新の正式版](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=%E6%9C%80%E6%96%B0%E3%81%AE%E6%AD%A3%E5%BC%8F%E7%89%88)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![ライセンス：GPL-3.0](https://img.shields.io/static/v1?label=%E3%83%A9%E3%82%A4%E3%82%BB%E3%83%B3%E3%82%B9&message=GPL-3.0&color=blue)](LICENSE) [![対応 OS：Windows 10 | 11](https://img.shields.io/static/v1?label=%E5%AF%BE%E5%BF%9C+OS&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![アーキテクチャ：x64 | ARM64](https://img.shields.io/static/v1?label=%E3%82%A2%E3%83%BC%E3%82%AD%E3%83%86%E3%82%AF%E3%83%81%E3%83%A3&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![ダウンロード数](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=%E3%83%80%E3%82%A6%E3%83%B3%E3%83%AD%E3%83%BC%E3%83%89%E6%95%B0)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian で開発を応援する](https://img.shields.io/static/v1?label=&message=Afdian+%E3%81%A7%E9%96%8B%E7%99%BA%E3%82%92%E5%BF%9C%E6%8F%B4%E3%81%99%E3%82%8B&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**音楽はスピーカーへ、ゲームはヘッドセットへ。**

SonicRoute は Windows の通知領域に常駐するオーディオツールです。アプリごとに再生デバイスと録音デバイスを選べます。音量や出力先は、ショートカットキー、通知領域のアイコン上でのホイール操作、クイックパネルから変更できます。よく使う操作はルールにまとめて自動化できます。

📥 [最新の正式版をダウンロード](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [ユーザーガイド（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（試験提供） · 9 言語に対応 · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="スマートフォンのアプリを PC のクイックパネルにドラッグし、音声の出力先を切り替える様子"></a>
<a href="docs/images/automation-editor-ja.png"><img src="docs/images/automation-editor-ja.png" width="380" align="top" alt="日本語表示の自動化ルール編集画面。アプリの音量やミュートを操作するアクションの一覧を表示"></a>
</div>

<sub>左の動画では、スマートフォンのアプリを PC のクイックパネルにドラッグして音声の出力先を切り替えています。右は自動化ルールの編集画面で、実行するアクションを選んでいるところです。</sub>

<details>
<summary><sub>🖼️ クイックパネルとスマートフォンの管理画面を見る</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="クイックパネル上部の出力デバイスとアプリ別の音量スライダー"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="スマートフォンの管理画面。Android 端末との接続、アプリのピン留め、仮想ディスプレイの音声設定を表示"></a>
</div>

</details>

## ✨ SonicRoute の主な機能

- **アプリ別の再生・録音デバイス。** ゲームや通話にはヘッドセット、音楽にはスピーカーといった使い分けができます。入力用のマイクもアプリごとに選べます。デバイスの割り当ては Windows が保存するため、SonicRoute を終了した後も有効です。
- **操作対象のアプリを自動で選択。** 既定の設定では、最近前面で使用したアプリのうち、オーディオセッションがあるものを優先します。通知領域の SonicRoute アイコンにポインターを合わせ、ホイールを回すとそのアプリの音量を調整でき、Alt キーを押しながら回すと出力先を切り替えられます。操作結果は画面に表示できます。ショートカットキーで、すべてのマイクのミュートも切り替えられます。
- **アプリごとの音量とレベルメーター。** クイックパネルでは、各アプリの再生レベルを見ながら音量を調整できます。表示位置は保存でき、高さは 350～850 論理ピクセルの範囲で設定できます。実際に表示される高さは画面の作業領域に合わせて調整されます。
- **Android 連携と AirPlay 受信（Full）。** Android アプリの音声を PC で再生・調整したり、PC の音声をスマートフォンに送ったりできます。Android アプリは独立した仮想ディスプレイで操作でき、映像は H.265 にも対応します。ゲームパッドの入力は、映像ウィンドウにフォーカスがあるときだけ端末に送られます。内蔵の C# 製 AirPlay 受信機能では、Apple 製デバイスから音声を受信したり、元の縦横比で画面をミラーリングしたりできます。クイックパネルでは、音声を PC の出力デバイス、Android 端末のスピーカー、またはその端末に接続した Bluetooth 機器に送れます。スマート音量調整（SmartVolume）で音量のばらつきも抑えられます。
- **6 種類のトリガーと 39 種類のアクション。** アプリを起動したとき、そのアプリに切り替えたとき、アプリを終了したときにルールを実行できます。指定時刻、ショートカットキー、SonicRoute 自体の起動もトリガーになります。デバイスの切り替え、音量調整、ミュート、プログラムの起動、PowerShell スクリプトの実行などを組み合わせて、スクリプトや外部コントローラーから `sr` コマンドで呼び出せます。
- **Windows のイベントを利用した状態検出。** Windows からの通知を受けて状態を更新し、必要な場合にだけ 1 秒間隔で状態を確認します。不要になったデータは随時解放します。スマートフォン連携と AirPlay は Full で利用できます。Lite と Legacy は PC の音声機能専用です。すべてのエディションが 9 言語に対応しています。

## 🎮 日常での使い方

ゲームの起動や終了に合わせて、出力先や音量を自動で切り替えられます。 [自動化を設定](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| こんなときに | 使い方 |
|---|---|
| 🎮 **ゲーム音とボイスチャットの音量が合わない** | Discord などの通話アプリだけ音量を調整できます。ゲームと通話に別々の出力デバイスを選ぶこともできます。 |
| 🎵 **ゲーム中に音楽や動画を別のスピーカーで流したい** | 音楽はスピーカー、ゲームはヘッドセットに割り当てます。操作対象のアプリは、通知領域のアイコン上でホイールを回すと音量を、Alt キーを押しながら回すと出力先を変更できます。 |
| ⚡ **ゲームを起動するたびに出力先を変えている** | ゲームの起動時にヘッドセットに切り替え、音量を 40% に設定して通話アプリを起動するルールを作ります。ゲームの終了時は別のルールでスピーカーに戻します。 |
| 🎙️ **席を外すときにマイクを切りたい** | 既定のショートカットキー Ctrl+Shift+N を押すと、すべてのマイクをミュートできます。もう一度押すと解除されます。ミュート中の画面表示（OSD）を有効にしておけば、その間は画面上で状態を確認できます。 |
| 📹 **OBS でゲーム配信や解説動画の収録をしたい** | 録音したいアプリを専用の出力デバイスや仮想オーディオデバイスに割り当てます。OBS でその出力を取り込み、通話や通知音は別の出力に分けます。 |
| 📱 **PC でスマートフォンの音声を再生したり、アプリを操作したい** | Android アプリの音声を PC で調整し、仮想ディスプレイでアプリを操作できます。Apple 製デバイスからは AirPlay で音声と画面を受信できます。音声の出力先はクイックパネルから PC や Android 端末のスピーカー、端末に接続した Bluetooth 機器に切り替えられます。 |

## 🚀 ダウンロードと初期設定

**最新の正式版：[v1.34](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.34)** · 2026年11月21日 · [リリースノート（簡体字中国語）](docs/release-v1.34.md)

Android 端末と PC の間で音声を転送したり、仮想ディスプレイや AirPlay 受信を使ったりする場合は **Full** を選んでください。PC の音声機能だけを使う場合は **Lite**、.NET Framework 4.8 を使用する x64 環境には **Legacy** があります。Full と Lite には x64 版のほか、試験提供中の ARM64 版もあります。下表の直接ダウンロードリンクは v1.34 のものです。Microsoft Store で配信中のバージョンは、Full と Lite それぞれのストアページで確認できます。

| エディション | ダウンロード | 動作要件 |
|---|---|---|
| ⭐ Full（.NET 10、スマートフォン連携対応） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-x64.exe) / [ARM64（試験提供）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-arm64.exe) | 単体の EXE ファイルです。使用する版と同じアーキテクチャの .NET 10 Desktop Runtime が必要です。Android 連携、仮想ディスプレイ、AirPlay を利用できます。 |
| ⭐ Lite（.NET 10、PC の音声のみ） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-x64.exe) / [ARM64（試験提供）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-arm64.exe) | PC の音声機能に絞った単体の EXE ファイルです。使用する版と同じアーキテクチャの .NET 10 Desktop Runtime が必要です。 |
| 🛡️ Legacy x64（PC の音声のみ、.NET Framework 4.8） | [ZIP ファイル](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Legacy-x64.zip) | Windows の .NET Framework 4.8 で動作します。ZIP ファイルを展開して起動してください。 |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full、Lite ともに x64 版の .NET 10 Desktop Runtime が必要です。配信中のバージョンは各ストアページで確認できます。 |

<details>
<summary><sub>📦 旧 .NET 8 版のダウンロード（更新終了）</sub></summary>

| エディション | ダウンロード | 動作要件 |
|---|---|---|
| Lite NET8（更新終了） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64（試験提供）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | この版の更新は終了しました。引き続きダウンロードできます。今後も更新を受け取るには、.NET 10 版または Legacy 版に移行してください。 |
</details>

> [!NOTE]
> Full と Lite には、ダウンロードした版と同じアーキテクチャの [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) が必要です。Legacy は Windows の .NET Framework 4.8 で動作します。[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) を使う旧版の更新は終了しましたが、引き続きダウンロードできます。

### 🏁 初めて使う方へ

1. **SonicRoute を起動する。** EXE ファイルをダブルクリックします。Legacy 版は先に ZIP ファイルを展開してください。通知領域に表示される SonicRoute のアイコンをダブルクリックするとメインウィンドウが開きます。アイコンが隠れている場合は、通知領域にドラッグしておくと便利です。
2. **アプリの出力先を選ぶ。** 設定で、クイックパネルに表示したいヘッドホンやスピーカーを選びます。通知領域のアイコンをクリックしてクイックパネルを開き、アプリの行を展開して出力先を選んでください。音量はスライダーで調整します。
3. **マウスホイールで操作する。** 通知領域の SonicRoute アイコンにポインターを合わせ、ホイールを回すと操作対象のアプリの音量を調整できます。Alt キーを押しながら回すと、そのアプリの出力先が切り替わります。Shift キーを押しながら回すと、Windows の既定の再生デバイスの音量を調整できます。設定でホイール操作の範囲を通知領域全体に広げることもできます。

### ⚡ コマンドラインから自動化ルールを実行

`sr` は、起動中の SonicRoute に名前付きパイプ経由でルールの実行を指示するコマンドです。SonicRoute が起動していない場合はエラーを返します。初めて使うときは、SonicRoute を一度起動してからターミナルを開き直してください。スクリプトや AutoHotkey、Stream Deck からも呼び出せます。詳しくは [よくある質問](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) をご覧ください。

```powershell
# 起動中の SonicRoute で自動化ルールを実行
sr "ルール名または ID"
```

### 🆕 v1.34 リリースの主な改善点

すべてのエディションで、アプリごとに複数の PC 再生デバイスを選べるようになりました。SonicRoute を再起動した後、そのアプリが再び音声を再生すると、選んだデバイスが復元されます。システムトレイでの音量操作とマウスショートカットは、1 つのシステムマウスフックを共有します。

Full では、2 つの Android パネルで USB と Wi-Fi の接続状態が同期されます。スマートフォンのクイックパネルを更新すると、選択中のデバイスに再接続することもできます。Android アプリの出力先メニューには、スマートフォンの現在の出力先に設定した名前が表示されます。以前のリリースについては [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) をご覧ください。

<details>
<summary>v1.33</summary>

Full に、C# で実装した AirPlay 受信機能が加わりました。PCM、ALAC、AAC-LC、AAC-ELD の音声を再生でき、画面のミラーリングでは元の縦横比を保ちます。接続用のパスコードは任意で設定できます。ミラーリングウィンドウは最前面に固定でき、F11 キーで全画面表示に切り替えられます。音声はドラッグ操作で PC の出力デバイス、Android 端末のスピーカー、またはその端末に接続した Bluetooth 機器に送れます。

Android と AirPlay の映像は H.265（HEVC）に対応しました。Android では H.265 エンコーダーを起動できない場合、H.264 に自動で切り替わります。Android 端末の設定では音声と映像のデコーダーを選べます。利用可能なエンコーダーを問い合わせるのは、その端末の設定画面を表示している間だけです。ゲームパッドの入力は、映像ウィンドウにフォーカスがあるときだけ端末に送られます。ペア設定用の QR コードは 2 分ごとに更新され、同じ画面でコードを手入力することもできます。再接続時やパネルの更新時には、保存済みの端末を LAN 内で探し、古いポートを除外して端末の識別情報から新しいアドレスとポートを特定します。端末が見つからない場合や接続に失敗した場合も、手入力したアドレスは残ります。端末を登録し直す際には、保存しておいた設定を復元できます。

全エディションで、「すべてのマイクがミュートのとき」と「既定の録音デバイスがミュートのとき」のそれぞれについて、画面表示（OSD）を出し続けるかどうかを選べます。設定ファイルの管理操作は 1 行にまとめました。アプリが 20 件を超える一覧では表示用の行を再利用し、連続する OSD の更新はまとめて処理します。デコード後の PNG の元データを解放し、Full では使い終わったテクスチャも解放します。Full のウィンドウを閉じたときのメモリ回収はランタイムに任せます。設定ファイルの .bak バックアップと、必要なときのエラー記録にも対応しました。Microsoft Store では Lite を単独で入手でき、各エディションで GPL-3.0-only のライセンスをオフラインで確認できます。詳しくは [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) をご覧ください。

</details>

### ❓ よくある質問

**SonicRoute を終了しても、アプリの出力先や音量は変わりませんか？**

Windows に保存されたデバイスの割り当てと、現在のオーディオセッションの音量はそのままです。ショートカットキー、通知領域での操作、自動化、スマートフォンとの音声転送、画面ミラーリング、クリップボードの同期を使うには、SonicRoute を起動したままにしてください。

**各エディションにはどのランタイムが必要ですか？**

Full と Lite には、使う版と同じアーキテクチャの .NET 10 Desktop Runtime が必要です。Microsoft Store 版にはその x64 版が必要です。Legacy は .NET Framework 4.8 を使用します。旧 .NET 8 版の更新は終了しましたが、引き続きダウンロードできます。

**常駐中の CPU 使用率やバッテリーへの影響はどの程度ですか？**

通常は Windows の通知を受けて動作し、必要な場合にだけ 1 秒間隔で状態を確認します。不要なデータは随時解放します。CPU 使用率や消費電力は環境や使用中の機能によって変わります。

## 📚 ドキュメント

詳細な Wiki は現在、簡体字中国語で公開されています。

| トピック | ガイド |
|---|---|
| インストール、パネル、日常の操作 | [ユーザーガイド（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [機能](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 既定のキー、割り当て、操作対象 | [ショートカットキー](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| トリガー、アクション、スクリプト連携 | [自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [コマンドライン](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| インストールとトラブルシューティング | [よくある質問](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| ソース、ビルド、リリース | [技術解説](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [リリース情報](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [バージョン履歴](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **AI アシスタント向けの技術資料**：Cursor、Copilot、ChatGPT、Claude などで設定の確認やトラブルの調査、スクリプトの作成を行うときは、[README.ai.md](README.ai.md) を参照してください。実装の概要と問題の切り分け方を、AI が参照しやすい形式でまとめています。

## 📌 ロードマップ

- **音声の出力先の切り替えと復元。** デバイスの抜き差しや状態の変化に合わせて出力先を切り替え、再接続時に復元する方法を検討しています。複数の音声セッションを使うアプリなどでも安定して切り替えられることを目指しています。

[アイデアの共有・問題の報告](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 💖 SonicRoute を応援する

SonicRoute は、開発とメンテナンスを続けている無料のオープンソースソフトウェアです。PC の音量を調整したり、出力先を切り替えたりするときに便利だと感じたら、Afdian から開発を応援していただけます。ご支援は、新機能の開発や互換性の向上、継続的なメンテナンスに役立てます。

☕ [Afdian で開発を応援する](https://afdian.com/a/koukou021)

寄付は任意です。すべての機能を無料でお使いいただけます。不具合の報告や周りの方へのご紹介、GitHub でのスターも開発の励みになります。ありがとうございます。

## 📜 ライセンス

- **GPL-3.0 オープンソース**：SonicRoute は GNU General Public License 第 3 版のみ（GPL-3.0-only）で提供されます。[LICENSE](LICENSE) を参照してください。GPL の条件に従う商用利用、販売、有償配布が可能です。
- **再配布**：必要な著作権表示とライセンス表記を保持し、変更を明記し、対象となる改変作品を GPL 第 3 版で配布してください。対象のバイナリを配布する場合は GPL 第 6 条に従い完全な対応ソースを提供してください。詳細は [LICENSE](LICENSE) を参照してください。
- **適用範囲と従来の許諾**：2026-11-17 の GPL 移行から、著作権者が許諾できる範囲で、本リビジョンの対象プロジェクト全体（以前のコードを含む）を GPL-3.0-only で提供します。既存の MIT および旧独自ライセンスによる許諾は有効で、既存タグや公開済みバイナリの表示は変更しません。第三者コンポーネントは元のライセンスを保持します（[サードパーティ製コンポーネントのライセンス](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)）。

---

<a href="EASTER_EGGS.md" title="昨日と明日のイースターエッグ"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="マスコットの隠しページを開く"></a>

SonicRoute をご利用いただきありがとうございます。 [ガイドを読む（簡体字中国語）](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [問題を報告](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 AI アシスタント向け資料</a></sub></div>
