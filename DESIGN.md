# MultiplayerFix — Design notes

Reference document for understanding why each patch exists and how to update
the mod when Schedule I receives a game update. Aimed at a future maintainer
(or future-you) reading this six months from now after the mod stops working.

## Background: why two Schedule I builds exist

Steam ships Schedule I in two parallel build configurations:

| Branch | Backend | Modding layer | What `MelonLoader/Dependencies/` looks like |
|---|---|---|---|
| Default Public | IL2CPP (Unity → C++ → native) | `Il2CppInterop` generates dummy `.NET` shim DLLs at first launch; mods reference `Il2Cpp*` types | `Il2CppAssemblyGenerator/`, `SupportModules/Il2Cpp.dll` |
| `alternate`, `alternate-beta`, `alternate-previous` | Mono | Game DLLs in `Schedule I_Data/Managed/` are real .NET assemblies; mods reference them directly | `MonoBleedingEdgePatches/`, `SupportModules/Mono.dll` |

Modding the IL2CPP build is fragile (the shim DLLs are regenerated on every
game update, and `Il2CppInterop` itself can crash during generation, which is
exactly what blocked us before the branch switch). Modding the Mono build is
straightforward: it's just patching a regular .NET game with Harmony.

**MultiplayerFix targets the Mono build only.** It will not load on Default
Public, and it doesn't try to.

## The four problems we solve

### 1. Steam lobby capacity is hardcoded to 4

When the host calls `Lobby.CreateLobby`, the game requests 4 slots from Steam.
Even if the host accepts a 5th invite, Steam refuses. **Fixed by**:

- `Lobby.TryOpenInviteInterface` prefix — the original calls
  `SteamMatchmaking.CreateLobby(..., 4)` indirectly via `CreateLobby()`. We
  reimplement the method to call `CreateLobby()` then check Steam's reported
  member count against `LOBBY_CAPACITY` instead of 4.
- `Lobby.Players` field is reallocated to `new CSteamID[LOBBY_CAPACITY]` once
  the Menu scene initializes. The game uses this array to track lobby members.

### 2. Lobby UI shows "X / 4" and only renders 4 player slots

The `LobbyInterface` `GridLayoutGroup` ships with one template + 4 slot
children, and the player-count label reads from a hardcoded `4`. **Fixed by**:

- In `WaitForLobby` (called on Menu scene init): clone the slot template
  `LOBBY_CAPACITY - 4` times so we have enough UI rows.
- Rebuild `LobbyInterface.PlayerSlots` (a `RectTransform[]`) to point at the
  new children.
- `LobbyInterface.UpdateButtons` prefix: rewrite the invite-button-active
  check to use `LOBBY_CAPACITY` instead of the original `4`.
- `LobbyInterface.Awake` postfix: rewire `Lobby.onLobbyChange` so the title
  text reads `Lobby (X / LOBBY_CAPACITY)` after every change.

### 3. The version-mismatch wall (the actual blocker)

`ScheduleOne.Networking.Lobby.OnLobbyEntered` does an exact string
comparison and rejects any client whose `Application.version` differs from the
host's:

```csharp
private void OnLobbyEntered(LobbyEnter_t result)
{
    string lobbyData = SteamMatchmaking.GetLobbyData(...,  "version");
    if (lobbyData != Application.version)
    {
        Singleton<MainMenuPopup>.Instance.Open("Version Mismatch", ...);
        LeaveLobby();
        return;
    }
    // ... rest of the join flow
}
```

Two PCs can be on the same nominal version (`0.4.5f2`) but different
sub-branches (`alternate` vs `alternate-beta`) and report different
`Application.version` strings (e.g. `"0.4.5f2 Alternate"` vs
`"0.4.5f2 Alternate Beta"`) — looks identical in the popup, fails the equality
check.

**Fixed by an IL transpiler** on `Lobby.OnLobbyEntered`. The method contains
exactly one `string::op_Inequality(string, string)` call (verified by reading
the IL — every other comparison in the method is `op_Equality`). We replace
that single instruction with `pop; pop; ldc.i4.0`, which:

- pops the `Application.version` string
- pops the `lobbyData` string
- pushes `0` (false) onto the stack

The following `brfalse` then always branches over the bad path. The original
`SteamMatchmaking.GetLobbyData(...)` call still runs (no observable side
effect) and the rest of the method proceeds as if the versions matched.

This patch is **deliberately surgical**. We don't reimplement the method, so
any new logic the game adds to `OnLobbyEntered` continues to work — we only
neuter the version comparison.

### 4. Player names don't sync past 4 players

Once 5+ players are in the session, `Player.SendPlayerNameData` (a FishNet
server RPC) only fires its writer for the first 4 players. The decompiled
RPC body is:

```csharp
public void RpcLogic___SendPlayerNameData_586648380(string playerName, ulong id)
{
    ReceivePlayerNameData(null, playerName, id.ToString());
}
```

…which on the host side relays to all clients but does **not** apply the name
to the local `Player` object. The result: extra players show up nameless and
without a player code.

**Fixed by** patching the RPC's local logic to also assign `__instance.PlayerName`
and `__instance.PlayerCode` directly on the receiving Player. Same logic
MedicalMess used in MonoFGMutliplayer+, ported here.

The wrinkle: FishNet generates the method name with a hash suffix derived
from the method signature (`_586648380`). If a future game update tweaks the
signature, the hash changes and a hardcoded patch silently no-ops. To avoid
this we resolve the method by name prefix at startup:

```csharp
var rpc = AccessTools.GetDeclaredMethods(typeof(Player))
    .FirstOrDefault(m => m.Name.StartsWith("RpcLogic___SendPlayerNameData_"));
```

…then patch whichever instance we find. If the method is gone entirely, we
log a warning and load anyway (lobby resize still works, just not name sync).

## What we deliberately left out

The upstream IL2CPP MedicalMess `Multiplayer+` v2.0 source (downloadable as a
decompiled `.cs` from Thunderstore) contains payloads we wanted nothing to do
with. None of these are in our build:

- `AntiTheft()` coroutine — `Process.GetCurrentProcess().Kill()` after a
  random 30–200 second delay
- `Checkforthing()` — scans for `OnlineFix.ini` (a Steam-emu cracker config),
  rewrites it, then triggers `AntiTheft`
- Hardcoded `if (LocalPlayerID == "76561199091812419")` that triggers
  `AntiTheft` against a specific Steam user
- Writing `disable = true` into `MelonLoader/UserData/Loader.cfg`
- `Application.Quit()` after wrong lobby password

The Mono port (`MonoFGMutliplayer+.dll`) shipped without these payloads —
verified by string analysis before we ported anything from it. We carry that
property forward.

We also deliberately omitted the **lobby password** feature, the
**`Application.version.Contains("Alternate")`** load gate, and the
universal `/4` → `/16` text replacement on every TMPro element (which
collaterally damaged unrelated UI text in the IL2CPP version).

## Where each piece came from

| Patch / behavior | Origin |
|---|---|
| Lobby array resize, UI slot cloning | MedicalMess MonoFGMutliplayer+ |
| `TryOpenInviteInterface` capacity bypass | MedicalMess MonoFGMutliplayer+ |
| `LobbyInterface.Awake` rewire of `onLobbyChange` | MedicalMess MonoFGMutliplayer+ |
| `LobbyInterface.UpdateButtons` capacity check | MedicalMess MonoFGMutliplayer+ |
| FishNet `SendPlayerNameData` RPC patch | MedicalMess MonoFGMutliplayer+ |
| `Lobby.OnLobbyEntered` version-check bypass | New — designed by reading decompiled `Assembly-CSharp.dll` |
| Hash-tolerant FishNet RPC resolution | New — defensive against future game updates |
| Diagnostic logging with Steam-ID prefix | New |
| `.csproj` with publicizer + game DLL refs | NyxisStudio `SO_MultiplayerEnhanced` (template) |

## How to update when the game changes

If a Schedule I update breaks the mod, the log is the first stop.
`MultiplayerFix v1.1.0` writes loud warnings exactly when the things that can
break do break:

| Log line | Meaning | Action |
|---|---|---|
| `RpcLogic___SendPlayerNameData_* NOT FOUND` | The FishNet method was renamed entirely | Decompile the new `Assembly-CSharp.dll`, find what replaced it, update the prefix string in `Core.OnInitializeMelon` |
| `OnLobbyEntered transpiler: op_Inequality(string,string) NOT found - version check IS still active` | The IL pattern the transpiler looks for is gone | Re-decompile `Lobby.OnLobbyEntered`, see how the version check is now expressed (maybe they switched to `string.Equals` or moved it to a separate method), update the transpiler |
| Any `_Postfix failed` / `_Prefix failed` with stack trace | Patched method's signature or behavior changed enough that our patch threw | Pinpoint from stack trace, check the method in the new `Assembly-CSharp.dll` |
| Mod loads, no warnings, but lobby still shows `(X / 4)` | One of the four UI patches no-op'd silently | Check that `LobbyInterface`, `Lobby`, and the `GridLayoutGroup` child layout still match assumptions |

### Re-decompiling after a game update

```bash
ilspycmd "<game>/Schedule I_Data/Managed/Assembly-CSharp.dll" \
    -t ScheduleOne.Networking.Lobby > Lobby.cs
ilspycmd "<game>/Schedule I_Data/Managed/Assembly-CSharp.dll" \
    -t ScheduleOne.UI.Multiplayer.LobbyInterface > LobbyInterface.cs
ilspycmd "<game>/Schedule I_Data/Managed/Assembly-CSharp.dll" \
    -t ScheduleOne.PlayerScripts.Player > Player.cs
```

Useful searches in the dumped sources:

- `grep -n "version" Lobby.cs` — confirms the version comparison still lives
  in `OnLobbyEntered` and looks the same
- `grep -nE "RpcLogic___SendPlayerNameData|RpcLogic___ReceivePlayerNameData" Player.cs`
  — confirms the FishNet hash suffix
- `grep -nE "public.*PlayerSlots|public.*Lobby Lobby|public TextMeshProUGUI LobbyTitle|public Button (InviteButton|LeaveButton)" LobbyInterface.cs`
  — confirms the LobbyInterface fields we touch are still public and still
  named the same

If `Krafs.Publicizer` stops finding a member, the field/method may have been
renamed; check the decompile.

## How to extend

- **Different capacity:** edit `LOBBY_CAPACITY` in `Core`. Keep ≤ 20 — Steam
  caps lobbies at 250 but Schedule I's networking layer hasn't been tested
  beyond ~20 by the community.
- **Additional networked-state sync past 4 players:** if other game state
  (positions, inventory, etc.) breaks for the 5th+ player, look for other
  FishNet `RpcLogic___…` methods on `Player` or related networked classes
  and add prefixes that explicitly apply the data locally as well as relaying.

## Build environment recap

- Target framework: `netstandard2.1` (matches MonoBleedingEdge in Schedule I
  on Mono)
- References: `MelonLoader.dll`, `0Harmony.dll` from `MelonLoader/net6/`;
  `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`, `FishNet.Runtime.dll`,
  `com.rlabrecque.steamworks.net.dll`, `Unity.TextMeshPro.dll`, and the Unity
  modules from `Schedule I_Data/Managed/`
- `Krafs.Publicizer 2.2.1` to expose `internal`/`private` game members
- Build target `CopyToMods` auto-deploys the DLL into `<game>/Mods/`

The 42 build warnings are all `MSB3277` reference-version conflicts between
`netstandard2.1` and `net6` of `System.*` assemblies. They originate in
MelonLoader's diagnostic dependencies, which we don't actually call. Cosmetic
only.
