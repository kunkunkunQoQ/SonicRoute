<!-- Generated from docs/readme/shared.json and docs/readme/locales/zh-TW.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 音躍 SonicRoute

[简体中文](README.md) | [English](README.en.md) | **繁體中文** | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![最新正式版](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=%E6%9C%80%E6%96%B0%E6%AD%A3%E5%BC%8F%E7%89%88)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![授權條款說明：GPL-3.0](https://img.shields.io/static/v1?label=%E6%8E%88%E6%AC%8A%E6%A2%9D%E6%AC%BE%E8%AA%AA%E6%98%8E&message=GPL-3.0&color=blue)](LICENSE) [![作業系統：Windows 10 | 11](https://img.shields.io/static/v1?label=%E4%BD%9C%E6%A5%AD%E7%B3%BB%E7%B5%B1&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![架構：x64 | ARM64](https://img.shields.io/static/v1?label=%E6%9E%B6%E6%A7%8B&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![下載次數](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=%E4%B8%8B%E8%BC%89%E6%AC%A1%E6%95%B8)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![透過愛發電贊助 SonicRoute](https://img.shields.io/static/v1?label=&message=%E9%80%8F%E9%81%8E%E6%84%9B%E7%99%BC%E9%9B%BB%E8%B4%8A%E5%8A%A9+SonicRoute&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**讓音樂從喇叭播放，讓遊戲聲留在耳機裡。**

SonicRoute 是常駐 Windows 系統匣的音訊工具。您可以為每個應用程式選擇播放和錄音裝置，使用快速鍵、滑鼠滾輪或快速面板調整音量，並把常用操作組合成自動化規則。

📥 [下載最新版本](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [使用指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

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

## ✨ SonicRoute 的功能

- **為每個應用程式選擇播放與錄音裝置。** 遊戲與通話可以使用耳麥，音樂則從喇叭播放。Windows 會儲存各應用程式的裝置設定，結束 SonicRoute 後仍然有效。
- **跟隨前景應用程式。** SonicRoute 會偵測視窗切換。在系統匣圖示上捲動滾輪，就能調整目前控制的應用程式音量，或按住 Alt 捲動來切換輸出裝置。可用螢幕提示（OSD）查看變更，也可用全域快速鍵將麥克風靜音。
- **即時查看各應用程式的音量。** 快速面板會顯示各程式的音量滑桿和即時音量表。面板會記住顯示位置，高度可在 350 至 850 個邏輯像素之間調整。
- **Android 連線與 AirPlay 接收（Full 版）。** 將 Android 應用程式的音訊傳到電腦調整，或將電腦音訊傳到手機。虛擬螢幕支援 H.265，視窗在前景時可用遊戲控制器操作。內建以 C# 實作的 AirPlay 接收功能，可播放 Apple 裝置的音訊並按原始比例鏡像顯示畫面。在快速面板中可選擇電腦輸出、Android 手機喇叭或手機連線的藍牙裝置，並透過智慧音量（SmartVolume）減少音量落差。
- **6 種觸發條件、39 種自動化動作。** 規則可在應用程式啟動、切換到前景、結束、到達指定時間、按下快速鍵或啟動 SonicRoute 時執行。可組合切換裝置、調整音量、靜音、啟動程式及執行 PowerShell 等動作，也能透過 `sr` 指令從指令碼或外接控制台執行規則。
- **依 Windows 通知偵測變更。** 需要時才會每秒確認一次狀態，並適時釋放用不到的資料。Full 提供選用的手機與 AirPlay 功能，Lite 和 Legacy 則提供電腦音訊功能。介面支援九種語言。

## 🎮 日常使用情境

遊戲啟動或結束時，可以用規則自動切換輸出裝置和音量。 [設定自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 使用情境 | 操作方式 |
|---|---|
| 🎮 **遊戲聲蓋過隊友的語音，或語音音量太大** | 單獨調整 Discord 等語音通話軟體的音量，遊戲音量不受影響。遊戲和通話也可以分別指定不同的輸出裝置。 |
| 🎵 **玩遊戲時想用喇叭播放音樂或影片** | 將音樂送到喇叭，遊戲聲留在耳機。將指標移到系統匣圖示上，捲動可調整目前控制的應用程式音量，按住 Alt 捲動則可切換它的輸出裝置。 |
| ⚡ **每次開啟遊戲都要重新選擇耳機** | 新增一條遊戲啟動規則，依序切換到耳機、將音量設為 40%，再啟動語音通話軟體。另設一條規則，在遊戲結束時切回喇叭。 |
| 🎙️ **暫時離席，或有不想讓通話對方聽見的談話** | 使用預設快速鍵 Ctrl+Shift+N，將麥克風一併靜音或解除靜音。啟用螢幕提示（OSD）即可確認狀態。 |
| 📹 **用 OBS 直播遊戲或錄製教學影片** | 將要錄製的應用程式指定到專用的實體或虛擬輸出裝置，再讓 OBS 擷取該裝置。私人通話與通知音可使用其他輸出。 |
| 📱 **在電腦上聽手機音訊或操作手機應用程式** | 將 Android 音訊傳到電腦調整音量，並在虛擬螢幕上操作應用程式。也可透過 AirPlay 接收 Apple 裝置的音訊與畫面，在快速面板選擇電腦輸出、Android 手機喇叭或手機連線的藍牙裝置。 |

## 🚀 下載與快速入門

**最新正式版：[v1.33](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.33)** · 2026年11月18日 · [版本資訊（簡體中文）](docs/release-v1.33.md)

若要使用 Android 音訊、虛擬螢幕或 AirPlay，請選 **Full x64**。只需要電腦音訊功能，可選 **Lite x64**。若電腦只能使用 .NET Framework 4.8，請選 **Legacy x64**。ARM64 版目前為實驗版本。下表提供 v1.33 的直接下載連結。Full 和 Lite 也可從 Microsoft Store 取得，上架版本請見各自的商店頁面。

| 版本 | 下載 | 系統需求 |
|---|---|---|
| ⭐ Full（.NET 10，含手機連線功能） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-arm64.exe) | 單一執行檔，內含選用的 Android 音訊、虛擬螢幕與 AirPlay 功能。需要與程式架構相符的 .NET 10 Desktop Runtime。 |
| ⭐ Lite（.NET 10，僅電腦音訊） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-arm64.exe) | 單一執行檔，僅提供電腦音訊功能。需要與程式架構相符的 .NET 10 Desktop Runtime。 |
| 🛡️ Legacy x64（僅電腦功能，.NET Framework 4.8） | [ZIP 壓縮檔](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Legacy-x64.zip) | 使用 Windows 的 .NET Framework 4.8，解壓縮後即可執行。 |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full 與 Lite 均需要 x64 版 .NET 10 Desktop Runtime。上架版本請見各自的商店頁面。 |

<details>
<summary><sub>📦 下載舊版 NET8 相容版本（已停止維護）</sub></summary>

| 版本 | 下載 | 系統需求 |
|---|---|---|
| Lite NET8（已停止維護） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | 此版本已停止維護，仍可下載。建議改用 .NET 10 或 Legacy 版。 |
</details>

> [!NOTE]
> Full 與 Lite 需要安裝 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。Legacy 使用 Windows 的 .NET Framework 4.8。舊版 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 相容版本已停止維護，仍可下載。

### 🏁 三步驟開始使用

1. **啟動 SonicRoute。** 按兩下執行檔。使用 Legacy 版時，請先解壓縮 ZIP 檔案。程式會常駐系統匣，按兩下圖示即可開啟主視窗。如果圖示藏在選單中，可以將它拖到工作列的通知區域。
2. **選擇應用程式的輸出裝置。** 先在設定中勾選要在快速面板顯示的耳機與喇叭。按一下系統匣圖示開啟快速面板，展開應用程式所在的那一列來選擇輸出裝置，或拖曳滑桿來調整音量。
3. **用滑鼠滾輪控制音訊。** 將指標移到系統匣的 SonicRoute 圖示上，捲動可調整目前控制的應用程式音量。按住 Alt 捲動可切換該程式的輸出裝置，按住 Shift 捲動可調整系統音量。也可以在設定中將滾輪的作用範圍擴大至整個系統匣。

### ⚡ 從命令列執行自動化規則

`sr` 指令透過具名管道，請正在執行的 SonicRoute 執行指定規則。如果程式尚未執行，請先開啟程式。SonicRoute 首次啟動時會註冊這個指令，關閉並重新開啟終端機後，即可從指令碼、AutoHotkey 或 Stream Deck 呼叫。詳見 [常見問題](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)。

```powershell
# 在已啟動的 SonicRoute 中執行指定的自動化規則
sr "規則名稱或 ID"
```

### 🆕 v1.33 更新重點

Full 新增以 C# 實作的 AirPlay 接收功能，可播放 PCM、ALAC、AAC-LC 與 AAC-ELD 音訊，並依原始比例鏡像顯示畫面。可選擇啟用連線碼、將視窗保持在最上層或按 F11 切換全螢幕。拖曳即可把音訊送到電腦、Android 手機喇叭或手機連線的藍牙裝置。

Android 連線支援 H.265（HEVC），可選擇音訊與視訊解碼器。只有在開啟裝置設定時才會查詢編碼器，遊戲控制器的輸入也只會傳到前景虛擬螢幕。配對頁將每兩分鐘更新一次的 QR 碼與手動配對碼放在同一張卡片中。從裝置設定重新連線、重新整理快速面板或側邊欄時，都會在區域網路搜尋已儲存的裝置，略過失效的連接埠，並依裝置識別資訊找回新的 IP 位址與連接埠。找不到裝置或連線失敗時會保留手動輸入的位址，重新連線時也可還原裝置設定。

所有版本都能分別設定兩種麥克風狀態是否持續顯示在螢幕上，並將設定檔管理操作集中在同一列。應用程式清單超過 20 項時會重複利用顯示列，連續的螢幕提示（OSD）也會合併更新。程式會釋放已解碼 PNG 的原始資料；Full 也會釋放停用的圖形紋理，關閉視窗時由執行階段自行回收記憶體。其他更新包括 .bak 設定備份、按需產生的錯誤診斷紀錄、獨立的 Microsoft Store Lite 版，以及可離線閱讀的 GPL-3.0-only 授權條款。詳見 [版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

### ❓ 常見問題

**結束 SonicRoute 後，應用程式的輸出裝置與音量會保留嗎？**

Windows 會保留已儲存的裝置設定，以及現有音訊工作階段的音量。快速鍵、系統匣操作、自動化、手機音訊、螢幕鏡像輸出與剪貼簿同步則需要 SonicRoute 持續執行。

**各版本需要安裝哪種執行階段？**

Full 和 Lite 需要與程式架構相符的 .NET 10 Desktop Runtime，Microsoft Store 版需要 x64 執行階段。Legacy 使用 .NET Framework 4.8。舊 NET8 版已停止維護，仍可下載。

**程式在背景執行時會使用多少 CPU 或電力？**

程式主要依 Windows 通知偵測變更，必要時才會每秒確認一次狀態，並適時清理不再使用的資料。實際的 CPU 使用量和耗電量視系統與使用情況而定。

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
