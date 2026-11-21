<!-- Generated from docs/readme/shared.json, docs/readme/locales/de-DE.json, and docs/readme/releases/v1.34.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | **Deutsch** | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Aktuelle Version](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=Aktuelle%20Version)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Lizenz: GPL-3.0](https://img.shields.io/static/v1?label=Lizenz&message=GPL-3.0&color=blue)](LICENSE) [![Plattform: Windows 10 | 11](https://img.shields.io/static/v1?label=Plattform&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Architektur: x64 | ARM64](https://img.shields.io/static/v1?label=Architektur&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=Downloads)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Auf Afdian unterstützen](https://img.shields.io/static/v1?label=&message=Auf+Afdian+unterst%C3%BCtzen&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Legen Sie für jede App fest, wo ihr Ton zu hören ist.**

Musik über die Lautsprecher, Spiele und Anrufe über das Headset: Mit SonicRoute wählen Sie unter Windows für jede App das Ausgabe- und das Eingabegerät. Regeln Sie den Ton über das Symbol im Infobereich, das Schnellpanel oder Tastenkürzel. Wiederkehrende Schritte lassen sich als Automatisierungsregeln speichern.

📥 [Neueste Version herunterladen](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [Benutzerhandbuch (Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (auf Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimentell) · Neun Sprachen · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Eine Android-App wird ins Schnellpanel des PCs gezogen. Dort lässt sich ihr Ausgabegerät wechseln."></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Regeleditor mit Tastenkürzel als Auslöser. Im geöffneten Aktionsmenü ist das Festlegen des Standardwiedergabegeräts ausgewählt."></a>
</div>

<sub>Links wird der Ton einer Smartphone-App per Drag-and-drop auf den PC umgeleitet und die Ausgabe gewechselt. Rechts ist das Aktionsmenü einer Regel geöffnet, die per Tastenkürzel ausgelöst wird.</sub>

<details>
<summary><sub>🖼️ Weitere Screenshots von Schnellpanel und Smartphone-Verwaltung</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute-Schnellpanel mit Ausgabegerät und Lautstärkereglern pro App"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Smartphone-Verwaltung mit Android-Kopplung, angehefteten Apps und Audioeinstellungen für virtuelle Displays"></a>
</div>

</details>

## ✨ Das bietet SonicRoute

- **Ein- und Ausgabe pro App.** Leiten Sie Musik auf die Lautsprecher und den Spielton auf das Headset. Für den Sprachchat können Sie zusätzlich das Mikrofon wählen. Windows behält die Gerätezuordnung auch nach dem Beenden von SonicRoute bei.
- **Lautstärke im Infobereich regeln.** SonicRoute verfolgt anhand von Windows-Ereignissen, welche App im Vordergrund ist. Über dem SonicRoute-Symbol regelt das Mausrad die Lautstärke der aktuellen App. Alt und Mausrad wechseln ihre Ausgabe. Eine Bildschirmanzeige (OSD) meldet Änderungen. Per Tastenkürzel lassen sich auch alle Mikrofone stummschalten.
- **Schnellpanel mit Pegelanzeige.** Jede App hat einen eigenen Lautstärkeregler und eine Pegelanzeige. Das Panel merkt sich seine Position. Seine Höhe lässt sich zwischen 350 und 850 logischen Pixeln einstellen.
- **Android und AirPlay in Full.** Spielen Sie Android-Audio am PC oder PC-Audio auf dem Smartphone ab. Android-Apps lassen sich in virtuellen Displays mit H.265 und Gamepad-Unterstützung im aktiven Fenster bedienen. Der in C# entwickelte AirPlay-Empfänger übernimmt Ton und Bildschirmbild im ursprünglichen Seitenverhältnis. Im Schnellpanel können Sie den Ton auf den PC, den Lautsprecher eines verbundenen Android-Geräts oder dessen Bluetooth-Ausgabe leiten. Die intelligente Lautstärke (SmartVolume) gleicht Pegelunterschiede aus.
- **6 Auslöser und 39 Aktionen.** Starten Sie Regeln per Tastenkürzel, nach Zeitplan, beim Start von SonicRoute oder wenn eine App startet, in den Vordergrund wechselt oder beendet wird. Eine Regel kann das Gerät wechseln, die Lautstärke ändern, stummschalten, Programme starten oder PowerShell-Skripte ausführen. Mit `sr` lassen sich Regeln aus Skripten oder über ein Stream Deck aufrufen.
- **Erkennung über Systemereignisse.** SonicRoute wertet Windows-Benachrichtigungen aus und prüft den Zustand bei Bedarf zusätzlich einmal pro Sekunde. Nicht mehr benötigte Daten werden bei Bedarf freigegeben. Lite und Legacy bieten die PC-Funktionen, Full zusätzlich Android und AirPlay. Die Oberfläche ist in neun Sprachen verfügbar.

## 🎮 Für den Alltag

Eine Regel kann mit dem Spiel auch den Sprachchat starten und die Audioausgabe auf das Headset umstellen. [Automatisierung einrichten](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Alltagsszenario | So hilft SonicRoute |
|---|---|
| 🎮 **Der Sprachchat ist zu laut oder geht im Spielton unter** | Regeln Sie Discord unabhängig vom Spiel. Bei Bedarf können Spiel und Sprachchat auch auf unterschiedlichen Geräten wiedergegeben werden. |
| 🎵 **Beim Spielen Musik hören oder Videos ansehen** | Leiten Sie Musik auf die Lautsprecher und den Spielton auf das Headset. Das Mausrad über dem SonicRoute-Symbol regelt die aktuelle App. Mit Alt und Mausrad wechseln Sie ihre Ausgabe. |
| ⚡ **Bei jedem Spielstart das Headset und den Sprachchat einrichten** | Lassen Sie beim Spielstart das Headset auswählen, die Lautstärke auf 40 % setzen und den Sprachchat starten. Eine zweite Regel schaltet bei Spielende auf die Lautsprecher zurück. |
| 🎙️ **Kurz den Platz verlassen oder ein privates Gespräch führen** | Mit Strg+Umschalt+N schalten Sie standardmäßig alle Mikrofone stumm oder wieder ein. Die Bildschirmanzeige zeigt den neuen Zustand. |
| 📹 **Mit OBS streamen oder ein Tutorial aufnehmen** | Leiten Sie Spiel und Sprachchat auf getrennte physische oder virtuelle Ausgabegeräte. Wählen Sie in OBS die Ausgabe, die aufgezeichnet werden soll. |
| 📱 **Smartphone-Audio am PC hören oder Android-Apps vom PC aus bedienen** | Hören Sie Android-Audio am PC und bedienen Sie Apps in einem virtuellen Display. Per AirPlay können Sie Ton und Bild eines Apple-Geräts empfangen. Das Schnellpanel wechselt die Audioausgabe zwischen PC, Android-Lautsprecher und verbundenem Bluetooth-Gerät. |

## 🚀 Herunterladen und loslegen

**Aktuelle Version: [v1.34](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.34)** · 21. November 2026 · [Versionshinweise (Chinesisch)](docs/release-v1.34.md)

Wählen Sie **Full x64** für Android-Audio, virtuelle Displays und AirPlay oder **Lite x64** für die Audiofunktionen am PC. **Legacy x64** eignet sich für PCs mit .NET Framework 4.8. Die ARM64-Ausgaben sind experimentell. Full und Lite sind auch im Microsoft Store erhältlich. Die folgenden Direktdownloads gehören zu v1.34.

| Ausgabe | Download | Voraussetzungen |
|---|---|---|
| ⭐ Full (.NET 10, mit optionaler Smartphone-Integration) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-x64.exe) / [ARM64 (experimentell)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-arm64.exe) | Eine portable EXE für PC-Audio, Android-Audio, virtuelle Displays und AirPlay. Erfordert die .NET 10 Desktop Runtime für die gewählte Architektur. |
| ⭐ Lite (.NET 10, nur PC-Audio) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-x64.exe) / [ARM64 (experimentell)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-arm64.exe) | Eine portable EXE für PC-Audio. Erfordert die .NET 10 Desktop Runtime für die gewählte Architektur. |
| 🛡️ Legacy x64 (nur PC-Audio, .NET Framework 4.8) | [ZIP-Archiv](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Legacy-x64.zip) | ZIP-Archiv entpacken und das Programm starten. Es nutzt das in Windows vorhandene .NET Framework 4.8. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full und Lite benötigen die .NET 10 Desktop Runtime für x64. Die verfügbare Version steht auf der jeweiligen Store-Seite. |

<details>
<summary><sub>📦 Ältere Ausgaben für .NET 8 (eingestellt)</sub></summary>

| Ausgabe | Download | Voraussetzungen |
|---|---|---|
| Lite NET8 (eingestellt) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (experimentell)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Diese Ausgabe erhält keine Updates mehr. Für neue Installationen empfiehlt sich die .NET-10- oder die Legacy-Ausgabe. |
</details>

> [!NOTE]
> Full und Lite benötigen die [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) für die jeweilige Architektur. Legacy verwendet das in Windows vorhandene .NET Framework 4.8. Die früheren Ausgaben für die [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) sind noch als Download verfügbar, erhalten aber keine Updates mehr.

### 🏁 Schnellstart in 3 Schritten

1. **SonicRoute starten.** Starten Sie die EXE. Das Symbol erscheint im Infobereich der Taskleiste. Sie können es aus der Liste ausgeblendeter Symbole in den sichtbaren Bereich ziehen. Ein Doppelklick auf das Symbol öffnet das Hauptfenster.
2. **Geräte und Lautstärke wählen.** Markieren Sie in den Einstellungen die Geräte, die im Schnellpanel zur Auswahl stehen sollen. Öffnen Sie das Panel mit einem Klick auf das SonicRoute-Symbol. Klappen Sie die Zeile der App auf, um ihr Ausgabegerät zu wählen. Der Schieberegler ändert die Lautstärke.
3. **Ton per Mausrad steuern.** Drehen Sie das Mausrad über dem SonicRoute-Symbol, um die Lautstärke der aktuellen App zu ändern. Halten Sie dabei Alt gedrückt, um die App-Ausgabe zu wechseln, oder die Umschalttaste, um die Systemlautstärke zu regeln. In den Einstellungen können Sie die Steuerung auf den gesamten Infobereich ausweiten.

### ⚡ Automatisierungsregeln über die Befehlszeile ausführen

Beim ersten regulären Start richtet SonicRoute den Befehl `sr` für Ihr Benutzerkonto ein. Schließen Sie danach das Terminal und öffnen Sie es erneut. `sr` führt eine Regel über eine Named Pipe in der bereits laufenden App aus. So können Sie Regeln etwa aus Skripten, AutoHotkey oder über ein Stream Deck aufrufen. Weitere Hinweise finden Sie unter [Häufige Fragen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```powershell
# Automatisierungsregel ausführen (SonicRoute muss bereits laufen)
sr "Regelname oder ID"
```

### 🆕 Neu in v1.34

In allen Editionen lassen sich jetzt für jede App mehrere PC-Wiedergabegeräte auswählen. Wenn die App nach einem Neustart von SonicRoute erneut Audio wiedergibt, wird die Auswahl wiederhergestellt. Die Lautstärkeregelung im Infobereich und die Maus-Tastenkürzel nutzen gemeinsam einen systemweiten Maus-Hook.

In der Full-Edition zeigen beide Android-Bedienfelder denselben USB- und WLAN-Verbindungsstatus an. Beim Aktualisieren des Smartphone-Schnellpanels lässt sich auch das ausgewählte Gerät erneut verbinden. Das Ausgabemenü von Android-Apps zeigt jetzt den benutzerdefinierten Namen der aktuellen Audioausgabe des Smartphones an. Frühere Versionen finden Sie unter [Versionsverlauf](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

<details>
<summary>v1.33</summary>

Full empfängt in v1.33 AirPlay-Audio (PCM, ALAC, AAC-LC und AAC-ELD) und spiegelt Bildschirme im ursprünglichen Seitenverhältnis. Der in C# entwickelte Empfänger unterstützt einen optionalen Zugangscode, ein Fenster im Vordergrund und den Vollbildmodus mit F11. Den Ton können Sie auf eine PC-Ausgabe, den Lautsprecher eines verbundenen Android-Geräts oder dessen Bluetooth-Ausgabe ziehen.

Full unterstützt außerdem H.265 (HEVC). Für Android stehen Audio- und Videodecoder zur Wahl. Verfügbare Encoder werden nur bei geöffneter Geräteseite abgefragt. Gamepad-Eingaben gehen an das aktive Videofenster. QR-Code und manueller Kopplungscode stehen auf einer Karte. Der QR-Code erneuert sich alle zwei Minuten. Beim erneuten Verbinden oder Aktualisieren über Schnellpanel und Seitenleiste sucht SonicRoute gespeicherte WLAN-Geräte im lokalen Netz, verwirft veraltete Ports und erkennt Geräte auch nach einem Adresswechsel. Bei einem Fehlschlag bleibt die manuell eingegebene Adresse erhalten. Geräteeinstellungen lassen sich wiederherstellen.

Alle Ausgaben bieten zwei getrennte Optionen für die dauerhafte Mikrofon-Stummschaltanzeige, eine kompakte Profilverwaltung und bei mehr als 20 Apps eine virtualisierte Liste. Bildschirmanzeigen werden gebündelt, PNG-Rohdaten und ausgediente Texturen freigegeben. Full überlässt die Speicherbereinigung beim Schließen eines Fensters der Laufzeitumgebung. Hinzu kommen Konfigurationssicherungen in `config.json.bak`, Fehlerprotokolle bei Bedarf, eine eigene Lite-Ausgabe im Store und der offline lesbare Lizenztext (GPL-3.0-only). Weitere Einzelheiten: [Versionsverlauf](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

</details>

### ❓ Häufige Fragen

**Was bleibt erhalten, wenn ich SonicRoute beende?**

Windows behält die Gerätezuordnung pro App bei. Die Lautstärke bestehender Audiositzungen bleibt ebenfalls erhalten. Tastenkürzel, Steuerung im Infobereich, Automatisierung, Smartphone-Audio, Bildschirmspiegelung und der Abgleich der Zwischenablage setzen voraus, dass SonicRoute läuft.

**Welche Laufzeitumgebung benötigt meine Ausgabe?**

Full und Lite benötigen die .NET 10 Desktop Runtime für ihre jeweilige Architektur, die Store-Ausgaben die x64-Version. Legacy verwendet .NET Framework 4.8. Frühere .NET-8-Ausgaben können weiterhin heruntergeladen werden, erhalten aber keine Updates mehr.

**Wie stark belastet SonicRoute im Hintergrund den Prozessor oder den Akku?**

SonicRoute reagiert vor allem auf Windows-Ereignisse. Nur wenn nötig, prüft es den Zustand zusätzlich einmal pro Sekunde. Es gibt nicht mehr benötigte Daten bei Bedarf frei. Die tatsächliche CPU-Auslastung hängt vom System und seiner Nutzung ab.

## 📚 Dokumentation

Das Wiki ist derzeit auf vereinfachtem Chinesisch.

| Thema | Anleitungen |
|---|---|
| Installation, Schnellpanel und tägliche Bedienung | [Benutzerhandbuch (auf Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Funktionen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Standardtastenkürzel, eigene Belegungen und gesteuerte Geräte oder Apps | [Tastenkürzel](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Auslöser, Aktionen und Skripte | [Automatisierung](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Befehlszeile](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Installation und Fehlerbehebung | [Häufige Fragen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Quellcode, Builds und Versionen | [Technische Umsetzung](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Informationen zur Version](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Versionsverlauf](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **Referenz für KI-Assistenten**: [README.ai.md](README.ai.md) fasst Architektur, Befehle und Hinweise zur Fehlersuche in strukturierter Form zusammen. Sie können die Datei einem KI-Assistenten wie Cursor, Copilot, ChatGPT oder Claude für die Diagnose oder zum Schreiben von Skripten mitgeben.

## 📌 Roadmap

- **Zuverlässigerer Gerätewechsel.** Geplant sind flexiblere Übergaben zwischen Ausgaben und die automatische Wiederherstellung der Zuordnung, wenn Geräte angeschlossen oder getrennt werden. Auch Apps mit mehreren Audiositzungen sollen davon profitieren.

[Idee teilen oder Problem melden](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 💖 SonicRoute unterstützen

SonicRoute ist ein kostenloses Open-Source-Projekt und wird laufend weiterentwickelt. Wenn Ihnen SonicRoute im Alltag hilft, können Sie über Afdian spenden. Ihre Spende hilft uns, neue Funktionen zu entwickeln, die Kompatibilität zu verbessern und SonicRoute langfristig zu pflegen.

☕ [Auf Afdian unterstützen](https://afdian.com/a/koukou021)

Spenden sind freiwillig. Sie können alle Funktionen auch ohne Spende nutzen. Auch Fehlermeldungen, Weiterempfehlungen und ein Stern auf GitHub helfen dem Projekt. Vielen Dank!

## 📜 Lizenz

- **Lizenz.** Für SonicRoute gilt ausschließlich Version 3 der GNU General Public License (GPL-3.0-only). Kommerzielle Nutzung, Verkauf und entgeltliche Weitergabe sind unter den Bedingungen der GPL gestattet. Siehe [LICENSE](LICENSE).
- **Weitergabe.** Behalten Sie die erforderlichen Urheberrechts- und Lizenzhinweise bei, kennzeichnen Sie Änderungen und geben Sie geänderte, von der Lizenz erfasste Werke unter GPL Version 3 weiter. Bei der Weitergabe erfasster Binärdateien müssen Sie den vollständigen zugehörigen Quellcode gemäß Abschnitt 6 der GPL bereitstellen. Siehe [LICENSE](LICENSE).
- **Geltungsbereich und frühere Lizenzen.** Seit dem 17. November 2026 gilt GPL-3.0-only für das von der Lizenz erfasste Projekt in dieser Fassung, einschließlich älteren Codes, soweit der Rechteinhaber die Lizenz erteilen kann. Bereits erteilte Rechte aus der MIT-Lizenz und der früheren projektspezifischen Lizenz bleiben bestehen. Frühere Tags und veröffentlichte Binärdateien behalten ihre damalige Lizenzkennzeichnung. Für Komponenten Dritter gelten die jeweiligen Lizenzen (siehe [Hinweise zu Drittanbietern](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)).

---

<a href="EASTER_EGGS.md" title="Ein Easter Egg: gestern und morgen"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Ein Klick auf das Maskottchen öffnet das Easter Egg."></a>

Vielen Dank, dass Sie SonicRoute verwenden. [Anleitungen lesen (Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Problem melden](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Autor: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 Referenz für KI-Assistenten</a></sub></div>
