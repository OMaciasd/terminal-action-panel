# Terminal Action Panel

[![CI](https://github.com/OMaciasd/terminal-action-panel/actions/workflows/ci.yml/badge.svg)](https://github.com/OMaciasd/terminal-action-panel/actions/workflows/ci.yml)
[![Release](https://github.com/OMaciasd/terminal-action-panel/actions/workflows/release.yml/badge.svg)](https://github.com/OMaciasd/terminal-action-panel/actions/workflows/release.yml)

Panel compacto para Windows que ejecuta el contenido del portapapeles y administra una **sesión real de Windows PowerShell** desde botones. El proyecto es gratuito, de código abierto y no requiere instalar dependencias de terceros.

> **PASTE + EXECUTE ejecuta comandos arbitrarios inmediatamente.** Revisa el texto del portapapeles y el usuario/token que aparecen en la ventana antes de pulsar el botón. La aplicación no solicita ni activa elevación.

## Requisitos

- Windows 10 x64, versión 1809 (compilación 17763) o posterior; Windows 11 también es compatible.
- Windows PowerShell 5.1, incluido en Windows.
- Una cuenta con permisos para ejecutar el script del instalador y descargar versiones desde GitHub.
- No necesita Windows Terminal, .NET SDK, Node.js, WSL2 ni permisos de administrador. La aplicación usa WinForms y el .NET Framework incluido con Windows.

## Instalación

Abre Windows PowerShell 5.1 con tu usuario habitual y descarga el instalador desde la rama pública `main`:

```powershell
irm https://raw.githubusercontent.com/OMaciasd/terminal-action-panel/main/install.ps1 | iex
```

El instalador obtiene la última versión estable de las publicaciones de GitHub, valida el SHA-256 anunciado por la API de GitHub, comprueba los archivos y la versión del paquete y lo instala en:

```text
%LOCALAPPDATA%\TerminalActionPanel
```

Crea accesos directos en el escritorio y en el menú Inicio, e inicia la aplicación al terminar. No cambia la política de ejecución del sistema ni necesita ejecutarse como administrador. También puedes descargar el repositorio y ejecutar `./install.ps1` desde Windows PowerShell; la instalación sigue obteniendo el binario verificado de la publicación más reciente.

El ejecutable y el instalador no están firmados con un certificado comercial, por lo que SmartScreen puede mostrar una advertencia de aplicación desconocida. La verificación de integridad del paquete se realiza con el SHA-256 publicado por GitHub; no se solicita ni se omite ninguna elevación.

## Uso

La aplicación abre una sesión nueva de `powershell.exe` mediante **ConPTY**; no se conecta a una pestaña externa ya abierta.

- **PASTE + EXECUTE** lee texto Unicode del portapapeles y lo ejecuta en esa sesión en un solo clic. Admite comandos multilínea. El botón queda deshabilitado hasta que PowerShell devuelve el prompt; una sesión cerrada, un portapapeles vacío o un error se informan en la barra de estado.
- **COPY ALL** copia el transcript completo que la aplicación conserva, no solo una selección ni la última ejecución.
- **CLEAR ALL** vacía la pantalla y el transcript en memoria de la aplicación. No borra archivos, registros, la historia persistente de PowerShell ni información del sistema. Cualquier salida nueva que llegue después de limpiar se vuelve a mostrar.
- Si el proceso está esperando entrada, **Entrada al proceso** permite enviar una línea. **INTERRUMPIR** envía Ctrl+C.
- No se permite iniciar otra ejecución de portapapeles mientras la sesión sigue ocupada, para evitar duplicados o que un comando termine como entrada accidental de otro proceso.
- Al cerrar la ventana mientras hay un comando activo se pide confirmación. La sesión alojada y sus procesos se terminan al cerrar la aplicación.

La ventana muestra el usuario actual y si el token efectivo es estándar/no elevado o administrador. PowerShell se inicia con el mismo token que la aplicación; no se usa `RunAs` ni se cambia el nivel de permisos.

## Actualización

Cierra la aplicación y ejecuta:

```powershell
& "$env:LOCALAPPDATA\TerminalActionPanel\update.ps1"
```

`update.ps1` consulta el último release estable y usa el mismo proceso de descarga y validación SHA-256 que la instalación. No reemplaza una aplicación que siga abierta.

## Desinstalación

```powershell
& "$env:LOCALAPPDATA\TerminalActionPanel\uninstall.ps1"
```

Si hay una sesión abierta, el script pide confirmación antes de terminarla; `-Force` omite esa pregunta. El desinstalador elimina solo los archivos de la aplicación que conoce y sus dos accesos directos. Conserva cualquier archivo ajeno que alguien haya agregado al directorio de instalación y no borra historiales de PowerShell, scripts, logs ni datos del sistema.

## Arquitectura y alcance

- `src/TerminalActionPanel.cs` contiene una interfaz WinForms y un anfitrión **ConPTY** (`CreatePseudoConsole`, API nativa de Windows disponible desde Windows 10 1809). Inicia el ejecutable real `powershell.exe`, transmite entrada y recoge continuamente su salida. No es una consola simulada ni una automatización de teclado/ratón sobre otra ventana.
- `host-init.ps1` instala un marcador privado en el prompt para detectar de forma fiable cuándo PowerShell vuelve a estar listo. La sesión se inicia con `-NoProfile`, configura PSReadLine para no guardar nuevos comandos en el archivo de historial del usuario y define una función de prompt propia. Los comandos pegados se ejecutan en el ámbito actual para mantener estado y directorio entre ejecuciones.
- La aplicación aloja el transcript y el scrollback que muestra. Conserva hasta **5 000 000 caracteres**; `COPY ALL` copia todo lo retenido. Si se supera ese límite, elimina la parte más antigua. `CLEAR ALL` vacía ese único buffer temporal.
- `install.ps1` y `update.ps1` solo aceptan releases versionados del repositorio `OMaciasd/terminal-action-panel`, comparan el digest SHA-256 del asset con el valor publicado por la API de GitHub y validan el manifiesto de archivos y la versión antes de copiar. `.github/workflows/release.yml` vuelve a compilar y probar el paquete en Windows antes de adjuntarlo al release.

La interfaz nativa usada está documentada por Microsoft en [CreatePseudoConsole](https://learn.microsoft.com/windows/console/createpseudoconsole) y [Creating a Pseudoconsole session](https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session). La documentación oficial la define como la API de alojamiento de una aplicación de terminal y asigna al anfitrión la responsabilidad de leer y presentar el flujo de salida.

### Integraciones y limitaciones

| Objetivo | Alcance |
| --- | --- |
| Windows PowerShell 5.1 | Integración nativa y objetivo soportado. Cada ventana controla una sesión real nueva de este proceso. |
| Windows Terminal ya abierto | No hay una API pública estable de Windows Terminal para que otra aplicación lea todo el scrollback de una pestaña existente. El panel no intenta adjuntarse ni automatiza su interfaz. |
| PowerShell 7 | Puede iniciarse manualmente como un comando desde la sesión, pero no es el shell anfitrión configurado por esta versión. |
| WSL2 | `wsl.exe` puede invocarse desde PowerShell; la sesión y el transcript siguen siendo los del anfitrión ConPTY. No se presenta como conexión a una terminal WSL externa. |
| OpenCode | Puede ejecutarse desde el anfitrión. La salida de comandos no interactivos queda en el transcript; la interfaz VT de pantalla completa de OpenCode no se renderiza como un emulador VT completo. |

El panel muestra un transcript de texto y elimina secuencias ANSI de color/título. **No es un renderizador completo de terminal VT**: aplicaciones interactivas de pantalla completa, edición de cursor, gráficos, progreso que reescribe líneas y atributos de color pueden verse de forma simplificada o incorrecta. La aplicación sí recoge la salida textual que atraviesa su sesión ConPTY y conserva el historial que controla. No puede recuperar la salida de una sesión ya existente de Windows Terminal, PowerShell u otra aplicación.

Los comandos se envían como un bloque multilínea en el ámbito actual de PowerShell. Directivas que exigen ejecutarse en el nivel superior de un archivo (`using` en ciertos scripts), comandos que cierran el shell (`exit`) o programas que requieren una terminal VT completa tienen las limitaciones propias de ese anfitrión.

## Compilar y probar desde el código

En Windows, con Windows PowerShell 5.1:

```powershell
./build.ps1 -Version 1.0.3 -OutputDirectory ./dist
./tests/Run-Tests.ps1 -ExecutablePath ./dist/TerminalActionPanel.exe
./tests/Ui-SmokeTest.ps1 -ExecutablePath ./dist/TerminalActionPanel.exe
./tests/Lifecycle-Tests.ps1
```

`--self-test` comprueba el filtro VT, el detector de prompt entre lecturas parciales, el límite y limpieza del transcript, la conversión de comandos multilínea y una integración real PowerShell 5.1 + ConPTY. GitHub Actions compila y ejecuta estas pruebas en `windows-latest` tanto en CI como antes de publicar un release.

`Ui-SmokeTest.ps1` es una prueba de interfaz que requiere una sesión de escritorio interactiva y Windows PowerShell en modo STA (`powershell.exe -STA`). Abre la aplicación real, pulsa los botones mediante UI Automation y verifica: identificación de objetivo/permisos, espera de disponibilidad, bloqueo durante la ejecución, comando multilínea, entrada a un proceso interactivo mediante `ENVIAR`, interrupción con Ctrl+C, `COPY ALL` con la primera y última línea de 120 líneas de scrollback, `CLEAR ALL` con el portapapeles y el transcript vacíos, portapapeles vacío, nueva ejecución tras limpiar, cierre de la terminal, que el historial persistente de PowerShell no se modifique y cierre limpio de la ventana. Restaura el portapapeles del usuario al terminar. No se ejecuta en CI porque los runners alojados no garantizan un escritorio interactivo.

`Lifecycle-Tests.ps1` prueba el ciclo de vida completo contra la última publicación estable: instalación, instalación repetida con hashes idénticos, apertura mediante el acceso directo, actualización, desinstalación conservando archivos ajenos, desinstalación limpia y reinstalación. Descarga la versión publicada y valida su SHA-256; requiere Internet, no requiere administrador y cierra cualquier instancia abierta antes de empezar. No se ejecuta en CI para no instalar en el perfil del runner ni depender de una publicación concreta.

## Repositorio

Código fuente y releases públicos: <https://github.com/OMaciasd/terminal-action-panel>

Licencia MIT.
