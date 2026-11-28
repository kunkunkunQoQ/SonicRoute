# Synthetic AirPlay AAC-ELD fixtures

These files contain 16 AAC-ELD access units each. The input uses independent
sine tones in each stereo channel; no recording or commercial media is included.

| File | Sample rate | Samples per frame | SBR |
| --- | --- | --- | --- |
| `eld-480.au` | 44.1 kHz | 480 | off |
| `eld-512.au` | 48 kHz | 512 | off |

The first byte gives the length of the MPEG-4 AudioSpecificConfig, followed by
that configuration. Each subsequent record is a little-endian 16-bit length
followed by one raw access unit.

Build `encode_eld_fixture.cpp` with the FDK AAC v2.0.3 encoder for regeneration:
run the resulting tool with `44100 480 16 eld-480.au` or
`48000 512 16 eld-512.au`. FDK AAC is used only to generate test data and is
not needed to build or run SonicRoute or the checks.

## SRAP fixtures

The nearby pairing and business protocol samples are shared with the [Android implementation](https://github.com/kunkunkunQoQ/SonicRoute-App/tree/main/docs/protocol-fixtures). Both repositories keep the same wire bytes for the matching version. From the Windows repository root, run:

```powershell
$checks = 'checks/AndroidAudioChecks'
$fixtureRun = Join-Path $env:TEMP ([guid]::NewGuid().ToString())
dotnet run --project $checks -c Release -- --srap-nearby-vectors "$checks/fixtures/srap-nearby-v1.txt"
dotnet run --project $checks -c Release -- --srap-business $fixtureRun "$checks/fixtures/srap-business-v1.txt"
```
