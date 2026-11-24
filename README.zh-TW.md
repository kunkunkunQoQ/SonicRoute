<!-- Generated from docs/readme/shared.json, docs/readme/locales/zh-TW.json, and docs/readme/releases/v1.36.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 音躍 SonicRoute

[简体中文](README.md) | [English](README.en.md) | **繁體中文** | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![最新正式版](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=%E6%9C%80%E6%96%B0%E6%AD%A3%E5%BC%8F%E7%89%88)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![授權條款說明：GPL-3.0-only](https://img.shields.io/static/v1?label=%E6%8E%88%E6%AC%8A%E6%A2%9D%E6%AC%BE%E8%AA%AA%E6%98%8E&message=GPL-3.0-only&color=blue)](LICENSE) [![作業系統：Windows 10 | 11](https://img.shields.io/static/v1?label=%E4%BD%9C%E6%A5%AD%E7%B3%BB%E7%B5%B1&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![架構：x64 | ARM64](https://img.shields.io/static/v1?label=%E6%9E%B6%E6%A7%8B&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![下載次數](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=%E4%B8%8B%E8%BC%89%E6%AC%A1%E6%95%B8)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![透過愛發電贊助 SonicRoute](https://img.shields.io/static/v1?label=&message=%E9%80%8F%E9%81%8E%E6%84%9B%E7%99%BC%E9%9B%BB%E8%B4%8A%E5%8A%A9+SonicRoute&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**讓音樂從喇叭播放，讓遊戲聲留在耳機裡。**

SonicRoute 是常駐 Windows 通知區域的音訊工具。您可以分別指定各個應用程式的播放與錄音裝置，也能讓同一個程式的聲音同時從多個裝置播放。

📥 [下載最新版本](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [選擇版本](#downloads) · [三步驟開始使用](#quick-start) · [使用指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

Windows 10 2004+ / Windows 11 · x64 / ARM64（實驗性） · 支援九種介面語言 · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="將手機應用程式拖到電腦的快速面板，切換音訊輸出裝置"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="自動化規則編輯器：觸發條件為按下快速鍵，動作選單中已選取設定系統預設播放裝置"></a>
</div>

<sub>左圖示範如何將手機上的應用程式拖到電腦的快速面板，再切換音訊輸出。右圖示範如何為快速鍵規則選擇要執行的音訊動作。</sub>

<details>
<summary><sub>🖼️ 查看更多快速面板與手機管理畫面</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="快速面板頂端的輸出裝置與各應用程式的音量滑桿"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="手機管理畫面中的 Android 裝置配對、已釘選的應用程式與虛擬螢幕音訊設定"></a>
</div>

</details>

<a name="downloads"></a>

## 🚀 下載與快速入門

**最新正式版：[v1.36](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.36)** · 2026年11月24日 · [更新說明（簡體中文、英文）](docs/release-v1.36.md)

需要 Android 手機連線或 AirPlay 接收功能，請選 **Full**。只管理電腦音訊可選 **Lite**。使用 .NET Framework 4.8 的 x64 電腦也可選 **Legacy**。下表提供正式版 v1.36 的可攜版下載及 Microsoft Store 連結。

| 版本 | 下載 | 系統需求 |
|---|---|---|
| ⭐ Full（電腦與手機音訊） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-arm64.exe) | 單一執行檔。需要與程式架構相符的 .NET 10 Desktop Runtime。 |
| ⚡ Lite（僅電腦音訊） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-arm64.exe) | 單一執行檔。需要與程式架構相符的 .NET 10 Desktop Runtime。 |
| 🛡️ Legacy（僅電腦音訊，x64） | [ZIP 壓縮檔](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Legacy-x64.zip) | 需要 .NET Framework 4.8。請將 ZIP 檔完整解壓縮後再執行。 |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full 與 Lite 均使用 x64 版 .NET 10 Desktop Runtime。上架版本請見各自的商店頁面。 |

<details>
<summary><sub>📦 下載舊版 NET8 相容版本（已停止維護）</sub></summary>

| 版本 | 下載 | 系統需求 |
|---|---|---|
| Lite NET8（已停止維護） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | 此版本已停止維護，仍可下載。建議改用 .NET 10 或 Legacy 版。 |

</details>

> [!NOTE]
> Full 與 Lite 需要安裝 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。Legacy 使用 Windows 的 .NET Framework 4.8。舊版 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 相容版本已停止維護，仍可下載。

<a name="quick-start"></a>

### 🏁 三步驟開始使用

1. **啟動程式。** 執行下載的 EXE 檔。使用 Legacy 版時，請先將 ZIP 檔完整解壓縮。按兩下系統匣的 SonicRoute 圖示可開啟主視窗。
2. **選擇播放裝置。** 在「設定」的「顯示的裝置」中勾選要使用的耳機或喇叭。按一下系統匣圖示開啟快速面板，展開應用程式所在的那一列，選擇輸出裝置並拖曳滑桿調整音量。
3. **用滑鼠滾輪調整音量。** 將指標移到系統匣圖示上，捲動可調整目前應用程式的音量。按住 Alt 捲動可切換該程式的輸出裝置，按住 Shift 捲動可調整系統預設播放裝置的音量。預設會優先控制最近在前景使用、且有音訊工作階段的應用程式。

## ✨ SonicRoute 的功能

- **為各個應用程式指定播放與錄音裝置。** 在快速面板或「應用程式」分頁中，按住 Ctrl 點選即可加入或取消選取，按住 Shift 點選可保留原有選取並加入連續範圍，按住 Ctrl+Shift 點選可選取或切換原生路由裝置。各版本都支援多個電腦輸出裝置同時播放，Full 版還能選用已連線 Android 手機的輸出裝置。
- **查看並調整各個程式的音量。** 快速面板的音量表會顯示哪些程式正在發出聲音。拖曳滑桿可分別調整音量，在系統匣圖示上捲動滑鼠滾輪可調整目前程式的音量。用全域快速鍵即可一次將所有麥克風靜音或解除靜音。
- **讓例行操作自動執行。** 使用 6 種觸發條件和 39 種動作組合規則，例如啟動遊戲時切換到耳機並調整音量。也可在命令列用 `sr` 執行已儲存的規則。
- **在電腦上播放手機音訊與畫面（Full 版）。** 可以在電腦播放 Android 應用程式的聲音、將電腦音訊傳到手機，或在獨立的虛擬螢幕上操作 Android 應用程式。Apple 裝置則可透過 AirPlay 將聲音傳到電腦，或將螢幕鏡像輸出到電腦。也可瀏覽 Android 手機的共用儲存空間，一次傳送或接收多個檔案。

## 🎮 日常使用情境

遊戲啟動或結束時，可以用規則自動切換輸出裝置和音量。 [設定自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 使用情境 | 操作方式 |
|---|---|
| 🎮 **遊戲聲蓋過隊友的語音，或語音音量太大** | 單獨調整 Discord 等語音通話軟體的音量，遊戲音量不受影響。遊戲和通話也可以分別指定不同的輸出裝置。 |
| 🎵 **玩遊戲時想用喇叭播放音樂或影片** | 將音樂送到喇叭，遊戲聲留在耳機。將指標移到系統匣圖示上，捲動可調整目前控制的應用程式音量，按住 Alt 捲動則可切換它的輸出裝置。 |
| ⚡ **每次開啟遊戲都要重新選擇耳機** | 新增一條遊戲啟動規則，依序切換到耳機、將音量設為 40%，再啟動語音通話軟體。另設一條規則，在遊戲結束時切回喇叭。 |
| 🎙️ **暫時離席，或有不想讓通話對方聽見的談話** | 使用預設快速鍵 Ctrl+Shift+N，將麥克風一併靜音或解除靜音。啟用螢幕提示（OSD）即可確認狀態。 |
| 📹 **用 OBS 直播遊戲或錄製教學影片** | 將要錄製的應用程式指定到專用的實體或虛擬輸出裝置，再讓 OBS 擷取該裝置。私人通話與通知音可使用其他輸出。 |
| 📱 **在電腦上聽手機音訊或操作手機應用程式** | 在 Full 版的虛擬螢幕上玩 Android 遊戲時，畫面可使用 H.265 編碼。只有虛擬螢幕視窗取得焦點時，遊戲才會收到控制器輸入。將遊戲音效傳到電腦耳機後，可開啟「智慧音量」來減少音量落差。 |

### ⚡ 從命令列執行自動化規則

`sr` 指令透過具名管道，請正在執行的 SonicRoute 執行指定規則。如果程式尚未執行，請先開啟程式。SonicRoute 首次啟動時會註冊這個指令，關閉並重新開啟終端機後，即可從指令碼、AutoHotkey 或 Stream Deck 呼叫。詳見 [常見問題](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)。

```powershell
# 在已啟動的 SonicRoute 中執行指定的自動化規則
sr "規則名稱或 ID"
```

## ❓ 常見問題

**結束 SonicRoute 後，應用程式的輸出裝置與音量會保留嗎？**

Windows 會保留應用程式的主要播放裝置、錄音裝置及現有音訊工作階段的音量。多裝置同時播放、快速鍵、系統匣操作、自動化、手機音訊、螢幕鏡像與剪貼簿同步都需要 SonicRoute 持續執行。

**各版本需要安裝哪種執行階段？**

Full 和 Lite 需要與程式架構相符的 .NET 10 Desktop Runtime，Microsoft Store 版需要 x64 執行階段。Legacy 使用 .NET Framework 4.8。舊 NET8 版已停止維護，仍可下載。

**程式在背景執行時會使用多少 CPU 或電力？**

程式主要依 Windows 通知偵測變更，必要時才會每秒確認一次狀態，並適時清理不再使用的資料。實際的 CPU 使用量和耗電量視系統與使用情況而定。

## 🆕 v1.36 更新重點

所有版本已將快速面板的多裝置操作更新為：`Shift` 點選保留原有選取並加入連續範圍，`Ctrl+Shift` 點選選取或切換 Windows 原生路由裝置，並以不同按鈕樣式區分原生路由與鏡像分發；主視窗左側主導覽列支援收合並記憶狀態，「應用程式」分頁也補齊了與快速面板一致的多輸出操作。

Full 新增 AirPlay 多裝置同時輸出、Android 未壓縮 PCM 傳輸與「超低延遲 / 平衡 / 穩定」三段音訊延遲模式，將應用程式音訊擷取相容門檻延伸至 Android 11 以上，並為無線虛擬螢幕加入自適應視訊位元率控制；Store 版保持工作列識別並宣告 AirPlay 防火牆規則。舊版資訊請參閱[版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

<details>
<summary>v1.35</summary>

在快速面板為電腦應用程式選擇麥克風時，選取的裝置會立即反白。切換期間仍可繼續點選，SonicRoute 會依序套用變更。Full 版中只能選擇單一輸出的外部音源也有相同的操作方式。選擇輸出裝置後，應用程式旁的裝置名稱會立即更新。裝置切換快速鍵現在可以連續使用。按快速鍵切換目前應用程式的輸出時，若在 45 毫秒內完成，畫面只會顯示結果。如果已顯示切換進度，成功結果會在提示顯示至少 500 毫秒後出現，失敗則會立即顯示。當 Windows 將應用程式的音訊工作階段移至另一個已選取的輸出時，其餘已選取的輸出仍會繼續播放。

在 Full 版中，切換 Android 音訊擷取模式時，仍可變更輸出裝置並操作虛擬螢幕。關閉某支手機的剪貼簿同步後，該手機尚未完成的電腦剪貼簿寫入作業會取消，其他手機仍可繼續同步。寫入失敗時，SonicRoute 也會釋放為該次寫入配置的記憶體。舊版更新內容請參閱[版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

</details>

## 📚 文件

詳細 Wiki 目前以簡體中文提供。

| 主題 | 指南 |
|---|---|
| 安裝、面板與日常操作 | [使用指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [功能](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 預設快速鍵、按鍵指派與控制對象 | [快速鍵](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| 觸發條件、多步驟動作與指令碼整合 | [自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [命令列](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| 安裝與疑難排解 | [常見問題](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| 原始碼、建置與版本紀錄 | [原始碼與建置](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [發布資訊](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **給 AI 助理的技術文件**：使用 Cursor、Copilot、ChatGPT 或 Claude 等 AI 助理排解問題、瞭解程式架構或撰寫指令碼時，可以參考 [README.ai.md](README.ai.md)。文件以結構化格式整理實作重點與疑難排解流程，方便直接查閱。

## 📌 開發規劃

- **改善音訊輸出的切換與還原。** 我們正在研究如何在裝置插拔或狀態改變時重新指派音訊輸出，並在裝置重新連線後還原設定，讓同時使用多個音訊工作階段的應用程式也能穩定切換裝置。

[分享想法或回報問題](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 💖 支持 SonicRoute

SonicRoute 是持續維護的免費開放原始碼專案。如果它讓你在電腦上聽音樂、玩遊戲或通話更方便，歡迎透過愛發電贊助，支持新功能開發、相容性改善與長期維護。

☕ [透過愛發電贊助 SonicRoute](https://afdian.com/a/koukou021)

贊助完全自願，所有功能都能免費使用。回報問題、向朋友推薦 SonicRoute，或在 GitHub 上給專案一顆星，也都是對專案的支持。謝謝你！

## 📜 授權條款說明

- **GPL-3.0 開源授權**：SonicRoute 採用 GNU 通用公共授權條款第 3 版，僅限該版本（GPL-3.0-only），詳見 [LICENSE](LICENSE)。允許商用、銷售與付費散布，須遵守 GPL 條款。
- **再散布要求**：保留必要的著作權與授權聲明，標明修改，並以 GPL 第 3 版散布所涵蓋的修改作品；散布所涵蓋的二進位檔案時，須依 GPL 第 6 條提供完整對應原始碼，詳見 [LICENSE](LICENSE)。
- **適用範圍與歷史授權**：自 2026-11-17 本次 GPL 轉換起，在著作權人有權授權的範圍內，目前版本所涵蓋的完整專案（含先前程式碼）採用 GPL-3.0-only。先前已授予的 MIT 與舊自訂授權權利繼續有效，舊標籤與已發布二進位檔案不因此改標；第三方元件保留各自授權（見 [第三方授權聲明](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)）。

---

<a href="EASTER_EGGS.md" title="昨日與明日的彩蛋"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="開啟吉祥物彩蛋"></a>

感謝使用 SonicRoute。 [閱讀指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [回報問題](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 AI 助理參考資料</a></sub></div>
