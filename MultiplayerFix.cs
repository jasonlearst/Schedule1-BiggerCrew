using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FishNet.Connection;
using HarmonyLib;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Networking;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI.Multiplayer;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(MultiplayerFix.Core), "MultiplayerFix", "1.1.0", "you")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace MultiplayerFix
{
    public class Core : MelonMod
    {
        public const int LOBBY_CAPACITY = 16;
        public const string MOD_VERSION = "1.1.0";

        private HarmonyLib.Harmony _harmony;

        public override void OnInitializeMelon()
        {
            Log.I($"=== MultiplayerFix v{MOD_VERSION} initializing ===");
            Log.I($"Game version: '{Application.version}'");
            Log.I($"Unity version: {Application.unityVersion}");
            Log.I($"Lobby capacity target: {LOBBY_CAPACITY}");

            _harmony = new HarmonyLib.Harmony("MultiplayerFix");
            _harmony.PatchAll();

            // FishNet generates RPC method names with a hash suffix
            // (e.g. RpcLogic___SendPlayerNameData_586648380). The hash can change
            // between game versions, so we resolve it dynamically by name prefix.
            var sendNameRpc = AccessTools.GetDeclaredMethods(typeof(Player))
                .FirstOrDefault(m => m.Name.StartsWith("RpcLogic___SendPlayerNameData_"));
            if (sendNameRpc != null)
            {
                _harmony.Patch(sendNameRpc,
                    prefix: new HarmonyMethod(typeof(PlayerPatches),
                        nameof(PlayerPatches.SendPlayerNameData_Prefix)));
                Log.I($"Patched FishNet RPC: {sendNameRpc.Name}");
            }
            else
            {
                Log.W("RpcLogic___SendPlayerNameData_* NOT FOUND - player name sync will be vanilla");
            }

            Log.I($"=== MultiplayerFix loaded ===");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Log.I($"Scene initialized: '{sceneName}' (buildIndex={buildIndex})");
            if (sceneName == "Menu")
                MelonCoroutines.Start(WaitForLobby());
        }

        private static IEnumerator WaitForLobby()
        {
            Log.I("WaitForLobby: waiting for Lobby singleton...");
            while (Singleton<Lobby>.Instance == null)
                yield return null;
            var lobby = Singleton<Lobby>.Instance;

            if (!SteamManager.Initialized)
            {
                Log.W("SteamManager not initialized at Menu - hiding lobby");
                lobby.gameObject.SetActive(false);
                yield break;
            }

            Log.LocalSteamId = SteamUser.GetSteamID().m_SteamID;
            Log.I($"Steam ready. LocalSteamId={Log.LocalSteamId}");

            lobby.Players = new CSteamID[LOBBY_CAPACITY];
            Log.I($"Resized Lobby.Players to {LOBBY_CAPACITY}");

            Log.I("WaitForLobby: waiting for LobbyInterface singleton...");
            while (Singleton<LobbyInterface>.Instance == null)
                yield return null;
            var lobbyUI = Singleton<LobbyInterface>.Instance;

            try
            {
                var entries = lobbyUI.GetComponentInChildren<GridLayoutGroup>().transform;
                int existingChildren = entries.childCount;
                Log.I($"LobbyInterface entries.childCount before clone = {existingChildren}");

                if (entries.childCount > 1)
                {
                    var template = entries.GetChild(1);
                    for (int i = 0; i < LOBBY_CAPACITY - 4; i++)
                    {
                        var newEntry = UnityEngine.Object.Instantiate(template.gameObject, entries);
                        int newIndex = entries.childCount - 1;
                        newEntry.name = template.gameObject.name + " (" + newIndex + ")";
                    }
                }

                int slotCount = entries.childCount - 1;
                var newSlots = new RectTransform[slotCount];
                for (int j = 1; j < entries.childCount; j++)
                    newSlots[j - 1] = entries.GetChild(j).GetComponent<RectTransform>();
                lobbyUI.PlayerSlots = newSlots;

                entries.GetChild(1).SetSiblingIndex(LOBBY_CAPACITY);
                lobbyUI.LobbyTitle.text = $"Lobby ({lobbyUI.Lobby.PlayerCount}/{LOBBY_CAPACITY})";
                lobbyUI.enabled = true;

                Log.I($"LobbyInterface configured: PlayerSlots={slotCount}, title='{lobbyUI.LobbyTitle.text}'");
            }
            catch (Exception ex)
            {
                Log.E("WaitForLobby UI setup failed", ex);
            }
        }
    }

    /// <summary>Tagged logger that prepends LocalSteamId so logs from multiple
    /// players can be correlated when collected together.</summary>
    internal static class Log
    {
        public static ulong LocalSteamId;
        private static string Prefix => LocalSteamId == 0 ? "" : $"[{LocalSteamId}] ";
        public static void I(string msg) => MelonLogger.Msg($"{Prefix}{msg}");
        public static void W(string msg) => MelonLogger.Warning($"{Prefix}{msg}");
        public static void E(string msg) => MelonLogger.Error($"{Prefix}{msg}");
        public static void E(string msg, Exception ex) => MelonLogger.Error($"{Prefix}{msg}\n{ex}");
    }

    [HarmonyPatch(typeof(LobbyInterface))]
    internal static class LobbyInterfacePatches
    {
        [HarmonyPatch("Awake")]
        [HarmonyPostfix]
        private static void Awake_Postfix(LobbyInterface __instance)
        {
            try
            {
                if (__instance.Lobby == null)
                {
                    Log.W("LobbyInterface.Awake: Lobby is null, skipping");
                    return;
                }
                var lobby = __instance.Lobby;
                lobby.onLobbyChange = null;
                lobby.onLobbyChange = (Action)Delegate.Combine(lobby.onLobbyChange, (Action)delegate
                {
                    try
                    {
                        __instance.UpdateButtons();
                        __instance.UpdatePlayers();
                        __instance.LobbyTitle.text =
                            $"Lobby ({lobby.PlayerCount}/{Core.LOBBY_CAPACITY})";
                    }
                    catch (Exception ex) { Log.E("onLobbyChange handler failed", ex); }
                });
                Log.I("LobbyInterface.Awake patched - onLobbyChange rewired");
            }
            catch (Exception ex) { Log.E("LobbyInterface.Awake_Postfix failed", ex); }
        }

        [HarmonyPatch("UpdateButtons")]
        [HarmonyPrefix]
        private static bool UpdateButtons_Prefix(LobbyInterface __instance)
        {
            try
            {
                __instance.InviteButton.gameObject.SetActive(
                    __instance.Lobby.IsHost && __instance.Lobby.PlayerCount < Core.LOBBY_CAPACITY);
                __instance.LeaveButton.gameObject.SetActive(!__instance.Lobby.IsHost);
            }
            catch (Exception ex) { Log.E("UpdateButtons_Prefix failed", ex); }
            return false;
        }
    }

    [HarmonyPatch(typeof(Lobby))]
    internal static class LobbyPatches
    {
        [HarmonyPatch("TryOpenInviteInterface")]
        [HarmonyPrefix]
        private static bool TryOpenInviteInterface_Prefix(Lobby __instance)
        {
            try
            {
                int memberCount = __instance.IsInLobby
                    ? SteamMatchmaking.GetNumLobbyMembers(__instance.LobbySteamID) : 0;
                Log.I($"TryOpenInviteInterface: IsInLobby={__instance.IsInLobby} members={memberCount} cap={Core.LOBBY_CAPACITY}");

                if (!__instance.IsInLobby)
                    __instance.CreateLobby();
                if (SteamMatchmaking.GetNumLobbyMembers(__instance.LobbySteamID) >= Core.LOBBY_CAPACITY)
                {
                    Log.I("TryOpenInviteInterface: at capacity, not opening invite dialog");
                    return false;
                }
                SteamFriends.ActivateGameOverlayInviteDialog(__instance.LobbySteamID);
            }
            catch (Exception ex) { Log.E("TryOpenInviteInterface_Prefix failed", ex); }
            return false;
        }

        [HarmonyPatch("OnLobbyEntered")]
        [HarmonyPrefix]
        private static void OnLobbyEntered_Prefix(LobbyEnter_t result)
        {
            try
            {
                var lobbyId = new CSteamID(result.m_ulSteamIDLobby);
                var hostVer = SteamMatchmaking.GetLobbyData(lobbyId, "version");
                var owner = SteamMatchmaking.GetLobbyOwner(lobbyId);
                bool isHost = owner.m_SteamID == Log.LocalSteamId;
                bool versionMatches = hostVer == Application.version;
                Log.I($"OnLobbyEntered: lobby={result.m_ulSteamIDLobby} owner={owner.m_SteamID} isHost={isHost} hostVer='{hostVer}' clientVer='{Application.version}' wouldVanillaMatch={versionMatches}");
            }
            catch (Exception ex) { Log.E("OnLobbyEntered_Prefix failed", ex); }
        }

        [HarmonyPatch("OnLobbyEntered")]
        [HarmonyPostfix]
        private static void OnLobbyEntered_Postfix(Lobby __instance, LobbyEnter_t result)
        {
            try
            {
                Log.I($"OnLobbyEntered done: IsInLobby={__instance.IsInLobby} LobbyID={__instance.LobbyID} PlayerCount={__instance.PlayerCount}");
            }
            catch (Exception ex) { Log.E("OnLobbyEntered_Postfix failed", ex); }
        }

        // Vanilla OnLobbyEntered does:  if (lobbyData != Application.version) { popup; LeaveLobby; return; }
        // Two clients on slightly different sub-branches (alternate vs alternate-beta) can have
        // different Application.version strings even if the displayed numbers look identical.
        // Replace the single string op_Inequality call in the method with `pop; pop; ldc.i4.0`
        // so the comparison always evaluates to false and the early-return is skipped.
        [HarmonyPatch("OnLobbyEntered")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> OnLobbyEntered_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var inequality = AccessTools.Method(typeof(string), "op_Inequality",
                new[] { typeof(string), typeof(string) });
            var codes = instructions.ToList();
            bool patched = false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (!patched
                    && codes[i].opcode == OpCodes.Call
                    && codes[i].operand is MethodInfo mi
                    && mi == inequality)
                {
                    codes[i] = new CodeInstruction(OpCodes.Pop);
                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Ldc_I4_0));
                    patched = true;
                }
            }
            if (patched)
                Log.I("OnLobbyEntered transpiler: version check bypass applied");
            else
                Log.W("OnLobbyEntered transpiler: op_Inequality(string,string) NOT found - version check IS still active");
            return codes;
        }

        [HarmonyPatch("PlayerEnterOrLeave")]
        [HarmonyPostfix]
        private static void PlayerEnterOrLeave_Postfix(Lobby __instance, LobbyChatUpdate_t result)
        {
            try
            {
                ulong changed = result.m_ulSteamIDUserChanged;
                ulong by = result.m_ulSteamIDMakingChange;
                uint state = result.m_rgfChatMemberStateChange;
                string action = state switch
                {
                    1 => "ENTERED",
                    2 => "LEFT",
                    4 => "DISCONNECTED",
                    8 => "KICKED",
                    16 => "BANNED",
                    _ => $"state={state}",
                };
                Log.I($"Lobby change: {changed} {action} (by {by}). PlayerCount now {__instance.PlayerCount}");
            }
            catch (Exception ex) { Log.E("PlayerEnterOrLeave_Postfix failed", ex); }
        }
    }

    internal static class PlayerPatches
    {
        public static bool SendPlayerNameData_Prefix(Player __instance, string playerName, ulong id)
        {
            try
            {
                Log.I($"SendPlayerNameData: name='{playerName}' id={id} target='{__instance.gameObject.name}'");
                __instance.ReceivePlayerNameData(null, playerName, id.ToString());
                __instance.PlayerName = playerName;
                __instance.PlayerCode = id.ToString();
            }
            catch (Exception ex) { Log.E("SendPlayerNameData_Prefix failed", ex); }
            return false;
        }
    }
}
