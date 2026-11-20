<!-- Generated from docs/readme/shared.json and docs/readme/locales/fr-FR.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute v1.34 Beta

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | **Français** | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Beta : v1.34](https://img.shields.io/static/v1?label=Beta&message=v1.34&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.33...Beta) [![Licence : GPL-3.0](https://img.shields.io/static/v1?label=Licence&message=GPL-3.0&color=blue)](LICENSE) [![Système : Windows 10 | 11](https://img.shields.io/static/v1?label=Syst%C3%A8me&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Architecture : x64 | ARM64](https://img.shields.io/static/v1?label=Architecture&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Téléchargements](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=T%C3%A9l%C3%A9chargements)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Soutenir SonicRoute sur Afdian](https://img.shields.io/static/v1?label=&message=Soutenir+SonicRoute+sur+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

## Principaux changements depuis v1.33

- **Associez deux PC Windows.** Activez Windows Link dans SonicRoute Full sur les deux PC. Recherchez l’autre PC sur le réseau local ou saisissez son adresse. Vérifiez que le même code à huit chiffres s’affiche des deux côtés, puis confirmez sur chaque PC.
- **Choisissez où écouter le son.** Faites glisser une application vers l’autre PC pour l’écouter sur celui-ci ou sur les deux à la fois. Vous pouvez aussi transmettre tout le son du système ou choisir plusieurs sorties pour une même source.
- **Partagez une fenêtre ou un écran avec le son.** Vous pouvez autoriser l’autre personne à agir sur la fenêtre ou l’écran partagé avec sa souris et son clavier. Vous pouvez aussi transférer des fichiers et des dossiers. Pour partager le texte brut que vous copiez, activez la synchronisation du presse-papiers sur les deux PC.
- **Parcourez les fichiers Android depuis le PC.** Sélectionnez plusieurs fichiers à transférer du téléphone vers le PC ou inversement. La transmission audio sans fil et la synchronisation du presse-papiers reprennent de manière plus fiable après une période d’inactivité ou une reconnexion.

[Guide de Windows Link (en chinois)](docs/windows-link.md) · [Voir toutes les modifications](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.33...Beta)

**Envoyez le son de chaque application vers l’appareil de votre choix.**

Écoutez la musique sur vos enceintes et gardez le casque pour les jeux et les appels. SonicRoute vous permet de choisir, sous Windows, une sortie et une entrée audio pour chaque application. Réglez le son à l’aide des raccourcis clavier, du panneau rapide ou de la molette sur l’icône de SonicRoute dans la zone de notification. Vous pouvez aussi regrouper vos réglages habituels dans une règle d’automatisation.

📥 [Télécharger la version stable](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [Guide d’utilisation (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (expérimental) · Interface disponible en neuf langues · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Une application Android est déplacée vers le panneau rapide du PC pour y choisir sa sortie audio."></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Éditeur de règles avec un déclencheur lié à une application et plusieurs actions audio"></a>
</div>

<sub>À gauche, le son d’une application Android est envoyé au PC par glisser-déposer, puis sa sortie est modifiée. À droite, l’éditeur permet de choisir le déclencheur d’une règle et la suite d’actions à exécuter.</sub>

<details>
<summary><sub>🖼️ Autres captures du panneau rapide et de la gestion des téléphones</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panneau rapide avec le périphérique de sortie et les réglages de volume de chaque application"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Gestion des appareils Android avec les options d’association, les applications épinglées et les réglages audio de l’écran virtuel"></a>
</div>

</details>

## ✨ Ce que propose SonicRoute

- **Une entrée et une sortie par application.** Envoyez la musique vers les enceintes et le son du jeu vers le casque. Pour la discussion vocale, choisissez aussi le micro à utiliser. Windows conserve ces choix après la fermeture de SonicRoute.
- **Contrôle du son dans la zone de notification.** SonicRoute suit l’application au premier plan grâce aux événements Windows. Survolez l’icône de SonicRoute dans la zone de notification et tournez la molette pour régler le volume de l’application ciblée. Avec Alt, la molette change sa sortie. Un indicateur à l’écran (OSD) signale les changements. Un raccourci permet également de couper tous les micros.
- **Panneau rapide avec indicateurs de niveau.** Chaque application dispose de son propre curseur de volume et d’un indicateur de niveau en temps réel. Le panneau mémorise sa position et sa hauteur est réglable de 350 à 850 pixels logiques.
- **Android et AirPlay dans l’édition Full.** Écoutez le son d’Android sur le PC ou celui du PC sur le téléphone. Utilisez vos applications Android dans des écrans virtuels avec prise en charge du H.265 et de la manette dans la fenêtre active. Le récepteur AirPlay écrit en C# permet aussi de recevoir le son et de recopier l’écran en conservant ses proportions. Le panneau rapide permet de choisir une sortie du PC, le haut-parleur d’un appareil Android connecté ou sa sortie Bluetooth. Le volume intelligent (SmartVolume) atténue les écarts de niveau.
- **6 déclencheurs et 39 actions.** Lancez une règle par raccourci clavier, à une heure donnée, au démarrage de SonicRoute ou à l’ouverture, à la fermeture ou au passage au premier plan d’une application. Enchaînez changement de périphérique, réglage du volume, mise en sourdine, lancement de programmes et scripts PowerShell. La commande `sr` permet d’appeler vos règles depuis un script ou un Stream Deck.
- **Détection par événements.** SonicRoute utilise les notifications de Windows et, si nécessaire, vérifie aussi l’état du système une fois par seconde. Il libère les données devenues inutiles à la demande. Lite et Legacy couvrent les fonctions du PC. Full ajoute Android et AirPlay. L’interface est disponible en neuf langues.

## 🎮 Pour le quotidien

Une règle peut sélectionner le casque et lancer la discussion vocale à l’ouverture d’un jeu. [Configurer l’automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Situation | Comment SonicRoute vous aide |
|---|---|
| 🎮 **La discussion vocale couvre le jeu, ou inversement** | Réglez le volume de Discord indépendamment de celui du jeu. Vous pouvez aussi leur affecter deux sorties différentes. |
| 🎵 **Écouter de la musique ou regarder une vidéo pendant une partie** | Envoyez la musique vers les enceintes et le jeu vers le casque. La molette sur l’icône de SonicRoute règle le volume de l’application ciblée. Maintenez Alt pour changer sa sortie. |
| ⚡ **Régler le casque et lancer la discussion vocale à chaque partie** | À l’ouverture du jeu, une règle peut sélectionner le casque, régler le volume à 40 % et lancer la discussion vocale. Une seconde règle rétablit les enceintes à la fermeture du jeu. |
| 🎙️ **S’absenter ou prendre un appel privé** | Par défaut, Ctrl+Maj+N coupe ou réactive tous les micros. L’indicateur à l’écran permet d’en vérifier l’état. |
| 📹 **Diffuser un jeu avec OBS ou enregistrer un tutoriel** | Envoyez le jeu et la discussion vocale vers des sorties physiques ou virtuelles distinctes. Dans OBS, choisissez la sortie à enregistrer. |
| 📱 **Écouter le téléphone sur le PC ou y utiliser une application Android** | Écoutez le son d’Android sur le PC et utilisez les applications dans un écran virtuel. Avec AirPlay, recevez le son et l’image d’un appareil Apple. Pour le son, le panneau rapide propose les sorties du PC, le haut-parleur Android et sa sortie Bluetooth. |

## 🚀 Télécharger et commencer

**Version stable : [v1.33](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.33)** · 18 novembre 2026 · [Notes de version (en chinois)](docs/release-v1.33.md)

Choisissez **Full x64** pour l’audio Android, les écrans virtuels et AirPlay, ou **Lite x64** pour l’audio du PC. **Legacy x64** convient aux PC équipés de .NET Framework 4.8. Les éditions ARM64 sont expérimentales. Full et Lite sont aussi disponibles dans le Microsoft Store. Les liens de téléchargement direct correspondent à v1.33.

| Édition | Téléchargement | Configuration requise |
|---|---|---|
| ⭐ Full (.NET 10, intégration mobile facultative) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-arm64.exe) | Un seul exécutable portable pour l’audio du PC, l’audio Android, les écrans virtuels et AirPlay. Nécessite .NET 10 Desktop Runtime pour l’architecture choisie. |
| ⭐ Lite (.NET 10, audio PC uniquement) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-arm64.exe) | Un seul exécutable portable pour l’audio du PC. Nécessite .NET 10 Desktop Runtime pour l’architecture choisie. |
| 🛡️ Legacy x64 (audio PC uniquement, .NET Framework 4.8) | [Archive ZIP complète](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Legacy-x64.zip) | Décompressez l’archive et lancez SonicRoute. Cette édition utilise .NET Framework 4.8 fourni avec Windows. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Les éditions Full et Lite nécessitent .NET 10 Desktop Runtime pour x64. Consultez leur fiche pour connaître la version disponible. |

<details>
<summary><sub>📦 Anciennes éditions pour .NET 8 (fin de maintenance)</sub></summary>

| Édition | Téléchargement | Configuration requise |
|---|---|---|
| Lite NET8 (fin de maintenance) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Cette édition ne reçoit plus de mises à jour. Pour une nouvelle installation, privilégiez une édition .NET 10 ou Legacy. |
</details>

> [!NOTE]
> Full et Lite nécessitent [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) pour l’architecture choisie. Legacy utilise .NET Framework 4.8 fourni avec Windows. Les anciennes éditions pour [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) restent téléchargeables, mais ne reçoivent plus de mises à jour.

### 🏁 Premiers pas en trois étapes

1. **Lancez SonicRoute.** Double-cliquez sur l’exécutable. L’icône de SonicRoute apparaît dans la zone de notification. Pour la garder visible, faites-la glisser hors de la liste des icônes masquées. Un double-clic sur l’icône ouvre la fenêtre principale.
2. **Choisissez les périphériques.** Dans les paramètres, cochez les périphériques à proposer dans le panneau rapide. Cliquez sur l’icône de SonicRoute pour ouvrir ce panneau. Dépliez la ligne d’une application pour choisir sa sortie. Le curseur règle son volume.
3. **Utilisez la molette.** Placez le pointeur sur l’icône de SonicRoute et tournez la molette pour régler le volume de l’application ciblée. Maintenez Alt pour changer sa sortie, ou Maj pour régler le volume du système. Une option permet d’étendre cette commande à toute la zone de notification.

### ⚡ Exécuter une règle en ligne de commande

Au premier lancement normal, SonicRoute enregistre la commande `sr` pour votre compte. Fermez puis rouvrez le terminal. `sr` exécute une règle dans l’instance déjà ouverte de SonicRoute en communiquant avec elle par un canal nommé. Vous pouvez ainsi lancer une règle depuis un script, AutoHotkey ou un Stream Deck. Consultez la [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) pour plus de détails.

```powershell
# Exécuter une règle d’automatisation (SonicRoute doit déjà être ouvert)
sr "Nom ou ID de la règle"
```

### 🆕 Points forts de la version v1.33

Dans l’édition Full, la version 1.33 ajoute un récepteur AirPlay écrit en C# pour l’audio (PCM, ALAC, AAC-LC et AAC-ELD) et la recopie d’écran au format d’origine. Un code d’accès facultatif protège la diffusion. La fenêtre peut rester au premier plan ou passer en plein écran avec F11. Un glisser-déposer envoie le son vers une sortie du PC, le haut-parleur d’un appareil Android connecté ou sa sortie Bluetooth.

Full prend aussi en charge le H.265 (HEVC). Sur Android, vous pouvez choisir les décodeurs audio et vidéo. La recherche des encodeurs disponibles se fait uniquement lorsque la page de l’appareil est ouverte. Seule la fenêtre vidéo active reçoit les commandes de la manette. Le QR code, renouvelé toutes les deux minutes, et le code d’association manuelle figurent au même endroit. Lors d’une reconnexion ou d’une actualisation depuis le panneau rapide ou la barre latérale, SonicRoute retrouve les appareils sans fil enregistrés sur le réseau local, même si leur adresse IP ou leur port a changé. Les anciens ports sont écartés et l’adresse saisie à la main est conservée en cas d’échec. Les réglages des appareils peuvent être restaurés.

Toutes les éditions proposent deux options indépendantes pour afficher en permanence l’état des micros coupés, regroupent la gestion des profils sur une ligne et utilisent une liste virtualisée au-delà de 20 applications. Les mises à jour de l’affichage à l’écran sont regroupées, les données PNG d’origine et les textures inutilisées libérées. À la fermeture d’une fenêtre, Full laisse .NET gérer la récupération de mémoire. S’y ajoutent la sauvegarde `config.json.bak`, des diagnostics en cas d’erreur, une édition Lite distincte dans le Microsoft Store et le texte de la licence GPL-3.0-only consultable hors ligne. Pour en savoir plus, consultez la rubrique « [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) ».

### ❓ Questions fréquentes

**Quels réglages sont conservés quand je quitte SonicRoute ?**

Windows conserve les périphériques attribués aux applications ainsi que le volume des sessions audio existantes. Les raccourcis, les commandes de la zone de notification, l’automatisation, l’audio du téléphone, la recopie d’écran et la synchronisation du presse-papiers nécessitent que SonicRoute reste en cours d’exécution.

**De quel environnement d’exécution chaque édition a-t-elle besoin ?**

Full et Lite nécessitent .NET 10 Desktop Runtime pour leur architecture. Les éditions du Microsoft Store utilisent la version x64 et Legacy utilise .NET Framework 4.8. Les anciennes éditions pour .NET 8 restent téléchargeables, mais ne reçoivent plus de mises à jour.

**Quel est l’effet de SonicRoute sur le processeur et la batterie en arrière-plan ?**

SonicRoute réagit principalement aux événements de Windows. Il ne vérifie l’état du système une fois par seconde que lorsque c’est nécessaire et libère les données inutiles à la demande. La charge du processeur dépend du PC et des fonctions utilisées.

## 📚 Documentation

Le Wiki est actuellement en chinois simplifié.

| Sujet | Guides |
|---|---|
| Installation, panneau rapide et commandes courantes | [Guide d’utilisation (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Fonctionnalités](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Raccourcis par défaut, personnalisation et éléments contrôlés | [Raccourcis clavier](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Déclencheurs, actions et scripts | [Automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Ligne de commande](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Installation et dépannage | [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Code source, compilation et versions | [Implémentation technique](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Informations sur la version](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **Référence pour les assistants IA** : [README.ai.md](README.ai.md) présente l’architecture, les commandes et les pistes de dépannage sous une forme structurée. Vous pouvez le fournir à un assistant comme Cursor, Copilot, ChatGPT ou Claude pour analyser un problème ou écrire un script.

## 📌 Feuille de route

- **Changement de sortie audio.** Les améliorations envisagées portent sur le passage d’une sortie à l’autre, le rétablissement du routage après le branchement ou le débranchement d’un périphérique, et la fiabilité avec les applications qui ouvrent plusieurs sessions audio.

[Partager une idée ou signaler un problème](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 💖 Soutenir SonicRoute

SonicRoute est un logiciel libre et gratuit que nous continuons à faire évoluer. S’il vous est utile au quotidien, vous pouvez faire un don sur Afdian. Vos dons nous aident à développer de nouvelles fonctionnalités, à améliorer la compatibilité et à maintenir le logiciel sur le long terme.

☕ [Soutenir SonicRoute sur Afdian](https://afdian.com/a/koukou021)

Les dons sont facultatifs. Toutes les fonctionnalités sont accessibles sans don. Signaler un problème, faire connaître SonicRoute ou donner une étoile au projet sur GitHub sont autant de façons de contribuer. Merci !

## 📜 Licence

- **Licence.** SonicRoute est distribué sous la seule version 3 de la licence publique générale GNU (GPL-3.0-only). L’utilisation commerciale, la vente et la distribution payante sont autorisées dans le respect de ses conditions. Voir [LICENSE](LICENSE).
- **Redistribution.** Conservez les mentions obligatoires de droit d’auteur et de licence, signalez les modifications et distribuez les œuvres modifiées couvertes sous la version 3 de la GPL. Si vous distribuez des binaires couverts, fournissez le code source correspondant complet dans les conditions de l’article 6 de la GPL. Voir [LICENSE](LICENSE).
- **Portée et licences antérieures.** Depuis le 17 novembre 2026, la GPL-3.0-only couvre l’ensemble du projet dans cette révision, code antérieur compris, dans la limite des droits que peut accorder le titulaire. Les autorisations déjà accordées au titre de la licence MIT et de l’ancienne licence propre au projet restent valables. Les tags et les binaires déjà publiés conservent leur mention de licence d’origine. Les composants tiers restent soumis à leurs propres licences (voir les [mentions des composants tiers](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)).

---

<a href="EASTER_EGGS.md" title="Un œuf de Pâques : hier et demain"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Cliquez sur la mascotte pour découvrir une surprise."></a>

Merci d’utiliser SonicRoute. [Lire les guides (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Signaler un problème](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Auteur : [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 Documentation pour les assistants IA</a></sub></div>
