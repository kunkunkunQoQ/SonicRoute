<!-- Generated from docs/readme/shared.json and docs/readme/locales/fr-FR.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | **Français** | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Soutenir sur Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envoyez le son de chaque application vers l’appareil de votre choix.**

Écoutez votre musique sur les haut-parleurs et vos jeux ou appels dans le casque. SonicRoute est un utilitaire audio pour Windows, présent dans la zone de notification : choisissez les périphériques de sortie et d’entrée de chaque application, contrôlez le son avec des raccourcis, la molette dans la zone de notification ou un panneau, et combinez des actions courantes dans des règles d’automatisation.

📥 [Télécharger la dernière version](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [Guide d’utilisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (expérimental) · Neuf langues d’interface · Licence MIT

> [!TIP]
> **Résumé IA et dépannage à faible consommation de tokens** : Si vous utilisez des assistants IA (comme Cursor, Copilot, ChatGPT ou Claude) pour diagnostiquer un problème, comprendre l'architecture ou concevoir des scripts, nous vous recommandons de consulter [README.ai.md](README.ai.md). Rédigé dans un DSL structuré haute densité pour les LLM, il évite les textes verbeux, réduit considérablement la consommation de tokens et cible la cause racine en quelques secondes.

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panneau rapide SonicRoute : périphérique de sortie et commandes de volume par application"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="Éditeur de règles d'automatisation SonicRoute : sélection des déclencheurs et actions audio"></a>
</div>

<sub>Aperçu de l'interface : panneau rapide (hauteur adaptable et style acrylique) pour le routage et le volume des applications ; éditeur d'automatisation pour chaîner déclencheurs et actions.</sub>

## ✨ Ce que propose SonicRoute

- **Routage audio indépendant et bidirectionnel par application (lecture + enregistrement) :** attribuez des périphériques d'entrée et de sortie dédiés à chaque application (jeux et voix sur le casque, musique sur les haut-parleurs). Les paramètres sont conservés nativement par Windows après fermeture.
- **Suivi intelligent du premier plan et réglage à l'aveugle via la molette.** Les événements système ciblent l'application active en quelques millisecondes. Faites défiler la molette sur la zone de notification pour régler le volume ou utilisez Alt+molette pour changer de périphérique, avec affichage OSD et coupure micro globale.
- **Panneau rapide et indicateur de niveau sonore en direct :** le panneau sous forme de cartes offre une hauteur ajustable de 350 à 850 px et mémorise sa position ; contrôlez le volume individuel avec des indicateurs sonores en temps réel.
- **Intégration smartphone Android & affichage virtuel indépendant (Full)**: Redirigez l'audio mobile vers le PC avec réglage du volume par application et vumètres crête, ou transférez l'audio du PC vers le téléphone ; affichez les applications mobiles dans une fenêtre virtuelle adaptative sur PC, contrôlez les sorties du téléphone depuis le panneau rapide et équilibrez le niveau sonore avec SmartVolume.
- **Moteur d'automatisation et CLI ultrarapide (6 déclencheurs × 39 actions) :** déclenchements par lancement, focus ou fermeture d'application, minuterie ou raccourcis. Enchaînez changements d'appareil, volume, scripts PowerShell et commande sr sans processus d'arrière-plan superflu.
- **Détection prioritairement événementielle :** notifications système et vérification ponctuelle par seconde si nécessaire. L’intégration mobile est réservée à Full ; Lite et Legacy restent dédiés au PC. Disponible en neuf langues.

## 🎮 Pour le quotidien

Transformez les réglages manuels fastidieux en une expérience automatisée prête à l'emploi. [Configurer l’automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Votre scénario quotidien | Comment SonicRoute vous aide |
|---|---|
| 🎮 **Chat vocal trop fort ou sons du jeu couvrant vos coéquipiers** | Réglez le volume de Discord indépendamment sans toucher au jeu ; jeu dans le casque et chat sur écouteur dédié |
| 🎵 **Écouter de la musique ou regarder une vidéo tout en jouant** | Diffusez la musique sur les haut-parleurs et gardez les jeux dans le casque. Survolez l'icône de notification et utilisez la molette ou Alt+molette pour changer d'appareil sans quitter votre jeu. |
| ⚡ **Changer de casque et modifier les paramètres Windows à chaque jeu** | Règle auto : lancer le jeu → basculer sur casque + volume à 40 % + lancer Discord ; retour enceintes à la fermeture |
| 🎙️ **S'éloigner du PC ou passer un appel privé rapidement** | Coupez tous les micros avec Ctrl+Maj+N et confirmez l'état en un coup d'œil grâce à la bannière OSD à l'écran |
| 📹 **Diffusion en direct sur OBS ou enregistrement de tutoriels** | Attribuez des pistes audio distinctes par application pour ne jamais inclure vos conversations privées en vidéo |
| 📱 **Écouter l'audio mobile sur PC ou piloter ses applications mobiles sur le bureau** | Acheminez musique, vidéos ou jeux mobiles vers vos écouteurs PC avec mixage indépendant ; affichez l'écran dans une fenêtre virtuelle et basculez les haut-parleurs/Bluetooth depuis le panneau rapide. |

## 🚀 Télécharger et commencer

**Version actuelle : [v1.31](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.31)** · 2026-11-08 · [Notes de version (en chinois)](docs/release-v1.31.md)

Choisissez **Full x64** pour l’audio Android et les écrans virtuels, **Lite x64** pour les fonctions audio PC seules, ou **Legacy x64** pour .NET Framework 4.8. Les éditions ARM64 sont expérimentales. L’intégration du téléphone est facultative et réservée à Full. Le paquet Store est basé sur Full ; la version actuellement disponible est indiquée sur la page Store. Liens directs pour v1.31.

| Édition | Télécharger | Configuration requise |
|---|---|---|
| ⭐ Full (.NET 10, avec intégration smartphone facultative) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.31/SonicRoute-v1.31-x64.exe) / [ARM64 (expérimental) · v1.30](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.30/SonicRoute-v1.30-arm64.exe) | Édition portable mono-EXE ; requiert le .NET 10 Desktop Runtime correspondant, inclut l'intégration Android audio et affichage virtuel. |
| ⭐ Lite (.NET 10, audio PC uniquement) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.31/SonicRoute-v1.31-Lite-x64.exe) / [ARM64 (expérimental) · v1.30](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.30/SonicRoute-v1.30-Lite-arm64.exe) | Édition portable en un seul fichier ; requiert le .NET 10 Desktop Runtime correspondant, audio PC uniquement. |
| 🛡️ Legacy x64 (audio PC uniquement, .NET Framework 4.8) | [Archive ZIP complète](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.31/SonicRoute-v1.31-Legacy-x64.zip) | Aucun runtime externe requis ; repose sur .NET Framework 4.8 intégré, décompressez et lancez. |
| 🔄 Microsoft Store (basé sur Full) | [Installer depuis le Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | Compilé à partir de Full ; requiert .NET 10 Desktop Runtime x64 ; la version en ligne dépend du Store. |

<details>
<summary><sub>📦 Anciennes éditions compatibles NET8 (abandonnées, cliquer pour déplier)</sub></summary>

| Édition | Télécharger | Configuration requise |
|---|---|---|
| Lite NET8 (Abandonné) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Version abandonnée et non maintenue ; migration vers .NET 10 ou Legacy recommandée. |
</details>

> [!NOTE]
> Full et Lite requièrent [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) ; Legacy fonctionne avec .NET Framework 4.8 sans installation supplémentaire (anciennes versions [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) abandonnées).

### 🏁 Prise en main rapide en 3 étapes

1. **Lancer et épingler dans la zone de notification** : Double-cliquez pour exécuter ; SonicRoute reste dans la barre des tâches (glissez l'icône hors du menu déroulant pour la garder visible). Un double-clic ouvre la fenêtre principale.
2. **Sélectionner les appareils et assigner** : Cochez vos casques et enceintes dans « Paramètres → Appareils conservés ». Un clic gauche sur l'icône ouvre le panneau rapide pour router chaque application et régler ses curseurs de volume.
3. **Contrôle à l'aveugle via la molette** : Survolez la zone de notification et tournez la molette pour ajuster le volume de l'application, Alt+molette pour changer d'appareil et Shift+molette pour le volume général du système, sans quitter votre jeu en plein écran.

### ⚡ CLI et automatisation (Power Users)

La commande `sr` dialogue via tube nommé (Named Pipe) en quelques millisecondes avec l'instance SonicRoute active (ne démarre pas l'application si absente). Lancez le logiciel une première fois pour déclarer la commande, puis redémarrez votre terminal pour l'utiliser dans vos scripts, AutoHotkey ou Stream Deck. Détails dans [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```bash
# Déclencher une règle d'automatisation (SonicRoute doit être en cours d'exécution)
sr "Nom ou ID de la règle"
```

### 🆕 Points forts de la version v1.31

v1.31 ajoute à Full le miroir plein écran du smartphone, la synchronisation bidirectionnelle du presse-papiers texte, des noms d’encodeurs conviviaux avec détection matérielle, des débits de 24/32/50 Mbps et 120 fps, tout en complétant la localisation en neuf langues et la stabilité des paramètres ; Lite et Legacy restent dédiés au PC. Détails : [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

### ❓ Foire aux questions (FAQ)

**Q : Le routage et les réglages de volume restent-ils conservés après avoir fermé SonicRoute ?**

A : Windows conserve les routages par application et les sessions audio existantes après la fermeture de SonicRoute. Les raccourcis, la zone de notification, l’automatisation, l’audio du téléphone, la recopie d’écran et la synchronisation du presse-papiers nécessitent que SonicRoute reste ouvert.

**Q : Quel runtime chaque édition nécessite-t-elle ?**

A : Full et Lite nécessitent le .NET 10 Desktop Runtime correspondant ; Legacy utilise .NET Framework 4.8. Le paquet Store nécessite le runtime x64 .NET 10. Les anciennes versions NET8 ne sont plus maintenues, mais restent disponibles en archive.

**Q : L'exécution de SonicRoute en arrière-plan consomme-t-elle du processeur ou de la batterie ?**

A : La détection privilégie les événements système et utilise, si nécessaire, une vérification ponctuelle toutes les secondes. Les données inutiles sont nettoyées à la demande ; l’utilisation réelle du processeur dépend du système et de l’activité.

## 📚 Documentation

Le Wiki est actuellement en chinois simplifié.

| Sujet | Guides |
|---|---|
| Installation, panneaux et commandes courantes | [Guide d’utilisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Fonctionnalités](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Touches par défaut, raccourcis et cibles de contrôle | [Raccourcis clavier](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Déclencheurs, actions et intégration aux scripts | [Automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Ligne de commande](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Installation et dépannage | [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Code source, compilations et versions | [Implémentation technique](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Informations sur la version](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

## 📌 Feuille de route

- **Amélioration du routage audio (Route Enhancement)** : Évolution du moteur de routage avec relais dynamique des flux, récupération automatique lors du branchement/débranchement d’appareils et meilleure stabilité pour les sessions audio complexes.

[Partager une idée ou signaler un problème](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 🧩 Autres projets et combinaisons

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)** : Un lecteur audio pour Windows. Associez-le à l’action de lancement de programme de l’automatisation pour jouer des effets sonores avec des raccourcis. [Guide de configuration (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)
- **[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)** : Une comédie absurde autour d’une magical girl née dans un œuf, dont chaque tentative d’aide provoque une nouvelle catastrophe.

---

<a href="EASTER_EGGS.md" title="Un œuf de Pâques : hier et demain"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Découvrez un œuf de Pâques"></a>

Merci d’utiliser SonicRoute. [Lire les guides (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Signaler un problème](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Auteur : [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
