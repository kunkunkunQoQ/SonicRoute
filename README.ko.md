<!-- Generated from docs/readme/shared.json and docs/readme/locales/ko-KR.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | **한국어** | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![최신 정식 버전](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=%EC%B5%9C%EC%8B%A0%20%EC%A0%95%EC%8B%9D%20%EB%B2%84%EC%A0%84)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![라이선스: GPL-3.0](https://img.shields.io/static/v1?label=%EB%9D%BC%EC%9D%B4%EC%84%A0%EC%8A%A4&message=GPL-3.0&color=blue)](LICENSE) [![운영 체제: Windows 10 | 11](https://img.shields.io/static/v1?label=%EC%9A%B4%EC%98%81+%EC%B2%B4%EC%A0%9C&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![아키텍처: x64 | ARM64](https://img.shields.io/static/v1?label=%EC%95%84%ED%82%A4%ED%85%8D%EC%B2%98&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![다운로드 수](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=%EB%8B%A4%EC%9A%B4%EB%A1%9C%EB%93%9C%20%EC%88%98)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Afdian에서 후원](https://img.shields.io/static/v1?label=&message=Afdian%EC%97%90%EC%84%9C+%ED%9B%84%EC%9B%90&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**앱마다 원하는 오디오 장치를 지정하세요.**

SonicRoute는 Windows 작업 표시줄의 알림 영역에서 실행되는 오디오 도구입니다. 앱마다 출력 장치와 입력 장치를 따로 지정해 음악은 스피커로, 게임과 통화 소리는 헤드셋으로 들을 수 있습니다. 단축키, 마우스 휠, 빠른 패널로 소리를 조절하고 자주 쓰는 작업은 규칙으로 묶어 자동화하세요.

📥 [최신 릴리스 다운로드](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [사용자 가이드(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (실험 버전) · 9개 언어 지원 · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="휴대폰 앱을 PC의 빠른 패널로 끌어와 오디오 출력 장치를 바꾸는 모습"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="자동화 규칙 편집기에서 앱과 관련된 실행 조건과 오디오 작업을 선택하는 화면"></a>
</div>

<sub>왼쪽에서는 휴대폰 앱을 PC의 빠른 패널로 끌어와 소리를 재생할 장치를 바꾸는 모습을 볼 수 있습니다. 오른쪽은 규칙을 실행할 조건과 작업 순서를 정하는 자동화 편집기입니다.</sub>

<details>
<summary><sub>🖼️ 빠른 패널과 휴대폰 관리 화면 더 보기</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="빠른 패널 상단에 표시된 출력 장치와 앱별 볼륨 슬라이더"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Android 기기 연결, 고정한 앱, 가상 디스플레이의 오디오 설정이 표시된 휴대폰 관리 화면"></a>
</div>

</details>

## ✨ SonicRoute의 주요 기능

- **앱별 재생 및 녹음 장치 선택.** 게임과 통화는 헤드셋으로, 음악은 스피커로 보내세요. 녹음에 사용할 마이크도 앱별로 지정할 수 있습니다. 장치 설정은 Windows에 저장되므로 SonicRoute를 종료해도 유지됩니다.
- **현재 사용하는 앱에 맞춰 제어.** SonicRoute는 창 전환을 감지합니다. 알림 영역 아이콘 위에서 휠을 굴려 제어 대상 앱의 볼륨을 조절하고, Alt 키를 누른 채 굴려 출력 장치를 바꿀 수 있습니다. 변경 사항은 화면 표시(OSD)로 확인할 수 있으며 단축키로 마이크를 한꺼번에 음소거할 수도 있습니다.
- **빠른 패널과 앱별 레벨 미터.** 앱마다 재생 레벨을 보면서 슬라이더로 볼륨을 조절할 수 있습니다. 패널 높이는 350~850 논리 픽셀 범위에서 조절할 수 있으며 표시 위치도 저장됩니다.
- **Android 연동 및 AirPlay 수신(Full).** Android 앱의 소리를 PC로 보내 볼륨을 조절하거나 PC 소리를 휴대폰으로 보낼 수 있습니다. 가상 디스플레이는 H.265를 지원하며 해당 창이 활성화되어 있을 때 게임패드로 조작할 수 있습니다. C#으로 구현한 AirPlay 수신 기능으로 Apple 기기의 소리를 재생하고 원래 비율로 화면을 미러링할 수 있습니다. 빠른 패널에서 PC 출력, Android 휴대폰 스피커, 휴대폰에 연결된 Bluetooth 장치 중 재생할 곳을 선택하고 스마트 볼륨 조절(SmartVolume)로 음량 차이를 줄일 수 있습니다.
- **6가지 실행 조건과 39가지 작업.** 앱을 시작하거나 전환하거나 종료할 때, 예약한 시각에, 단축키를 누를 때, 또는 SonicRoute를 시작할 때 규칙을 실행할 수 있습니다. 장치 전환, 볼륨 조절, 음소거, 프로그램 시작, PowerShell 실행 등을 조합할 수 있습니다. 스크립트나 외부 컨트롤러에서는 `sr` 명령으로 규칙을 호출합니다.
- **Windows 알림을 통한 변경 감지.** 시스템 알림을 우선 사용하고 필요할 때만 1초 간격으로 상태를 확인합니다. 더 이상 필요하지 않은 데이터는 정리합니다. 휴대폰 연동과 AirPlay는 Full에서 이용할 수 있으며 Lite와 Legacy는 PC용 오디오 기능을 제공합니다. 인터페이스는 9개 언어를 지원합니다.

## 🎮 활용 예시

게임을 시작하거나 종료할 때 규칙을 실행해 출력 장치와 볼륨을 자동으로 바꿀 수 있습니다. [자동화 설정](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 사용 상황 | 사용 방법 |
|---|---|
| 🎮 **음성 채팅 소리가 너무 크거나 게임 소리에 팀원 목소리가 묻힐 때** | Discord 등 음성 채팅 앱의 볼륨만 조절해 게임 소리는 그대로 유지하세요. 게임과 통화에 서로 다른 출력 장치를 지정할 수도 있습니다. |
| 🎵 **게임을 하면서 음악이나 영상을 스피커로 재생할 때** | 음악은 스피커로, 게임은 헤드셋으로 보냅니다. 알림 영역 아이콘 위에서 휠을 굴리면 현재 제어 중인 앱의 볼륨이 바뀌고, Alt 키를 누른 채 굴리면 해당 앱의 출력 장치가 바뀝니다. |
| ⚡ **게임을 시작할 때마다 출력 장치를 바꿀 때** | 게임을 시작하면 헤드셋으로 전환하고 볼륨을 40%로 맞춘 다음 음성 채팅 앱을 실행하도록 규칙을 만드세요. 게임이 종료되면 스피커로 복귀하는 규칙을 별도로 설정합니다. |
| 🎙️ **잠시 자리를 비우거나 마이크를 꺼야 할 때** | 기본 단축키 Ctrl+Shift+N으로 마이크를 한꺼번에 음소거하거나 음소거를 해제합니다. 화면 표시(OSD)를 켜면 음소거 상태를 확인할 수 있습니다. |
| 📹 **OBS로 게임을 방송하거나 설명 영상을 녹화할 때** | 녹화할 앱에는 전용 출력 장치나 가상 오디오 장치를 지정하고 OBS에서 그 장치의 소리를 캡처합니다. 개인 통화와 알림 소리는 다른 출력 장치로 보냅니다. |
| 📱 **PC에서 휴대폰 소리를 듣거나 앱을 조작할 때** | Android 앱의 소리를 PC에서 조절하고 가상 디스플레이에서 앱을 조작하세요. Apple 기기에서는 AirPlay로 소리와 화면을 전송할 수 있습니다. 빠른 패널에서 PC 출력, Android 휴대폰 스피커, 휴대폰에 연결된 Bluetooth 장치 중 하나를 선택해 소리를 재생합니다. |

## 🚀 다운로드 및 첫 설정

**최신 정식 버전: [v1.33](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.33)** · 2026년 11월 18일 · [릴리스 노트(중국어 간체)](docs/release-v1.33.md)

Android 오디오, 가상 디스플레이, AirPlay를 사용하려면 **Full x64**를 선택하세요. PC 오디오 기능만 필요하면 **Lite x64**, .NET Framework 4.8을 사용하는 환경에서는 **Legacy x64**가 적합합니다. ARM64용 빌드는 실험 버전입니다. 아래 표에서 v1.33 버전을 직접 다운로드할 수 있습니다. Microsoft Store에서 배포하는 Full과 Lite의 버전은 각 스토어 페이지에서 확인하세요.

| 에디션 | 다운로드 | 요구 사항 |
|---|---|---|
| ⭐ Full(.NET 10, 휴대폰 연동 지원) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-x64.exe) / [ARM64 (실험 버전)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-arm64.exe) | 단일 실행 파일로 제공되며, Android 오디오, 가상 디스플레이 및 AirPlay 연동을 선택해 사용할 수 있습니다. 앱과 아키텍처가 같은 .NET 10 Desktop Runtime이 필요합니다. |
| ⭐ Lite(.NET 10, PC 오디오 전용) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-x64.exe) / [ARM64 (실험 버전)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-arm64.exe) | PC 오디오 기능만 담은 단일 실행 파일입니다. 앱과 아키텍처가 같은 .NET 10 Desktop Runtime이 필요합니다. |
| 🛡️ Legacy x64(PC 오디오 전용, .NET Framework 4.8) | [ZIP 파일](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Legacy-x64.zip) | Windows의 .NET Framework 4.8을 사용합니다. ZIP 파일의 압축을 풀고 실행하세요. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full과 Lite 모두 x64용 .NET 10 Desktop Runtime이 필요합니다. 배포 중인 버전은 각 스토어 페이지에서 확인하세요. |

<details>
<summary><sub>📦 이전 NET8 호환 버전 다운로드(지원 종료)</sub></summary>

| 에디션 | 다운로드 | 요구 사항 |
|---|---|---|
| Lite NET8(지원 종료) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (실험 버전)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | 지원이 종료된 버전으로 기존 파일은 계속 다운로드할 수 있습니다. .NET 10 또는 Legacy 버전으로 전환하는 것을 권장합니다. |
</details>

> [!NOTE]
> Full과 Lite에는 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)이 필요합니다. Legacy는 Windows의 .NET Framework 4.8을 사용합니다. 이전 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 호환 버전은 지원이 종료되었지만 계속 다운로드할 수 있습니다.

### 🏁 세 단계로 시작하기

1. **SonicRoute를 실행합니다.** EXE 파일을 두 번 클릭하세요. Legacy 버전은 먼저 ZIP 파일의 압축을 풀어야 합니다. 실행 후 알림 영역에 표시되는 SonicRoute 아이콘을 두 번 클릭하면 기본 창이 열립니다. 아이콘이 숨겨져 있으면 작업 표시줄의 알림 영역으로 끌어다 놓으세요.
2. **앱의 출력 장치를 선택합니다.** 설정에서 빠른 패널에 표시할 헤드폰과 스피커를 선택하세요. 알림 영역 아이콘을 한 번 클릭하면 빠른 패널이 열립니다. 앱 항목을 펼쳐 출력 장치를 고르고, 슬라이더로 볼륨을 조절하세요.
3. **마우스 휠로 소리를 조절합니다.** 알림 영역의 SonicRoute 아이콘 위에서 휠을 굴리면 현재 제어 중인 앱의 볼륨이 바뀝니다. Alt 키를 누르고 휠을 굴리면 해당 앱의 출력 장치가, Shift 키를 누르고 굴리면 시스템 볼륨이 바뀝니다. 설정에서 휠을 사용할 범위를 알림 영역 전체로 넓힐 수 있습니다.

### ⚡ 명령줄에서 자동화 규칙 실행

`sr`은 명명된 파이프를 통해 실행 중인 SonicRoute에 규칙 실행을 요청하는 명령입니다. SonicRoute가 꺼져 있으면 먼저 앱을 실행하세요. 처음 실행할 때 명령이 등록됩니다. 터미널을 완전히 닫았다가 다시 열면 스크립트, AutoHotkey, Stream Deck에서도 호출할 수 있습니다. 자세한 내용은 [자주 묻는 질문](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)를 참고하세요.

```powershell
# 실행 중인 SonicRoute에서 자동화 규칙 실행
sr "규칙 이름 또는 ID"
```

### 🆕 v1.33 주요 변경 사항

Full에 C#으로 구현한 AirPlay 수신 기능이 추가되었습니다. PCM, ALAC, AAC-LC, AAC-ELD 오디오를 재생하고 원래 화면 비율로 미러링할 수 있습니다. 연결 PIN, 창을 항상 위에 표시하는 옵션, F11 전체 화면을 지원하며, 드래그하여 소리를 PC나 Android 휴대폰의 스피커 또는 휴대폰에 연결된 Bluetooth 장치로 보낼 수 있습니다.

Android 연동에서는 H.265(HEVC)를 지원하며 오디오 및 비디오 디코더를 선택할 수 있습니다. 인코더 목록은 기기 설정 화면이 열려 있을 때만 조회하며 게임패드 입력은 현재 사용 중인 가상 디스플레이 창으로만 보냅니다. 페어링 화면에는 2분마다 갱신되는 QR 코드와 수동 페어링 코드 입력란이 함께 표시됩니다. 기기 설정에서 다시 연결하거나 빠른 패널과 사이드바를 새로 고치면 로컬 네트워크에서 저장된 기기를 찾습니다. 유효하지 않은 포트를 거르고 기기 식별 정보로 새 IP 주소와 포트를 확인합니다. 기기를 찾지 못하거나 연결에 실패해도 직접 입력한 주소는 유지되며, 다시 연결할 때 기기 설정을 복원할 수 있습니다.

모든 에디션에서 마이크 전체를 음소거했을 때와 기본 녹음 장치를 음소거했을 때의 화면 표시(OSD)를 각각 설정할 수 있습니다. 설정 파일 관리 기능은 한 줄에 모았습니다. 목록에 앱이 20개를 넘으면 화면에 표시할 행을 재사용하고 연속된 OSD 업데이트는 한 번에 처리합니다. PNG 디코딩이 끝나면 원본 데이터를 해제합니다. Full에서는 더 이상 사용하지 않는 텍스처도 해제하며, 창을 닫을 때 메모리를 회수할 시점은 런타임이 결정합니다. 설정 파일의 .bak 백업, 오류가 발생했을 때의 진단 로그, 별도의 Microsoft Store용 Lite 버전, 오프라인 GPL-3.0-only 라이선스 열람도 지원합니다. 자세한 내용은 [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)에서 확인하세요.

### ❓ 자주 묻는 질문

**SonicRoute를 종료해도 앱의 출력 장치와 볼륨이 유지되나요?**

Windows에 저장된 앱별 장치 설정과 현재 오디오 세션의 볼륨은 그대로 유지됩니다. 단축키, 알림 영역 조작, 자동화, 휴대폰 오디오, 화면 미러링, 클립보드 동기화를 사용하려면 SonicRoute를 계속 실행해야 합니다.

**에디션별로 어떤 런타임이 필요한가요?**

Full과 Lite는 앱과 아키텍처가 같은 .NET 10 Desktop Runtime이 필요합니다. Microsoft Store 버전은 x64 런타임이 필요하며 Legacy는 .NET Framework 4.8을 사용합니다. 이전 NET8 버전은 지원이 종료되었지만 계속 다운로드할 수 있습니다.

**백그라운드에서 실행하면 CPU 사용량이나 배터리 소모가 얼마나 늘어나나요?**

일반적으로 Windows가 변경을 알릴 때 작동하고 필요한 경우에만 1초 간격으로 상태를 확인합니다. 사용하지 않는 데이터는 필요에 따라 정리합니다. 실제 CPU 사용량과 전력 소모는 시스템과 사용 중인 기능에 따라 달라집니다.

## 📚 문서

자세한 Wiki는 현재 중국어 간체로 제공됩니다.

| 주제 | 가이드 |
|---|---|
| 설치, 패널 사용법 및 기본 조작 | [사용 가이드(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [기능](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 기본 단축키, 키 지정 및 제어 대상 | [단축키](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| 자동화 실행 조건, 여러 단계의 작업 및 스크립트 연동 | [자동화](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [명령줄](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| 설치 및 문제 해결 | [자주 묻는 질문](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| 소스 코드, 빌드 및 릴리스 | [소스 및 빌드](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [릴리스 정보](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [버전 기록](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **AI 어시스턴트용 기술 자료**: Cursor, Copilot, ChatGPT, Claude 등으로 문제를 진단하거나 프로그램 구조를 파악하고 스크립트를 작성할 때는 [README.ai.md](README.ai.md)를 참고하세요. 주요 동작과 문제 해결 절차가 AI가 참조하기 쉬운 형식으로 정리되어 있습니다.

## 📌 로드맵

- **오디오 출력 전환 및 복구 개선.** 장치를 연결하거나 분리할 때 출력을 전환하고 다시 연결하면 원래 출력으로 복구하는 방법을 검토하고 있습니다. 여러 오디오 세션을 사용하는 앱 등에서도 안정적으로 전환할 수 있도록 개선할 계획입니다.

[아이디어 공유 또는 문제 제보](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 📜 라이선스

- **GPL-3.0 오픈 소스**: SonicRoute는 GNU 일반 공중 사용 허가서 제3판만 적용합니다(GPL-3.0-only). [LICENSE](LICENSE)를 참조하세요. GPL 조건에 따른 상업적 사용, 판매 및 유료 배포가 허용됩니다.
- **재배포**: 필요한 저작권 및 라이선스 고지를 유지하고, 변경 사항을 명시하며, 적용 대상 수정 저작물을 GPL 제3판으로 배포해야 합니다. 적용 대상 바이너리를 배포할 때는 GPL 제6조에 따라 완전한 대응 소스를 제공해야 합니다. 자세한 내용은 [LICENSE](LICENSE)를 참조하세요.
- **범위 및 기존 허가**: 2026-11-17 GPL 전환부터 저작권자가 허가할 수 있는 범위에서 이 리비전의 전체 적용 대상 프로젝트(이전 코드 포함)에 GPL-3.0-only가 적용됩니다. 기존 MIT 및 이전 자체 라이선스로 부여된 권한은 유효하며, 기존 태그와 배포된 바이너리의 라이선스 표시는 바꾸지 않습니다. 제삼자 구성 요소는 원래 라이선스를 유지합니다([서드파티 라이선스 고지](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)).

---

<a href="EASTER_EGGS.md" title="어제와 내일의 이스터 에그"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="마스코트의 이스터 에그 열기"></a>

SonicRoute를 사용해 주셔서 감사합니다. [가이드 읽기(중국어 간체)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [문제 제보](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**제작자: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 AI 도우미용 참고 자료</a></sub></div>
