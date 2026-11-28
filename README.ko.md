<!-- Generated from docs/readme/shared.json, docs/readme/locales/ko-KR.json, and docs/readme/releases/v2.00.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute 2.00

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | **한국어** | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![버전: v2.00](https://img.shields.io/static/v1?label=%EB%B2%84%EC%A0%84&message=v2.00&color=34A853&logo=github&logoColor=white)](docs/release-v2.00.md) [![라이선스: GPL-3.0-only](https://img.shields.io/static/v1?label=%EB%9D%BC%EC%9D%B4%EC%84%A0%EC%8A%A4&message=GPL-3.0-only&color=blue)](LICENSE) [![운영 체제: Windows 10 | 11](https://img.shields.io/static/v1?label=%EC%9A%B4%EC%98%81+%EC%B2%B4%EC%A0%9C&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![아키텍처: x64 | ARM64](https://img.shields.io/static/v1?label=%EC%95%84%ED%82%A4%ED%85%8D%EC%B2%98&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![다운로드 수](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=%EB%8B%A4%EC%9A%B4%EB%A1%9C%EB%93%9C%20%EC%88%98)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian에서 후원](https://img.shields.io/static/v1?label=&message=Afdian%EC%97%90%EC%84%9C+%ED%9B%84%EC%9B%90&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**앱마다 원하는 오디오 장치를 지정하세요.**

SonicRoute는 Windows 알림 영역에서 앱별로 소리를 조절할 수 있는 도구입니다. 음악은 스피커로 들으면서 게임과 음성 채팅은 헤드셋으로 들을 수 있습니다.

📥 [GitHub 릴리스 페이지](https://github.com/kunkunkunQoQ/SonicRoute/releases) · [에디션 선택](#downloads) · [세 단계로 시작하기](#quick-start) · [사용자 가이드(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (실험 버전) · 9개 언어 지원 · [GPL-3.0](LICENSE)

<a name="interface-preview"></a>

<table width="100%">
<tr>
<th scope="col" width="50%">빠른 패널 · PC와 휴대폰 앱의 재생 출력</th>
<th scope="col" width="50%">앱 라우팅 캔버스 · 기본 출력과 추가 전달</th>
</tr>
<tr>
<td width="50%" valign="top"><a href="docs/images/quick-panel-output-v2.00.gif"><img src="docs/images/quick-panel-output-v2.00.gif" width="100%" alt="빠른 패널에서 PC와 휴대폰 앱을 펼쳐 각각의 재생 출력을 선택합니다."></a></td>
<td width="50%" valign="top"><a href="https://raw.githubusercontent.com/wiki/kunkunkunQoQ/SonicRoute/images/routing-canvas-v2.00.png"><img src="docs/images/routing-canvas-v2.00-preview.png" width="100%" alt="SonicRoute 2.00 라우팅 캔버스에 앱과 재생·녹음 장치의 연결이 표시된 화면"></a></td>
</tr>
</table>

<a name="downloads"></a>

## 🚀 다운로드 및 첫 설정

**v2.00** · [릴리스 노트(중국어·영어)](docs/release-v2.00.md) · [GitHub 릴리스 페이지](https://github.com/kunkunkunQoQ/SonicRoute/releases)

v2.00 포터블 패키지는 Android 연동과 AirPlay 수신을 지원하는 **Full**과 PC 오디오를 관리하는 **Lite**로 제공됩니다. PC에 맞는 x64 또는 ARM64 패키지를 선택하세요.

| 에디션 또는 배포 경로 | 다운로드 | 설치 및 실행 환경 |
|---|---|---|
| ⭐ Full(PC 오디오·Android·AirPlay) | [`SonicRoute-v2.00-x64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v2.00/SonicRoute-v2.00-x64.exe)<br>[`SonicRoute-v2.00-arm64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v2.00/SonicRoute-v2.00-arm64.exe) | 단일 EXE에 .NET 10 Native AOT 런타임이 포함되어 있어 런타임을 별도로 설치할 필요가 없습니다. |
| ⚡ Lite(PC 오디오 전용) | [`SonicRoute-v2.00-Lite-x64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v2.00/SonicRoute-v2.00-Lite-x64.exe)<br>[`SonicRoute-v2.00-Lite-arm64.exe`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v2.00/SonicRoute-v2.00-Lite-arm64.exe) | 단일 EXE에 .NET 10 Native AOT 런타임이 포함되어 있어 런타임을 별도로 설치할 필요가 없습니다. |
| 🔄 **Microsoft Store** | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Microsoft Store 버전은 스토어에서 별도로 배포하고 업데이트합니다. 제공 중인 버전은 스토어에서 확인하세요. |

📱 [Android 앱 릴리스](https://github.com/kunkunkunQoQ/SonicRoute-App/releases) · [설치 안내](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/README.md#install)

> [!NOTE]
> 기본 지원 범위는 Windows 10 버전 2004 이상입니다. 앱의 소리를 프로세스 단위로 캡처해 추가 출력 장치로 보내려면 Windows 빌드 20348 이상이 필요합니다.

<a name="quick-start"></a>

### 🏁 세 단계로 시작하기

1. **SonicRoute를 실행하세요.** PC의 아키텍처에 맞는 Full 또는 Lite EXE를 실행하세요. 알림 영역에서 SonicRoute 아이콘을 두 번 클릭하면 기본 창이 열립니다.
2. **재생 장치를 선택하세요.** 설정의 표시할 장치에서 자주 사용하는 장치를 선택하세요. 앱 페이지에서 앱을 고른 뒤 캔버스의 기본 출력을 원하는 장치에 연결하세요. 빠르게 바꾸려면 알림 영역의 아이콘을 클릭하고 빠른 패널에서 앱의 행을 펼치세요.
3. **마우스 휠을 사용해 보세요.** SonicRoute 아이콘 위에서 휠을 굴려 앱 볼륨을 조절하세요. 기본적으로 오디오 세션이 있는 앱 중 현재 사용 중이거나 가장 최근에 사용한 포그라운드 앱을 우선합니다. Alt 키를 누른 채 굴리면 해당 앱의 출력이, Shift 키를 누른 채 굴리면 Windows 기본 재생 장치의 볼륨이 바뀝니다.

## ✨ SonicRoute의 주요 기능

- **빠른 패널에서 소리를 캡처할 앱을 선택하세요.** PC 앱의 행을 펼치고 `Ctrl` 키를 누른 채 다른 재생 장치를 클릭하면 그 앱의 소리가 두 장치에서 동시에 재생됩니다. Shift 키를 누르고 클릭하면 여러 출력을 한꺼번에 추가할 수 있습니다. ‘빠른 패널에 마이크 표시’를 켜면 같은 행에서 앱의 녹음용 마이크도 고를 수 있습니다.
- **전달 출력마다 음량 설정을 저장하세요.** 캔버스에서는 실선을 끌어 기본 출력을 바꾸거나 ‘+’로 전달 출력을 추가할 수 있습니다. 음량, 음소거 상태, 음량 상한은 앱과 출력 장치의 조합별로 저장됩니다. 각 전달 출력의 음량 슬라이더를 마우스 오른쪽 버튼으로 클릭하면 상한을 1%에서 300%까지 설정할 수 있습니다.
- **앱별 볼륨을 바로 조절하세요.** 빠른 패널의 레벨 미터로 소리가 나는 앱을 확인하고 슬라이더로 볼륨을 조절하세요. 알림 영역의 SonicRoute 아이콘 위에서 휠을 굴리면 현재 앱의 볼륨이 바뀝니다. 전역 단축키로 모든 마이크의 음소거를 한꺼번에 켜거나 끌 수도 있습니다.
- **자주 쓰는 작업을 규칙으로 만드세요.** 실행 조건 6가지와 작업 39가지를 조합해 장치를 바꾸거나 볼륨을 조절하거나 프로그램을 실행할 수 있습니다. 명령줄에서도 `sr`로 저장한 규칙을 실행할 수 있습니다.
- **Android 연동 및 AirPlay 수신(Full).** 같은 로컬 네트워크에서 양쪽에 표시된 8자리 숫자를 확인해 SonicRoute 앱과 페어링하세요. 휴대폰과 PC 사이에서 양방향으로 소리를 보내고, 휴대폰에서 공유한 화면을 PC에서 볼 수 있습니다. 독립 가상 화면에서 앱을 조작하거나 파일을 주고받고 텍스트 클립보드를 동기화하려면 ADB로 연결하세요. Apple 기기의 AirPlay 오디오와 화면 미러링도 받을 수 있습니다.

## 🎮 활용 예시

게임을 시작하거나 종료할 때 규칙을 실행해 출력 장치와 볼륨을 자동으로 바꿀 수 있습니다. [자동화 설정](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 사용 상황 | 사용 방법 |
|---|---|
| 🎮 **음성 채팅 소리가 너무 크거나 게임 소리에 팀원 목소리가 묻힐 때** | Discord 등 음성 채팅 앱의 볼륨만 조절해 게임 소리는 그대로 유지하세요. 게임과 통화에 서로 다른 출력 장치를 지정할 수도 있습니다. |
| 🎵 **게임을 하면서 음악이나 영상을 스피커로 재생할 때** | 음악은 스피커로, 게임은 헤드셋으로 보냅니다. 알림 영역 아이콘 위에서 휠을 굴리면 현재 제어 중인 앱의 볼륨이 바뀌고, Alt 키를 누른 채 굴리면 해당 앱의 출력 장치가 바뀝니다. |
| ⚡ **게임을 시작할 때마다 출력 장치를 바꿀 때** | 게임을 시작하면 헤드셋으로 전환하고 볼륨을 40%로 맞춘 다음 음성 채팅 앱을 실행하도록 규칙을 만드세요. 게임이 종료되면 스피커로 복귀하는 규칙을 별도로 설정합니다. |
| 🎙️ **잠시 자리를 비우거나 마이크를 꺼야 할 때** | 기본 단축키 Ctrl+Shift+N으로 마이크를 한꺼번에 음소거하거나 음소거를 해제합니다. 화면 표시(OSD)를 켜면 음소거 상태를 확인할 수 있습니다. |
| 📹 **OBS로 게임을 방송하거나 설명 영상을 녹화할 때** | 녹화할 앱에는 전용 출력 장치나 가상 오디오 장치를 지정하고 OBS에서 그 장치의 소리를 캡처합니다. 개인 통화와 알림 소리는 다른 출력 장치로 보냅니다. |
| 📱 **PC에서 휴대폰 소리를 듣거나 앱을 조작할 때** | Full에서 휴대폰의 SonicRoute 앱과 페어링하면 휴대폰 소리를 PC 헤드폰으로 듣거나 공유 화면을 볼 수 있습니다. PC에서 Android 게임을 조작하려면 ADB로 연결한 뒤 독립 가상 화면에서 앱을 여세요. 게임패드 입력은 가상 화면 창에 포커스가 있을 때만 전달됩니다. |

### ⚡ 명령줄에서 자동화 규칙 실행

`sr`은 명명된 파이프를 통해 실행 중인 SonicRoute에 규칙 실행을 요청하는 명령입니다. SonicRoute가 꺼져 있으면 먼저 앱을 실행하세요. 처음 실행할 때 명령이 등록됩니다. 터미널을 완전히 닫았다가 다시 열면 스크립트, AutoHotkey, Stream Deck에서도 호출할 수 있습니다. 자세한 내용은 [자주 묻는 질문](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)를 참고하세요.

```powershell
# 실행 중인 SonicRoute에서 자동화 규칙 실행
sr "규칙 이름 또는 ID"
```

## ❓ 자주 묻는 질문

**SonicRoute를 종료해도 앱의 출력 장치와 볼륨이 유지되나요?**

앱마다 지정한 주 재생 장치와 녹음 장치는 Windows에 저장되며, 현재 오디오 세션의 볼륨도 유지됩니다. 여러 출력 장치에서 동시에 재생하거나 단축키, 알림 영역 조작, 자동화, 휴대폰 오디오, 화면 미러링, 클립보드 동기화를 사용하려면 SonicRoute를 계속 실행해야 합니다.

**에디션별로 어떤 런타임이 필요한가요?**

Full과 Lite의 EXE에는 .NET 10 Native AOT 런타임이 포함되어 있습니다. PC의 아키텍처에 맞는 패키지를 선택하세요. Microsoft Store 버전의 설치와 업데이트는 스토어에서 처리합니다.

**백그라운드에서 실행하면 CPU 사용량이나 배터리 소모가 얼마나 늘어나나요?**

평소에는 Windows 알림을 받아 상태를 갱신하고 필요한 경우에만 상태를 확인합니다. CPU 사용량과 배터리 소모는 환경과 사용 기능에 따라 달라집니다.

**Android에서 PC로 보내는 소리의 지연 시간은 어떻게 조절하나요?**

Full에서 ADB로 연결한 휴대폰의 장치 설정을 여세요. 기본 지연 모드는 균형입니다. 지연을 줄이려면 초저지연을, 소리가 끊기면 안정 모드를 사용해 보세요. 압축 없이 전송하려면 디코더에서 Raw PCM을 선택하세요. 설정은 해당 휴대폰의 다음 오디오 전송에 적용됩니다.

## 🆕 v2.00 주요 변경 사항

**새로운 시작**

Full과 Lite는 .NET 10 Native AOT로 전환했으며 런타임을 포함한 단일 EXE로 실행됩니다. 앱 페이지의 캔버스에서 실선은 Windows가 직접 재생하는 출력을 나타냅니다. 「＋」로 전달 출력을 추가할 수 있습니다. 같은 앱을 같은 출력 장치에 다시 연결하면 해당 경로의 음량, 음소거 상태, 음량 상한을 복원할 수 있습니다. 상한은 1%에서 300%까지 설정할 수 있습니다.

Full의 장치 페이지에서 같은 로컬 네트워크에 연결된 SonicRoute 앱과 양쪽의 8자리 숫자를 확인해 페어링하면, PC와 휴대폰 간 양방향 오디오와 화면 공유를 이용할 수 있습니다. ADB 연결은 독립 가상 화면과 입력, 파일 전송 및 클립보드 동기화를 지원합니다. AirPlay 오디오와 화면 미러링도 수신할 수 있습니다. 자세한 내용은 [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)에서 확인하세요.

<details>
<summary>v1.36</summary>

빠른 패널에서 선택한 출력 장치가 ‘앱’ 페이지에도 바로 반영되며, 반대쪽에서 바꿔도 마찬가지입니다. Shift 키를 누른 채 클릭하면 기존 선택에 연속된 장치를 추가하고, PC 앱에서 Ctrl+Shift 키를 누른 채 클릭하면 주 출력 장치를 바꿀 수 있습니다. PC 앱의 경우 Windows가 직접 재생하는 장치와 SonicRoute가 같은 소리를 보내는 추가 장치를 버튼 모양으로 구분할 수 있습니다. 주 창의 왼쪽 사이드바를 접을 수 있으며, 다음에 창을 열 때도 마지막 상태가 유지됩니다.

Full에서는 AirPlay로 받은 소리를 여러 장치에서 동시에 재생할 수 있습니다. Android 앱 소리를 PC로 전송하려면 Android 11 이상이 필요하며, 캡처 가능 여부는 기기와 앱에 따라 다릅니다. 휴대폰마다 비압축 PCM을 선택하고 지연 모드를 조절할 수 있습니다. 기기의 인코더가 지원하면 가상 디스플레이의 영상 비트레이트가 연결 상태에 맞춰 조절됩니다. Microsoft Store 버전에서는 실행 중인 창이 작업 표시줄에 고정한 아이콘에 표시되며, Full 패키지에는 개인 네트워크와 도메인 네트워크에서 AirPlay를 수신하는 데 필요한 방화벽 규칙이 포함됩니다. 이전 버전의 변경 사항은 [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)에서 확인하세요.

</details>

## 📚 문서

자세한 Wiki는 현재 중국어 간체로 제공됩니다.

| 주제 | 가이드 |
|---|---|
| 설치, 패널 사용법 및 기본 조작 | [사용 가이드(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [기능](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) · [장치 연결 가이드(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94) |
| 기본 단축키, 키 지정 및 제어 대상 | [단축키](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| 자동화 실행 조건, 여러 단계의 작업 및 스크립트 연동 | [자동화](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [명령줄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| 설치 및 문제 해결 | [자주 묻는 질문](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| 소스 코드, 빌드 및 릴리스 | [소스 및 빌드](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [릴리스 정보](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **AI 어시스턴트용 기술 자료**: Cursor, Copilot, ChatGPT, Claude 등으로 문제를 진단하거나 프로그램 구조를 파악하고 스크립트를 작성할 때는 [README.ai.md](README.ai.md)를 참고하세요. 주요 동작과 문제 해결 절차가 AI가 참조하기 쉬운 형식으로 정리되어 있습니다.

## 📌 로드맵

- **오디오 출력 전환 및 복구 개선.** 장치를 연결하거나 분리할 때 출력을 전환하고 다시 연결하면 원래 출력으로 복구하는 방법을 검토하고 있습니다. 여러 오디오 세션을 사용하는 앱 등에서도 안정적으로 전환할 수 있도록 개선할 계획입니다.

[아이디어 공유 또는 문제 제보](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 💖 SonicRoute 후원하기

SonicRoute의 Windows 버전과 Android 버전은 모두 무료 오픈 소스 소프트웨어입니다. 소리 설정을 여는 수고가 줄었다면 [Afdian](https://afdian.com/a/koukou021)에서 개발자에게 커피 한 잔을 선물할 수 있습니다.

GitHub에서 별표를 누르거나 친구에게 SonicRoute를 소개해 주셔도 좋습니다. 문제가 생기면 [Windows](https://github.com/kunkunkunQoQ/SonicRoute/issues) 또는 [Android](https://github.com/kunkunkunQoQ/SonicRoute-App/issues) 저장소의 이슈에 기기 모델과 문제를 재현하는 방법을 남겨 주세요.

## 📜 라이선스

- **GPL-3.0 오픈 소스**: SonicRoute는 GNU 일반 공중 사용 허가서 제3판만 적용합니다(GPL-3.0-only). [LICENSE](LICENSE)를 참조하세요. GPL 조건에 따른 상업적 사용, 판매 및 유료 배포가 허용됩니다.
- **재배포**: 필요한 저작권 및 라이선스 고지를 유지하고, 변경 사항을 명시하며, 적용 대상 수정 저작물을 GPL 제3판으로 배포해야 합니다. 적용 대상 바이너리를 배포할 때는 GPL 제6조에 따라 완전한 대응 소스를 제공해야 합니다. 자세한 내용은 [LICENSE](LICENSE)를 참조하세요.
- **범위 및 기존 허가**: 2026-11-17 GPL 전환부터 저작권자가 허가할 수 있는 범위에서 이 리비전의 전체 적용 대상 프로젝트(이전 코드 포함)에 GPL-3.0-only가 적용됩니다. 기존 MIT 및 이전 자체 라이선스로 부여된 권한은 유효하며, 기존 태그와 배포된 바이너리의 라이선스 표시는 바꾸지 않습니다. 제삼자 구성 요소는 원래 라이선스를 유지합니다([서드파티 라이선스 고지](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)).

---

<a href="EASTER_EGGS.md" title="어제와 내일의 이스터 에그"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="마스코트의 이스터 에그 열기"></a>

SonicRoute를 사용해 주셔서 감사합니다. [가이드 읽기(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [문제 제보](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**제작자: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 AI 도우미용 참고 자료</a></sub></div>
