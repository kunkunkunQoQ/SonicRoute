<!-- Generated from docs/readme/shared.json and docs/readme/locales/ko-KR.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | **한국어** | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian에서 후원](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**앱마다 원하는 오디오 장치를 지정하세요.**

SonicRoute는 Windows 알림 영역에 상주하는 오디오 유틸리티입니다. 앱마다 출력 및 입력 장치를 선택해 음악은 스피커로, 게임이나 통화는 헤드셋으로 들을 수 있습니다. 단축키, 알림 영역 휠 또는 패널로 오디오를 제어하고, 자주 하는 작업을 자동화 규칙으로 묶어 보세요.

📥 [최신 릴리스 다운로드](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [사용자 가이드](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (실험적) · 인터페이스 9개 언어 지원 · MIT 라이선스

> [!TIP]
> **AI를 위한 빠른 요약 및 토큰 절약 문제 해결**: Cursor, Copilot, ChatGPT, Claude 등 AI 어시스턴트로 문제를 진단하거나 구조를 파악하고 스크립트를 작성할 때 전용 사양서 [README.ai.md](README.ai.md) 문서를 직접 참조하는 것을 권장합니다. LLM을 위한 고밀도 구조화 DSL로 핵심 사실을 정리하여 불필요한 텍스트를 줄이고, 토큰 소비를 대폭 절약하며 원인을 신속하게 도출합니다.

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="사용자 가이드(중국어 간체)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="마스코트를 클릭해 사용자 가이드 열기"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute 빠른 패널: 위쪽의 출력 장치와 앱별 볼륨 제어"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute 자동화 규칙 편집기: 앱 트리거 및 오디오 동작 단계 선택"></a>
</div>

<sub>인터페이스 미리보기: 높이 조절 및 아크릴 스타일을 지원하는 퀵 패널로 앱 볼륨과 장치를 즉시 제어하고, 자동화 편집기에서 트리거와 동작을 자유롭게 구성할 수 있습니다.</sub>

## ✨ SonicRoute의 주요 기능

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="기능"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="마스코트를 클릭해 SonicRoute 기능 살펴보기"></a>

- **앱별 개별 사운드 분할 및 양방향 라우팅(재생 + 녹음):** 앱마다 재생 및 녹음 장치를 개별 지정하여 게임과 음성 채팅은 헤드셋으로, 음악은 스피커로 전달합니다. 설정은 Windows에 네이티브로 유지되어 프로그램을 종료해도 유지됩니다.
- **포그라운드 스마트 추적 및 트레이 휠 간편 제어:** 시스템 이벤트를 통해 소리가 나는 활성 앱을 즉시 감지합니다. 작업 표시줄 트레이에서 휠 스크롤로 볼륨 조절 및 Alt+휠로 장치 전환이 가능하며, OSD 알림과 마이크 음소거로 화면 전환 없이 제어합니다.
- **빠른 패널 및 실시간 오디오 레벨 표시:** 카드 패널은 350~850px 높이 조절과 위치 저장을 지원합니다. 앱별 볼륨 슬라이더와 실시간 오디오 레벨을 직관적으로 확인하세요.
- **자동화 콤보 엔진 및 초고속 CLI(6가지 트리거 × 37개 작업):** 앱 실행/전환/종료, 예약, 단축키 등 6가지 트리거 지원. 장치 전환, 볼륨, 음소거, PowerShell 등을 조합할 수 있으며, 내장된 sr 명령어로 외부 스크립트 및 하드웨어와 손쉽게 연동됩니다.
- **이벤트 우선 감지:** 시스템 알림을 사용하고 필요한 경우 1초 간격 스냅샷 확인으로 보완하며, 불필요한 데이터는 필요할 때 정리합니다. Legacy(.NET 4.8), .NET 10 및 NET8 Lite, Store 버전을 9개 언어로 제공합니다.

## 🎮 일상적인 활용

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="자동화"><img src="docs/images/mascot-automation-kr.png" width="48" align="middle" alt="작은 로봇을 클릭해 자동화 규칙 알아보기"></a> 번거로운 수동 설정을 한 번의 구성으로 언제나 간편한 자동화로 전환하세요. [자동화 설정](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 일상 속 사용 시나리오 | SonicRoute의 해결 방법 |
|---|---|
| 🎮 **음성 채팅이 너무 크거나 게임 소리에 팀원 목소리가 묻힐 때** | Discord 등 음성 채팅 볼륨만 따로 조절하여 게임 사운드를 유지하고, 게임과 음성을 분리 출력 |
| 🎵 **게임을 하면서 배경으로 음악이나 영상을 재생할 때** | 음악은 스피커로, 게임은 헤드셋으로 분리. 전체 화면 게임 중에도 작업 표시줄 트레이에서 휠 스크롤로 볼륨을 조절하거나 Alt+휠로 장치를 즉시 전환 |
| ⚡ **게임을 할 때마다 헤드셋을 다시 연결하거나 설정을 바꿀 때** | 게임 실행 시 '헤드셋 전환 + 볼륨 40% + 음성 앱 실행', 게임 종료 시 스피커로 자동 복귀 규칙 설정 |
| 🎙️ **잠시 자리를 비우거나 개인적인 통화를 해야 할 때** | Ctrl+Shift+N으로 모든 마이크를 즉시 음소거하고, 화면 OSD 배너로 상태를 한눈에 확인 |
| 📹 **OBS 게임 방송 또는 화면 녹화 튜토리얼 제작 시** | 앱별로 독립된 오디오 트랙을 지정하여 개인 대화나 알림음이 녹화에 섞여 들어가는 것 방지 |

## 🚀 다운로드 및 시작하기

**현재 릴리스: [v1.29](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.29)** · 2026-10-27 · [릴리스 노트(중국어 간체)](docs/release-v1.29.md)

일반 PC 환경에서는 **Lite x64** (단일 실행 파일, 가벼운 메모리 점유)를 권장합니다. 런타임 설치가 제한된 업무용 PC는 **Legacy x64** (추가 설치 불필요, 압축 해제 즉시 실행), ARM64 PC는 **Lite ARM64**를 선택하세요. **Microsoft Store**를 통한 설치 및 자동 업데이트도 지원합니다. 아래 링크는 최신 릴리스 v1.29입니다.

| 에디션 | 다운로드 | 요구 사항 |
|---|---|---|
| ⭐ Lite x64 (.NET 10 권장) | [단일 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-x64.exe) | 휴대용 단일 실행 파일; x64 .NET 10 Desktop Runtime 필요 |
| ⚡ Lite ARM64 (.NET 10) (실험적) | [단일 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-arm64.exe) | 휴대용 ARM64 실행 파일; ARM64 .NET 10 Desktop Runtime 필요 |
| ☕ Lite NET8 x64 | [단일 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) | NET8 호환 Lite; x64 .NET 8 Desktop Runtime 필요 |
| ☕ Lite NET8 ARM64 (실험적) | [단일 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | NET8 호환 Lite; ARM64 .NET 8 Desktop Runtime 필요 |
| 🛡️ Legacy x64 (.NET 8 불필요) | [전체 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Legacy-x64.zip) | 추가 런타임 불필요, 제한된 환경에 적합; Windows 내장 .NET Framework 4.8 기반, 압축 해제 후 즉시 실행 |
| 🔄 Microsoft Store (Store 업데이트) | [Store에서 설치](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | .NET 10 Store 패키지에는 x64 .NET 10 Desktop Runtime이 필요합니다. 현재 제공 중인 버전은 Store 페이지에서 확인하세요(64비트 Windows 10/11). |

> [!NOTE]
> 런타임 안내: 기본 Lite는 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), NET8 호환 버전은 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치가 필요합니다 (2026년 11월 이후 지원 종료 예정으로 .NET 10 또는 Legacy 전환 권장). Legacy 버전은 Windows 내장 .NET Framework 4.8을 사용하여 추가 설치가 필요 없습니다.

### 🏁 3단계 빠른 시작

1. **실행 및 트레이 고정**: 더블 클릭하여 실행하면 작업 표시줄 트레이에 상주합니다 (오버플로 영역에서 밖으로 끌어내어 항상 표시 권장). 트레이 아이콘을 더블 클릭하면 메인 창이 열립니다.
2. **장치 선택 및 앱 라우팅**: '설정 → 보존할 장치'에서 사용하는 헤드폰과 스피커를 선택합니다. 트레이 아이콘을 클릭하여 퀵 패널을 열고 슬라이더로 앱별 출력 장치와 볼륨을 즉시 조절합니다.
3. **트레이 휠 블라인드 조절**: 작업 표시줄 트레이 영역에 마우스를 올리고 휠을 굴려 앱 볼륨을 조절하고, Alt+휠로 장치를 즉시 전환하며, Shift+휠로 시스템 마스터 볼륨을 조절할 수 있습니다. 게임 중 화면을 전환하지 않고 손쉽게 제어하세요.

### ⚡ CLI 및 자동화 연동 (파워 유저)

`sr` 명령은 Named Pipe를 통해 실행 중인 SonicRoute 인스턴스와 밀리초 단위로 통신합니다 (앱이 실행 중이지 않으면 시작되지 않습니다). 앱을 처음 실행하면 명령어가 등록되며, 터미널 재시작 후 스크립트, AutoHotkey, Stream Deck에서 직접 호출할 수 있습니다. 자세한 내용은 [자주 묻는 질문](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)를 참조하세요.

```bash
# 자동화 규칙 트리거 (SonicRoute가 실행 중이어야 함)
sr "규칙 이름 또는 ID"
```

### 🆕 v1.29 릴리스 주요 변경사항

v1.29에서는 시스템 이벤트 기반 온디맨드 갱신과 보존 장치 필터링을 지원하는 출력 장치 볼륨 패널이 추가되었습니다. 또한 마스터 볼륨 조절을 위한 Shift+휠 동작이 추가되어 기존 앱 볼륨 조절 및 Alt+휠 장치 전환과 유기적으로 작동합니다. 자세한 이력은 [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)를 확인하세요.

### ❓ 자주 묻는 질문 (FAQ)

**Q: 프로그램을 종료한 후에도 앱 라우팅 및 볼륨 설정이 유지되나요?**

A: 네, 유지됩니다! 앱 오디오 라우팅은 Windows 오디오 엔진에 의해 기본적으로 영구 저장되므로 프로그램을 종료해도 유지됩니다. 단축키, 트레이 제어, 자동화 규칙만 상주가 필요합니다.

**Q: 각 버전에 필요한 Runtime은 무엇인가요?**

A: 기본 Lite에는 시스템에 맞는 .NET 10 Desktop Runtime이, Lite NET8에는 .NET 8이 필요합니다. Legacy는 별도 .NET Desktop Runtime 없이 .NET Framework 4.8로 실행됩니다. 현재 Store 패키지는 x64 .NET 10 Desktop Runtime이 필요합니다.

**Q: 백그라운드 상주 중 CPU나 배터리를 소모하나요?**

A: 감지는 시스템 이벤트를 우선 사용하고 필요한 경우에만 1초 간격 스냅샷 확인을 보완적으로 사용합니다. 불필요한 데이터는 필요할 때 정리하며 실제 CPU 사용량은 시스템과 작업에 따라 다릅니다.

## 📚 문서

자세한 Wiki는 현재 중국어 간체로 제공됩니다.

| 주제 | 가이드 |
|---|---|
| 설치, 패널 및 일상적인 제어 | [사용자 가이드](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [기능](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 기본 키, 할당 및 제어 대상 | [단축키](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| 트리거, 작업 및 스크립트 연동 | [자동화](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [명령줄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| 설치 및 문제 해결 | [자주 묻는 질문](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| 소스, 빌드 및 릴리스 | [소스 및 빌드](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [릴리스 정보](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

## 📌 로드맵

- **오디오 라우팅 기능 향상 (Route Enhancement)**: 핵심 라우팅 엔진을 고도화하여 기기 연결/해제 시 자동 복구 및 동적 스트림 전환을 지원하고, 복잡한 다중 세션 환경에서의 라우팅 안정성을 강화할 계획입니다.

[아이디어 공유 또는 문제 제보](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 🧩 다른 프로젝트와 함께 사용하기

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**: Windows용 오디오 플레이어입니다. 자동화의 ‘프로그램 실행’ 동작과 함께 사용하면 단축키로 효과음을 재생할 수 있습니다. [설정 가이드(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="XiaoYue Comics"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="달걀 껍데기에서 고개를 내밀고 만화책을 든 샤오웨를 클릭해 연재 만화 읽기"></a>

**[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

달걀에서 튀어나온 마법소녀가 도와주려 할 때마다 일이 더 꼬이는 황당한 일상을 그린 개그 만화입니다.

<br clear="all">

---

<a href="EASTER_EGGS.md" title="어제와 내일의 이스터 에그"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="쉬고 있는 마스코트를 클릭해 이스터 에그 찾기"></a>

SonicRoute를 사용해 주셔서 감사합니다. [가이드 읽기(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [문제 제보](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**제작자: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
