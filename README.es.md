<!-- Generated from docs/readme/shared.json and docs/readme/locales/es-ES.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | **Español** | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Apoyar en Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Envía el audio de cada aplicación al dispositivo que elijas.**

Escucha música por los altavoces y juegos o llamadas con los auriculares. SonicRoute es una utilidad de audio para Windows que permanece en el área de notificación: elige los dispositivos de salida y entrada de cada aplicación, controla el audio con atajos, la rueda del área de notificación o un panel, y combina acciones habituales en reglas de automatización.

📥 [Descargar la última versión](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [Guía de usuario](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Nueve idiomas de interfaz · Licencia MIT

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="Guía de usuario (en chino)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="Haz clic en la mascota para abrir la guía de usuario"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Panel rápido de SonicRoute: dispositivo de salida y controles de volumen por aplicación"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="Editor de reglas de automatización de SonicRoute: selección de disparadores y acciones de audio"></a>
</div>

<sub>Vista previa de la interfaz: panel rápido (altura adaptable y estilo acrílico) para volumen y enrutamiento inmediato; editor de automatización para combinar disparadores y acciones.</sub>

## ✨ Qué ofrece SonicRoute

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="Funciones"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="Haz clic en la mascota para descubrir las funciones de SonicRoute"></a>

- **Enrutamiento bidireccional y separación de audio por app (reproducción + grabación):** asigne dispositivos de salida y entrada específicos a cada aplicación (juegos y voz a auriculares, música a altavoces). La configuración se guarda de forma nativa en Windows incluso al cerrar el programa.
- **Seguimiento inteligente del primer plano y control directo desde la bandeja:** detecta al instante la aplicación activa mediante eventos del sistema. Ajuste el volumen desplazando la rueda sobre la bandeja del sistema, con notificaciones OSD y silencio global de micrófono.
- **Panel rápido e indicador de nivel de sonido en tiempo real:** el panel de tarjetas ofrece una altura ajustable de 350 a 850 px y guarda su posición; controle el volumen de cada aplicación con medidores de sonido en directo.
- **Motor de automatización y CLI ultrarrápida (6 disparadores × 37 acciones):** active reglas al iniciar, cambiar o cerrar aplicaciones, por temporizador o atajos. Encadene dispositivos, volumen, scripts PowerShell y use el comando sr integrado para enlazar con scripts o consolas.
- **Detección basada primero en eventos:** utiliza notificaciones del sistema y, cuando hace falta, una comprobación necesaria cada segundo; limpia los datos innecesarios bajo demanda. Disponible como Legacy (.NET 4.8), Lite para .NET 10 y NET8, y edición Store en 9 idiomas.

## 🎮 Para el día a día

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Automatización"><img src="docs/images/mascot-automation-es.png" width="48" align="middle" alt="Haz clic en el pequeño robot para conocer las reglas de automatización"></a> Convierta los tediosos ajustes manuales en una experiencia automatizada y lista para usar. [Configurar automatización](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Escenario cotidiano | Cómo le ayuda SonicRoute |
|---|---|
| 🎮 **Chat de voz muy alto o el juego tapa la voz de los compañeros** | Ajuste el volumen de Discord sin alterar el juego; mantenga el juego en auriculares y el chat en el microauricular |
| 🎵 **Escuchar música o ver vídeos mientras juegas en segundo plano** | Música a los altavoces y juego a los auriculares; ajuste el volumen con la rueda en la bandeja sin salir del juego |
| ⚡ **Cambiar cables de auriculares y ajustar Windows cada vez que juegas** | Regla rápida: abrir juego → pasar a auriculares + volumen 40 % + abrir chat; volver a altavoces al salir |
| 🎙️ **Alejarse del equipo o atender una llamada privada al instante** | Silencie todos los micrófonos con Ctrl+Mayús+N y confirme el estado de inmediato con el aviso OSD en pantalla |
| 📹 **Transmisión con OBS o grabación de tutoriales de pantalla** | Asigne pistas de audio independientes a cada app para que las charlas privadas nunca aparezcan en la grabación |

## 🚀 Descarga y primeros pasos

**Versión actual: [v1.27](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.27)** · 2026-10-24 · [Notas de la versión (en chino)](docs/release-v1.27.md)

En Windows x64, elige Lite con .NET 10, Lite compatible con NET8 o Legacy con .NET Framework 4.8. La página de Store muestra la versión disponible allí y las actualizaciones se gestionan desde Store. Los enlaces directos siguientes corresponden a v1.27.

| Edición | Descargar | Requisitos |
|---|---|---|
| ⭐ Lite x64 (.NET 10, estándar) | [Un solo EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-x64.exe) | Ejecutable portátil de un solo archivo; requiere .NET 10 Desktop Runtime x64 |
| ⚡ Lite ARM64 (.NET 10) (experimental) | [Un solo EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-arm64.exe) | Ejecutable portátil ARM64; requiere .NET 10 Desktop Runtime ARM64 |
| ☕ Lite NET8 x64 | [Un solo EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-NET8-x64.exe) | Lite compatible con NET8; requiere .NET 8 Desktop Runtime x64 |
| ☕ Lite NET8 ARM64 (experimental) | [Un solo EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Lite-NET8-arm64.exe) | Lite compatible con NET8; requiere .NET 8 Desktop Runtime ARM64 |
| 🛡️ Legacy x64 (Sin .NET 8) | [ZIP completo](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.27/SonicRoute-v1.27-Legacy-x64.zip) | No requiere .NET 8; funciona directamente con el .NET Framework 4.8 integrado de Windows; extraiga el ZIP |
| 🔄 Microsoft Store (actualizaciones de Store) | [Instalar desde Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | El paquete de Store con .NET 10 requiere .NET 10 Desktop Runtime x64. Consulta la página de Store para ver la versión disponible actualmente; para Windows 10/11 de 64 bits. |

Lite estándar requiere [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); Lite compatible con NET8 requiere [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). La edición NET8 dejará de ofrecerse y mantenerse después de noviembre de 2026. Desde entonces, en x64 se podrá elegir manualmente .NET 10 o Legacy (.NET Framework 4.8); en ARM64, .NET 10.

1. **Iniciar y mantener en bandeja**: Haz doble clic para iniciar; permanece en la bandeja del sistema. Doble clic en el icono para abrir la ventana principal.
2. **Seleccionar y enrutar dispositivos**: Marca tus auriculares y altavoces en Configuración → Dispositivos conservados. Clic izquierdo para abrir el panel rápido y enrutar cada app al instante.
3. **Control de volumen a ciegas**: Pasa el ratón sobre el icono y gira la rueda para ajustar el volumen a ciegas, o usa atajos y reglas de automatización sin salir del juego.

Antes de usar `sr` por primera vez, inicia SonicRoute con normalidad para registrar el comando y vuelve a abrir el terminal. Si el cambio de dispositivo no funciona como esperabas, consulta las [Preguntas frecuentes](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```bash
# Ejecutar regla de automatización (SonicRoute debe estar en ejecución)
sr "Nombre o ID de la regla"
```

La versión 1.27 unifica el panel rápido y elimina el antiguo panel de aplicaciones. Mejora las notificaciones de sesiones de audio y la recuperación, y corrige los destinos de los atajos de silencio y los avisos duplicados. Las tarjetas priorizan nombres personalizados e incorporan barras de desplazamiento comunes y filas de ajustes reutilizadas. Cambios anteriores: [Historial de versiones](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

### ❓ Preguntas frecuentes (FAQ)

**Q: ¿Se mantienen el enrutamiento de aplicaciones y el volumen tras cerrar SonicRoute?**

A: ¡Sí! El enrutamiento de audio por aplicación es conservado de forma nativa por el motor de Windows y sigue activo aunque cierre SonicRoute. Solo los atajos, la rueda de la bandeja y la automatización requieren que permanezca en segundo plano.

**Q: ¿Qué runtime requiere cada edición?**

A: Lite estándar requiere .NET 10 Desktop Runtime; Lite NET8 requiere .NET 8. Legacy no necesita un paquete .NET Desktop Runtime adicional y utiliza .NET Framework 4.8. El paquete Store actual requiere .NET 10 Desktop Runtime x64.

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

## 📌 Hoja de ruta

- **Mejora del enrutamiento de audio (Route Enhancement)**: Plan para perfeccionar el motor de enrutamiento con transferencia dinámica de flujos, recuperación automática al conectar/desconectar dispositivos y mayor estabilidad en sesiones complejas.

[Comparte una idea o informa de un problema](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 🧩 Otros proyectos y combinaciones

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**: Un reproductor de audio para Windows. Combínalo con la acción de iniciar programas de la automatización para reproducir efectos de sonido mediante atajos. [Guía de configuración (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="XiaoYue Comics"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="Haz clic en XiaoYue asomándose de un huevo con un cómic para leer la serie"></a>

**[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

Una comedia absurda sobre una chica mágica que sale de un huevo y provoca un nuevo desastre cada vez que intenta ayudar.

<br clear="all">

---

<a href="EASTER_EGGS.md" title="Un huevo de Pascua: ayer y mañana"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Descubre un huevo de Pascua"></a>

Gracias por usar SonicRoute. [Leer las guías (en chino)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Informar de un problema](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Autor: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
