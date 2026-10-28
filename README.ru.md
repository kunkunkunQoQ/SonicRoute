<!-- Generated from docs/readme/shared.json and docs/readme/locales/ru-RU.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | **Русский** | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Поддержать на Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Направляйте звук каждого приложения на нужное вам устройство.**

Слушайте музыку через колонки, а игры и звонки — в наушниках. SonicRoute — аудиопрограмма для Windows, работающая в области уведомлений: выбирайте устройства вывода и ввода для отдельных приложений, управляйте звуком с помощью горячих клавиш, прокрутки в области уведомлений или панели и объединяйте повторяющиеся действия в правила автоматизации.

📥 [Скачать последнюю версию](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [Руководство пользователя](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Вики (на китайском)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (экспериментально) · Девять языков интерфейса · Лицензия MIT

> [!TIP]
> **Быстрый анализ с помощью ИИ и диагностика с экономией токенов**: При использовании ИИ-ассистентов (Cursor, Copilot, ChatGPT или Claude) для диагностики проблем, изучения архитектуры или написания скриптов рекомендуем обращаться к спецификации [README.ai.md](README.ai.md). Она составлена на плотном структурированном DSL для LLM, избавляет от чтения длинных текстов, существенно экономит токены контекста и помогает быстро найти первопричину.

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="Руководство пользователя (на китайском)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="Нажмите на маскота, чтобы открыть руководство пользователя"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Быстрая панель SonicRoute: устройство вывода и управление громкостью приложений"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="Редактор правил автоматизации SonicRoute: выбор триггеров приложений и шагов аудиодействий"></a>
</div>

<sub>Предпросмотр интерфейса: панель быстрого доступа (с адаптивной высотой и акриловым эффектом) для регулировки громкости и перенаправления; редактор правил для объединения триггеров и действий.</sub>

## ✨ Возможности SonicRoute

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="Возможности"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="Нажмите на маскота, чтобы узнать о возможностях SonicRoute"></a>

- **Разделение звука и двунаправленная маршрутизация для приложений (воспроизведение + запись):** назначайте устройства вывода и ввода для каждого приложения отдельно: игры и связь — в гарнитуру, музыка — в колонки. Настройки сохраняются Windows даже после закрытия программы.
- **Умное следование за активным окном и регулировка колесом в трее.** Системные события мгновенно определяют звучащее приложение. Прокручивайте колесо мыши над треем для регулировки громкости или переключайте устройства через Alt+колесо с экранным OSD и отключением микрофона.
- **Быстрая панель и индикаторы уровня звука:** панель с карточками поддерживает высоту от 350 до 850 px и сохранение позиции; регулируйте громкость каждого приложения с наглядными индикаторами звуковой активности.
- **Движок автоматизации и быстрый CLI (6 триггеров × 37 действий):** запуск по событиям приложений, таймеру или горячим клавишам. Переключение устройств, громкость, скрипты PowerShell и команда sr по именованному каналу для интеграции со сторонним ПО.
- **Сначала системные события:** используются системные уведомления, при необходимости — снимок состояния раз в секунду; ненужные данные очищаются по мере необходимости. Доступны Legacy (.NET 4.8), Lite на .NET 10 и NET8, а также версия Store на 9 языках.

## 🎮 На каждый день

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Автоматизация"><img src="docs/images/mascot-automation-ru.png" width="48" align="middle" alt="Нажмите на маленького робота, чтобы узнать о правилах автоматизации"></a> Превратите утомительную ручную настройку в удобную автоматизацию в один клик. [Настроить автоматизацию](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Повседневный сценарий | Как SonicRoute решает задачу |
|---|---|
| 🎮 **Голосовой чат слишком громкий, или звуки игры заглушают тиммейтов** | Настраивайте Discord отдельно, не меняя звук игры; разделяйте вывод игры и голосового чата по разным устройствам |
| 🎵 **Фоновое прослушивание музыки или просмотр видео во время игры** | Музыка на колонках, игра в гарнитуре. Наведите курсор на значок в трее и прокрутите колесо для регулировки громкости или Alt+колесо для смены устройства без сворачивания игры. |
| ⚡ **Постоянное переключение гарнитур и настроек Windows перед каждой игрой** | Правило: запуск игры → переход на гарнитуру + громкость 40% + запуск Discord; возврат на колонки при выходе |
| 🎙️ **Необходимость отойти от ПК или принять личный звонок** | Отключайте все микрофоны по Ctrl+Shift+N с мгновенным подтверждением статуса на экранном баннере OSD |
| 📹 **Стримы в OBS или запись обучающих видео с экрана** | Назначайте приложениям отдельные звуковые дорожки, чтобы личные разговоры и уведомления не попадали в запись |

## 🚀 Скачивание и начало работы

**Текущая версия: [v1.29](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.29)** · 2026-10-27 · [Примечания к выпуску (на китайском)](docs/release-v1.29.md)

Для большинства пользователей рекомендуется **Lite x64** (портативный единый файл, минимальное потребление памяти); для рабочих ПК со строгими ограничениями выбирайте **Legacy x64** (не требует дополнительных сред выполнения, готов к работе сразу после распаковки); для устройств ARM64 выберите **Lite ARM64**. Доступна также установка из **Microsoft Store** с автоматическим обновлением. Прямые ссылки ниже относятся к v1.29.

| Вариант | Скачать | Требования |
|---|---|---|
| ⭐ Lite x64 (.NET 10, Рекомендуется) | [Один EXE-файл](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-x64.exe) | Портативный файл; требуется x64 .NET 10 Desktop Runtime |
| ⚡ Lite ARM64 (.NET 10) (экспериментально) | [Один EXE-файл](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-arm64.exe) | Портативный файл ARM64; требуется ARM64 .NET 10 Desktop Runtime |
| ☕ Lite NET8 x64 | [Один EXE-файл](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) | Совместимая с NET8 версия Lite; требуется x64 .NET 8 Desktop Runtime |
| ☕ Lite NET8 ARM64 (экспериментально) | [Один EXE-файл](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Совместимая с NET8 версия Lite; требуется ARM64 .NET 8 Desktop Runtime |
| 🛡️ Legacy x64 (Без .NET 8) | [Полный ZIP-архив](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Legacy-x64.zip) | Не требует .NET 8; работает напрямую через встроенный .NET Framework 4.8; распакуйте ZIP-архив |
| 🔄 Microsoft Store (обновления через Store) | [Установить из Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | Для пакета Store на .NET 10 требуется x64 .NET 10 Desktop Runtime. Текущую доступную версию смотрите на странице Store; Windows 10/11, 64-разрядная. |

> [!NOTE]
> Требования к среде выполнения: Стандартная Lite требует [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); совместимая версия NET8 требует [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (поддержка завершается после ноября 2026 года; рекомендуется переход на .NET 10 или Legacy). Legacy использует встроенный в Windows .NET Framework 4.8 и не требует установки сторонних библиотек.

### 🏁 Быстрый старт за 3 шага

1. **Запуск и фиксация в трее**: Дважды нажмите для запуска; приложение сворачивается в системный трей (рекомендуется вытащить значок из скрытой зоны на панель задач). Двойной клик открывает главное окно.
2. **Выбор устройств и маршрутизация**: В разделе «Настройки → Сохраненные устройства» отметьте наушники и колонки. Кликните по значку в трее, чтобы открыть быструю панель и распределить звук приложений ползунками.
3. **Слепое управление колесиком в трее**: Наведите курсор на область трея и прокручивайте колесико для регулировки звука приложения, Alt+колесико — для смены устройства, Shift+колесико — для общей громкости Windows, не выходя из полноэкранных игр.

### ⚡ Командная строка и автоматизация (Для продвинутых)

Команда `sr` обменивается данными с работающим экземпляром SonicRoute через именованный канал за миллисекунды (не запускает закрытое приложение). При первом запуске путь регистрируется автоматически; после перезапуска терминала команду можно вызывать в скриптах, AutoHotkey или Stream Deck. Вопросы и ответы: [Частые вопросы](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```bash
# Запустить правило автоматизации (SonicRoute должен быть запущен)
sr "Название правила или ID"
```

### 🆕 Главные нововведения v1.29

В версии 1.29 добавлена панель регулировки громкости для устройств вывода с обновлением по системным событиям и фильтрацией сохраненных устройств, а также управление общей громкостью через Shift+колесико в трее. Полная история изменений: [История версий](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

### ❓ Часто задаваемые вопросы (FAQ)

**Q: Сохраняются ли маршрутизация приложений и громкость после закрытия SonicRoute?**

A: Да! Маршрутизация звука приложений сохраняется на уровне аудиослужбы Windows и действует даже после закрытия SonicRoute. Только горячие клавиши, управление из трея и автоматизация требуют работы программы в фоне.

**Q: Какая среда выполнения нужна для каждой версии?**

A: Для стандартной Lite требуется подходящая .NET 10 Desktop Runtime, для Lite NET8 — .NET 8. Legacy работает на .NET Framework 4.8 и не требует отдельного пакета .NET Desktop Runtime. Для текущего пакета Store требуется x64 .NET 10 Desktop Runtime.

**Q: Потребляет ли работа SonicRoute в фоне ресурсы процессора или батарею?**

A: Обнаружение в первую очередь использует системные события и при необходимости дополняется снимком состояния раз в секунду. Ненужные данные очищаются по мере необходимости; фактическая нагрузка на CPU зависит от системы и задач.

## 📚 Документация

Вики сейчас доступна на упрощённом китайском языке.

| Тема | Руководства |
|---|---|
| Установка, панели и повседневное управление | [Руководство пользователя](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Возможности](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Клавиши по умолчанию, сочетания и объекты управления | [Горячие клавиши](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Триггеры, действия и интеграция со скриптами | [Автоматизация](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Командная строка](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Установка и устранение неполадок | [Частые вопросы](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Исходный код, сборки и выпуски | [Техническая реализация](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Сведения о версии](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [История версий](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

## 📌 План развития

- **Развитие маршрутизации звука (Route Enhancement)**: Планируется улучшение ядра маршрутизации с поддержкой динамического переключения потоков, автовосстановления при переподключении устройств и повышения стабильности в сложных сценариях.

[Предложить идею или сообщить о проблеме](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 🧩 Другие проекты и сочетания

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**: Аудиоплеер для Windows. Используйте действие запуска программы в автоматизации, чтобы включать звуковые эффекты с помощью горячих клавиш. [Руководство по настройке (на китайском)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="XiaoYue Comics"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="Нажмите на Сяоюэ с комиксом в яичной скорлупе, чтобы читать серию"></a>

**[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

Абсурдная комедия о девочке-волшебнице, которая вылупилась из яйца и устраивает новый переполох при каждой попытке помочь.

<br clear="all">

---

<a href="EASTER_EGGS.md" title="Пасхалка: вчера и завтра"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Найдите пасхалку"></a>

Спасибо, что пользуетесь SonicRoute. [Руководства (на китайском)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Сообщить о проблеме](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Автор: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
