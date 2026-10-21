<!-- Generated from docs/readme/shared.json and docs/readme/locales/ko-KR.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | **한국어** | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md)

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE) [![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian에서 후원](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**앱마다 원하는 오디오 장치를 지정하세요.**

SonicRoute는 Windows 알림 영역에 상주하는 오디오 유틸리티입니다. 앱마다 출력 및 입력 장치를 선택해 음악은 스피커로, 게임이나 통화는 헤드셋으로 들을 수 있습니다. 단축키, 알림 영역 휠 또는 패널로 오디오를 제어하고, 자주 하는 작업을 자동화 규칙으로 묶어 보세요.

📥 [최신 릴리스 다운로드](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [사용자 가이드](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (실험적) · 인터페이스 9개 언어 지원 · MIT 라이선스

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="사용자 가이드(중국어 간체)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="마스코트를 클릭해 사용자 가이드 열기"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute 빠른 패널: 위쪽의 출력 장치와 앱별 볼륨 제어"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute v1.23 자동화 규칙 편집기: 앱 실행 조건 및 오디오 작업 선택"></a>
</div>

<sub>화면 예시: 빠른 패널에서 앱별 볼륨과 장치를 조절하고, 자동화 편집기에서 실행 조건과 작업 단계를 조합할 수 있습니다.</sub>

## ✨ SonicRoute의 주요 기능

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="기능"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="마스코트를 클릭해 SonicRoute 기능 살펴보기"></a>

- **앱별 양방향 라우팅(재생 + 녹음):** 앱마다 재생 및 녹음 장치를 개별 지정하여 게임과 음성 채팅은 헤드셋으로, 음악은 스피커로 전달합니다. 설정은 Windows에 네이티브로 유지되어 프로그램을 종료해도 유지됩니다.
- **포그라운드 앱 감지 및 간편한 제어:** 시스템 이벤트를 통해 오디오가 실행 중인 포그라운드 앱을 즉시 감지합니다. 작업 표시줄 트레이 영역에서 휠을 굴려 앱 볼륨을 조절할 수 있으며, OSD 알림과 전역 마이크 음소거를 지원합니다.
- **두 가지 패널 스타일 및 실시간 오디오 레벨:** 기본 목록 패널과 모던 카드 패널을 제공하며, 350~800px 높이 조절을 지원합니다. 앱별 개별 볼륨 슬라이더와 실시간 소리 감지 레벨을 직관적으로 확인하세요.
- **37개 작업을 지원하는 자동화 및 CLI 연동:** 앱 실행/전환/종료, 예약, 단축키 등 6가지 트리거 지원. 장치 전환, 볼륨, 음소거, PowerShell 등을 조합할 수 있으며, 내장된 sr 명령어로 외부 스크립트 및 하드웨어와 손쉽게 연동됩니다.
- **가볍고 절제된 이벤트 기반 아키텍처:** 완전한 시스템 이벤트 구동으로 불필요한 백그라운드 폴링 타이머가 없어 유휴 CPU 점유율이 거의 0에 가깝습니다. .NET 4.8 기반 Legacy, 단일 파일 Lite(x64 / ARM64 실험), Store 버전을 제공하며 9개 언어를 지원합니다.

## 🎮 일상적인 활용

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="자동화"><img src="docs/images/mascot-automation-kr.png" width="48" align="middle" alt="작은 로봇을 클릭해 자동화 규칙 알아보기"></a> 번거로운 수동 설정을 한 번의 구성으로 언제나 간편한 자동화로 전환하세요. [자동화 설정](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 일상 속 사용 시나리오 | SonicRoute의 해결 방법 |
|---|---|
| **음성 채팅이 너무 크거나 게임 소리에 팀원 목소리가 묻힐 때** | Discord 등 음성 채팅 볼륨만 따로 조절하여 게임 사운드를 유지하고, 게임과 음성을 분리 출력 |
| **게임을 하면서 배경으로 음악이나 영상을 재생할 때** | 음악은 스피커로, 게임은 헤드폰으로 출력. 전체화면 게임을 나가지 않고 작업표시줄 휠 스크롤로 조절 |
| **게임을 할 때마다 헤드셋을 다시 연결하거나 설정을 바꿀 때** | 게임 실행 시 '헤드셋 전환 + 볼륨 40% + 음성 앱 실행', 게임 종료 시 스피커로 자동 복귀 규칙 설정 |
| **잠시 자리를 비우거나 개인적인 통화를 해야 할 때** | Ctrl+Shift+N으로 모든 마이크를 즉시 음소거하고, 화면 OSD 배너로 상태를 한눈에 확인 |
| **OBS 게임 방송 또는 화면 녹화 튜토리얼 제작 시** | 앱별로 독립된 오디오 트랙을 지정하여 개인 대화나 알림음이 녹화에 섞여 들어가는 것 방지 |

## 🚀 다운로드 및 시작하기

**현재 릴리스: [v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [릴리스 노트(중국어 간체)](docs/release-v1.24.md)

대부분의 Windows x64 사용자에게는 간편한 설치와 자동 업데이트를 제공하는 Microsoft Store 버전을 권장합니다. 단일 실행 파일을 원하면 Lite x64, .NET 8 설치가 어렵다면 Legacy x64를 선택하세요. 위 다운로드 링크는 항상 최신 릴리스를 가리키며, 아래 직접 링크는 v1.24용입니다.

| 에디션 | 다운로드 | 요구 사항 |
|---|---|---|
| Lite x64 | [단일 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | x64 .NET 8 Desktop Runtime |
| Lite ARM64 (실험적) | [단일 EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | ARM64 .NET 8 Desktop Runtime |
| Legacy x64 | [전체 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | .NET Framework 4.8; 실행 전에 ZIP 전체 압축을 푸세요 |
| Microsoft Store | [Store에서 설치](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64; 자동 업데이트; Store에서 제공 중인 버전을 확인할 수 있습니다 |

Lite에는 호환되는 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)이 필요합니다. Legacy에는 .NET 8이 필요하지 않습니다.

1. SonicRoute를 시작한 뒤 Windows 알림 영역에서 아이콘을 찾으세요(숨겨진 아이콘에 있을 수 있습니다). 아이콘을 두 번 클릭하면 전체 인터페이스가 열립니다.
2. 설정에서 원하는 출력 및 입력 장치를 선택하세요.
3. 알림 영역 아이콘을 클릭해 패널을 열거나 단축키를 설정하세요. 여러 작업을 조합하려면 자동화 규칙을 만드세요.

`sr`을 처음 사용하기 전에 SonicRoute를 일반 실행해 명령을 등록한 다음 터미널을 다시 여세요. 장치 전환이 예상대로 작동하지 않으면 [자주 묻는 질문](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)를 참고하세요.

1.24에서는 자동화 시작 템플릿, 최근 실행 상세 기록(기본값 꺼짐, 저장 용량 설정 가능), 앱별 아이콘 가져오기를 추가했습니다. 앱 아이콘이 없거나 기본 아이콘이면 앱 이름의 첫 글자를 표시합니다. 가져온 아이콘은 설정 백업에도 포함됩니다. 이전 변경 사항은 [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)에서 확인하세요.

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

- **v1.25 · 대규모 업데이트:** 자세한 내용은 추후 릴리스 노트에서 안내합니다.
- **향후 단계:** 의견을 바탕으로 기존 기능을 계속 다듬습니다. 계획은 피드백에 따라 변경될 수 있습니다.

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

**제작자: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://www.ifdian.net/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**
