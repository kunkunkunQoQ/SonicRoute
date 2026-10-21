<!-- Generated from docs/readme/shared.json and docs/readme/locales/fr-FR.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | **Français** | [Español](README.es.md) | [Русский](README.ru.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Soutenir sur Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**Envoyez le son de chaque application vers l’appareil de votre choix.**

Écoutez votre musique sur les haut-parleurs et vos jeux ou appels dans le casque. SonicRoute est un utilitaire audio pour Windows, présent dans la zone de notification : choisissez les périphériques de sortie et d’entrée de chaque application, contrôlez le son avec des raccourcis, la molette dans la zone de notification ou un panneau, et combinez des actions courantes dans des règles d’automatisation.

📥 [Télécharger la dernière version](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [Guide d’utilisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (expérimental) · Neuf langues d’interface · Licence MIT

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="Guide d’utilisation (en chinois)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="Cliquez sur la mascotte pour ouvrir le guide d’utilisation"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panneau rapide SonicRoute : périphérique de sortie et commandes de volume par application"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="Éditeur de règles d'automatisation SonicRoute : sélection des déclencheurs et actions audio"></a>
</div>

<sub>Aperçu de l'interface : panneau rapide (hauteur adaptable et style acrylique) pour le routage et le volume des applications ; éditeur d'automatisation pour chaîner déclencheurs et actions.</sub>

## ✨ Ce que propose SonicRoute

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="Fonctionnalités"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="Cliquez sur la mascotte pour découvrir les fonctionnalités de SonicRoute"></a>

- **Routage audio indépendant et bidirectionnel par application (lecture + enregistrement) :** attribuez des périphériques d'entrée et de sortie dédiés à chaque application (jeux et voix sur le casque, musique sur les haut-parleurs). Les paramètres sont conservés nativement par Windows après fermeture.
- **Suivi intelligent du premier plan et réglage direct dans la barre des tâches :** verrouille instantanément l'application audio active via les événements système. Ajustez le volume en faisant défiler la molette sur la zone de notification, avec retour visuel OSD et coupure micro globale.
- **Deux styles de panneau et indicateur de niveau sonore en direct :** choisissez entre la vue classique par ligne et le panneau moderne sous forme de cartes (hauteur ajustable de 350 à 850 px) ; contrôlez le volume individuel avec indicateurs d'activité sonore en temps réel.
- **Moteur d'automatisation et CLI ultrarapide (6 déclencheurs × 37 actions) :** déclenchements par lancement, focus ou fermeture d'application, minuterie ou raccourcis. Enchaînez changements d'appareil, volume, scripts PowerShell et commande sr sans processus d'arrière-plan superflu.
- **Architecture légère et purement événementielle (0 % CPU au repos) :** aucun minuteur de scrutation permanent, consommation CPU quasi nulle au repos et gestion mémoire optimisée. Décliné en Legacy (.NET 4.8), fichier unique Lite (x64 / ARM64 expérimental) et Microsoft Store en 9 langues.

## 🎮 Pour le quotidien

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Automatisation"><img src="docs/images/mascot-automation-fr.png" width="48" align="middle" alt="Cliquez sur le petit robot pour découvrir les règles d’automatisation"></a> Transformez les réglages manuels fastidieux en une expérience automatisée prête à l'emploi. [Configurer l’automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Votre scénario quotidien | Comment SonicRoute vous aide |
|---|---|
| 🎮 **Chat vocal trop fort ou sons du jeu couvrant vos coéquipiers** | Réglez le volume de Discord indépendamment sans toucher au jeu ; jeu dans le casque et chat sur écouteur dédié |
| 🎵 **Écouter de la musique ou regarder une vidéo tout en jouant** | Musique sur les haut-parleurs et jeu dans le casque ; ajustez le volume via la molette sur la zone de notification |
| ⚡ **Changer de casque et modifier les paramètres Windows à chaque jeu** | Règle auto : lancer le jeu → basculer sur casque + volume à 40 % + lancer Discord ; retour enceintes à la fermeture |
| 🎙️ **S'éloigner du PC ou passer un appel privé rapidement** | Coupez tous les micros avec Ctrl+Maj+N et confirmez l'état en un coup d'œil grâce à la bannière OSD à l'écran |
| 📹 **Diffusion en direct sur OBS ou enregistrement de tutoriels** | Attribuez des pistes audio distinctes par application pour ne jamais inclure vos conversations privées en vidéo |

## 🚀 Télécharger et commencer

**Version actuelle : [v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [Notes de version (en chinois)](docs/release-v1.24.md)

Pour la plupart des PC Windows x64, la version du Microsoft Store offre une installation pratique et des mises à jour automatiques. Choisissez Lite x64 pour un exécutable unique, ou Legacy x64 si l’installation de .NET 8 n’est pas pratique. Le lien de téléchargement ci-dessus mène toujours à la dernière version ; les liens directs ci-dessous correspondent à v1.24.

| Édition | Télécharger | Configuration requise |
|---|---|---|
| ⭐ Lite x64 (Recommandé) | [Un seul EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | Exécutable autonome à fichier unique ; nécessite le .NET 8 Desktop Runtime x64 correspondant |
| ⚡ Lite ARM64 (expérimental) | [Un seul EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | Performances natives sur Surface Pro et PC Snapdragon ARM64 ; nécessite le .NET 8 Desktop Runtime ARM64 |
| 🛡️ Legacy x64 (Sans .NET 8) | [Archive ZIP complète](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | Aucun .NET 8 requis ; s'exécute directement via le .NET Framework 4.8 intégré ; décompressez le ZIP |
| 🔄 Microsoft Store (Mises à jour auto) | [Installer depuis le Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | Installation en un clic avec mises à jour automatiques via le Microsoft Store ; pour Windows 10/11 64 bits |

Lite nécessite le [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) correspondant. Legacy ne nécessite pas .NET 8.

1. **Lancer & garder en barre des tâches** : Double-cliquez pour démarrer ; reste actif dans la zone de notification. Double-cliquez sur l'icône pour ouvrir la fenêtre principale.
2. **Sélectionner & router les périphériques** : Cochez vos casques et haut-parleurs dans Paramètres → Périphériques conservés. Cliquez sur l'icône pour afficher le panneau rapide et router chaque application.
3. **Contrôle du volume à l'aveugle** : Survolez l'icône de notification et utilisez la molette pour ajuster le volume à l'aveugle, ou utilisez des raccourcis et des règles d'automatisation sans quitter vos jeux.

Avant d’utiliser `sr` pour la première fois, lancez SonicRoute normalement pour enregistrer la commande, puis ouvrez de nouveau votre terminal. Si le changement de périphérique ne fonctionne pas comme prévu, consultez la [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```bash
# Déclencher une règle d'automatisation (SonicRoute doit être en cours d'exécution)
sr "Nom ou ID de la règle"
```

La version 1.24 ajoute des modèles d’automatisation, un historique détaillé des exécutions récentes (désactivé par défaut, avec une limite de stockage réglable) et l’importation d’icônes par application. Lorsqu’une icône est absente ou générique, le premier caractère du nom de l’application s’affiche. Les sauvegardes des paramètres incluent les icônes importées. Consultez [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) pour les changements précédents.

### ❓ Foire aux questions (FAQ)

**Q : Le routage et les réglages de volume restent-ils conservés après avoir fermé SonicRoute ?**

A : Oui ! Le routage audio des applications est conservé nativement par le moteur audio de Windows et reste actif même après la fermeture de SonicRoute. Seuls les raccourcis, la molette et l'automatisation nécessitent son exécution.

**Q : Que faire si le système indique que le .NET 8 Desktop Runtime est manquant ?**

A : Cliquez sur le lien du runtime dans le tableau ci-dessus pour installer le .NET 8 Desktop Runtime officiel de Microsoft, ou téléchargez l'édition Legacy qui ne nécessite aucun composant supplémentaire.

**Q : L'exécution de SonicRoute en arrière-plan consomme-t-elle du processeur ou de la batterie ?**

A : Pratiquement zéro. SonicRoute repose entièrement sur les événements système de Windows ; au repos, l'utilisation CPU est de 0% sans aucune boucle de scrutation inutile.

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

- **v1.25 · Mise à jour majeure (En développement)** : Extension de l'automatisation à 6 déclencheurs et 37 actions avancées (sélection détaillée d'enregistrement, contrôle micro par application, volume global par lot et réinitialisation en un clic).
- **Étapes ultérieures** : Perfectionnement continu des écouteurs d'événements à faible latence et de l'architecture sans scrutation selon les retours de la communauté.

[Partager une idée ou signaler un problème](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 🧩 Autres projets et combinaisons

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)** : Un lecteur audio pour Windows. Associez-le à l’action de lancement de programme de l’automatisation pour jouer des effets sonores avec des raccourcis. [Guide de configuration (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="XiaoYue Comics"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="Cliquez sur XiaoYue qui sort de sa coquille avec un manga pour lire la série"></a>

**[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

Une comédie absurde autour d’une magical girl née dans un œuf, dont chaque tentative d’aide provoque une nouvelle catastrophe.

<br clear="all">

---

<a href="EASTER_EGGS.md" title="Un œuf de Pâques : hier et demain"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Découvrez un œuf de Pâques"></a>

Merci d’utiliser SonicRoute. [Lire les guides (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Signaler un problème](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Auteur : [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://www.ifdian.net/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**
