<!-- Generated from docs/readme/shared.json and docs/readme/locales/zh-TW.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 音躍 SonicRoute

[简体中文](README.md) | [English](README.en.md) | **繁體中文** | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: Source Available 1.0](https://img.shields.io/badge/License-Source%20Available%201.0-4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![在 Afdian 支援我們](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**將每個應用程式的音訊輸出至您選擇的裝置。**

SonicRoute 是常駐於 Windows 系統匣的音訊工具：可為個別應用程式選擇輸出與輸入裝置，讓音樂從喇叭播放，遊戲或通話則使用耳機。透過快速鍵、系統匣滾輪或面板控制音訊，並將日常操作組合成自動化規則。

📥 [下載最新版本](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（實驗性） · 支援九種介面語言 · [原始碼可見授權 1.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="SonicRoute 即時示範：將手機應用程式音訊拖曳至電腦快速面板並切換輸出裝置"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="SonicRoute 自動化規則編輯器：選擇應用程式觸發器與音訊操作步驟"></a>
</div>

<sub>介面預覽：左側示範手機與電腦音訊拖曳分流與快速切換輸出；右側展示自動化規則編輯器自由編排觸發器與執行動作。</sub>

<details>
<summary><sub>🖼️ 展開查看更多介面截圖（快速面板 &amp; 手機互聯管理）</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute 快速面板：頂端輸出裝置與各應用程式音量控制"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="SonicRoute 手機互聯管理介面：Android 裝置配對、固定顯示應用程式與虛擬螢幕音訊設定"></a>
</div>

</details>

## ✨ SonicRoute 的功能

- **應用獨立聲音分流與雙向路由（播放 + 錄音）**：為每個應用分別指定輸出與輸入設備，讓遊戲與語音走耳機和麥克風，音樂走音箱。設定由 Windows 原生持久化，結束軟體後依然生效，不破壞系統混音器設定。
- **前景焦點智慧跟隨與匣區滾輪盲操**：系統事件驅動秒級鎖定目前前景有聲應用；支援在工作列匣區捲動滾輪盲調音量，按住 Alt 捲動即時換軌裝置，配合螢幕浮動 OSD 提示與全域麥克風靜音，無需切換視窗。
- **快速面板與即時出聲電平指示**：卡片式快速面板支援 350~850px 高度調整與位置儲存；直覺掌控各應用獨立音量，內建即時聲音電平指示。
- **Android 手機互聯與獨立虛擬螢幕（Full 版）**：支援手機音訊接入電腦獨立分流與調音，電腦聲音送入手機播放；支援手機應用在電腦獨立虛擬螢幕視窗自適應顯示，快速面板直顯源手機揚聲器與藍牙裝置並持續接管，搭配預設智慧音量（SmartVolume）感知平衡響度。
- **自動化連招引擎與極速 CLI（6 種觸發 × 39 種操作）**：支援應用啟閉與切換、排程、快捷鍵等 6 種觸發，自由組合設備切換、音量、靜音、程式關聯啟動與 PowerShell；內建 sr 具名管道短命令，無額外常駐開銷，輕鬆連動腳本與硬體控制台。
- **事件優先偵測**：使用系統通知，並在需要時以每秒一次的快照檢查作為備援；不需要的資料會按需清理。Full 提供可選 Android 手機音訊與虛擬螢幕整合；Lite 與 Legacy 僅含電腦功能。支援九種語言。

## 🎮 日常使用情境

把繁瑣的手動改設定，變成一次配置、隨開隨用的自動化體驗。 [設定自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 你的日常場景 | SonicRoute 怎麼幫你解決 |
|---|---|
| 🎮 **遊戲開黑，隊友太吵或遊戲聲蓋過語音** | 單獨調整語音軟體（Discord/YY）音量，遊戲音效不受影響；遊戲走耳機，語音走耳麥，互不串音 |
| 🎵 **邊玩遊戲邊在後台聽歌追劇** | 音樂送去音箱，遊戲留在耳機；無需切出遊戲全螢幕，滑鼠懸停在工作列匣區直接滾輪調音量或 Alt+滾輪換軌裝置 |
| ⚡ **每次開遊戲都要插拔耳機、改 Windows 設定** | 設定規則：遊戲開啟時自動「切換耳機 + 音量 40% + 啟動語音軟體」，結束遊戲自動切回音箱 |
| 🎙️ **臨時離開電腦、拿外賣或私密通話** | 一鍵全域麥克風靜音（快捷鍵 Ctrl+Shift+N），配合螢幕常駐 OSD 浮窗，一眼確認是否閉麥 |
| 📹 **OBS 遊戲直播 / 錄屏教學** | 為各應用指定獨立實體或虛擬軌道，避免錄影時把私人聊天或系統提示音混入錄像 |
| 📱 **手機聲音接入電腦播放，或在電腦上操作手機應用** | 把手機音樂、影片或遊戲聲音接入電腦耳機或音箱獨立調音；手機畫面放入電腦獨立虛擬視窗中操作，快速面板直接控制源手機揚聲器與藍牙裝置，無需頻繁看手機 |

## 🚀 下載與快速入門

**目前版本：[v1.32](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.32)** · 2026-11-14 · [發布說明（簡體中文）](docs/release-v1.32.md)

需要可選 Android 手機音訊與虛擬螢幕功能時選擇 **Full x64**；只使用電腦音訊功能可選 **Lite x64**；受限環境可選依賴系統 .NET Framework 4.8 的 **Legacy x64**。ARM64 版本為實驗性。Full 包含可選手機整合，Lite 與 Legacy 不含手機功能。商店套件以 Full 為建置來源，目前上架版本以 Store 頁面為準。下方直連對應 v1.32。

| 版本 | 下載 | 需求 |
|---|---|---|
| ⭐ Full（.NET 10，含可選手機互聯） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-arm64.exe) | 單 EXE 便攜版；需要匹配架構的 .NET 10 Desktop Runtime，包含可選 Android 手機音訊與虛擬螢幕集成。 |
| ⭐ Lite（.NET 10，僅電腦音訊） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Lite-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Lite-arm64.exe) | 單檔案便攜版；需要匹配架構的 .NET 10 Desktop Runtime，僅包含電腦音訊功能。 |
| 🛡️ Legacy x64（僅電腦功能，.NET Framework 4.8） | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.32/SonicRoute-v1.32-Legacy-x64.zip) | 免裝額外執行庫，適合受限辦公機；依賴系統自帶 .NET Framework 4.8，解壓即用。 |
| 🔄 Microsoft Store（基於 Full） | [從 Store 安裝](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | 以 Full 為建置來源；需要 x64 .NET 10 Desktop Runtime，在架版本以 Store 頁面為準。 |

<details>
<summary><sub>📦 歷史 NET8 相容版（已停更，保留歷史下載，點擊展開）</sub></summary>

| 版本 | 下載 | 需求 |
|---|---|---|
| Lite NET8（已停更） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64（實驗性）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | 已停更，保留歷史下載（後續版本不再提供）；建議遷移至 .NET 10 或 Legacy。 |
</details>

> [!NOTE]
> 執行階段說明：Full 與 Lite 都需要安裝 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；Legacy 依賴系統自帶 .NET Framework 4.8，無需額外安裝執行階段（歷史 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 相容版已停止維護，歷史下載保留）。

### 🏁 快速上手三步驟

1. **按兩下啟動常駐**：下載解壓縮或按兩下執行後軟體常駐工作列系統匣（建議從溢出區拖出固定以便隨時檢視）；按兩下系統匣圖示即可打開主視窗。
2. **勾選裝置與分流**：首次在「設定 → 保留的裝置」勾選常用耳機與音響；按一下系統匣圖示呼出快速面板，滑動應用程式滑桿即時分流裝置與調整音量。
3. **系統匣盲調與快捷掌控**：滑鼠懸停在工作列系統匣區直接滾動滾輪調整應用程式音量，按住 Alt 滾動即時換軌裝置，Shift 滾輪調系統總音量；配合全域靜音快速鍵或自動化連動，無需切換畫面即可從容掌控。

### ⚡ 命令列與自動化連動（極客推薦）

`sr` 命令基於具名管道與已執行的 SonicRoute 執行個體毫秒級通訊（軟體未執行時不會啟動）。首次使用請先啟動軟體自動註冊環境變數，重新啟動終端機後即可在指令碼、AutoHotkey 或 Stream Deck 旋鈕中呼叫。遇到疑問可查閱 [常見問題](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)。

```bash
# 觸發指定自動化規則（需 SonicRoute 正在運行）
sr "規則名稱或 ID"
```

### 🆕 v1.32 版本亮點速覽

v1.32 為 Full 併入純 C# 自研 Android 後端，移除外部 adb/scrcpy/FFmpeg 等第三方二進位檔並大幅縮小單檔體積，新增虛擬螢幕遊戲手把支援、調整手機面板間距（6 DIP）、手機「系統輸出」自動跟隨 Windows 預設播放裝置與多項管線修復；全版本（Full/Lite/Legacy）同步修復匣長名稱子選單重疊並最佳化圖示、OSD、主題字典與閒置記憶體佔用。詳見 [版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

### ❓ 常見問題速覽

**Q：關閉軟體後，應用程式的分流與音量設定還會保留嗎？**

A：Windows 會保留應用路由和現有音訊工作階段。快捷鍵、系統匣控制、自動化以及手機音訊、鏡像、剪貼簿同步需要 SonicRoute 繼續執行。

**Q：各版本需要什麼執行階段？**

A：Full 與 Lite 需要符合架構的 .NET 10 Desktop Runtime；Legacy 使用 .NET Framework 4.8。Store 套件需要 x64 .NET 10 Desktop Runtime。歷史 NET8 已停止維護，舊檔案仍可下載。

**Q：軟體在後台常駐會佔用 CPU 或耗電嗎？**

A：偵測優先使用系統事件，必要時以每秒一次的快照檢查作為備援；不需要的資料會按需清理。實際 CPU 使用量取決於系統與使用情況。

## 📚 文件

詳細 Wiki 目前以簡體中文提供。

| 主題 | 指南 |
|---|---|
| 安裝、面板與日常操作 | [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [功能](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 預設按鍵、綁定與控制目標 | [快速鍵](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| 觸發條件、操作與指令碼整合 | [自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [命令列](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| 安裝與疑難排解 | [常見問題](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| 原始碼、建置與發布 | [原始碼與建置](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [發布資訊](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **AI 快速總結與省 Token 排錯**：使用 Cursor、Copilot、ChatGPT 或 Claude 等 AI 助手排查問題、理解架構或編寫腳本時，建議直接參考專用機器規範 [README.ai.md](README.ai.md)。該文件以高密度結構化 DSL 提煉核心機制與排障決策樹，避免大模型掃描冗長圖文，大幅節省上下文 Token 開銷並秒級定位根因。

## 📌 開發規劃

- **音訊分流與路由能力進階（Route Enhancement）**：持續打磨核心音訊路由引擎，探索更靈活的應用流向接力、裝置熱插拔與狀態切換時的智慧重定向及動態復原，進一步增強特殊應用與多音訊工作階段場景下的分流穩定性。

[分享想法或回報問題](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 🧩 其他專案與組合用法

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**：Windows 音訊播放器。搭配「啟動程式」動作，即可透過快速鍵播放音效。 [設定指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)
- **[小躍的奇妙日常](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**：從雞蛋裡蹦出來的魔法少女，以及她越幫越忙的離譜日常。

## 📜 授權條款說明

- **原始碼可見協議（非 OSI 開源授權）**：依據 [LICENSE](LICENSE)（SonicRoute Source Available License 1.0），任何人皆可免費使用、檢視、學習與修改原始碼，包含企業內部免費使用。
- **公開發布與限制**：公開分發衍生作品時須保留版權聲明、註明 SonicRoute 專案名稱與 GitHub 原始倉庫、標註修改內容，並以同協議免費提供完整對應原始碼；未經書面授權，禁止商業轉賣、收費綑綁、付費分發、閉源分發及冒充官方（商業授權申請詳見 [LICENSE](LICENSE)）。
- **生效範圍與歷史 MIT 權利**：自 2026-11-14 提交 442dac3 起，在著作權人有權授權的範圍內，主分支完整專案（包括此前編寫的程式碼）統一以本協議提供。新授權發行範圍包含 v1.32 系列，無須等待 v1.33；其 Source Available 修訂版須獨立標示並註明原始碼提交及授權。原始 v1.32（4e3138f / 標籤 v1.32）及原始發行產物仍為 MIT。歷史 MIT 版本（含 v1.31 及更早版本）的權利不被撤銷，即使今天或以後才取得副本，也可繼續依 MIT 使用、修改及散布。第三方及無法重新授權的程式碼保留原授權（見 [第三方授權聲明](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)）。

---

<a href="EASTER_EGGS.md" title="昨日與明日的彩蛋"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="發現彩蛋"></a>

感謝使用 SonicRoute。 [閱讀指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [回報問題](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
