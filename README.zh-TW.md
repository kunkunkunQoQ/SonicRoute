<!-- Generated from docs/readme/shared.json and docs/readme/locales/zh-TW.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 音躍 SonicRoute

[简体中文](README.md) | [English](README.en.md) | **繁體中文** | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![在 Afdian 支援我們](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**將每個應用程式的音訊輸出至您選擇的裝置。**

SonicRoute 是常駐於 Windows 系統匣的音訊工具：可為個別應用程式選擇輸出與輸入裝置，讓音樂從喇叭播放，遊戲或通話則使用耳機。透過快速鍵、系統匣滾輪或面板控制音訊，並將日常操作組合成自動化規則。

📥 [下載最新版本](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（實驗性） · 支援九種介面語言 · MIT 授權

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="使用指南（簡體中文）"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="點擊看板娘開啟使用指南"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute 快速面板：頂端輸出裝置與各應用程式音量控制"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute 自動化規則編輯器：選擇應用程式觸發器與音訊操作步驟"></a>
</div>

<sub>介面預覽：快速面板（支援自適應與透明壓克力風格）即時控制音量與裝置；自動化規則編輯器自由編排觸發器與執行動作。</sub>

## ✨ SonicRoute 的功能

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="功能"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="點擊看板娘瀏覽 SonicRoute 功能"></a>

- **應用獨立聲音分流與雙向路由（播放 + 錄音）**：為每個應用分別指定輸出與輸入設備，讓遊戲與語音走耳機和麥克風，音樂走音箱。設定由 Windows 原生持久化，結束軟體後依然生效，不破壞系統混音器設定。
- **前景焦點智慧跟隨與匣區滾輪盲操**：系統事件驅動秒級鎖定目前前景有聲應用；支援在工作列匣區捲動滾輪盲調音量，按住 Alt 捲動即時換軌裝置，配合螢幕浮動 OSD 提示與全域麥克風靜音，無需切換視窗。
- **快速面板與即時出聲電平指示**：卡片式快速面板支援 350~850px 高度調整與位置儲存；直覺掌控各應用獨立音量，內建即時聲音電平指示。
- **自動化連招引擎與極速 CLI（6 種觸發 × 37 種操作）**：支援應用啟閉與切換、排程、快捷鍵等 6 種觸發，自由組合設備切換、音量、靜音、程式關聯啟動與 PowerShell；內建 sr 具名管道短命令，無額外常駐開銷，輕鬆連動腳本與硬體控制台。
- **事件優先偵測**：使用系統通知，並在需要時以每秒一次的快照檢查作為備援；不需要的資料會按需清理。提供 Legacy（.NET 4.8）、.NET 10 與 NET8 Lite，以及商店版，支援九種語言。

## 🎮 日常使用情境

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="自動化"><img src="docs/images/mascot-automation.png" width="48" align="middle" alt="點擊小機器人了解自動化規則"></a> 把繁瑣的手動改設定，變成一次配置、隨開隨用的自動化體驗。 [設定自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 你的日常場景 | SonicRoute 怎麼幫你解決 |
|---|---|
| 🎮 **遊戲開黑，隊友太吵或遊戲聲蓋過語音** | 單獨調整語音軟體（Discord/YY）音量，遊戲音效不受影響；遊戲走耳機，語音走耳麥，互不串音 |
| 🎵 **邊玩遊戲邊在後台聽歌追劇** | 音樂送去音箱，遊戲留在耳機；無需切出遊戲全螢幕，滑鼠懸停在工作列匣區直接滾輪調音量或 Alt+滾輪換軌裝置 |
| ⚡ **每次開遊戲都要插拔耳機、改 Windows 設定** | 設定規則：遊戲開啟時自動「切換耳機 + 音量 40% + 啟動語音軟體」，結束遊戲自動切回音箱 |
| 🎙️ **臨時離開電腦、拿外賣或私密通話** | 一鍵全域麥克風靜音（快捷鍵 Ctrl+Shift+N），配合螢幕常駐 OSD 浮窗，一眼確認是否閉麥 |
| 📹 **OBS 遊戲直播 / 錄屏教學** | 為各應用指定獨立實體或虛擬軌道，避免錄影時把私人聊天或系統提示音混入錄像 |

## 🚀 下載與快速入門

**目前版本：[v1.29](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.29)** · 2026-10-27 · [發布說明（簡體中文）](docs/release-v1.29.md)

Windows x64 使用者可選擇 .NET 10 Lite、NET8 相容版 Lite，或以 .NET Framework 4.8 為基礎的 Legacy。商店頁面顯示目前上架版本，更新透過 Microsoft Store 提供。下方直連下載對應 v1.29。

| 版本 | 下載 | 需求 |
|---|---|---|
| ⭐ Lite x64（.NET 10 預設版） | [單一 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-x64.exe) | 單一檔案可攜版；需要相符的 x64 .NET 10 Desktop Runtime |
| ⚡ Lite ARM64（.NET 10）（實驗性） | [單一 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-arm64.exe) | ARM64 可攜版；需要 ARM64 .NET 10 Desktop Runtime |
| ☕ Lite NET8 x64 | [單一 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) | NET8 相容版 Lite；需要 x64 .NET 8 Desktop Runtime |
| ☕ Lite NET8 ARM64（實驗性） | [單一 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | NET8 相容版 Lite；需要 ARM64 .NET 8 Desktop Runtime |
| 🛡️ Legacy x64（免裝執行庫） | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Legacy-x64.zip) | 免裝額外執行庫，適合受限辦公電腦；依賴系統內建 .NET Framework 4.8，解壓即用 |
| 🔄 Microsoft Store（商店提供更新） | [從 Store 安裝](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | NET10 商店套件需要 x64 .NET 10 Desktop Runtime。上架版本請以 Microsoft Store 頁面為準；適用於 64 位元 Windows 10/11。 |

預設 Lite 需要 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；NET8 相容版需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。NET8 版將於 2026 年 11 月之後停止提供與維護。此後 x64 使用者可手動選擇 .NET 10 或 Legacy（.NET Framework 4.8）；ARM64 使用者請選擇 .NET 10。

1. **按兩下啟動常駐**：按兩下啟動後常駐系統匣（可在工作列角溢位區拖出固定），按兩下系統匣圖示開啟主介面。
2. **勾選分流裝置**：在「設定 → 保留的裝置」勾選常用輸出與輸入裝置；按一下系統匣圖示滑出面板，即時滑動調節並分流各個應用程式的聲音輸出。
3. **無感盲調掌控**：把滑鼠移到匣區圖示上直接捲動滾輪盲調音量，按住 Alt 捲動即時換軌裝置；配合快捷鍵與自動化規則，無需切出全螢幕遊戲也能從容掌控。

首次使用 `sr` 前，請先正常啟動 SonicRoute 以註冊指令，然後重新開啟終端機。若裝置切換未如預期運作，請參閱 [常見問題](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)。

```bash
# 觸發指定自動化規則（需 SonicRoute 正在運行）
sr "規則名稱或 ID"
```

1.29 新增輸出裝置附加音量面板，按需載入並由事件更新，可篩選為僅顯示保留裝置。Shift+滾輪調整系統音量；一般滾輪、Alt+滾輪與 Alt+Shift+滾輪行為維持不變。先前變更見 [版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

### ❓ 常見問題速覽

**Q：關閉軟體後，應用程式的分流與音量設定還會保留嗎？**

A：會！應用程式路由由 Windows 核心音訊服務原生持久化保存；關閉 SonicRoute 後依然有效。僅快捷鍵、系統匣控制與自動化規則需要軟體在後台常駐生效。

**Q：各版本需要什麼執行階段？**

A：預設 Lite 需要相符的 .NET 10 Desktop Runtime，Lite NET8 需要 .NET 8。Legacy 以 .NET Framework 4.8 為基礎，不需額外安裝 .NET Desktop Runtime。目前商店套件需要 x64 .NET 10 Desktop Runtime。

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

## 📌 開發規劃

- **音訊分流與路由能力進階（Route Enhancement）**：持續打磨核心音訊路由引擎，探索更靈活的應用流向接力、裝置熱插拔與狀態切換時的智慧重定向及動態復原，進一步增強特殊應用與多音訊工作階段場景下的分流穩定性。

[分享想法或回報問題](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 🧩 其他專案與組合用法

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**：Windows 音訊播放器。搭配「啟動程式」動作，即可透過快速鍵播放音效。 [設定指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="小躍的奇妙日常"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="點擊從蛋殼裡探頭、抱著漫畫冊的小躍，閱讀漫畫連載"></a>

**[小躍的奇妙日常](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

從雞蛋裡蹦出來的魔法少女，以及她越幫越忙的離譜日常。

<br clear="all">

---

<a href="EASTER_EGGS.md" title="昨日與明日的彩蛋"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="發現彩蛋"></a>

感謝使用 SonicRoute。 [閱讀指南（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [回報問題](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
