# Third-party licenses and notices for SonicRoute

> [!IMPORTANT]
> **适用版本说明 / Applicability Notice**:
> - **仅限 1.30 Full 与 1.31 Full 版本需要**：本第三方开源许可声明清单**仅限 SonicRoute 1.30 Full 与 1.31 Full 版本需要**。
> - **后续版本及其他版本均不需要**：**后续版本的 Full（包括 1.32 Full 及后续所有版本）均不再需要此清单**；Lite、Legacy 等其他非 Full 发行版本也都不需要。
> 
> - **Required ONLY for v1.30 Full & v1.31 Full**: This third-party license and notice inventory is **required strictly and exclusively for SonicRoute v1.30 Full and v1.31 Full**.
> - **NOT needed for subsequent Full or other versions**: Subsequent Full releases (**including v1.32 Full and all future versions**) no longer require or include this inventory; other editions such as Lite and Legacy do not require it either.

This is the online entry for the third-party notices required exclusively by **SonicRoute 1.30 Full and 1.31 Full**. The application opens this page in your browser without exporting an additional local text file. Subsequent versions of Full (starting from **v1.32 Full and later**) as well as other editions (Lite, Legacy) do not require or reference this notice. Required license copies remain included in the application where applicable. Component-specific source notices and build observations are recorded below; this inventory is not a legal opinion.

## Complete license texts

| Component | Full text |
| --- | --- |
| SonicRoute (complete project offered under the new license from commit `442dac3`) | [SonicRoute Source Available License 1.0](../../LICENSE) |
| SonicRoute and its Android helpers (historical MIT versions, regardless of when obtained or used) | [Historical MIT notice](#sonicroute-historical-mit-notice-and-catalog-helper) |
| Concentus / Opus | [Copyright, redistribution conditions and disclaimer](#concentus-222) |
| NAudio | [MIT and source-file notices](#naudio-221) |
| Android platform-tools / ADB | [Complete aggregate notice](../../AndroidRuntime/ADB-NOTICE.txt) |
| scrcpy audio server | [Apache 2.0](../../AndroidRuntime/SCRCPY-LICENSE.txt) |
| Modified scrcpy video client/server | [Apache 2.0](video-licenses/scrcpy-LICENSE.txt) |
| Historical scrcpy lab integration patches (`packaging/AndroidLab/native/`) | Apache 2.0 (see file headers) |
| FFmpeg libraries | [LGPL 2.1 or later](video-licenses/FFmpeg-LICENSE.txt) |
| SDL2 | [zlib license](video-licenses/SDL2-LICENSE.txt) |
| zlib | [zlib license](video-licenses/zlib-LICENSE.txt) |
| QRCoder | [MIT](QRCoder-LICENSE.txt) |
| System.Management, System.Text.Json, Microsoft.Windows.CsWinRT | MIT (NuGet package metadata) |

## SonicRoute Historical MIT Notice and Catalog Helper

From 2026-11-14 at commit 442dac3, the complete main-branch project, including previously written code, is offered under this License to the extent the copyright holder has the right to license it. This includes v1.32 without waiting for v1.33; Source Available revisions must be separately identified with their source commit and license. Original v1.32 (4e3138f / tag v1.32-mit-original) and its original artifacts remain MIT. Prior MIT versions (including v1.31 and earlier) retain their rights for continued use, modification and redistribution, even for copies obtained today or later. Third-party and non-relicensable code retain their licenses (see LICENSE and the component notices below). The catalog helper in `android-helper/Catalog.java` was originally published under MIT in v1.30/v1.31; its historical MIT notice remains valid and is reproduced below:

Copyright (c) 2026 kunkunkunQoQ

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### Current .NET Dependencies and Historical Apache-2.0 Lab Files

- **Current `.NET` NuGet dependencies** (`SonicRoute`, `SonicRoute.Core`, `SonicRoute.AndroidAudio`, `StoreInterop`): `System.Management 8.0.0` (MIT), `System.Text.Json 8.0.5` (MIT, `net48` target), `Microsoft.Windows.CsWinRT 2.3.1` (MIT), `QRCoder 1.7.0` (MIT, Full only), `NAudio.Wasapi 2.2.1` / `NAudio.Core 2.2.1` (MIT + Ray Molenkamp zlib source notice, Full only), and `Concentus 2.2.2` (BSD-style / Opus license, Full only). All retain their original permissive licenses.
- **Historical `packaging/AndroidLab/native/` integration patches and helpers**: Files carrying `SPDX-License-Identifier: Apache-2.0` (including `virtual_resize.patch`, `OwnedAppMonitor.java`, `OwnedDisplayPolicy.java`, `PairingControl.java`, `PhoneDisplaySizing.java`, `UnicodeInputBridge.java`, `flex_resize.h`, `ime_routing.h`, `mirror_resize.h`, and `resize_packet.h`) remain under **Apache-2.0** and are not relicensed.

## Concentus 2.2.2

The local NuGet package declares its license as a package file and contains the following license text. Project: [lostromb/concentus](https://github.com/lostromb/concentus).

Copyright (c) by various holding parties, including (but not limited to): Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation, Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry, Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of Internet Society, IETF or IETF Trust, nor the names of specific contributors, may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The package license file further states that its independently compiled Opus C reference library is redistributed under the same general Opus license terms and points to the Opus 1.5.2 source.

## NAudio 2.2.1

The local `NAudio.Wasapi` and `NAudio.Core` nuspec files declare MIT, author Mark Heath, copyright © Mark Heath 2023. The unpacked NuGet directories contain no standalone `LICENSE` file. Preserve this package-level MIT text when distributing the NuGet package:

Copyright © Mark Heath 2023

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The upstream v2.2.1 source files `NAudio.Wasapi/CoreAudioApi/MMDevice.cs` and `MMDeviceEnumerator.cs` also carry this Ray Molenkamp file-header notice. This source-level notice is distinct from the NuGet package's MIT metadata:

```text
Copyright (C) 2007 Ray Molenkamp

This source code is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this source code or the software it produces.

Permission is granted to anyone to use this source code for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:
1. The origin of this source code must not be misrepresented; you must not
   claim that you wrote the original source code. If you use this source code
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original source code.
3. This notice may not be removed or altered from any source distribution.
```

Source references: [MMDevice.cs](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/CoreAudioApi/MMDevice.cs) and [MMDeviceEnumerator.cs](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/CoreAudioApi/MMDeviceEnumerator.cs).

## scrcpy-server and Android platform-tools

The package build script copies the full upstream Apache-2.0 license to `AndroidRuntime/SCRCPY-LICENSE.txt` and the complete platform-tools aggregate notice to `AndroidRuntime/ADB-NOTICE.txt`. Keep both files with the corresponding `scrcpy-server`, `adb.exe`, and companion DLLs; do not replace either with this summary.

## Virtual-display runtime inventory (1.30 Full & 1.31 Full)

The target runtime manifest for 1.30 Full and 1.31 Full was `SonicRoute.AndroidAudio/VirtualDisplayRuntime.files.json` and lists seven files: a locally built `scrcpy.exe` (593408 bytes), its separate video/control server, the unchanged reference SDL2.dll, and four locally built FFmpeg n8.1 DLLs. Exact sizes and SHA256 values live in that manifest; native build inputs/outputs are recorded in `outputs/video-client/build-receipt.json` and `outputs/video-runtime/build-receipt.json`. The client adds free resizing and Windows IME composition-key handling through `packaging/AndroidLab/native/flex_resize.h`. The video server includes the matching resize protocol, phone-resolution sizing budget, private-display focus policy, and explicit Unicode commit via device-side paste. Device clipboard content may change during Unicode input; the desktop clipboard is not synchronized. `OwnedDisplayPolicy.java` requests unrestricted application sizing only for its own trusted virtual display on supported Android 16+ systems, with a capability-checked standard fallback. This is a locally modified scrcpy build, not an unmodified upstream binary. It runs with `--video-codec=h264 --no-audio`; audio remains in SonicRoute's process and its original pinned audio server is separate.

The four FFmpeg DLLs are a minimal local build retaining H264/HEVC/PNG decoding, PNG/image2 demuxing, file protocol, ASM optimization, and w32threads. The local build enables zlib and statically links zlib 1.3.2; it has no separate zlib runtime DLL. No GPL configure switch or external GPL library was used. The runtime check reports `avcodec_license()` as `LGPL version 2.1 or later`. `FFmpeg-LICENSE.txt` now contains the actual source-tree `COPYING.LGPLv2.1`; `zlib-LICENSE.txt` contains the zlib notice. These are file/build observations, not a legal conclusion. The earlier BtbN gpl-shared FFmpeg/GPL-3 inventory applies only to the previous reference-runtime stage and does not describe the newly built DLLs.

Keep the complete license texts under `packaging/AndroidLab/video-licenses/` (`FFmpeg-LICENSE.txt`, `SDL2-LICENSE.txt`, `scrcpy-LICENSE.txt`, and `zlib-LICENSE.txt`) with the corresponding files when packaging. The earlier local runtime test decoded H264 and HEVC 64×128 frames and a PNG 64×64 image (`outputs/runtime-check-20261101-173500/check.log`). The current Android 16 Bilibili virtual-display sizing/input evidence is separate, under `outputs/bili-flex-final-20261102` and `outputs/bili-flex-keyboard-gpu-20261102`. A cached upstream GPL FFmpeg command-line tool was used read-only for DXGI diagnostic screenshots; it and its DLLs are not part of the product runtime. This inventory is not legal advice or release clearance. Recheck the actual package contents and notices before any distribution.

## QRCoder 1.7.0

Used for local AOSP ADB pairing QR generation. NuGet metadata pins source commit 2dc55ee03cb9abf66ffed7d683cbcce0a141b124. The complete MIT notice is shipped in QRCoder-LICENSE.txt, downloaded from https://raw.githubusercontent.com/Shane32/QRCoder/2dc55ee03cb9abf66ffed7d683cbcce0a141b124/LICENSE.txt. QR passwords are ephemeral and are not configuration inputs.
