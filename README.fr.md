<!-- Generated from docs/readme/shared.json, docs/readme/locales/fr-FR.json, and docs/readme/releases/v2.00.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute 2.00

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | **Français** | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Version : v2.00](https://img.shields.io/static/v1?label=Version&message=v2.00&color=34A853&logo=github&logoColor=white)](docs/release-v2.00.md) [![Licence : GPL-3.0-only](https://img.shields.io/static/v1?label=Licence&message=GPL-3.0-only&color=blue)](LICENSE) [![Système : Windows 10 | 11](https://img.shields.io/static/v1?label=Syst%C3%A8me&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Architecture : x64 | ARM64](https://img.shields.io/static/v1?label=Architecture&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Téléchargements](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=T%C3%A9l%C3%A9chargements)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Soutenir SonicRoute sur Afdian](https://img.shields.io/static/v1?label=&message=Soutenir+SonicRoute+sur+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envoyez le son de chaque application vers l’appareil de votre choix.**

Écoutez la musique sur vos enceintes et gardez le casque pour les jeux et les appels. Depuis la zone de notification de Windows, SonicRoute permet de choisir la sortie et le micro de chaque application et d’en régler le volume.

📥 [Voir les versions sur GitHub](https://github.com/kunkunkunQoQ/SonicRoute/releases) · [Comparer les éditions](#downloads) · [Premiers pas en trois étapes](#quick-start) · [Guide d’utilisation (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (expérimental) · Interface disponible en neuf langues · [GPL-3.0](LICENSE)

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas"><img src="https://raw.githubusercontent.com/wiki/kunkunkunQoQ/SonicRoute/images/routing-canvas-v2.00.png" width="820" alt="Fenêtre de SonicRoute 2.00 avec un trait plein entre une application et sa sortie principale, et une redirection en pointillés vers un deuxième périphérique."></a>

<sub>L’espace de routage audio de SonicRoute 2.00 montre la sortie principale d’une application et une redirection vers un deuxième périphérique.</sub>

<details>
<summary><sub>🖼️ Exemples d’interface de la version 1.36</sub></summary>

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Une application Android est déplacée vers le panneau rapide du PC pour y choisir sa sortie audio."></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Éditeur de règles avec un raccourci clavier comme déclencheur. Le menu des actions est ouvert sur le choix du périphérique de lecture par défaut."></a>
</div>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panneau rapide avec le périphérique de sortie et les réglages de volume de chaque application"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Gestion des appareils Android avec les options d’association, les applications épinglées et les réglages audio de l’écran virtuel"></a>
</div>

</details>

<a name="downloads"></a>

## 🚀 Télécharger et commencer

**v2.00** · [Notes de version (en anglais et en chinois)](docs/release-v2.00.md) · [Voir les versions sur GitHub](https://github.com/kunkunkunQoQ/SonicRoute/releases)

SonicRoute v2.00 se décline en deux éditions. Choisissez **Full** pour l’audio du PC, Android et AirPlay, ou **Lite** pour l’audio du PC. Sélectionnez l’architecture qui correspond à votre machine, x64 ou ARM64.

| Édition | Paquets portables | Configuration requise |
|---|---|---|
| ⭐ Full (PC, Android et AirPlay) | `SonicRoute-v2.00-x64.exe`<br>`SonicRoute-v2.00-arm64.exe` | Exécutable portable avec .NET 10 Native AOT intégré, sans installation d’un environnement d’exécution séparé. |
| ⚡ Lite (audio du PC) | `SonicRoute-v2.00-Lite-x64.exe`<br>`SonicRoute-v2.00-Lite-arm64.exe` | Exécutable portable avec .NET 10 Native AOT intégré, sans installation d’un environnement d’exécution séparé. |

Microsoft Store · [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) — La distribution et les mises à jour passent séparément par le Microsoft Store. La version disponible figure sur sa fiche.

<details>
<summary><sub>📦 Anciennes éditions pour .NET 8 (fin de maintenance)</sub></summary>

| Édition | Paquets portables | Configuration requise |
|---|---|---|
| Lite NET8 (fin de maintenance) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (expérimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Paquet historique pour .NET 8. Il ne reçoit plus de mises à jour. |

</details>

> [!NOTE]
> L’application fonctionne à partir de Windows 10 version 2004 et sous Windows 11. La capture de l’audio d’un processus pour le rediriger vers des sorties supplémentaires exige la build 20348 de Windows ou une version ultérieure.

<a name="quick-start"></a>

### 🏁 Premiers pas en trois étapes

1. **Lancez SonicRoute.** Ouvrez l’exécutable portable. Un double-clic sur l’icône de SonicRoute dans la zone de notification ouvre la fenêtre principale.
2. **Choisissez la sortie d’une application.** Dans la fenêtre principale, ouvrez « Applications », sélectionnez votre application et faites glisser le connecteur de sa sortie principale vers le périphérique voulu. Pour changer de sortie rapidement, cliquez sur l’icône de notification et dépliez la ligne de l’application dans le panneau rapide.
3. **Essayez la molette.** Tournez la molette sur l’icône de SonicRoute pour régler le volume de l’application ciblée. Par défaut, SonicRoute privilégie l’application au premier plan si elle a une session audio, puis la dernière application au premier plan qui en avait une. Maintenez Alt pour changer sa sortie, ou Maj pour régler le volume du périphérique de lecture par défaut de Windows.

## ✨ Ce que propose SonicRoute

- **Affectez les sorties audio de chaque application.** Dans « Applications », un trait plein représente la sortie principale utilisée directement par Windows. Le bouton « + Ajouter une sortie » ajoute une sortie de redirection. Chaque redirection a ses propres réglages de volume et de sourdine. Vous pouvez aussi attribuer un périphérique d’enregistrement dans cet espace. Dans le panneau rapide, utilisez Ctrl+clic ou Maj+clic pour sélectionner plusieurs sorties.
- **Réglez le volume de chaque application.** Les indicateurs de niveau du panneau rapide montrent quelles applications émettent du son. Chacune a son propre curseur de volume. Utilisez la molette sur l’icône de notification pour régler l’application ciblée. Un raccourci clavier permet de couper puis de réactiver tous les micros.
- **Automatisez les réglages courants.** Vous disposez de 6 déclencheurs et de 39 actions pour créer vos règles. À l’ouverture d’un jeu, une règle peut sélectionner le casque et lancer votre application de discussion vocale. La commande `sr` permet aussi d’exécuter une règle depuis un terminal ou un script.
- **Connectez un téléphone Android ou recevez des contenus AirPlay (Full).** Associez l’application SonicRoute sur le même réseau local et confirmez que le code à huit chiffres est identique sur les deux appareils. Vous pouvez transmettre le son dans les deux sens et regarder l’écran partagé du téléphone sur le PC. Une connexion ADB permet d’ouvrir un écran virtuel indépendant avec contrôle des entrées, de transférer des fichiers et de synchroniser le presse-papiers. Full reçoit aussi le son et la recopie d’écran AirPlay des appareils Apple.

## 🎮 Pour le quotidien

Une règle peut sélectionner le casque et lancer la discussion vocale à l’ouverture d’un jeu. [Configurer l’automatisation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Situation | Comment SonicRoute vous aide |
|---|---|
| 🎮 **La discussion vocale couvre le jeu, ou inversement** | Réglez le volume de Discord indépendamment de celui du jeu. Vous pouvez aussi leur affecter deux sorties différentes. |
| 🎵 **Écouter de la musique ou regarder une vidéo pendant une partie** | Envoyez la musique vers les enceintes et le jeu vers le casque. La molette sur l’icône de SonicRoute règle le volume de l’application ciblée. Maintenez Alt pour changer sa sortie. |
| ⚡ **Régler le casque et lancer la discussion vocale à chaque partie** | À l’ouverture du jeu, une règle peut sélectionner le casque, régler le volume à 40 % et lancer la discussion vocale. Une seconde règle rétablit les enceintes à la fermeture du jeu. |
| 🎙️ **S’absenter ou prendre un appel privé** | Par défaut, Ctrl+Maj+N coupe ou réactive tous les micros. L’indicateur à l’écran permet d’en vérifier l’état. |
| 📹 **Diffuser un jeu avec OBS ou enregistrer un tutoriel** | Envoyez le jeu et la discussion vocale vers des sorties physiques ou virtuelles distinctes. Dans OBS, choisissez la sortie à enregistrer. |
| 📱 **Écouter le téléphone sur le PC ou y utiliser une application Android** | Écoutez le son du téléphone sur le PC ou affichez-y l’écran partagé. Pour utiliser une application Android à la souris et au clavier dans un écran virtuel indépendant, reliez le téléphone à Full par ADB. |

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

Les exécutables portables Full et Lite intègrent .NET 10 Native AOT. Choisissez x64 ou ARM64 selon l’architecture de votre système Windows.

**Quel est l’effet de SonicRoute sur le processeur et la batterie en arrière-plan ?**

SonicRoute réagit principalement aux événements de Windows. Il ne vérifie l’état du système une fois par seconde que lorsque c’est nécessaire et libère les données inutiles à la demande. La charge du processeur dépend du PC et des fonctions utilisées.

**Comment régler la latence quand j’écoute le son d’une application Android sur le PC ?**

Dans Full, ouvrez les paramètres du téléphone connecté par ADB. Dans la rubrique « Audio », choisissez « Équilibré », « Latence minimale » ou « Stable ». En cas de coupures, essayez « Stable ». Pour le son non compressé, choisissez « Raw PCM » au lieu d’Opus. Les réglages s’appliquent à la prochaine transmission audio depuis ce téléphone.

## 🆕 Points forts de la version v2.00

Full et Lite utilisent .NET 10 Native AOT. Les exécutables portables x64 et ARM64 intègrent l’environnement d’exécution. Le nouvel espace de routage visualise la sortie principale et les redirections d’une application. Le volume et la sourdine se règlent pour chaque redirection.

Dans Full, la page Appareils réunit Android et AirPlay. Associez l’application SonicRoute sur le réseau local avec un code à huit chiffres pour transférer le son dans les deux sens ou afficher l’écran du téléphone sur le PC. La connexion ADB donne accès à un écran virtuel indépendant avec contrôle des entrées, au transfert de fichiers et au presse-papiers. Pour les autres versions, consultez l’[Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

<details>
<summary>v1.36</summary>

Dans le panneau rapide et sur la page Applications, Maj+clic ajoute plusieurs sorties consécutives à la sélection en cours. Pour une application Windows, Ctrl+Maj+clic sur une sortie du PC en fait sa sortie principale. Les boutons permettent de repérer la sortie utilisée par Windows et celles sur lesquelles SonicRoute diffuse une copie du son. La fenêtre principale mémorise aussi la position ouverte ou repliée du volet latéral. Dans les deux éditions du Microsoft Store, la fenêtre de l’application reste associée à son icône épinglée dans la barre des tâches.

L’édition Full peut diffuser le son reçu via AirPlay sur plusieurs sorties à la fois. À partir d’Android 11, elle peut capter le son des applications compatibles, le transmettre en Opus ou en PCM non compressé et utiliser l’un des trois modes de latence. Si l’encodeur du téléphone le permet, le débit vidéo de l’écran virtuel s’adapte à la connexion USB ou Wi-Fi. L’édition Full distribuée sur le Microsoft Store inclut les règles de pare-feu pour les connexions AirPlay entrantes sur les réseaux privés ou de domaine. Retrouvez les versions précédentes dans [Historique des versions](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

</details>

## 📚 Documentation

Le Wiki est actuellement en chinois simplifié.

| Sujet | Guides |
|---|---|
| Installation, panneau rapide et commandes courantes | [Guide d’utilisation (en chinois)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Fonctionnalités](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) · [Connexion des appareils](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94) |
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
