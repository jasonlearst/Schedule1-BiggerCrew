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

[assembly: MelonInfo(typeof(MultiplayerFix.Core), "MultiplayerFix", "1.0.0", "you")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace MultiplayerFix
{
    public class Core : MelonMod
    {
        public const int LOBBY_CAPACITY = 16;

        private HarmonyLib.Harmony _harmony;

        public override void OnInitializeMelon()
        {
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
                MelonLogger.Msg($"Patched {sendNameRpc.Name}");
            }
            else
            {
                MelonLogger.Warning("RpcLogic___SendPlayerNameData_* not found - name sync will be vanilla");
            }

            MelonLogger.Msg($"Loaded - lobby capacity {LOBBY_CAPACITY}");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
                MelonCoroutines.Start(WaitForLobby());
        }

        private static IEnumerator WaitForLobby()
        {
            while (Singleton<Lobby>.Instance == null)
                yield return null;
            var lobby = Singleton<Lobby>.Instance;
            lobby.Players = new CSteamID[LOBBY_CAPACITY];

            if (!SteamManager.Initialized)
            {
                lobby.gameObject.SetActive(false);
                yield break;
            }

            while (Singleton<LobbyInterface>.Instance == null)
                yield return null;
            var lobbyUI = Singleton<LobbyInterface>.Instance;

            var entries = lobbyUI.GetComponentInChildren<GridLayoutGroup>().transform;
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
        }
    }

    [HarmonyPatch(typeof(LobbyInterface))]
    internal static class LobbyInterfacePatches
    {
        [HarmonyPatch("Awake")]
        [HarmonyPostfix]
        private static void Awake_Postfix(LobbyInterface __instance)
        {
            if (__instance.Lobby == null) return;
            var lobby = __instance.Lobby;
            lobby.onLobbyChange = null;
            lobby.onLobbyChange = (Action)Delegate.Combine(lobby.onLobbyChange, (Action)delegate
            {
                __instance.UpdateButtons();
                __instance.UpdatePlayers();
                __instance.LobbyTitle.text =
                    $"Lobby ({lobby.PlayerCount}/{Core.LOBBY_CAPACITY})";
            });
        }

        [HarmonyPatch("UpdateButtons")]
        [HarmonyPrefix]
        private static bool UpdateButtons_Prefix(LobbyInterface __instance)
        {
            __instance.InviteButton.gameObject.SetActive(
                __instance.Lobby.IsHost && __instance.Lobby.PlayerCount < Core.LOBBY_CAPACITY);
            __instance.LeaveButton.gameObject.SetActive(!__instance.Lobby.IsHost);
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
            if (!__instance.IsInLobby)
                __instance.CreateLobby();
            if (SteamMatchmaking.GetNumLobbyMembers(__instance.LobbySteamID) >= Core.LOBBY_CAPACITY)
                return false;
            SteamFriends.ActivateGameOverlayInviteDialog(__instance.LobbySteamID);
            return false;
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
                MelonLogger.Msg("OnLobbyEntered version check bypassed");
            else
                MelonLogger.Warning("OnLobbyEntered version check NOT patched - IL pattern not found");
            return codes;
        }
    }

    internal static class PlayerPatches
    {
        public static bool SendPlayerNameData_Prefix(Player __instance, string playerName, ulong id)
        {
            __instance.ReceivePlayerNameData(null, playerName, id.ToString());
            __instance.PlayerName = playerName;
            __instance.PlayerCode = id.ToString();
            return false;
        }
    }
}
