<!-- Generated from docs/readme/shared.json and docs/readme/locales/es-ES.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | **Español** | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Apoyar en Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envía el audio de cada aplicación al dispositivo que elijas.**

Escucha música por los altavoces y juegos o llamadas con los auriculares. SonicRoute es una utilidad de audio para Windows que permanece en el área de notificación: elige los dispositivos de salida y entrada de cada aplicación, controla el audio con atajos, la rueda del área de notificación o un panel, y combina acciones habituales en reglas de automatización.

📥 [Descargar la última versión](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [Guía de usuario](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Nueve idiomas de interfaz · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Demostración de SonicRoute: arrastrar audio de app móvil al panel rápido del PC y cambiar la salida"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Editor de reglas de automatización de SonicRoute: selección de disparadores y acciones de audio"></a>
</div>

<sub>Vista previa de la interfaz: a la izquierda, demostración de arrastrar y soltar audio entre móvil y PC; a la derecha, editor de automatización para combinar disparadores y acciones.</sub>

<details>
<summary><sub>🖼️ Ver más capturas: panel rápido y gestión móvil</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panel rápido de SonicRoute: dispositivo de salida y controles de volumen por aplicación"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Gestión de teléfono en SonicRoute: vinculación Android, apps fijadas y audio de pantalla virtual"></a>
</div>

</details>

## ✨ Qué ofrece SonicRoute

- **Enrutamiento bidireccional y separación de audio por app (reproducción + grabación):** asigne dispositivos de salida y entrada específicos a cada aplicación (juegos y voz a auriculares, música a altavoces). La configuración se guarda de forma nativa en Windows incluso al cerrar el programa.
- **Seguimiento inteligente del primer plano y ajuste ciego con la rueda.** Los eventos del sistema detectan la aplicación activa en milisegundos. Desplace la rueda sobre la bandeja del sistema para ajustar el volumen o use Alt+rueda para cambiar de dispositivo, con avisos OSD y silencio global de micrófono.
- **Panel rápido e indicador de nivel de sonido en tiempo real:** el panel de tarjetas ofrece una altura ajustable de 350 a 850 px y guarda su posición; controle el volumen de cada aplicación con medidores de sonido en directo.
- **Integración Android y recepción AirPlay propia (Full)**: Envíe audio Android al PC con ventanas virtuales adaptables (con H.265 y mandos en primer plano) o transmita audio del PC al teléfono; reciba audio y duplicación de pantalla proporcional AirPlay en C#, enrútelo desde el panel rápido hacia salidas del PC o altavoces/Bluetooth Android y equilibre el volumen con SmartVolume.
- **Motor de automatización y CLI ultrarrápida (6 disparadores × 39 acciones):** active reglas al iniciar, cambiar o cerrar aplicaciones, por temporizador o atajos. Encadene dispositivos, volumen, scripts PowerShell y use el comando sr integrado para enlazar con scripts o consolas.
- **Detección basada primero en eventos:** notificaciones del sistema y comprobación por segundo si hace falta. La integración móvil y AirPlay solo está en Full; Lite y Legacy son solo para PC. Disponible en 9 idiomas.

## 🎮 Para el día a día

Convierta los tediosos ajustes manuales en una experiencia automatizada y lista para usar. [Configurar automatización](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Escenario cotidiano | Cómo le ayuda SonicRoute |
|---|---|
| 🎮 **Chat de voz muy alto o el juego tapa la voz de los compañeros** | Ajuste el volumen de Discord sin alterar el juego; mantenga el juego en auriculares y el chat en el microauricular |
| 🎵 **Escuchar música o ver vídeos mientras juegas en segundo plano** | Envía música a los altavoces y mantén los juegos en los auriculares. Pasa el ratón sobre el icono de la bandeja y gira la rueda o usa Alt+rueda para cambiar de dispositivo sin salir del juego. |
| ⚡ **Cambiar cables de auriculares y ajustar Windows cada vez que juegas** | Regla rápida: abrir juego → pasar a auriculares + volumen 40 % + abrir chat; volver a altavoces al salir |
| 🎙️ **Alejarse del equipo o atender una llamada privada al instante** | Silencie todos los micrófonos con Ctrl+Mayús+N y confirme el estado de inmediato con el aviso OSD en pantalla |
| 📹 **Transmisión con OBS o grabación de tutoriales de pantalla** | Asigne pistas de audio independientes a cada app para que las charlas privadas nunca aparezcan en la grabación |
| 📱 **Reproducir audio del móvil en el PC o usar aplicaciones móviles en el escritorio** | Envíe el audio de Android al PC y use sus aplicaciones en una ventana virtual, o reciba audio y duplicación de pantalla AirPlay desde dispositivos Apple alternando salidas de PC o Android desde el panel rápido. |

## 🚀 Descarga y primeros pasos

**Versión actual: [v1.33](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.33)** · 2026-11-18 · [Notas de la versión (en chino)](docs/release-v1.33.md)

Elija **Full x64** para integración Android, pantallas virtuales y AirPlay, **Lite x64** para funciones de audio solo en PC o **Legacy x64** para .NET Framework 4.8. Las ediciones ARM64 son experimentales. La integración móvil y AirPlay solo está en Full. Microsoft Store ofrece Full y Lite; la disponibilidad actual se muestra en sus páginas de Store. Enlaces directos para v1.33.

| Edición | Descargar | Requisitos |
|---|---|---|
| ⭐ Full (.NET 10, con integración móvil opcional) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-arm64.exe) | Edición portable en un solo EXE; requiere .NET 10 Desktop Runtime adecuado, incluye integración Android, pantalla virtual y AirPlay. |
| ⭐ Lite (.NET 10, solo audio de PC) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-arm64.exe) | Edición portable de un solo archivo; requiere .NET 10 Desktop Runtime adecuado, solo funciones de audio para PC. |
| 🛡️ Legacy x64 (solo audio de PC, .NET Framework 4.8) | [ZIP completo](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Legacy-x64.zip) | Sin dependencias adicionales; utiliza .NET Framework 4.8 integrado, descomprimir y ejecutar. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Ediciones Full / Lite; requiere x64 .NET 10 Desktop Runtime; versión según Store. |

<details>
<summary><sub>📦 Ediciones anteriores compatibles con NET8 (descatalogadas, clic para desplegar)</sub></summary>

| Edición | Descargar | Requisitos |
|---|---|---|
| Lite NET8 (Descatalogado) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Descatalogado y sin mantenimiento; se recomienda migrar a .NET 10 o Legacy. |
</details>

> [!NOTE]
> Full y Lite requieren [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); Legacy utiliza el .NET Framework 4.8 integrado sin instalaciones adicionales (versiones históricas [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) descatalogadas).

### 🏁 Inicio rápido en 3 pasos

1. **Iniciar y anclar a la bandeja** : Haga doble clic para iniciar; SonicRoute permanece residente en la bandeja del sistema (arrastre el icono para mantenerlo visible). Un doble clic abre la ventana principal.
2. **Elegir dispositivos y enrutar** : Marque sus auriculares y altavoces en «Ajustes → Dispositivos conservados». Un clic izquierdo en la bandeja despliega el panel rápido para enrutar cada app y ajustar su volumen.
3. **Control a ciegas desde la bandeja** : Pase el cursor sobre el área de la bandeja y use la rueda para ajustar el volumen de la app, Alt+rueda para conmutar dispositivos y Shift+rueda para el volumen general, sin tener que salir del juego en pantalla completa.

### ⚡ Línea de comandos y automatización (Avanzado)

El comando `sr` se comunica mediante Named Pipe en milisegundos con la instancia activa de SonicRoute (no inicia la aplicación si está cerrada). Al iniciar la app por primera vez se registra el comando; reinicie el terminal para usarlo desde scripts, AutoHotkey o Stream Deck. Consulte [Preguntas frecuentes](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```bash
# Ejecutar regla de automatización (SonicRoute debe estar en ejecución)
sr "Nombre o ID de la regla"
```

### 🆕 Aspectos destacados de la versión v1.33

v1.33 añade a Full un receptor AirPlay propio en C# para audio y duplicación de pantalla proporcional (PCM/ALAC/AAC-LC/AAC-ELD, PIN opcional, siempre visible/pantalla completa F11 y enrutamiento por arrastre a salidas de PC o altavoces/Bluetooth Android), H.265 (HEVC) con selectores de decodificador de audio/vídeo y consulta de codificadores solo con la página visible, mandos limitados al primer plano, tarjeta unificada de QR (auto-renovación cada 2 min) y emparejamiento manual, búsqueda LAN y reconexión/actualización unificada desde panel rápido y barra lateral para dispositivos inalámbricos guardados (filtrado de puertos caducados, recuperación de IP/puerto actualizados por identidad guardada y conservación del borrador manual si falla) y restauración de ajustes; todas las ediciones (Full/Lite/Legacy) incorporan dos interruptores OSD de micrófono independientes, tarjeta de gestión de configuración en una fila, virtualización de listas (>20 filas), fusión OSD, liberación de bytes PNG y texturas en desuso, eliminación de solicitudes explícitas de GC al cerrar ventanas en Full (recolección natural del runtime), respaldo config.bak, diagnóstico bajo error, soporte Store Lite independiente y visor GPL-3.0-only sin conexión. Consulte [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

### ❓ Preguntas frecuentes (FAQ)

**Q: ¿Se mantienen el enrutamiento de aplicaciones y el volumen tras cerrar SonicRoute?**

A: Windows conserva las rutas por aplicación y las sesiones de audio existentes tras cerrar SonicRoute. Los atajos, los controles de bandeja, la automatización, el audio del teléfono, la duplicación de pantalla y la sincronización del portapapeles requieren que SonicRoute siga abierto.

**Q: ¿Qué runtime requiere cada edición?**

A: Full y Lite requieren el .NET 10 Desktop Runtime correspondiente; Legacy utiliza .NET Framework 4.8. El paquete Store requiere el runtime x64 de .NET 10. Las versiones NET8 ya no se mantienen, pero sus descargas históricas siguen disponibles.

**Q: ¿Consume CPU o batería mantener SonicRoute abierto en segundo plano?**

A: La detección prioriza los eventos del sistema y, cuando hace falta, usa una comprobación necesaria cada segundo. Los datos innecesarios se limpian bajo demanda; el uso real de CPU depende del sistema y de la actividad.

## 📚 Documentación

La Wiki está actualmente en chino simplificado.

| Tema | Guías |
|---|---|
| Instalación, paneles y controles habituales | [Guía de usuario](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Funciones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Teclas predeterminadas, asignaciones y objetivos de control | [Atajos de teclado](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Desencadenantes, acciones e integración con scripts | [Automatización](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Línea de comandos](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Instalación y solución de problemas | [Preguntas frecuentes](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Código fuente, compilaciones y versiones | [Implementación técnica](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Información de la versión](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **Resumen con IA y resolución de problemas con ahorro de tokens**: Al utilizar asistentes de IA (como Cursor, Copilot, ChatGPT o Claude) para diagnosticar fallos, comprender la arquitectura o crear scripts, se recomienda consultar [README.ai.md](README.ai.md). Diseñado en un DSL estructurado de alta densidad para LLM, evita textos extensos, reduce notablemente el gasto de tokens y localiza la causa raíz en cuestión de segundos.

## 📌 Hoja de ruta

- **Mejora del enrutamiento de audio (Route Enhancement)**: Plan para perfeccionar el motor de enrutamiento con transferencia dinámica de flujos, recuperación automática al conectar/desconectar dispositivos y mayor estabilidad en sesiones complejas.

[Comparte una idea o informa de un problema](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 📜 Licencia

- **Código abierto GPL-3.0**: SonicRoute se distribuye únicamente bajo la versión 3 de la Licencia Pública General de GNU (GPL-3.0-only); véase [LICENSE](LICENSE). Se permiten el uso comercial, la venta y la distribución de pago conforme a la GPL.
- **Redistribución**: Conserve los avisos necesarios de derechos de autor y licencia, indique los cambios y distribuya las obras modificadas cubiertas bajo GPL versión 3. Al distribuir binarios cubiertos, proporcione el código fuente correspondiente completo según la sección 6 de la GPL; véase [LICENSE](LICENSE).
- **Alcance y licencias anteriores**: Desde la migración GPL del 2026-11-17, todo el proyecto cubierto en esta revisión, incluido el código anterior, se ofrece bajo GPL-3.0-only en la medida en que el titular pueda autorizarlo. Los permisos MIT y de la anterior licencia personalizada siguen vigentes; no se cambia la licencia indicada en etiquetas ni binarios ya publicados. Los componentes de terceros conservan sus licencias (véase [avisos de terceros](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)).

---

<a href="EASTER_EGGS.md" title="Un huevo de Pascua: ayer y mañana"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Descubre un huevo de Pascua"></a>

Gracias por usar SonicRoute. [Leer las guías (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Informar de un problema](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Autor: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
