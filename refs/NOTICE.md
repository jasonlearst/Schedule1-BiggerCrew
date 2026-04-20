# Reference assemblies — licensing notice

The `.dll` files in this directory and its subdirectories are **reference
assemblies**: stripped copies of third-party libraries and the Schedule I
game assemblies. All IL method bodies have been removed (replaced with
`throw null;`); only type metadata, member signatures, and attributes
remain. They are used solely so that this project can be compiled in
environments that don't have the game installed (notably the GitHub
Actions runner that produces the published `BiggerCrew.dll`).

## These files are NOT covered by the project's GPL-3.0 license

The GPL-3.0 license that covers the BiggerCrew source code does **not**
apply to anything in this `refs/` directory. We have no rights to
relicense someone else's code, and we are not attempting to.

## Copyright holders

| File(s) | Original copyright | Original license |
|---|---|---|
| `Managed/Assembly-CSharp.dll`, `Managed/Assembly-CSharp-firstpass.dll` | © TVGS | Proprietary (Schedule I game code) |
| `Managed/UnityEngine*.dll` | © Unity Technologies | Unity Companion License |
| `Managed/Unity.TextMeshPro.dll` | © Unity Technologies | Unity Companion License |
| `Managed/FishNet.Runtime.dll` | © FirstGearGames | MIT |
| `Managed/com.rlabrecque.steamworks.net.dll` | © Riley Labrecque | MIT |
| `MelonLoader/MelonLoader.dll` | © LavaGang | Apache 2.0 |
| `MelonLoader/0Harmony.dll` | © Andreas Pardeike | MIT |

## Why include them at all

To make CI builds reproducible. With these stripped refs in the repo, the
GitHub Actions workflow can rebuild `BiggerCrew.dll` from source on every
push. Without them, only people with the game installed could verify that
the published DLL matches the published source.

## Use restrictions

These files are included under fair-use principles for build
interoperability. They contain no executable game logic. Anyone
redistributing or repurposing them must comply with each file's
underlying license.

If you are a rights holder and want a file removed, open an issue on
this repository and we will remove it.

## Refresh procedure

When Schedule I publishes an update that changes any of the API surface
this mod patches, regenerate the refs locally with:

```bash
# from the repo root, with the game installed at ../Schedule I/
for f in Assembly-CSharp Assembly-CSharp-firstpass FishNet.Runtime \
         com.rlabrecque.steamworks.net Unity.TextMeshPro UnityEngine \
         UnityEngine.CoreModule UnityEngine.UI UnityEngine.UIModule; do
  assembly-publicizer "../Schedule I_Data/Managed/${f}.dll" \
    --strip-only -o "refs/Managed/${f}.dll" -f
done
for f in MelonLoader 0Harmony; do
  assembly-publicizer "../MelonLoader/net6/${f}.dll" \
    --strip-only -o "refs/MelonLoader/${f}.dll" -f
done
```

Requires `BepInEx.AssemblyPublicizer.Cli`:
`dotnet tool install -g BepInEx.AssemblyPublicizer.Cli`
