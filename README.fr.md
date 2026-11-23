<!-- Generated from docs/readme/shared.json, docs/readme/locales/fr-FR.json, and docs/readme/releases/v1.35.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute v1.35 Beta

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | **Français** | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Beta : v1.35](https://img.shields.io/static/v1?label=Beta&message=v1.35&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.35...Beta) [![Licence : GPL-3.0-only](https://img.shields.io/static/v1?label=Licence&message=GPL-3.0-only&color=blue)](LICENSE) [![Système : Windows 10 | 11](https://img.shields.io/static/v1?label=Syst%C3%A8me&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Architecture : x64 | ARM64](https://img.shields.io/static/v1?label=Architecture&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Téléchargements](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=T%C3%A9l%C3%A9chargements)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Soutenir SonicRoute sur Afdian](https://img.shields.io/static/v1?label=&message=Soutenir+SonicRoute+sur+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envoyez le son de chaque application vers l’appareil de votre choix.**

Écoutez la musique sur vos enceintes et gardez le casque pour les jeux et les appels. Depuis la zone de notification de Windows, SonicRoute permet de choisir la sortie et le micro de chaque application et d’en régler le volume.

📥 [Télécharger la dernière version](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Comparer les éditions](#downloads) · [Premiers pas en trois étapes](#quick-start) · [Windows Link](#windows-link) · [Guide d’utilisation (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (expérimental) · Interface disponible en neuf langues · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Une application Android est déplacée vers le panneau rapide du PC pour y choisir sa sortie audio."></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Éditeur de règles avec un raccourci clavier comme déclencheur. Le menu des actions est ouvert sur le choix du périphérique de lecture par défaut."></a>
</div>

<sub>À gauche, le son d’une application Android est envoyé au PC par glisser-déposer, puis sa sortie est modifiée. À droite, le menu des actions est ouvert pour une règle déclenchée par un raccourci clavier.</sub>

<details>
<summary><sub>🖼️ Autres captures du panneau rapide et de la gestion des téléphones</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panneau rapide avec le périphérique de sortie et les réglages de volume de chaque application"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Gestion des appareils Android avec les options d’association, les applications épinglées et les réglages audio de l’écran virtuel"></a>
</div>

</details>

<a name="downloads"></a>

## 🚀 Télécharger et commencer

**Version stable : [v1.35](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.35)** · 21 novembre 2026 · [Notes de version (en anglais et en chinois)](docs/release-v1.35.md)

Choisissez **Lite** pour l’audio du PC, **Full** pour bénéficier aussi d’Android et d’AirPlay, ou **Legacy** pour l’audio d’un PC x64 équipé de .NET Framework 4.8. Le tableau réunit les téléchargements de v1.35 et les liens vers les éditions du Microsoft Store.

| Édition | Téléchargement | Configuration requise |
|---|---|---|
| ⭐ Full (PC, Android et AirPlay) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-arm64.exe) | Exécutable portable. Nécessite .NET 10 Desktop Runtime pour l’architecture choisie. |
| ⚡ Lite (audio du PC) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-arm64.exe) | Exécutable portable. Nécessite .NET 10 Desktop Runtime pour l’architecture choisie. |
| 🛡️ Legacy (audio du PC, x64) | [Archive ZIP complète](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Legacy-x64.zip) | Décompressez toute l’archive ZIP avant de lancer SonicRoute. Nécessite .NET Framework 4.8. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full et Lite nécessitent .NET 10 Desktop Runtime pour x64. La version disponible figure sur la fiche de chaque édition. |

<details>
<summary><sub>📦 Anciennes éditions pour .NET 8 (fin de maintenance)</sub></summary>

| Édition | Téléchargement | Configuration requise |
|---|---|---|
| Lite NET8 (fin de maintenance) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Cette édition ne reçoit plus de mises à jour. Pour une nouvelle installation, privilégiez une édition .NET 10 ou Legacy. |

</details>

> [!NOTE]
> Full et Lite nécessitent [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) pour l’architecture choisie. Legacy utilise .NET Framework 4.8 fourni avec Windows. Les anciennes éditions pour [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) restent téléchargeables, mais ne reçoivent plus de mises à jour.

<a name="quick-start"></a>

### 🏁 Premiers pas en trois étapes

1. **Lancez SonicRoute.** Ouvrez l’exécutable. Pour Legacy, décompressez d’abord l’archive ZIP. Un double-clic sur l’icône de SonicRoute dans la zone de notification ouvre la fenêtre principale.
2. **Choisissez la sortie d’une application.** Dans les paramètres, sélectionnez les périphériques à afficher. Lancez la lecture dans une application, puis cliquez sur l’icône de SonicRoute. Dans le panneau rapide, dépliez la ligne de l’application pour choisir sa sortie. Le curseur règle son volume.
3. **Essayez la molette.** Tournez la molette sur l’icône de SonicRoute pour régler le volume de l’application ciblée. Par défaut, SonicRoute privilégie l’application au premier plan si elle a une session audio, puis la dernière application au premier plan qui en avait une. Maintenez Alt pour changer sa sortie, ou Maj pour régler le volume du périphérique de lecture par défaut de Windows.

## ✨ Ce que propose SonicRoute

- **Choisissez les périphériques de lecture et d’enregistrement de chaque application.** Dans le panneau rapide, un Ctrl+clic ajoute ou retire une sortie, et un Maj+clic sélectionne plusieurs sorties consécutives. Toutes les éditions peuvent diffuser le son d’une application sur plusieurs sorties du PC à la fois. Full peut y ajouter les sorties d’un téléphone Android connecté.
- **Réglez le volume de chaque application.** Les indicateurs de niveau du panneau rapide montrent quelles applications émettent du son. Chacune a son propre curseur de volume. Utilisez la molette sur l’icône de notification pour régler l’application ciblée. Un raccourci clavier permet de couper puis de réactiver tous les micros.
- **Automatisez les réglages courants.** Vous disposez de 6 déclencheurs et de 39 actions pour créer vos règles. À l’ouverture d’un jeu, une règle peut sélectionner le casque et lancer votre application de discussion vocale. La commande `sr` permet aussi d’exécuter une règle depuis un terminal ou un script.
- **Utilisez Android et AirPlay sur le PC (Full).** Écoutez une application Android sur le PC, envoyez le son du PC au téléphone ou utilisez une application Android dans son propre écran virtuel. SonicRoute reçoit également le son et la recopie d’écran des appareils Apple via AirPlay. Vous pouvez aussi parcourir le stockage partagé du téléphone Android et envoyer ou recevoir plusieurs fichiers à la fois.

## 🎮 Pour le quotidien

Une règle peut sélectionner le casque et lancer la discussion vocale à l’ouverture d’un jeu. [Configurer l’automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Situation | Comment SonicRoute vous aide |
|---|---|
| 🎮 **La discussion vocale couvre le jeu, ou inversement** | Réglez le volume de Discord indépendamment de celui du jeu. Vous pouvez aussi leur affecter deux sorties différentes. |
| 🎵 **Écouter de la musique ou regarder une vidéo pendant une partie** | Envoyez la musique vers les enceintes et le jeu vers le casque. La molette sur l’icône de SonicRoute règle le volume de l’application ciblée. Maintenez Alt pour changer sa sortie. |
| ⚡ **Régler le casque et lancer la discussion vocale à chaque partie** | À l’ouverture du jeu, une règle peut sélectionner le casque, régler le volume à 40 % et lancer la discussion vocale. Une seconde règle rétablit les enceintes à la fermeture du jeu. |
| 🎙️ **S’absenter ou prendre un appel privé** | Par défaut, Ctrl+Maj+N coupe ou réactive tous les micros. L’indicateur à l’écran permet d’en vérifier l’état. |
| 📹 **Diffuser un jeu avec OBS ou enregistrer un tutoriel** | Envoyez le jeu et la discussion vocale vers des sorties physiques ou virtuelles distinctes. Dans OBS, choisissez la sortie à enregistrer. |
| 📱 **Écouter le téléphone sur le PC ou y utiliser une application Android** | Jouez à un jeu Android sur un écran virtuel du PC, en H.265 si l’appareil le permet. La manette commande le jeu uniquement lorsque la fenêtre de l’écran virtuel est active. Sélectionnez les enceintes du PC dans le panneau rapide. L’option « Volume intelligent » atténue les écarts de volume. |

### ⚡ Exécuter une règle en ligne de commande

Au premier lancement normal, SonicRoute enregistre la commande `sr` pour votre compte. Fermez puis rouvrez le terminal. `sr` exécute une règle dans l’instance déjà ouverte de SonicRoute en communiquant avec elle par un canal nommé. Vous pouvez ainsi lancer une règle depuis un script, AutoHotkey ou un Stream Deck. Consultez la [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) pour plus de détails.

```powershell
# Exécuter une règle d’automatisation (SonicRoute doit déjà être ouvert)
sr "Nom ou ID de la règle"
```

## ❓ Questions fréquentes

**Quels réglages sont conservés quand je quitte SonicRoute ?**

Windows conserve, pour chaque application, la sortie principale du PC et le périphérique d’enregistrement, ainsi que le volume des sessions audio existantes. La lecture sur les sorties supplémentaires s’arrête quand vous quittez SonicRoute. Les raccourcis, les commandes de la zone de notification, l’automatisation, l’audio du téléphone, la recopie d’écran et la synchronisation du presse-papiers nécessitent que SonicRoute reste en cours d’exécution.

**De quel environnement d’exécution chaque édition a-t-elle besoin ?**

Full et Lite nécessitent .NET 10 Desktop Runtime pour leur architecture. Les éditions du Microsoft Store utilisent la version x64 et Legacy utilise .NET Framework 4.8. Les anciennes éditions pour .NET 8 restent téléchargeables, mais ne reçoivent plus de mises à jour.

**Quel est l’effet de SonicRoute sur le processeur et la batterie en arrière-plan ?**

SonicRoute réagit principalement aux événements de Windows. Il ne vérifie l’état du système une fois par seconde que lorsque c’est nécessaire et libère les données inutiles à la demande. La charge du processeur dépend du PC et des fonctions utilisées.

## 🆕 Points forts de la version v1.35

Dans le panneau rapide, le micro que vous choisissez pour une application du PC est aussitôt mis en surbrillance. Vous pouvez continuer à cliquer pendant que SonicRoute applique vos choix dans l’ordre. Dans l’édition Full, il en va de même pour les sources audio externes qui n’utilisent qu’une sortie. Quand vous changez de sortie, son nom s’affiche immédiatement sur la ligne de l’application. Les raccourcis de changement de périphérique peuvent être utilisés plusieurs fois de suite. Si vous changez la sortie de l’application actuelle avec son raccourci et que l’opération prend au plus 45 ms, seul le résultat s’affiche. Si un message de progression apparaît, il reste visible au moins 500 ms avant la confirmation. Les erreurs sont signalées immédiatement. Si Windows transfère la session audio d’une application vers une autre sortie sélectionnée, la lecture se poursuit sur les autres sorties choisies.

Dans l’édition Full, vous pouvez changer de sortie pendant un changement de mode de capture audio Android et continuer à utiliser l’écran virtuel. Si vous désactivez la synchronisation du presse-papiers pour un téléphone, les écritures de ce téléphone encore en cours vers le presse-papiers du PC sont annulées. Les autres téléphones continuent de se synchroniser. En cas d’échec d’écriture, SonicRoute libère également la mémoire réservée à cette opération. Pour les versions précédentes, consultez [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

<details>
<summary>v1.34</summary>

Toutes les éditions permettent désormais de sélectionner plusieurs périphériques de lecture du PC pour chaque application. Après le redémarrage de SonicRoute, la sélection est rétablie dès que l’application émet à nouveau du son. Le réglage du volume depuis la zone de notification et les raccourcis à la souris partagent le même mécanisme de suivi de la souris au niveau du système.

Dans l’édition Full, l’état des connexions USB et Wi-Fi est synchronisé entre les deux panneaux Android. L’actualisation du panneau rapide du téléphone permet aussi de reconnecter l’appareil sélectionné. Le menu de sortie des applications Android affiche désormais le nom personnalisé de la sortie audio actuelle du téléphone. Les versions précédentes figurent dans [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

</details>

## En développement pour l’édition Full

- Pour transmettre le son d’Android au PC, choisissez Opus (par défaut) ou le PCM non compressé. Chaque téléphone dispose de trois modes de latence : « Latence minimale », « Équilibré » (par défaut) et « Stable ». Vos réglages prennent effet à la prochaine transmission audio depuis ce téléphone.
- Si l’encodeur Android le permet, SonicRoute adapte le débit vidéo de l’écran virtuel aux conditions de connexion.

<a name="windows-link"></a>

### Windows Link dans la bêta de l’édition Full

- **Associez deux PC Windows.** Activez Windows Link dans SonicRoute Full sur les deux PC. Recherchez l’autre PC sur le réseau local ou saisissez son adresse. Vérifiez que le même code à huit chiffres s’affiche des deux côtés, puis confirmez sur chaque PC.
- **Choisissez où écouter le son.** Faites glisser une application vers l’autre PC pour l’écouter sur celui-ci ou sur les deux à la fois. Vous pouvez aussi transmettre tout le son du système ou choisir plusieurs sorties pour une même source.
- **Partagez une fenêtre ou un écran avec le son.** Vous pouvez autoriser l’autre personne à agir sur la fenêtre ou l’écran partagé avec sa souris et son clavier. Vous pouvez aussi transférer des fichiers et des dossiers. Pour partager le texte brut que vous copiez, activez la synchronisation du presse-papiers sur les deux PC.

[Guide de Windows Link (en chinois)](docs/windows-link.md) · [Voir toutes les modifications](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.35...Beta)

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
