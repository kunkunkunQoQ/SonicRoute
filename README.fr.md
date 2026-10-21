<!-- Generated from docs/readme/shared.json and docs/readme/locales/fr-FR.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | **Français** | [Español](README.es.md) | [Русский](README.ru.md)

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE) [![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Soutenir sur Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**Envoyez le son de chaque application vers l’appareil de votre choix.**

Écoutez votre musique sur les haut-parleurs et vos jeux ou appels dans le casque. SonicRoute est un utilitaire audio pour Windows, présent dans la zone de notification : choisissez les périphériques de sortie et d’entrée de chaque application, contrôlez le son avec des raccourcis, la molette dans la zone de notification ou un panneau, et combinez des actions courantes dans des règles d’automatisation.

📥 [Télécharger la dernière version](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [Guide d’utilisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (expérimental) · Neuf langues d’interface · Licence MIT

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="Guide d’utilisation (en chinois)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="Cliquez sur la mascotte pour ouvrir le guide d’utilisation"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panneau rapide SonicRoute : périphérique de sortie et commandes de volume par application"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="Éditeur de règles d’automatisation de SonicRoute v1.23 : déclencheur lié à une application et choix des actions audio"></a>
</div>

<sub>Exemples d’interface : panneau rapide pour le volume et les périphériques de chaque application ; éditeur d’automatisation pour combiner déclencheurs et actions.</sub>

## ✨ Ce que propose SonicRoute

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="Fonctionnalités"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="Cliquez sur la mascotte pour découvrir les fonctionnalités de SonicRoute"></a>

- **Routage bidirectionnel par application (lecture + enregistrement) :** attribuez des périphériques d'entrée et de sortie dédiés à chaque application (jeux et voix sur le casque, musique sur les haut-parleurs). Les paramètres sont conservés nativement par Windows après fermeture.
- **Détection intelligente du premier plan et réglage direct :** verrouille instantanément l'application audio active via les événements système. Ajustez le volume en faisant défiler la molette sur la zone de notification, avec retour visuel OSD et coupure micro globale.
- **Deux styles de panneau et activité sonore en direct :** choisissez entre la vue classique par ligne et le panneau moderne sous forme de cartes (hauteur ajustable de 350 à 800 px) ; contrôlez le volume individuel avec indicateurs d'activité sonore en temps réel.
- **Moteur d'automatisation à 37 actions et interface CLI :** déclenchements par lancement, focus ou fermeture d'application, minuterie ou raccourcis. Enchaînez changements d'appareil, volume, scripts PowerShell et commande sr sans processus d'arrière-plan superflu.
- **Architecture légère entièrement pilotée par événements :** aucun minuteur de scrutation permanent, consommation CPU quasi nulle au repos et gestion mémoire optimisée. Décliné en Legacy (.NET 4.8), fichier unique Lite (x64 / ARM64 expérimental) et Microsoft Store en 9 langues.

## 🎮 Pour le quotidien

Vos coéquipiers parlent trop fort ? Baissez uniquement le curseur de l’application de chat vocal, sans changer le volume du jeu.

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Automatisation"><img src="docs/images/mascot-automation-fr.png" width="48" align="middle" alt="Cliquez sur le petit robot pour découvrir les règles d’automatisation"></a> Par exemple, configurez au lancement du jeu : envoyer le son du jeu au casque → régler son volume à 50 % → ouvrir le chat vocal. Activez ensuite la règle. [Configurer l’automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

## 🚀 Télécharger et commencer

**Version actuelle : [v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [Notes de version (en chinois)](docs/release-v1.24.md)

Pour la plupart des PC Windows x64, la version du Microsoft Store offre une installation pratique et des mises à jour automatiques. Choisissez Lite x64 pour un exécutable unique, ou Legacy x64 si l’installation de .NET 8 n’est pas pratique. Le lien de téléchargement ci-dessus mène toujours à la dernière version ; les liens directs ci-dessous correspondent à v1.24.

| Édition | Télécharger | Configuration requise |
|---|---|---|
| Lite x64 | [Un seul EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | .NET 8 Desktop Runtime pour x64 |
| Lite ARM64 (expérimental) | [Un seul EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | .NET 8 Desktop Runtime pour ARM64 |
| Legacy x64 | [Archive ZIP complète](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | .NET Framework 4.8 ; extrayez toute l’archive ZIP avant de lancer l’application |
| Microsoft Store | [Installer depuis le Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64 ; mises à jour automatiques ; la version disponible est indiquée dans le Store |

Lite nécessite le [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) correspondant. Legacy ne nécessite pas .NET 8.

1. Lancez SonicRoute, repérez son icône dans la zone de notification Windows (elle peut se trouver parmi les icônes masquées), puis double-cliquez dessus pour ouvrir l’interface complète.
2. Dans les paramètres, sélectionnez vos périphériques de sortie et d’entrée préférés.
3. Cliquez sur l’icône de la zone de notification pour ouvrir un panneau ou configurer vos raccourcis. Créez des règles d’automatisation lorsque vous souhaitez combiner plusieurs actions.

Avant d’utiliser `sr` pour la première fois, lancez SonicRoute normalement pour enregistrer la commande, puis ouvrez de nouveau votre terminal. Si le changement de périphérique ne fonctionne pas comme prévu, consultez la [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

La version 1.24 ajoute des modèles d’automatisation, un historique détaillé des exécutions récentes (désactivé par défaut, avec une limite de stockage réglable) et l’importation d’icônes par application. Lorsqu’une icône est absente ou générique, le premier caractère du nom de l’application s’affiche. Les sauvegardes des paramètres incluent les icônes importées. Consultez [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) pour les changements précédents.

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

- **v1.25 · Mise à jour majeure :** les détails seront présentés dans les prochaines notes de version.
- **Étapes suivantes :** continuer à améliorer les fonctions existantes selon vos retours. Les projets peuvent évoluer en fonction des commentaires reçus.

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
