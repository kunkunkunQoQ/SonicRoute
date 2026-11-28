<!-- Generated from docs/readme/shared.json, docs/readme/locales/de-DE.json, and docs/readme/releases/v2.00.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute 2.00

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | **Deutsch** | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Version: v2.00](https://img.shields.io/static/v1?label=Version&message=v2.00&color=34A853&logo=github&logoColor=white)](docs/release-v2.00.md) [![Lizenz: GPL-3.0-only](https://img.shields.io/static/v1?label=Lizenz&message=GPL-3.0-only&color=blue)](LICENSE) [![Plattform: Windows 10 | 11](https://img.shields.io/static/v1?label=Plattform&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Architektur: x64 | ARM64](https://img.shields.io/static/v1?label=Architektur&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=Downloads)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Auf Afdian unterstützen](https://img.shields.io/static/v1?label=&message=Auf+Afdian+unterst%C3%BCtzen&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Legen Sie für jede App fest, wo ihr Ton zu hören ist.**

Hören Sie Musik über die Lautsprecher und nutzen Sie das Headset für Spiele und Anrufe. Über das SonicRoute-Symbol im Infobereich von Windows wählen Sie für jede App ein Wiedergabe- und ein Aufnahmegerät und regeln die Lautstärke.

📥 [GitHub-Releases öffnen](https://github.com/kunkunkunQoQ/SonicRoute/releases) · [Editionen vergleichen](#downloads) · [Schnellstart in 3 Schritten](#quick-start) · [Benutzerhandbuch (Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimentell) · Neun Sprachen · [GPL-3.0](LICENSE)

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas"><img src="https://raw.githubusercontent.com/wiki/kunkunkunQoQ/SonicRoute/images/routing-canvas-v2.00.png" width="820" alt="SonicRoute 2.00 zeigt eine App mit durchgezogener Verbindung zur Hauptausgabe und gestrichelter Weiterleitung zu einem zweiten Gerät."></a>

<sub>Die Audio-Arbeitsfläche in SonicRoute 2.00 zeigt die Hauptausgabe einer App und eine Weiterleitung an ein zweites Gerät.</sub>

<details>
<summary><sub>🖼️ Oberflächenbeispiele aus Version 1.36</sub></summary>

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Eine Android-App wird ins Schnellpanel des PCs gezogen. Dort lässt sich ihr Ausgabegerät wechseln."></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Regeleditor mit Tastenkürzel als Auslöser. Im geöffneten Aktionsmenü ist das Festlegen des Standardwiedergabegeräts ausgewählt."></a>
</div>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute-Schnellpanel mit Ausgabegerät und Lautstärkereglern pro App"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Smartphone-Verwaltung mit Android-Kopplung, angehefteten Apps und Audioeinstellungen für virtuelle Displays"></a>
</div>

</details>

<a name="downloads"></a>

## 🚀 Herunterladen und loslegen

**v2.00** · [Versionshinweise (auf Englisch und Chinesisch)](docs/release-v2.00.md) · [GitHub-Releases öffnen](https://github.com/kunkunkunQoQ/SonicRoute/releases)

SonicRoute v2.00 gibt es in zwei Editionen. **Full** ergänzt PC-Audio um Android und AirPlay. **Lite** bietet die Audiofunktionen für den PC. Wählen Sie die zu Ihrem Rechner passende Architektur, x64 oder ARM64.

| Ausgabe | Portable Pakete | Voraussetzungen |
|---|---|---|
| ⭐ Full (PC, Android und AirPlay) | `SonicRoute-v2.00-x64.exe`<br>`SonicRoute-v2.00-arm64.exe` | Portable EXE mit integrierter .NET 10 Native AOT-Laufzeit. Eine separate Laufzeitinstallation ist nicht nötig. |
| ⚡ Lite (PC-Audio) | `SonicRoute-v2.00-Lite-x64.exe`<br>`SonicRoute-v2.00-Lite-arm64.exe` | Portable EXE mit integrierter .NET 10 Native AOT-Laufzeit. Eine separate Laufzeitinstallation ist nicht nötig. |

Microsoft Store · [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) — Die Verteilung und Aktualisierung laufen getrennt über den Microsoft Store. Die dort verfügbare Version steht auf der Produktseite.

📱 [Releases der Android-App](https://github.com/kunkunkunQoQ/SonicRoute-App/releases) · [Installationsanleitung](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/README.md#install)

> [!NOTE]
> Die allgemeine Systemanforderung ist Windows 10 ab Version 2004 oder Windows 11. Wenn SonicRoute den Ton eines einzelnen Prozesses erfasst und an zusätzliche Ausgänge weiterleitet, ist Windows-Build 20348 oder neuer erforderlich.

<a name="quick-start"></a>

### 🏁 Schnellstart in 3 Schritten

1. **SonicRoute starten.** Öffnen Sie die portable EXE. Ein Doppelklick auf das SonicRoute-Symbol im Infobereich öffnet das Hauptfenster.
2. **Ausgabe für eine App wählen.** Öffnen Sie im Hauptfenster „Apps“ und wählen Sie die App aus. Ziehen Sie den Anschluss ihrer Hauptausgabe auf das gewünschte Gerät. Für eine schnelle Änderung öffnen Sie mit einem Klick auf das Symbol im Infobereich das Schnellpanel und klappen dort die App-Zeile auf.
3. **Das Mausrad ausprobieren.** Drehen Sie das Mausrad über dem SonicRoute-Symbol, um die Lautstärke der aktuellen App zu ändern. Standardmäßig hat die App im Vordergrund Vorrang, sofern sie eine Audiositzung hat. Sonst sucht SonicRoute nach der zuletzt im Vordergrund verwendeten App mit Audiositzung. Mit Alt wechseln Sie beim Scrollen den Ausgang der App, mit der Umschalttaste regeln Sie die Lautstärke des Standardwiedergabegeräts.

## ✨ Das bietet SonicRoute

- **Im Schnellpanel auswählen, welche App ihren Ton an ein zweites Gerät sendet.** Klappen Sie die Zeile der PC-App auf und klicken Sie bei gedrückter `Strg`-Taste auf ein weiteres Wiedergabegerät. Der Ton dieser App ist auf beiden Geräten zu hören. Mit Umschalt+Klick fügen Sie mehrere Ausgänge auf einmal hinzu. Aktivieren Sie „Mikrofon im Schnellpanel anzeigen“, um in derselben Zeile das Aufnahmemikrofon zu wählen. Auf der Arbeitsfläche wechseln Sie den Hauptausgang durch Ziehen der durchgezogenen Linie. Mit + fügen Sie eine Weiterleitung mit eigenem Lautstärkeregler und eigener Stummschaltung hinzu.
- **Die Lautstärke einzelner Apps regeln.** Im Schnellpanel sehen Sie an den Pegelanzeigen, welche Apps gerade Ton ausgeben. Jede App hat einen eigenen Lautstärkeregler. Mit dem Mausrad über dem Symbol im Infobereich ändern Sie die Lautstärke der aktuellen App. Per Tastenkürzel können Sie alle Mikrofone stummschalten und die Stummschaltung wieder aufheben.
- **Wiederkehrende Aufgaben automatisieren.** Für Regeln stehen 6 Auslöser und 39 Aktionen zur Verfügung. Beim Start eines Spiels kann eine Regel zum Headset wechseln und den Sprachchat öffnen. Gespeicherte Regeln lassen sich mit `sr` auch im Terminal oder aus einem Skript ausführen.
- **Android verbinden oder AirPlay empfangen (Full).** Koppeln Sie die SonicRoute-App im selben lokalen Netzwerk und bestätigen Sie den achtstelligen Code, wenn er auf beiden Geräten übereinstimmt. Danach können Sie Audio in beide Richtungen übertragen und die freigegebene Bildschirmansicht des Smartphones am PC ansehen. Für einen eigenständigen virtuellen Bildschirm mit Eingabesteuerung, Dateiübertragung und Zwischenablagesynchronisierung verwenden Sie eine ADB-Verbindung. Full kann auch AirPlay-Audio und Bildschirmspiegelungen von Apple-Geräten empfangen.

## 🎮 Für den Alltag

Eine Regel kann mit dem Spiel auch den Sprachchat starten und die Audioausgabe auf das Headset umstellen. [Automatisierung einrichten](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Alltagsszenario | So hilft SonicRoute |
|---|---|
| 🎮 **Der Sprachchat ist zu laut oder geht im Spielton unter** | Regeln Sie Discord unabhängig vom Spiel. Bei Bedarf können Spiel und Sprachchat auch auf unterschiedlichen Geräten wiedergegeben werden. |
| 🎵 **Beim Spielen Musik hören oder Videos ansehen** | Leiten Sie Musik auf die Lautsprecher und den Spielton auf das Headset. Das Mausrad über dem SonicRoute-Symbol regelt die aktuelle App. Mit Alt und Mausrad wechseln Sie ihre Ausgabe. |
| ⚡ **Bei jedem Spielstart das Headset und den Sprachchat einrichten** | Lassen Sie beim Spielstart das Headset auswählen, die Lautstärke auf 40 % setzen und den Sprachchat starten. Eine zweite Regel schaltet bei Spielende auf die Lautsprecher zurück. |
| 🎙️ **Kurz den Platz verlassen oder ein privates Gespräch führen** | Mit Strg+Umschalt+N schalten Sie standardmäßig alle Mikrofone stumm oder wieder ein. Die Bildschirmanzeige zeigt den neuen Zustand. |
| 📹 **Mit OBS streamen oder ein Tutorial aufnehmen** | Leiten Sie Spiel und Sprachchat auf getrennte physische oder virtuelle Ausgabegeräte. Wählen Sie in OBS die Ausgabe, die aufgezeichnet werden soll. |
| 📱 **Smartphone-Audio am PC hören oder Android-Apps vom PC aus bedienen** | Hören Sie den Ton des Smartphones am PC oder sehen Sie dort den freigegebenen Bildschirm. Wenn Sie eine Android-App in einem eigenen virtuellen Bildschirm mit Maus und Tastatur bedienen möchten, verbinden Sie das Smartphone mit Full über ADB. |

### ⚡ Automatisierungsregeln über die Befehlszeile ausführen

Beim ersten regulären Start richtet SonicRoute den Befehl `sr` für Ihr Benutzerkonto ein. Schließen Sie danach das Terminal und öffnen Sie es erneut. `sr` führt eine Regel über eine Named Pipe in der bereits laufenden App aus. So können Sie Regeln etwa aus Skripten, AutoHotkey oder über ein Stream Deck aufrufen. Weitere Hinweise finden Sie unter [Häufige Fragen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```powershell
# Automatisierungsregel ausführen (SonicRoute muss bereits laufen)
sr "Regelname oder ID"
```

## ❓ Häufige Fragen

**Was bleibt erhalten, wenn ich SonicRoute beende?**

Windows behält für jede App den primären PC-Ausgang und das Aufnahmegerät bei. Die Lautstärke bestehender Audiositzungen bleibt ebenfalls erhalten. Die Wiedergabe auf zusätzlichen Ausgängen endet, wenn Sie SonicRoute beenden. Tastenkürzel, die Steuerung im Infobereich, Automatisierung, Smartphone-Audio, Bildschirmspiegelung und der Abgleich der Zwischenablage setzen voraus, dass SonicRoute läuft.

**Welche Laufzeitumgebung benötigt meine Ausgabe?**

Die portablen Full- und Lite-EXE-Dateien enthalten die .NET 10 Native AOT-Laufzeit. Wählen Sie x64 oder ARM64 passend zu Ihrem Windows-System.

**Wie stark belastet SonicRoute im Hintergrund den Prozessor oder den Akku?**

SonicRoute reagiert vor allem auf Windows-Ereignisse. Nur wenn nötig, prüft es den Zustand zusätzlich einmal pro Sekunde. Es gibt nicht mehr benötigte Daten bei Bedarf frei. Die tatsächliche CPU-Auslastung hängt vom System und seiner Nutzung ab.

**Wie stelle ich die Latenz ein, wenn ich Android-Audio am PC höre?**

Öffnen Sie in Full die Einstellungen des über ADB verbundenen Smartphones. Im Audiobereich stehen „Ausgewogen“, „Sehr geringe Latenz“ und „Stabil“ zur Wahl. Bei Tonaussetzern probieren Sie „Stabil“. Für unkomprimierten Ton wählen Sie „Raw PCM“ anstelle von Opus. Die Einstellungen gelten ab der nächsten Audioübertragung von diesem Gerät.

## 🆕 Neu in v2.00

**Ein neuer Anfang**

Full und Lite verwenden .NET 10 Native AOT. Die portablen EXE-Dateien für x64 und ARM64 bringen die Laufzeit mit. Die neue Audio-Arbeitsfläche zeigt die Hauptausgabe und Weiterleitungen einer App. Jede Weiterleitung lässt sich einzeln regeln und stummschalten.

In Full bündelt die Geräteseite Android und AirPlay. Die SonicRoute-App lässt sich im lokalen Netzwerk mit einem achtstelligen Code koppeln und unterstützt Audio in beide Richtungen sowie die Bildschirmfreigabe zum PC. Über ADB stehen weiterhin eigenständige virtuelle Bildschirme mit Eingabesteuerung, Dateiübertragung und Zwischenablage bereit. Weitere Versionen finden Sie unter [Versionsverlauf](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

<details>
<summary>v1.36</summary>

Im Schnellpanel und auf der Seite „Apps“ erweitert Umschalt+Klick die bestehende Auswahl um mehrere aufeinanderfolgende Ausgänge. Mit Strg+Umschalt+Klick auf einen PC-Ausgang legen Sie ihn als primäres Wiedergabegerät einer Windows-App fest. An den Schaltflächen erkennen Sie, welchen Ausgang Windows für die App verwendet und auf welchen weiteren Ausgängen SonicRoute den Ton ausgibt. Das Hauptfenster merkt sich, ob die Seitenleiste ein- oder ausgeklappt war. Bei beiden Microsoft-Store-Editionen bleibt die laufende App ihrem angehefteten Taskleistensymbol zugeordnet.

Die Full-Edition kann per AirPlay empfangenen Ton auf mehreren Geräten gleichzeitig wiedergeben. Ab Android 11 lässt sich der Ton kompatibler Apps mit Opus oder als unkomprimiertes PCM übertragen. Dafür stehen drei Latenzmodi zur Wahl. Unterstützt der Encoder des Smartphones die Anpassung, richtet sich die Videobitrate des virtuellen Bildschirms nach der USB- oder WLAN-Verbindung. Das Store-Paket der Full-Edition enthält Firewallregeln für eingehende AirPlay-Verbindungen in privaten und Domänennetzwerken. Frühere Änderungen finden Sie unter [Versionsverlauf](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

</details>

## 📚 Dokumentation

Das Wiki ist derzeit auf vereinfachtem Chinesisch.

| Thema | Anleitungen |
|---|---|
| Installation, Schnellpanel und tägliche Bedienung | [Benutzerhandbuch (auf Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Funktionen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) · [Geräte verbinden](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94) |
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
