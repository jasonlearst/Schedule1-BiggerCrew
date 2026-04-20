# BiggerCrew

A [MelonLoader](https://melonwiki.xyz/) mod for **Schedule I** that raises the
multiplayer lobby limit from 4 to 16 and bypasses the `Application.version`
string-equality check that blocks clients on slightly different Steam
sub-branches from joining each other.

Targets the **alternate** (Mono) Steam branch of Schedule I. Will not load on
the default-public IL2CPP branch.

## Install

1. Opt into the `alternate` (or `alternate-beta`) branch in Steam:
   right-click Schedule I → Properties → Betas → select branch.
2. Install MelonLoader for Mono via the official installer:
   <https://github.com/LavaGang/MelonLoader.Installer/releases>
3. Drop `BiggerCrew.dll` into `<game>/Mods/`.
4. **Every player in the session must have the same `BiggerCrew.dll`**
   (verify by SHA256 in `MelonLoader/Latest.log`).

## Configuration

Compile-time only — edit `BiggerCrew.cs`:

```csharp
public const int LOBBY_CAPACITY = 16;   // raise to 20 max; >20 is unstable
```

then `dotnet build -c Release` — the build target auto-copies the new DLL into
`../Mods/`.

## Build

Requires .NET 8 SDK. References resolve from one of two places:

1. **`refs/`** (committed stripped reference assemblies) — used by CI and by
   anyone without the game installed
2. The local Schedule I install (`../Schedule I_Data/Managed/`,
   `../MelonLoader/net6/`) — used as a fallback when `refs/` is absent

Stripped refs preserve the full type/member metadata (only IL bodies are
removed), so both paths produce identical output. See `refs/NOTICE.md` for
the licensing situation around those files.

```bash
cd <repo>
dotnet build -c Release
```

The csproj uses [`Krafs.Publicizer`](https://github.com/krafs/Publicizer) so
that private game members (`Lobby.CreateLobby`, `LobbyInterface.UpdateButtons`,
`Player.ReceivePlayerNameData`, …) can be called directly without reflection.

## Reproducible builds via GitHub Actions

Every push to `main` triggers `.github/workflows/build.yml`, which builds
`BiggerCrew.dll` from source on a clean Ubuntu runner and uploads it as an
artifact alongside its SHA256.

Pushing a tag matching `v*` (e.g. `v1.1.0`) triggers
`.github/workflows/release.yml`, which builds the DLL and attaches it to a
GitHub release. The release body links back to the exact source commit so
anyone can verify that the published DLL came from the published source.

```bash
git tag v1.1.0
git push --tags
```

## Diagnostic logging

Every log line is prefixed with the local Steam ID once Steam initializes, so
logs collected from multiple PCs can be grouped per-player and correlated by
lobby ID. Look in `<game>/MelonLoader/Latest.log`. See `DESIGN.md` for the
full event list.

## License

GPL-3.0-or-later. Forks, redistributions, and binary releases must publish
their source under a compatible license. See `LICENSE`.

## Inspiration & credits

This mod is a clean, version-tolerant rewrite that combines ideas from several
prior community mods. None of their code is copied verbatim, but they each
informed a piece of the design:

| Mod | Author | What we took |
|---|---|---|
| [MultiplayerPlus (IL2CPP)](https://thunderstore.io/c/schedule-i/p/MedicalMess/MultiplayerPlus/) | MedicalMess | Lobby resize approach, `TryOpenInviteInterface` patch, dynamic UI slot cloning |
| MonoFGMutliplayer+ (Nexus mod [#55](https://www.nexusmods.com/schedule1/mods/55)) | MedicalMess | Mono-target structure, `Player.RpcLogic___SendPlayerNameData_*` FishNet RPC patch (the actual networking patch that lets 5+ players sync names) |
| [Schedule-1-Multiplayer-Mod-New-version](https://github.com/CyrilZ0817/Schedule-1-Multiplayer-Mod-New-version) | CyrilZ0817 | First look at `Il2CppSteamworks` patching surface |
| [SO_MultiplayerEnhanced](https://github.com/Nyxis-Studio/SO_MultiplayerEnhanced) | goustkor / Nyxis-Studio | `.csproj` template structure, clean `Core.cs` patch organisation |

Notably **not** carried over from the upstream IL2CPP MedicalMess source: the
anti-piracy `Process.GetCurrentProcess().Kill()` payload, the `OnlineFix.ini`
detector that triggers it, the hardcoded Steam-ID target, and the
`Loader.cfg`-disabling write. See `DESIGN.md` § "What we deliberately left
out" for details.

## Differences from prior mods

- **Version-mismatch bypass.** Vanilla `Lobby.OnLobbyEntered` rejects clients
  whose `Application.version` differs from the host's by even one character
  (e.g. `alternate` vs `alternate-beta`). None of the prior mods patched this.
  We rewrite the comparison's IL so it always evaluates to "match".
- **Hash-tolerant FishNet RPC patch.** Prior mods hard-coded
  `RpcLogic___SendPlayerNameData_586648380`. We resolve the method by name
  prefix at startup, so the inevitable next FishNet hash bump won't silently
  break player-name sync.
- **Branch-tolerant load.** No `Application.version.Contains("Alternate")`
  gate. The mod simply loads.
- **Safer.** No process-killing, no anti-piracy file writes, no hardcoded
  per-user behavior, no game `Application.Quit()`.
- **Diagnostic.** Every patch logs the data needed to debug a failed join
  (lobby ID, owner, host vs client version, FishNet hash, exception traces).

## Repository layout

```
BiggerCrew.csproj   - .NET project; Krafs.Publicizer + game DLL refs
BiggerCrew.cs       - Single source file (~250 lines)
README.md               - This file
DESIGN.md               - Technical design notes; reference if game updates
LICENSE                 - GPL-3.0
```
