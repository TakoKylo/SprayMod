# libs/

This folder holds the **game and engine assemblies the mod compiles against**. They are
proprietary *Puck* / Unity binaries and are **not** committed to this repository (see the
root `.gitignore`).

To build from source, populate this folder with the DLLs the project references
(`SprayMod.csproj` globs `libs\*.dll`). The simplest way is to copy the managed
assemblies from your local *Puck* install:

```
<Steam>\steamapps\common\Puck\Puck_Data\Managed\*.dll   ->   libs\
```

You also need **`0Harmony.dll`** (HarmonyX) here for the runtime patching.

Notes:
- `System.*.dll` in this folder are excluded from referencing by the `.csproj` (the
  framework provides them), so copying them is harmless.
- Assemblies are referenced with `<Private>false</Private>` so they are **not** copied
  to the build output — only `SprayMod.dll` ships.
