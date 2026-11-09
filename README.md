# NX Extractor

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/icon-dark.png">
  <img src="docs/icon.png" alt="NX Extractor icon" width="96">
</picture>

A simple Windows tool to extract and convert Nintendo Switch game dumps — no command line, no emulator required.

*Español más abajo.*

![NX Extractor screenshot](docs/screenshot.png)

## Features

**Extract**
- ExeFS, RomFS (with the update applied on top of the base game), DLC RomFS
- `control.nacp` plus icons, and an `info.txt` with the title name and version
- Input: NSP, NSZ, XCI, XCZ

**Convert**
- XCI / XCZ → NSP
- NSZ → NSP
- NSP / XCI → NSZ (zstd level 1–22)
- Batch conversion, drag & drop

**Other**
- Single self-contained `.exe` (no .NET install needed)
- Interface in English, Español, Français, 日本語 and Deutsch

## Usage

1. Download `NxExtractor.exe` from [Releases](../../releases).
2. Provide your own `prod.keys` (and optionally `title.keys`), dumped from your own console.
   If you put them next to the exe or in `%USERPROFILE%\.switch`, they are picked up automatically.
3. **Extract** tab: choose the base game, update and DLC, pick what to extract and click *Extract*.
4. **Convert** tab: add files, choose *to NSP* or *to NSZ* and click *Convert*.

Output layout:

```
<output>/<TitleID>/
  exefs/
  romfs/
  control.nacp
  control/
  info.txt
  dlc/<DLC TitleID>/romfs/
```

## Notes

- This tool does **not** include any keys or game content. Use it only with games you own.
- An update without its base game can only give the ExeFS (the update RomFS is a patch).
- NSP → XCI is not supported.

## Building

Requires the .NET 8 SDK.

```
dotnet publish -c Release -o dist
```

## Credits

Built on [LibHac](https://github.com/Thealexbarney/LibHac) and [ZstdSharp](https://github.com/oleg-st/ZstdSharp).

---

## Español

Herramienta para Windows que extrae y convierte dumps de juegos de Nintendo Switch, sin línea de comandos ni emulador.

- **Extraer:** ExeFS, RomFS (con el update aplicado), RomFS de DLC y `control.nacp` con iconos, desde NSP, NSZ, XCI o XCZ.
- **Convertir:** XCI/XCZ → NSP, NSZ → NSP, NSP/XCI → NSZ, por lotes.
- Un solo `.exe`, sin instalar nada. Interfaz en 5 idiomas.

Las claves (`prod.keys` y `title.keys`) las pones tú, sacadas de tu propia consola. La herramienta no incluye claves ni juegos.
