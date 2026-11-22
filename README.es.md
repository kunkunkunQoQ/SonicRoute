<!-- Generated from docs/readme/shared.json, docs/readme/locales/es-ES.json, and docs/readme/releases/v1.35.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | **Español** | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Versión estable](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=Versi%C3%B3n%20estable)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Licencia: GPL-3.0](https://img.shields.io/static/v1?label=Licencia&message=GPL-3.0&color=blue)](LICENSE) [![Sistema: Windows 10 | 11](https://img.shields.io/static/v1?label=Sistema&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arquitectura: x64 | ARM64](https://img.shields.io/static/v1?label=Arquitectura&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Descargas](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=Descargas)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Apoyar en Afdian](https://img.shields.io/static/v1?label=&message=Apoyar+en+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envía el audio de cada aplicación al dispositivo que elijas.**

Escucha la música por los altavoces y reserva los auriculares para los juegos y las llamadas. SonicRoute te permite elegir en Windows la entrada y la salida de audio de cada aplicación. En el panel rápido, usa Ctrl+clic para añadir o quitar dispositivos de reproducción y Mayús+clic para seleccionar un intervalo continuo; combina varias salidas del PC en cualquier edición o suma las salidas de audio del teléfono disponibles en Full. Controla el sonido con atajos de teclado, desde el panel rápido o con la rueda del ratón sobre el icono de SonicRoute en el área de notificación. Puedes combinar tus ajustes habituales en reglas de automatización.

📥 [Descargar la última versión](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [Guía de usuario (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Interfaz en nueve idiomas · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Una aplicación Android se arrastra al panel rápido del PC para elegir su salida de audio."></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Editor de reglas con un atajo de teclado como disparador y el menú de acciones abierto en la opción para establecer el dispositivo de reproducción predeterminado."></a>
</div>

<sub>A la izquierda se muestra cómo enviar el sonido de una aplicación Android al PC arrastrándola y cómo cambiar su salida. A la derecha está abierto el menú de acciones de una regla que se ejecuta con un atajo de teclado.</sub>

<details>
<summary><sub>🖼️ Más capturas del panel rápido y de la gestión de móviles</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panel rápido de SonicRoute: dispositivo de salida y controles de volumen por aplicación"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Panel de dispositivos Android con opciones de emparejamiento, aplicaciones ancladas y ajustes de audio de la pantalla virtual"></a>
</div>

</details>

## ✨ Qué ofrece SonicRoute

- **Una entrada y una salida para cada aplicación.** Escucha la música por los altavoces y el juego por los auriculares. Para el chat de voz también puedes elegir el micrófono. En el panel rápido, usa Ctrl+clic para añadir o quitar dispositivos de reproducción y Mayús+clic para seleccionar un intervalo continuo; todas las ediciones pueden combinar varias salidas del PC, y Full permite añadir además las salidas disponibles del teléfono. Windows conserva estas asignaciones al cerrar SonicRoute.
- **Control desde el área de notificación.** SonicRoute detecta los cambios de aplicación en primer plano mediante los eventos de Windows. Gira la rueda sobre el icono de SonicRoute para ajustar el volumen de la aplicación que estás controlando o mantén pulsada Alt al girarla para cambiar su salida. Un aviso en pantalla (OSD) muestra los cambios. También puedes silenciar todos los micrófonos con un atajo.
- **Panel rápido con medidores de nivel.** Cada aplicación tiene su propio control de volumen y un medidor que muestra el nivel de audio en tiempo real. El panel recuerda su posición y permite ajustar la altura entre 350 y 850 píxeles lógicos.
- **Android y AirPlay en la edición Full.** Escucha el audio de Android en el PC o el del PC en el móvil. Puedes usar las aplicaciones Android en pantallas virtuales, con vídeo H.265 y control con mando en la ventana activa. El receptor AirPlay escrito en C# también recibe audio y duplica la pantalla manteniendo la relación de aspecto. En el panel rápido puedes enviar el sonido a una salida del PC, al altavoz de un Android conectado o a su salida Bluetooth. El volumen inteligente (SmartVolume) atenúa las diferencias de nivel.
- **6 disparadores y 39 acciones.** Ejecuta una regla con un atajo, a una hora programada, al iniciar SonicRoute o cuando una aplicación se abre, pasa a primer plano o se cierra. Combina cambios de dispositivo, ajustes de volumen, silenciamiento, apertura de programas y scripts de PowerShell. Con `sr` puedes ejecutar reglas desde un script o un Stream Deck.
- **Detección mediante eventos.** SonicRoute utiliza las notificaciones de Windows y, cuando es necesario, comprueba el estado una vez por segundo. Los datos que ya no hacen falta se liberan cuando es necesario. Lite y Legacy incluyen las funciones del PC. Full añade Android y AirPlay. La interfaz está disponible en nueve idiomas.

## 🎮 Para el día a día

Con una regla puedes seleccionar los auriculares y abrir el chat de voz al iniciar un juego. [Configurar automatización](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Situación | Cómo te ayuda SonicRoute |
|---|---|
| 🎮 **El chat de voz tapa el sonido del juego o apenas oyes a tus compañeros** | Ajusta el volumen de Discord por separado del juego. Si lo prefieres, envía cada uno a una salida distinta. |
| 🎵 **Escuchar música o ver vídeos mientras juegas** | Envía la música a los altavoces y el juego a los auriculares. Gira la rueda sobre el icono de SonicRoute para ajustar el volumen de la aplicación que estás controlando o mantén pulsada Alt para cambiar su salida. |
| ⚡ **Configurar los auriculares y abrir el chat en cada partida** | Al abrir el juego, una regla puede seleccionar los auriculares, poner el volumen al 40 % y abrir el chat de voz. Otra regla restaura los altavoces al salir del juego. |
| 🎙️ **Apartarte del PC o atender una llamada privada** | De forma predeterminada, Ctrl+Mayús+N silencia o reactiva todos los micrófonos. El aviso en pantalla te permite comprobar su estado. |
| 📹 **Emitir con OBS o grabar un tutorial** | Envía el juego y el chat de voz a salidas físicas o virtuales distintas. En OBS, selecciona la salida que quieres grabar. |
| 📱 **Escuchar el móvil en el PC o usar en él una aplicación Android** | Escucha el audio de Android en el PC y usa sus aplicaciones en una pantalla virtual. Con AirPlay puedes recibir el sonido y la imagen de un dispositivo Apple. Para el audio, elige en el panel rápido una salida del PC, el altavoz del móvil Android o su salida Bluetooth. |

## 🚀 Descarga y primeros pasos

**Versión estable: [v1.35](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.35)** · 21 de noviembre de 2026 · [Notas de la versión (en chino)](docs/release-v1.35.md)

Elige **Full x64** para usar audio de Android, pantallas virtuales y AirPlay, o **Lite x64** para gestionar solo el audio del PC. **Legacy x64** está pensada para equipos con .NET Framework 4.8. Las ediciones ARM64 son experimentales. Full y Lite también están disponibles en Microsoft Store. Los enlaces de descarga directa corresponden a v1.35.

| Edición | Descarga | Requisitos |
|---|---|---|
| ⭐ Full (.NET 10, con integración móvil opcional) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-arm64.exe) | Un único ejecutable portátil para el audio del PC, el audio de Android, las pantallas virtuales y AirPlay. Requiere .NET 10 Desktop Runtime para la arquitectura elegida. |
| ⭐ Lite (.NET 10, solo audio de PC) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-arm64.exe) | Un único ejecutable portátil para el audio del PC. Requiere .NET 10 Desktop Runtime para la arquitectura elegida. |
| 🛡️ Legacy x64 (solo audio de PC, .NET Framework 4.8) | [ZIP completo](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Legacy-x64.zip) | Descomprime el archivo y ejecuta SonicRoute. Esta edición utiliza .NET Framework 4.8, incluido en Windows. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Las ediciones Full y Lite requieren .NET 10 Desktop Runtime para x64. Consulta la ficha de cada edición para ver qué versión está disponible. |

<details>
<summary><sub>📦 Ediciones anteriores para .NET 8 (sin mantenimiento)</sub></summary>

| Edición | Descarga | Requisitos |
|---|---|---|
| Lite NET8 (sin mantenimiento) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Esta edición ha dejado de recibir actualizaciones. Para instalaciones nuevas se recomienda una edición .NET 10 o Legacy. |
</details>

> [!NOTE]
> Full y Lite requieren [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) para la arquitectura elegida. Legacy utiliza .NET Framework 4.8, incluido en Windows. Las ediciones antiguas para [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) siguen disponibles para su descarga, aunque han dejado de recibir actualizaciones.

### 🏁 Primeros pasos

1. **Inicia SonicRoute.** Haz doble clic en el ejecutable. Verás el icono de SonicRoute en el área de notificación. Para que esté siempre a la vista, arrástralo fuera de la lista de iconos ocultos. Haz doble clic en el icono para abrir la ventana principal.
2. **Elige los dispositivos.** En los ajustes, marca los dispositivos que quieres que aparezcan en el panel rápido. Haz clic en el icono de SonicRoute para abrirlo. Despliega la fila de una aplicación para elegir su salida. Usa el control deslizante para ajustar su volumen.
3. **Controla el sonido con la rueda.** Sitúa el puntero sobre el icono de SonicRoute y gira la rueda para ajustar el volumen de la aplicación que estás controlando. Mantén pulsada Alt para cambiar su salida o Mayús para ajustar el volumen del sistema. En los ajustes puedes ampliar este control a toda el área de notificación.

### ⚡ Ejecutar reglas desde la línea de comandos

La primera vez que inicias SonicRoute de la forma habitual, se registra el comando `sr` para tu cuenta. Cierra el terminal y vuelve a abrirlo. El comando se comunica mediante una canalización con nombre con la instancia de SonicRoute que ya está en ejecución. Así puedes ejecutar tus reglas desde un script, AutoHotkey o un Stream Deck. Encontrarás más información en [Preguntas frecuentes](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```powershell
# Ejecutar una regla de automatización (SonicRoute debe estar en ejecución)
sr "Nombre o ID de la regla"
```

### 🆕 Aspectos destacados de la versión v1.35

Todas las ediciones unifican ahora el cambio inmediato en el panel rápido: las entradas de micrófono de las aplicaciones del PC y las filas de audio externo se resaltan al instante y se aplican en cola sin desactivar los botones, mientras el título de la fila actualiza de inmediato el nombre de la salida elegida. Los atajos de cambio de dispositivo se ejecutan en una cola sin bloqueos: si el cambio termina en menos de 45 ms muestra el resultado final sin parpadeos, y si tarda más mantiene el aviso localizado durante al menos 500 ms. También se conservan los espejos multisalida elegidos explícitamente cuando una sesión de audio nativa cambia entre salidas seleccionadas.

La edición Full mantiene la capacidad de respuesta al cambiar de modo en Android y en pantallas virtuales, y corrige condiciones de carrera y liberación de recursos en la sincronización del portapapeles entre varios dispositivos. Consulta [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) para ver las versiones anteriores.

<details>
<summary>v1.34</summary>

Todas las ediciones permiten seleccionar varios dispositivos de reproducción del PC para cada aplicación. Tras reiniciar SonicRoute, se recupera la selección cuando la aplicación vuelve a reproducir audio. El control de volumen desde la bandeja del sistema y los atajos del ratón comparten un único mecanismo de detección de eventos del ratón a nivel del sistema.

En la edición Full, los dos paneles de Android muestran el mismo estado de conexión USB y Wi-Fi. Al actualizar el panel rápido del teléfono, también se puede volver a conectar el dispositivo seleccionado. El menú de salida de las aplicaciones de Android muestra ahora el nombre personalizado de la salida de audio que usa el teléfono. Consulta [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) para ver las versiones anteriores.

</details>

### ❓ Preguntas frecuentes

**¿Qué ajustes se conservan al salir de SonicRoute?**

Windows conserva los dispositivos asignados a cada aplicación y el volumen de las sesiones de audio existentes. Los atajos, los controles del área de notificación, la automatización, el audio del móvil, la duplicación de pantalla y la sincronización del portapapeles requieren que SonicRoute siga en ejecución.

**¿Qué entorno de ejecución necesita cada edición?**

Full y Lite necesitan .NET 10 Desktop Runtime para su arquitectura. Las ediciones de Microsoft Store requieren la versión x64. Legacy utiliza .NET Framework 4.8. Las ediciones antiguas para .NET 8 siguen disponibles, aunque han dejado de recibir actualizaciones.

**¿Cuál es el consumo de procesador y batería en segundo plano?**

SonicRoute responde principalmente a eventos de Windows. Cuando es necesario, comprueba el estado una vez por segundo y libera los datos que ya no se utilizan. El uso de CPU depende del equipo y de las funciones que utilices.

## 📚 Documentación

La documentación de la wiki está en chino simplificado.

| Tema | Guías |
|---|---|
| Instalación, paneles y controles habituales | [Guía de uso (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Funciones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Atajos predeterminados, asignación de teclas y elementos que controlan | [Atajos de teclado](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Disparadores, acciones y scripts | [Automatización](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Línea de comandos](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Instalación y solución de problemas | [Preguntas frecuentes](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Código fuente, compilaciones y versiones | [Implementación técnica](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Información de la versión](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **Referencia para asistentes de IA**: [README.ai.md](README.ai.md) reúne en un formato estructurado la arquitectura, los comandos y las pautas de diagnóstico. Puedes usarlo como referencia con Cursor, Copilot, ChatGPT o Claude para investigar un fallo o escribir un script.

## 📌 Hoja de ruta

- **Cambio de salida de audio.** Está previsto facilitar el paso de una salida a otra, recuperar la asignación de dispositivos cuando se conecten o desconecten y mejorar la estabilidad en aplicaciones con varias sesiones de audio.

[Comparte una idea o informa de un problema](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 💖 Apoya SonicRoute

SonicRoute es un proyecto gratuito y de código abierto que seguimos desarrollando y manteniendo. Si te ayuda a gestionar el sonido de tu PC, puedes apoyarlo a través de Afdian. Tu aportación nos ayuda a desarrollar nuevas funciones, mejorar la compatibilidad y mantener el proyecto a largo plazo.

☕ [Apoyar en Afdian](https://afdian.com/a/koukou021)

Las aportaciones son voluntarias. Puedes usar todas las funciones sin hacer una donación. También ayudas cuando informas de un problema, recomiendas SonicRoute o le das una estrella en GitHub. ¡Gracias!

## 📜 Licencia

- **Licencia.** SonicRoute se distribuye bajo la versión 3, exclusivamente, de la Licencia Pública General de GNU (GPL-3.0-only). La licencia permite el uso comercial, la venta y la distribución de pago con arreglo a sus condiciones. Consulta [LICENSE](LICENSE).
- **Redistribución.** Conserva los avisos obligatorios de derechos de autor y de licencia, señala los cambios y distribuye las obras modificadas cubiertas por la licencia bajo la versión 3 de la GPL. Si distribuyes binarios cubiertos, debes proporcionar el código fuente correspondiente completo en las condiciones de la sección 6 de la GPL. Consulta [LICENSE](LICENSE).
- **Alcance y licencias anteriores.** Desde el 17 de noviembre de 2026, la GPL-3.0-only se aplica a todo el proyecto cubierto en esta revisión, incluido el código anterior, en la medida en que el titular de los derechos pueda otorgar esa licencia. Los permisos ya concedidos bajo la licencia MIT y la anterior licencia propia del proyecto siguen vigentes. Las etiquetas y los binarios ya publicados conservan su indicación de licencia original. Los componentes de terceros conservan sus propias licencias (véanse los [avisos de terceros](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)).

---

<a href="EASTER_EGGS.md" title="Un huevo de Pascua: ayer y mañana"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Haz clic en la mascota para descubrir una sorpresa."></a>

Gracias por usar SonicRoute. [Leer las guías (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Informar de un problema](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Autor: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 Referencia para asistentes de IA</a></sub></div>
