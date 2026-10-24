<!-- Generated from docs/readme/shared.json and docs/readme/locales/de-DE.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | **Deutsch** | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Unterstützung auf Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Leite den Ton jeder App an das gewünschte Gerät weiter.**

Lass Musik über deine Lautsprecher laufen und Spiele oder Anrufe über dein Headset. SonicRoute ist ein Windows-Audiowerkzeug im Infobereich: Wähle für einzelne Apps Ausgabe- und Eingabegeräte, steuere den Ton per Tastenkürzel, Mausrad im Infobereich oder Panel und kombiniere wiederkehrende Aktionen zu Automatisierungsregeln.

📥 [Neueste Version herunterladen](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [Benutzerhandbuch](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimentell) · Neun Sprachen · MIT-Lizenz

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="Benutzerhandbuch (Chinesisch)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="Klicke auf das Maskottchen, um das Benutzerhandbuch zu öffnen"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute-Schnellpanel mit Ausgabegerät und Lautstärkereglern pro App"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute Automatisierungsregel-Editor: Auswahl von App-Triggern und Audio-Aktionsschritten"></a>
</div>

<sub>Oberflächenvorschau: Schnellbedienfeld (mit adaptiver Höhe und Acryl-Design) zur sofortigen Steuerung von Lautstärke und Geräten; Automatisierungseditor für Trigger und Aktionsabläufe.</sub>

## ✨ Das bietet SonicRoute

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="Funktionen"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="Klicke auf das Maskottchen, um die SonicRoute-Funktionen zu entdecken"></a>

- **App-unabhängiges Audio-Routing (Wiedergabe + Aufnahme):** Weisen Sie Apps individuelle Ausgabe- und Eingabegeräte zu – Spiele und Chat auf das Headset, Musik auf die Lautsprecher. Einstellungen bleiben nativ in Windows erhalten, auch wenn SonicRoute beendet wird.
- **Intelligenter Vordergrund-Fokus und blinde Tray-Steuerung:** Erkennt aktive Audio-Apps blitzschnell per Systemereignis. Scrollen Sie im Infobereich der Taskleiste, um die Lautstärke anzupassen, unterstützt durch OSD-Anzeigen und globale Mikrofon-Stummschaltung.
- **Schnellpanel und Echtzeit-Pegelanzeige:** Das Karten-Panel bietet eine anpassbare Höhe von 350–850 px und speichert seine Position; steuern Sie individuelle App-Lautstärken mit Pegelanzeigen für aktive Tonausgabe.
- **Automatisierungs-Engine und blitzschnelle CLI (6 Auslöser × 37 Aktionen):** Auslöser durch App-Start, -Fokus, -Beenden, Zeitpläne oder Hotkeys. Kombinieren Sie Audiosteuerung, Programmstarts und PowerShell-Skripte; nutzen Sie den Befehl sr zur nahtlosen Steuerung per Skript oder Hardware-Deck.
- **Ereignisorientierte Erkennung:** Nutzt Systembenachrichtigungen und bei Bedarf eine notwendige Momentaufnahme im Abstand von einer Sekunde; nicht benötigte Daten werden bei Bedarf bereinigt. Verfügbar als Legacy (.NET 4.8), Lite für .NET 10 und NET8 sowie Store-Version in 9 Sprachen.

## 🎮 Für den Alltag

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Automatisierung"><img src="docs/images/mascot-automation-de.png" width="48" align="middle" alt="Klicke auf den kleinen Roboter, um mehr über Automatisierungsregeln zu erfahren"></a> Verwandeln Sie mühsame manuelle Einstellungen in nahtlose Automatisierungen. [Automatisierung einrichten](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Alltagsszenario | So hilft SonicRoute |
|---|---|
| 🎮 **Voice-Chat zu laut oder Spielsound übertönt Teamkollegen** | Voice-Chat (Discord) separat regeln, ohne den Spielsound zu verändern; Spiel und Chat auf getrennte Geräte legen |
| 🎵 **Beim Zocken im Hintergrund Musik hören oder Videos schauen** | Musik auf Lautsprecher, Spiel auf Kopfhörer; Lautstärke per Tray-Mausrad steuern, ohne das Spiel zu verlassen |
| ⚡ **Vor jedem Spiel Headsets umstecken und Windows-Einstellungen anpassen** | Regel erstellen: Spielstart → Headset wählen + Lautstärke 40 % + Chat starten; beim Beenden zurück auf Lautsprecher |
| 🎙️ **Kurz vom PC aufstehen oder ein privates Telefonat führen** | Alle Mikrofone mit Strg+Umschalt+N stummschalten und den Status sofort per OSD-Banner auf dem Bildschirm ablesen |
| 📹 **OBS-Gaming-Streams oder Aufnahme von Tutorials** | Apps dedizierten Audiospuren zuweisen, damit private Gespräche und Systemtöne nicht in der Aufnahme landen |

## 🚀 Herunterladen und loslegen

**Aktuelle Version: [v1.26](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.26)** · 2026-10-23 · [Versionshinweise (Chinesisch)](docs/release-v1.26.md)

Für Windows-x64-Systeme stehen Lite mit .NET 10, eine NET8-kompatible Lite-Version und Legacy mit .NET Framework 4.8 zur Auswahl. Die Store-Seite zeigt die dort verfügbare Version und verwendet Store-Updates. Die direkten Downloadlinks unten gelten für v1.26.

| Version | Herunterladen | Voraussetzungen |
|---|---|---|
| ⭐ Lite x64 (.NET 10, Standard) | [Einzelne EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-x64.exe) | Einzelne portable Datei; benötigt die passende x64 .NET 10 Desktop Runtime |
| ⚡ Lite ARM64 (.NET 10) (experimentell) | [Einzelne EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-arm64.exe) | Portable ARM64-Datei; benötigt die passende ARM64 .NET 10 Desktop Runtime |
| ☕ Lite NET8 x64 | [Einzelne EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-x64.exe) | NET8-kompatible Lite-Datei; benötigt die x64 .NET 8 Desktop Runtime |
| ☕ Lite NET8 ARM64 (experimentell) | [Einzelne EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Lite-NET8-arm64.exe) | NET8-kompatible Lite-Datei; benötigt die ARM64 .NET 8 Desktop Runtime |
| 🛡️ Legacy x64 (Ohne .NET 8) | [Vollständiges ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.26/SonicRoute-v1.26-Legacy-x64.zip) | Kein .NET 8 erforderlich; nutzt das integrierte .NET Framework 4.8; ZIP entpacken und direkt starten |
| 🔄 Microsoft Store (Updates über Store) | [Aus dem Store installieren](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | Das NET10-Store-Paket benötigt die x64 .NET 10 Desktop Runtime. Welche Version aktuell im Store verfügbar ist, zeigt die Store-Seite; für 64-Bit Windows 10/11. |

Lite Standard benötigt [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); die NET8-kompatible Lite-Version benötigt [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). Die NET8-Version wird nach November 2026 nicht mehr angeboten oder gewartet. Danach wählen x64-Nutzer manuell .NET 10 oder Legacy (.NET Framework 4.8); ARM64-Nutzer wählen .NET 10.

1. **Starten & im Tray behalten**: Doppelklicken zum Starten; bleibt im Infobereich aktiv. Doppelklick auf das Tray-Symbol öffnet das Hauptfenster.
2. **Geräte wählen & weiterleiten**: Aktivieren Sie Kopfhörer und Lautsprecher unter Einstellungen → Behaltene Geräte. Linksklick auf das Tray-Symbol öffnet das Schnellbedienfeld zum sofortigen Weiterleiten.
3. **Blind-Lautstärkeregelung**: Maus über das Tray-Symbol bewegen und am Rad drehen, um die Lautstärke blind zu regeln, oder Hotkeys und Automatisierungsregeln ohne Spielunterbrechung nutzen.

Bevor du `sr` zum ersten Mal verwendest, starte SonicRoute normal, damit der Befehl registriert wird, und öffne anschließend dein Terminal erneut. Wenn der Gerätewechsel nicht wie erwartet funktioniert, findest du Hilfe in den [Häufige Fragen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```bash
# Automatisierungsregel ausführen (SonicRoute muss bereits laufen)
sr "Regelname oder ID"
```

Version 1.26 behebt die Anzeige von Symbolen bei einigen Apps mit erhöhten Rechten, verbessert bedarfsgesteuertes Lesen von Audiodaten sowie die Persistenz unter .NET 10 und Legacy und optimiert das Auslesen des Stummschaltstatus und die Bereinigung beim Beenden. Frühere Änderungen: [Versionsverlauf](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

### ❓ Häufig gestellte Fragen (FAQ)

**Q: Bleiben Routing- und Lautstärkeeinstellungen nach dem Schließen von SonicRoute erhalten?**

A: Ja! Die Audio-Zuweisung wird nativ vom Windows-Audiosystem gespeichert und bleibt auch nach Beenden von SonicRoute aktiv. Nur Tastenkürzel, Infobereich-Gesten und Automatisierungsregeln erfordern die Hintergrund-Ausführung.

**Q: Welche Runtime benötigt meine Version?**

A: Lite Standard benötigt die passende .NET 10 Desktop Runtime; Lite NET8 benötigt .NET 8. Legacy benötigt kein zusätzliches .NET Desktop Runtime-Paket und läuft mit .NET Framework 4.8. Das aktuelle Store-Paket benötigt x64 .NET 10 Desktop Runtime.

**Q: Verbraucht SonicRoute im Hintergrund spürbar CPU oder Akku?**

A: Die Erkennung nutzt vorrangig Systemereignisse und ergänzt sie bei Bedarf durch eine notwendige Momentaufnahme im Abstand von einer Sekunde. Nicht benötigte Daten werden bei Bedarf bereinigt; die tatsächliche CPU-Auslastung hängt von System und Nutzung ab.

## 📚 Dokumentation

Das Wiki ist derzeit auf vereinfachtem Chinesisch.

| Thema | Anleitungen |
|---|---|
| Installation, Panels und alltägliche Bedienung | [Benutzerhandbuch](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Funktionen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Standardtasten, Belegungen und Steuerungsziele | [Tastenkürzel](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Auslöser, Aktionen und Skriptintegration | [Automatisierung](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Befehlszeile](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Installation und Fehlerbehebung | [Häufige Fragen](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Quellcode, Builds und Versionen | [Technische Umsetzung](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Informationen zur Version](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Versionsverlauf](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

## 📌 Roadmap

- **Erweiterung des Audio-Routings (Route Enhancement)**: Weiterentwicklung der Routing-Engine mit dynamischer Stream-Übergabe, automatischer Wiederherstellung beim Anschließen/Trennen von Geräten und verbesserter Stabilität für komplexe Audio-Sitzungen.

[Idee teilen oder Problem melden](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 🧩 Weitere Projekte und Kombinationen

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**: Ein Audioplayer für Windows. Kombiniere ihn mit der Programmstart-Aktion der Automatisierung, um Soundeffekte per Tastenkürzel abzuspielen. [Einrichtungsanleitung (Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="XiaoYue Comics"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="Klicke auf XiaoYue mit Comicbuch in der Eierschale, um die Reihe zu lesen"></a>

**[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

Eine absurde Comedy über ein magisches Mädchen, das aus einem Ei schlüpft und mit jedem Hilfsversuch neues Chaos anrichtet.

<br clear="all">

---

<a href="EASTER_EGGS.md" title="Ein Easter Egg: gestern und morgen"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Entdecke ein Easter Egg"></a>

Danke, dass du SonicRoute verwendest. [Anleitungen lesen (Chinesisch)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Problem melden](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Autor: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
