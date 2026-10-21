<!-- Generated from docs/readme/shared.json and docs/readme/locales/zh-TW.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 音躍 SonicRoute

[简体中文](README.md) | [English](README.en.md) | **繁體中文** | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md)

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE) [![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![在 Afdian 支援我們](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**將每個應用程式的音訊輸出至您選擇的裝置。**

SonicRoute 是常駐於 Windows 系統匣的音訊工具：可為個別應用程式選擇輸出與輸入裝置，讓音樂從喇叭播放，遊戲或通話則使用耳機。透過快速鍵、系統匣滾輪或面板控制音訊，並將日常操作組合成自動化規則。

📥 [下載最新版本](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki（簡體中文）](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（實驗性） · 支援九種介面語言 · MIT 授權

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="使用指南（簡體中文）"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="點擊看板娘開啟使用指南"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute 快捷面板：頂端輸出裝置與各應用程式音量控制"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute v1.23 自動化規則編輯器：應用程式觸發條件與音訊操作選擇"></a>
</div>

<sub>介面範例：快捷面板管理各應用程式音量與裝置；自動化編輯器組合觸發條件與操作步驟。</sub>

## ✨ SonicRoute 的功能

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="功能"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="點擊看板娘瀏覽 SonicRoute 功能"></a>

- **應用級雙向路由（播放 + 錄音）**：為每個應用分別指定輸出與輸入設備，讓遊戲與語音走耳機和麥克風，音樂走音箱。設定由 Windows 原生持久化，結束軟體後依然生效，不破壞系統混音器設定。
- **前景智慧感知與盲操調音**：系統事件驅動秒級鎖定目前前景有聲應用；支援在工作列匣區捲動滾輪直接調整目前應用音量，配合螢幕浮動 OSD 提示與全域麥克風靜音，無需切換視窗。
- **雙形態面板與即時聲音電平**：提供經典逐行管理面板與現代卡片面板（Modern），支援 350~800px 自適應高度與位置儲存；直覺掌控各應用獨立音量，內建即時聲音出聲強弱的電平指示。
- **37 種操作的自動化引擎與 CLI**：支援應用啟閉與切換、排程、快捷鍵等 6 種觸發，自由組合設備切換、音量、靜音、程式關聯啟動與 PowerShell；內建 sr 具名管道短命令，無額外常駐開銷，輕鬆連動腳本與硬體控制台。
- **輕量克制與純事件驅動架構**：全系統事件驅動，無常駐高頻輪詢計時器，閒置 CPU 佔用近乎為零，記憶體按需回收；提供免安裝相依項的 Legacy 版（.NET 4.8）、單檔案 Lite 版（x64 / ARM64 實驗）及商店版，內建九種語言。

## 🎮 日常使用情境

隊友聲音太大時，只調低語音聊天軟體的音量滑桿，不變更遊戲音量。

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="自動化"><img src="docs/images/mascot-automation.png" width="48" align="middle" alt="點擊小機器人了解自動化規則"></a> 例如，設定遊戲啟動時依序執行「遊戲聲音切換至耳機 → 遊戲音量設為 50% → 開啟語音聊天軟體」，並啟用規則。 [設定自動化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

## 🚀 下載與快速入門

**目前版本：[v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [發布說明（簡體中文）](docs/release-v1.24.md)

大多數 Windows x64 使用者可優先選擇 Microsoft Store 版本，安裝方便並支援自動更新。需要單一執行檔可選 Lite x64；不方便安裝 .NET 8 則可選 Legacy x64。上方下載連結一律指向最新版本；下方的直連下載對應 v1.24。

| 版本 | 下載 | 需求 |
|---|---|---|
| Lite x64 | [單一 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | x64 .NET 8 Desktop Runtime |
| Lite ARM64（實驗性） | [單一 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | ARM64 .NET 8 Desktop Runtime |
| Legacy x64 | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | .NET Framework 4.8；執行前請解壓縮整個 ZIP |
| Microsoft Store | [從 Store 安裝](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64；自動更新；Store 上會顯示目前提供的版本 |

Lite 需要相符的 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。Legacy 不需要 .NET 8。

1. 啟動 SonicRoute，在 Windows 系統匣中找到它的圖示（可能位於隱藏圖示中），然後按兩下以開啟完整介面。
2. 在設定中選擇偏好的輸出與輸入裝置。
3. 點擊系統匣圖示開啟面板，或設定快速鍵。需要組合多項操作時，再建立自動化規則。

首次使用 `sr` 前，請先正常啟動 SonicRoute 以註冊指令，然後重新開啟終端機。若裝置切換未如預期運作，請參閱 [常見問題](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)。

1.24 新增自動化入門範本、可設定儲存上限的最近執行詳細資料（預設關閉），以及個別應用程式圖示匯入。缺少圖示或只有系統通用圖示時，顯示應用程式名稱的第一個字；設定備份包含匯入的圖示。舊版變更請參閱 [版本紀錄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

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

- **v1.25 · 重大更新：** 具體內容將於後續更新說明公布。
- **後續階段：** 根據回饋持續改進現有功能。計畫可能隨回饋調整。

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

**作者：[Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://www.ifdian.net/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**
