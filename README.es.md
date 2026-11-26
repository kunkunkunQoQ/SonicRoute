<!-- Generated from docs/readme/shared.json, docs/readme/locales/es-ES.json, and docs/readme/releases/v1.36.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | **Español** | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Versión estable](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=Versi%C3%B3n%20estable)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Licencia: GPL-3.0-only](https://img.shields.io/static/v1?label=Licencia&message=GPL-3.0-only&color=blue)](LICENSE) [![Sistema: Windows 10 | 11](https://img.shields.io/static/v1?label=Sistema&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arquitectura: x64 | ARM64](https://img.shields.io/static/v1?label=Arquitectura&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Descargas](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=Descargas)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Apoyar en Afdian](https://img.shields.io/static/v1?label=&message=Apoyar+en+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envía el audio de cada aplicación al dispositivo que elijas.**

SonicRoute te permite controlar el audio de cada aplicación desde el área de notificación de Windows. Puedes escuchar música por los altavoces mientras el juego y el chat de voz suenan por los auriculares.

📥 [Descargar la última versión](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Comparar ediciones](#downloads) · [Primeros pasos](#quick-start) · [Guía de usuario (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

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

<a name="downloads"></a>

## 🚀 Descarga y primeros pasos

**Versión estable: [v1.36](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.36)** · 24 de noviembre de 2026 · [Notas de la versión (en chino e inglés)](docs/release-v1.36.md)

Para v1.36, elige **Full** si quieres usar Android o AirPlay y **Lite** si solo necesitas el audio del PC. **Legacy** ofrece las funciones de audio del PC en equipos x64 con .NET Framework 4.8.

| Edición | Descarga | Requisitos |
|---|---|---|
| ⭐ Full (.NET 10, con integración móvil opcional) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-arm64.exe) | EXE portátil. Requiere .NET 10 Desktop Runtime de la misma arquitectura que el programa. |
| ⚡ Lite (.NET 10, solo audio de PC) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-arm64.exe) | EXE portátil. Requiere .NET 10 Desktop Runtime de la misma arquitectura que el programa. |
| 🛡️ Legacy x64 (solo audio de PC, .NET Framework 4.8) | [ZIP completo](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Legacy-x64.zip) | Extrae todo el ZIP antes de ejecutar el programa. Requiere .NET Framework 4.8. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full y Lite requieren .NET 10 Desktop Runtime para x64. La ficha de cada edición indica la versión disponible. |

<details>
<summary><sub>📦 Ediciones anteriores para .NET 8 (sin mantenimiento)</sub></summary>

| Edición | Descarga | Requisitos |
|---|---|---|
| Lite NET8 (sin mantenimiento) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Esta edición ha dejado de recibir actualizaciones. Para instalaciones nuevas se recomienda una edición .NET 10 o Legacy. |

</details>

> [!NOTE]
> Full y Lite utilizan [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) para la arquitectura elegida. Legacy requiere .NET Framework 4.8. Las antiguas ediciones para [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) siguen disponibles y ya no reciben actualizaciones.

<a name="quick-start"></a>

### 🏁 Primeros pasos

1. **Abre SonicRoute.** Ejecuta el EXE descargado. Si usas Legacy, extrae primero todo el ZIP. Haz doble clic en el icono de SonicRoute del área de notificación para abrir la ventana principal.
2. **Elige una salida.** En los ajustes, marca los dispositivos que quieres ver en el panel rápido. Haz clic una vez en el icono de SonicRoute, despliega la fila de una aplicación y elige dónde reproducir su audio.
3. **Prueba la rueda.** Gírala sobre el icono de SonicRoute para ajustar el volumen. Por defecto, entre las aplicaciones con una sesión de audio, SonicRoute da prioridad a la que está en primer plano o, en su defecto, a la última que lo estuvo. Con Alt cambias su salida y con Mayús ajustas el volumen del dispositivo de reproducción predeterminado de Windows.

## ✨ Qué ofrece SonicRoute

- **Elige los dispositivos de reproducción y grabación de cada aplicación.** En el panel rápido o en «Aplicaciones», haz clic en una salida para usarla sola. Con Ctrl añades o quitas una salida y con Mayús añades varias consecutivas a la selección. Para una aplicación del PC, usa Ctrl+Mayús+clic en la salida del PC por la que Windows debe reproducir el sonido directamente. SonicRoute sigue enviando el audio a las demás salidas seleccionadas. En Full también puedes añadir salidas de un móvil Android conectado.
- **Ajusta el volumen sin salir del juego o de la llamada.** Los medidores del panel rápido muestran qué aplicaciones están sonando. Ajusta cada una con su control deslizante o usa la rueda sobre el icono de SonicRoute para regular la aplicación actual. Un atajo global permite silenciar o reactivar todos los micrófonos.
- **Automatiza los cambios de audio.** Combina 6 disparadores con 39 acciones para cambiar de dispositivo, ajustar el volumen o abrir un programa. También puedes ejecutar una regla guardada desde la línea de comandos con `sr`.
- **Usa Android y AirPlay desde el PC (Full).** Escucha el sonido de una aplicación Android en el PC o envía el del PC al altavoz del móvil o a un dispositivo Bluetooth conectado a él. Abre aplicaciones Android en pantallas virtuales con vídeo H.265 en equipos compatibles. Por AirPlay puedes duplicar la pantalla de un dispositivo Apple conservando sus proporciones y reproducir el audio que recibas por varias salidas del PC y de móviles Android a la vez. También puedes explorar el almacenamiento compartido del móvil y transferir varios archivos en ambos sentidos.

## 🎮 Para el día a día

Con una regla puedes seleccionar los auriculares y abrir el chat de voz al iniciar un juego. [Configurar automatización](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Situación | Cómo te ayuda SonicRoute |
|---|---|
| 🎮 **El chat de voz tapa el sonido del juego o apenas oyes a tus compañeros** | Ajusta el volumen de Discord por separado del juego. Si lo prefieres, envía cada uno a una salida distinta. |
| 🎵 **Escuchar música o ver vídeos mientras juegas** | Envía la música a los altavoces y el juego a los auriculares. Gira la rueda sobre el icono de SonicRoute para ajustar el volumen de la aplicación que estás controlando o mantén pulsada Alt para cambiar su salida. |
| ⚡ **Configurar los auriculares y abrir el chat en cada partida** | Al abrir el juego, una regla puede seleccionar los auriculares, poner el volumen al 40 % y abrir el chat de voz. Otra regla restaura los altavoces al salir del juego. |
| 🎙️ **Apartarte del PC o atender una llamada privada** | De forma predeterminada, Ctrl+Mayús+N silencia o reactiva todos los micrófonos. El aviso en pantalla te permite comprobar su estado. |
| 📹 **Emitir con OBS o grabar un tutorial** | Envía el juego y el chat de voz a salidas físicas o virtuales distintas. En OBS, selecciona la salida que quieres grabar. |
| 📱 **Escuchar el móvil en el PC o usar en él una aplicación Android** | Abre un juego de Android en una pantalla virtual y escúchalo por los altavoces del PC. El mando solo controla el juego mientras esa ventana tiene el foco. La opción «Volumen inteligente» suaviza los cambios bruscos de volumen. |

### ⚡ Ejecutar reglas desde la línea de comandos

La primera vez que inicias SonicRoute de la forma habitual, se registra el comando `sr` para tu cuenta. Cierra el terminal y vuelve a abrirlo. El comando se comunica mediante una canalización con nombre con la instancia de SonicRoute que ya está en ejecución. Así puedes ejecutar tus reglas desde un script, AutoHotkey o un Stream Deck. Encontrarás más información en [Preguntas frecuentes](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```powershell
# Ejecutar una regla de automatización (SonicRoute debe estar en ejecución)
sr "Nombre o ID de la regla"
```

## ❓ Preguntas frecuentes

**¿Qué ajustes se conservan al salir de SonicRoute?**

Windows conserva la salida principal y el dispositivo de grabación que elijas para cada aplicación, así como el volumen de las sesiones de audio existentes. Para reproducir el sonido por varias salidas a la vez, usar los atajos, los controles del área de notificación o la automatización, transmitir audio al móvil o desde él, duplicar la pantalla o sincronizar el portapapeles, SonicRoute debe seguir en ejecución.

**¿Qué entorno de ejecución necesita cada edición?**

Full y Lite necesitan .NET 10 Desktop Runtime para su arquitectura. Las ediciones de Microsoft Store requieren la versión x64. Legacy utiliza .NET Framework 4.8. Las ediciones antiguas para .NET 8 siguen disponibles, aunque han dejado de recibir actualizaciones.

**¿Cuál es el consumo de procesador y batería en segundo plano?**

SonicRoute responde principalmente a eventos de Windows. Cuando es necesario, comprueba el estado una vez por segundo y libera los datos que ya no se utilizan. El uso de CPU depende del equipo y de las funciones que utilices.

**¿Cómo ajusto la latencia del audio que llega al PC desde Android?**

En Full, abre la página del móvil. El modo de latencia de audio predeterminado es «Equilibrado». Prueba «Latencia ultrabaja» para reducir el retardo o «Estable» si oyes cortes. El móvil transmite el audio en Opus de forma predeterminada. Para enviarlo sin comprimir, elige «Raw PCM» en la lista de decodificadores. Los cambios se aplican la próxima vez que transmitas audio desde ese móvil.

## 🆕 Aspectos destacados de la versión v1.36

La selección de salidas se sincroniza entre el panel rápido y «Aplicaciones». Mayús+clic amplía la selección sin perder lo que ya habías elegido. Para una aplicación del PC, Ctrl+Mayús+clic cambia la salida principal. Los botones distinguen la salida por la que Windows reproduce el sonido de una aplicación de las salidas adicionales a las que SonicRoute envía ese mismo audio. Ahora puedes plegar la barra lateral de la ventana principal y SonicRoute recuerda cómo la dejaste.

En Full puedes escuchar el audio recibido por AirPlay en varios dispositivos a la vez. Para transmitir el sonido de una aplicación Android se necesita Android 11 o posterior. La compatibilidad también depende del móvil y de la aplicación. Ahora puedes elegir PCM sin comprimir y ajustar la latencia de cada móvil. Si el codificador lo admite, la pantalla virtual adapta la tasa de bits del vídeo a la conexión. Las ediciones de Microsoft Store mantienen la ventana agrupada con su icono anclado en la barra de tareas. El paquete Full incluye reglas de firewall para recibir AirPlay en redes privadas y de dominio. Consulta [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) para ver las versiones anteriores.

<details>
<summary>v1.35</summary>

Al elegir un micrófono para una aplicación del PC en el panel rápido, el dispositivo se resalta al instante. Puedes seguir haciendo clic mientras SonicRoute aplica los cambios en orden. En la edición Full ocurre lo mismo con las fuentes de audio externas que solo admiten una salida. Al elegir una salida, su nombre aparece de inmediato en la fila de la aplicación. Los atajos para cambiar de dispositivo se pueden pulsar varias veces seguidas. Si usas el atajo para cambiar la salida de la aplicación actual y el cambio tarda como máximo 45 ms, solo verás el resultado. Si aparece un aviso de cambio en curso, permanece visible al menos 500 ms antes de confirmar que el cambio se ha realizado. Los errores se muestran al instante. Si Windows mueve la sesión de audio de una aplicación a otra de las salidas seleccionadas, el sonido sigue reproduciéndose en las demás salidas elegidas.

En la edición Full, puedes cambiar de salida mientras Android cambia de modo de captura de audio y seguir usando la pantalla virtual. Si desactivas la sincronización del portapapeles para un teléfono, se cancelan sus escrituras pendientes en el portapapeles del PC. Los demás teléfonos siguen sincronizándose. Si falla una escritura, SonicRoute libera la memoria reservada para ella. Consulta [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) para ver los cambios de versiones anteriores.

</details>

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
