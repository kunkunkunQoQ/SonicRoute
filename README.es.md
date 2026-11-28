<!-- Generated from docs/readme/shared.json, docs/readme/locales/es-ES.json, and docs/readme/releases/v2.00.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute 2.00

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | **Español** | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Versión: v2.00](https://img.shields.io/static/v1?label=Versi%C3%B3n&message=v2.00&color=34A853&logo=github&logoColor=white)](docs/release-v2.00.md) [![Licencia: GPL-3.0-only](https://img.shields.io/static/v1?label=Licencia&message=GPL-3.0-only&color=blue)](LICENSE) [![Sistema: Windows 10 | 11](https://img.shields.io/static/v1?label=Sistema&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arquitectura: x64 | ARM64](https://img.shields.io/static/v1?label=Arquitectura&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Descargas](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=Descargas)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Apoyar en Afdian](https://img.shields.io/static/v1?label=&message=Apoyar+en+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envía el audio de cada aplicación al dispositivo que elijas.**

SonicRoute te permite controlar el audio de cada aplicación desde el área de notificación de Windows. Puedes escuchar música por los altavoces mientras el juego y el chat de voz suenan por los auriculares.

📥 [Ver versiones en GitHub](https://github.com/kunkunkunQoQ/SonicRoute/releases) · [Comparar ediciones](#downloads) · [Primeros pasos](#quick-start) · [Guía de usuario (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Interfaz en nueve idiomas · [GPL-3.0](LICENSE)

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas"><img src="https://raw.githubusercontent.com/wiki/kunkunkunQoQ/SonicRoute/images/routing-canvas-v2.00.png" width="820" alt="Ventana de SonicRoute 2.00 con una línea continua hasta la salida principal de una aplicación y una línea discontinua de reenvío hacia otro dispositivo."></a>

<sub>El espacio de enrutamiento de SonicRoute 2.00 muestra la salida principal de una aplicación y el reenvío hacia un segundo dispositivo.</sub>

<details>
<summary><sub>🖼️ Ejemplos de interfaz de la versión 1.36</sub></summary>

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="Una aplicación Android se arrastra al panel rápido del PC para elegir su salida de audio."></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Editor de reglas con un atajo de teclado como disparador y el menú de acciones abierto en la opción para establecer el dispositivo de reproducción predeterminado."></a>
</div>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panel rápido de SonicRoute: dispositivo de salida y controles de volumen por aplicación"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Panel de dispositivos Android con opciones de emparejamiento, aplicaciones ancladas y ajustes de audio de la pantalla virtual"></a>
</div>

</details>

<a name="downloads"></a>

## 🚀 Descarga y primeros pasos

**v2.00** · [Notas de la versión (en chino e inglés)](docs/release-v2.00.md) · [Ver versiones en GitHub](https://github.com/kunkunkunQoQ/SonicRoute/releases)

SonicRoute v2.00 tiene dos ediciones. Elige **Full** para usar el audio del PC, Android y AirPlay, o **Lite** para gestionar el audio del PC. Selecciona x64 o ARM64 según la arquitectura de tu equipo.

| Edición o canal | Descarga | Instalación y requisitos |
|---|---|---|
| ⭐ Full (.NET 10, con integración móvil opcional) | `SonicRoute-v2.00-x64.exe`<br>`SonicRoute-v2.00-arm64.exe` | EXE portátil con .NET 10 Native AOT integrado. No requiere instalar un entorno de ejecución aparte. |
| ⚡ Lite (.NET 10, solo audio de PC) | `SonicRoute-v2.00-Lite-x64.exe`<br>`SonicRoute-v2.00-Lite-arm64.exe` | EXE portátil con .NET 10 Native AOT integrado. No requiere instalar un entorno de ejecución aparte. |
| 🔄 **Microsoft Store** | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | La distribución y las actualizaciones se gestionan por separado en Microsoft Store. Consulta la versión disponible en la ficha de la tienda. |

📱 [Versiones de la aplicación Android](https://github.com/kunkunkunQoQ/SonicRoute-App/releases) · [Instrucciones de instalación](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/README.md#install)

> [!NOTE]
> La aplicación funciona a partir de Windows 10 versión 2004 y en Windows 11. Capturar el audio de un proceso para enviarlo a salidas adicionales requiere la compilación 20348 de Windows o posterior.

<a name="quick-start"></a>

### 🏁 Primeros pasos

1. **Abre SonicRoute.** Ejecuta el EXE portátil. Haz doble clic en el icono de SonicRoute del área de notificación para abrir la ventana principal.
2. **Elige la salida de una aplicación.** En la ventana principal, abre «Aplicaciones» y selecciona una aplicación. Arrastra el conector de su salida principal hasta el dispositivo que quieras usar. Para cambiar de salida rápidamente, haz clic en el icono del área de notificación y despliega la fila de la aplicación en el panel rápido.
3. **Prueba la rueda.** Gírala sobre el icono de SonicRoute para ajustar el volumen. Por defecto, entre las aplicaciones con una sesión de audio, SonicRoute da prioridad a la que está en primer plano o, en su defecto, a la última que lo estuvo. Con Alt cambias su salida y con Mayús ajustas el volumen del dispositivo de reproducción predeterminado de Windows.

## ✨ Qué ofrece SonicRoute

- **Elige en el panel rápido de qué aplicación capturar el audio.** Despliega la fila de la aplicación y haz clic en otro dispositivo de reproducción mientras pulsas `Ctrl` para que suene en ambos. Con Mayús+clic puedes añadir un rango de salidas. Activa «Mostrar micrófono en el panel rápido» para elegir también el micrófono de esa aplicación en la misma fila.
- **Guarda los ajustes de volumen de cada reenvío.** En el lienzo, arrastra la línea continua para cambiar la salida principal o pulsa «+» para añadir un reenvío. SonicRoute guarda el volumen, el estado de silencio y el límite de volumen para cada combinación de aplicación y dispositivo de salida. Haz clic derecho en el control de volumen del reenvío para fijar el límite entre el 1 % y el 300 %.
- **Ajusta el volumen sin salir del juego o de la llamada.** Los medidores del panel rápido muestran qué aplicaciones están sonando. Ajusta cada una con su control deslizante o usa la rueda sobre el icono de SonicRoute para regular la aplicación actual. Un atajo global permite silenciar o reactivar todos los micrófonos.
- **Automatiza los cambios de audio.** Combina 6 disparadores con 39 acciones para cambiar de dispositivo, ajustar el volumen o abrir un programa. También puedes ejecutar una regla guardada desde la línea de comandos con `sr`.
- **Conecta Android o recibe AirPlay (Full).** Empareja la aplicación SonicRoute en la misma red local y confirma que el código de ocho dígitos coincide en ambos dispositivos. Puedes transmitir audio en ambos sentidos y ver en el PC la pantalla que comparte el móvil. Para usar una pantalla virtual independiente con control de entrada, transferir archivos o sincronizar el portapapeles, usa una conexión ADB. Full también recibe audio y duplicación de pantalla de dispositivos Apple mediante AirPlay.

## 🎮 Para el día a día

Con una regla puedes seleccionar los auriculares y abrir el chat de voz al iniciar un juego. [Configurar automatización](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Situación | Cómo te ayuda SonicRoute |
|---|---|
| 🎮 **El chat de voz tapa el sonido del juego o apenas oyes a tus compañeros** | Ajusta el volumen de Discord por separado del juego. Si lo prefieres, envía cada uno a una salida distinta. |
| 🎵 **Escuchar música o ver vídeos mientras juegas** | Envía la música a los altavoces y el juego a los auriculares. Gira la rueda sobre el icono de SonicRoute para ajustar el volumen de la aplicación que estás controlando o mantén pulsada Alt para cambiar su salida. |
| ⚡ **Configurar los auriculares y abrir el chat en cada partida** | Al abrir el juego, una regla puede seleccionar los auriculares, poner el volumen al 40 % y abrir el chat de voz. Otra regla restaura los altavoces al salir del juego. |
| 🎙️ **Apartarte del PC o atender una llamada privada** | De forma predeterminada, Ctrl+Mayús+N silencia o reactiva todos los micrófonos. El aviso en pantalla te permite comprobar su estado. |
| 📹 **Emitir con OBS o grabar un tutorial** | Envía el juego y el chat de voz a salidas físicas o virtuales distintas. En OBS, selecciona la salida que quieres grabar. |
| 📱 **Escuchar el móvil en el PC o usar en él una aplicación Android** | Escucha el móvil en el PC o mira en él la pantalla compartida. Si quieres manejar una aplicación Android con ratón y teclado en una pantalla virtual independiente, conecta el móvil a Full mediante ADB. |

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

Los ejecutables portátiles de Full y Lite incluyen .NET 10 Native AOT. Elige x64 o ARM64 según la arquitectura de tu sistema Windows.

**¿Cuál es el consumo de procesador y batería en segundo plano?**

SonicRoute responde principalmente a eventos de Windows. Cuando es necesario, comprueba el estado una vez por segundo y libera los datos que ya no se utilizan. El uso de CPU depende del equipo y de las funciones que utilices.

**¿Cómo ajusto la latencia del audio que llega al PC desde Android?**

En Full, abre los ajustes del móvil conectado por ADB. En «Audio» puedes elegir «Equilibrado», «Latencia ultrabaja» o «Estable». Si oyes cortes, prueba «Estable». Para transmitir sin compresión, elige «Raw PCM» en lugar de Opus. Los cambios se aplican en la siguiente transmisión desde ese móvil.

## 🆕 Aspectos destacados de la versión v2.00

**Un nuevo comienzo**

Full y Lite usan .NET 10 Native AOT. Los ejecutables portátiles para x64 y ARM64 incluyen el entorno de ejecución. El nuevo espacio de enrutamiento muestra la salida principal y los reenvíos de cada aplicación. Al volver a conectar la misma aplicación al mismo dispositivo de salida, SonicRoute puede recuperar el volumen, el estado de silencio y el límite de volumen del reenvío. El límite se puede ajustar del 1 % al 300 %.

En Full, la página «Dispositivos» reúne Android y AirPlay. La aplicación SonicRoute se empareja en la red local con un código de ocho dígitos y permite transmitir audio en ambos sentidos y ver la pantalla del móvil en el PC. La conexión ADB permite abrir pantallas virtuales independientes, enviar entradas, transferir archivos y sincronizar el portapapeles. Consulta el [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) para ver otras versiones.

<details>
<summary>v1.36</summary>

La selección de salidas se sincroniza entre el panel rápido y «Aplicaciones». Mayús+clic amplía la selección sin perder lo que ya habías elegido. Para una aplicación del PC, Ctrl+Mayús+clic cambia la salida principal. Los botones distinguen la salida por la que Windows reproduce el sonido de una aplicación de las salidas adicionales a las que SonicRoute envía ese mismo audio. Ahora puedes plegar la barra lateral de la ventana principal y SonicRoute recuerda cómo la dejaste.

En Full puedes escuchar el audio recibido por AirPlay en varios dispositivos a la vez. Para transmitir el sonido de una aplicación Android se necesita Android 11 o posterior. La compatibilidad también depende del móvil y de la aplicación. Ahora puedes elegir PCM sin comprimir y ajustar la latencia de cada móvil. Si el codificador lo admite, la pantalla virtual adapta la tasa de bits del vídeo a la conexión. Las ediciones de Microsoft Store mantienen la ventana agrupada con su icono anclado en la barra de tareas. El paquete Full incluye reglas de firewall para recibir AirPlay en redes privadas y de dominio. Consulta [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) para ver las versiones anteriores.

</details>

## 📚 Documentación

La documentación de la wiki está en chino simplificado.

| Tema | Guías |
|---|---|
| Instalación, paneles y controles habituales | [Guía de uso (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Funciones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) · [Conexión de dispositivos](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94) |
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
