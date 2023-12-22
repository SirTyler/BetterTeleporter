using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;

using GameNetcodeStuff;
using UnityEngine;
using Unity.Netcode;
using static Unity.Netcode.CustomMessagingManager;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

using BetterTeleporter.Patches;
using BetterTeleporter.Config;


namespace BetterTeleporter
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        internal static ManualLogSource log;
        public static Plugin instance { get; private set; }

        private void Awake()
        {
            instance = this;
            log = this.Logger;
            ConfigSettings.Bind();
            harmony.PatchAll(typeof(ConfigSync));
            harmony.PatchAll(typeof(ShipTeleporterPatch));
            harmony.PatchAll(typeof(StartOfRoundPatch));
            log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }
    }
}
namespace BetterTeleporter.Patches
{
    [HarmonyPatch(typeof(ShipTeleporter))]
    public class ShipTeleporterPatch
    {
        private static readonly CodeMatch[] inverseTeleporterPatchIlMatch = new CodeMatch[] {
            new CodeMatch(i => i.IsLdloc()),
            new CodeMatch(i => i.LoadsConstant(1)),
            new CodeMatch(i => i.LoadsConstant(0)),
            new CodeMatch(i => i.Calls(originalMethodInfo))
        };
        private static readonly CodeMatch[] teleporterPatchIlMatch = new CodeMatch[] {
            new CodeMatch(i => i.IsLdarg(0)),
            new CodeMatch(i => i.opcode == OpCodes.Ldfld),
            new CodeMatch(i => i.LoadsConstant(1)),
            new CodeMatch(i => i.LoadsConstant(0)),
            new CodeMatch(i => i.Calls(originalMethodInfo))
        };

        private static readonly MethodInfo originalMethodInfo = typeof(PlayerControllerB).GetMethod("DropAllHeldItems", BindingFlags.Instance | BindingFlags.Public);
        private static readonly MethodInfo replaceMethodInfo = typeof(ShipTeleporterPatch).GetMethod("DropSomeItems", BindingFlags.Static | BindingFlags.NonPublic);

        [HarmonyTranspiler, HarmonyPatch("TeleportPlayerOutWithInverseTeleporter")]
        public static IEnumerable<CodeInstruction> InverseTeleporterDropAllButHeldItem(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher codeMatcher = new CodeMatcher(instructions);

            codeMatcher.Start();
            codeMatcher.MatchForward(false, inverseTeleporterPatchIlMatch);
            codeMatcher.Advance(1);
            codeMatcher.RemoveInstructionsWithOffsets(0, 2);
            codeMatcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldc_I4_0, 1));
            codeMatcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldc_I4_1, 1));
            codeMatcher.Insert(new CodeInstruction(OpCodes.Callvirt, replaceMethodInfo));

            Plugin.log.LogInfo("Patched 'ShipTeleporterPatch.TeleportPlayerOutWithInverseTeleporter'");

            return codeMatcher.Instructions();
        }

        [HarmonyTranspiler, HarmonyPatch("beamUpPlayer", MethodType.Enumerator)]
        public static IEnumerable<CodeInstruction> TeleporterDropAllButHeldItem(IEnumerable<CodeInstruction> instructions)
        {
            CodeMatcher codeMatcher = new CodeMatcher(instructions);

            codeMatcher.End();
            codeMatcher.MatchBack(false, teleporterPatchIlMatch);
            codeMatcher.Advance(2);
            codeMatcher.RemoveInstructionsWithOffsets(0, 2);
            codeMatcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldc_I4_0, 0));
            codeMatcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldc_I4_1, 1));
            codeMatcher.Insert(new CodeInstruction(OpCodes.Callvirt, replaceMethodInfo));

            Plugin.log.LogInfo("Patched 'ShipTeleporterPatch.beamUpPlayer'");

            return codeMatcher.Instructions();
        }

        [HarmonyPatch("Awake"), HarmonyPrefix]
        private static void Awake(ref bool ___isInverseTeleporter, ref float ___cooldownAmount)
        {
            if (___isInverseTeleporter) ___cooldownAmount = ConfigSettings.cooldownAmmountInverse;
            else ___cooldownAmount = ConfigSettings.cooldownAmmount;
        }

        private static void DropSomeItems(PlayerControllerB player, bool inverse = false, bool itemsFall = true)
        {
            MethodInfo methodInfo = player.GetType().GetMethod("SetSpecialGrabAnimationBool", BindingFlags.NonPublic | BindingFlags.Instance);

            var keepList = ConfigSettings.keepListItems;
            if (inverse) keepList = ConfigSettings.keepListItemsInverse;

            float weight = 1f;
            bool twohanded = false;

            for (int i = 0; i < player.ItemSlots.Length; i++)
            {
                GrabbableObject grabbableObject = player.ItemSlots[i];
                if (grabbableObject == null) continue;

                if (keepList.Contains(grabbableObject.GetType().ToString()))
                {
                    if (grabbableObject.insertedBattery != null && ConfigSettings.doDrainItems)
                    {
                        float new_charge = grabbableObject.insertedBattery.charge - (grabbableObject.insertedBattery.charge * ConfigSettings.drainItemsPercent);
                        if (new_charge < 0) new_charge = 0;

                        grabbableObject.insertedBattery = new Battery(isEmpty: (new_charge != 0f), new_charge);
                        grabbableObject.SyncBatteryServerRpc((int)(new_charge * 100f));
                    }
                    weight += Mathf.Clamp(grabbableObject.itemProperties.weight - 1f, 0f, 10f);
                    continue;
                }

                if (itemsFall)
                {
                    grabbableObject.parentObject = null;
                    grabbableObject.heldByPlayerOnServer = false;
                    if (player.isInElevator)
                    {
                        grabbableObject.transform.SetParent(player.playersManager.elevatorTransform, worldPositionStays: true);
                    }
                    else
                    {
                        grabbableObject.transform.SetParent(player.playersManager.propsContainer, worldPositionStays: true);
                    }

                    player.SetItemInElevator(player.isInHangarShipRoom, player.isInElevator, grabbableObject);
                    grabbableObject.EnablePhysics(enable: true);
                    grabbableObject.EnableItemMeshes(enable: true);
                    grabbableObject.transform.localScale = grabbableObject.originalScale;
                    grabbableObject.isHeld = false;
                    grabbableObject.isPocketed = false;
                    grabbableObject.startFallingPosition = grabbableObject.transform.parent.InverseTransformPoint(grabbableObject.transform.position);
                    grabbableObject.FallToGround(randomizePosition: true);
                    grabbableObject.fallTime = UnityEngine.Random.Range(-0.3f, 0.05f);
                    if (player.IsOwner)
                    {
                        grabbableObject.DiscardItemOnClient();
                    }
                    else if (!grabbableObject.itemProperties.syncDiscardFunction)
                    {
                        grabbableObject.playerHeldBy = null;
                    }
                }

                if (player.IsOwner)
                {
                    HUDManager.Instance.holdingTwoHandedItem.enabled = false;
                    HUDManager.Instance.itemSlotIcons[i].enabled = false;
                    HUDManager.Instance.ClearControlTips();
                    player.activatingItem = false;
                }

                player.ItemSlots[i] = null;
            }

            GrabbableObject held = player.ItemSlots[player.currentItemSlot];
            if (held == null)
            {
                player.isHoldingObject = false;
                if (player.currentlyHeldObjectServer != null)
                {
                    methodInfo.Invoke(player, new object[] { false, player.currentlyHeldObjectServer });
                }

                player.playerBodyAnimator.SetBool("cancelHolding", value: true);
                player.playerBodyAnimator.SetTrigger("Throw");
            }
            else twohanded = held.itemProperties.twoHanded;

            player.twoHanded = twohanded;
            player.carryWeight = weight;
            player.currentlyHeldObjectServer = held;
        }
    }

    [HarmonyPatch(typeof(StartOfRound))]
    internal class StartOfRoundPatch
    {
        private static readonly FieldInfo cooldownProp = typeof(ShipTeleporter).GetField("cooldownTime", BindingFlags.Instance | BindingFlags.NonPublic);

        [HarmonyPatch("StartGame"), HarmonyPostfix]
        private static void StartGame()
        {
            if (ConfigSettings.cooldownEnd) ResetCooldown();
        }

        [HarmonyPatch("EndOfGame"), HarmonyPostfix]
        private static void EndOfGame()
        {
            if (ConfigSettings.cooldownEnd) ResetCooldown();
        }

        [HarmonyPatch("EndOfGameClientRpc"), HarmonyPostfix]
        private static void EndOfGameClientRpc()
        {
            if (ConfigSettings.cooldownEnd) ResetCooldown();
        }

        private static void ResetCooldown()
        {
            ShipTeleporter[] array = UnityEngine.Object.FindObjectsOfType<ShipTeleporter>();
            foreach (ShipTeleporter obj in array)
            {
                cooldownProp.SetValue(obj, 0f);
            }
        }
    }
}
namespace BetterTeleporter.Config
{
    public static class ConfigSettings
    {
        public static int cooldownAmmount;
        public static int cooldownAmmountInverse;
        public static bool cooldownEnd;
        public static string[] keepListItems;
        public static string[] keepListItemsInverse;
        public static bool doDrainItems;
        public static float drainItemsPercent;

        public static ConfigEntry<int> cooldown;
        public static ConfigEntry<int> cooldownInverse;
        public static ConfigEntry<bool> cooldownEndDay;
        public static ConfigEntry<string> keepList;
        public static ConfigEntry<string> keepListInverse;
        public static ConfigEntry<bool> doDrain;
        public static ConfigEntry<float> drainPercent;

        public static void Bind()
        {
            cooldown = ((BaseUnityPlugin)Plugin.instance).Config.Bind<int>("General", "Cooldown", 10, "Number of seconds between teleporter uses");
            cooldownInverse = ((BaseUnityPlugin)Plugin.instance).Config.Bind<int>("General", "CooldownInverse", 210, "Number of seconds between teleporter uses");
            cooldownEndDay = ((BaseUnityPlugin)Plugin.instance).Config.Bind<bool>("General", "CooldownEndsOnNewDay", true, "true/false if cooldown should end on new day");
            keepList = ((BaseUnityPlugin)Plugin.instance).Config.Bind<string>("General", "KeepItemList", "KeyItem,FlashlightItem,WalkieTalkie", "Comma-seperated list of items to be kept when teleported");
            keepListInverse = ((BaseUnityPlugin)Plugin.instance).Config.Bind<string>("General", "KeepItemListInverse", "KeyItem,FlashlightItem,WalkieTalkie,RadarBoosterItem", "Comma-seperated list of items to be kept when teleported with inverse teleporter");
            doDrain = ((BaseUnityPlugin)Plugin.instance).Config.Bind<bool>("General", "DrainItem", true, "true/false if items should drain battery charge");
            drainPercent = ((BaseUnityPlugin)Plugin.instance).Config.Bind<float>("General", "DrainPercent", 0.5f, "The percentage (as float 0 to 1) of total charge that battery items lose when teleporting");

            cooldownAmmount = cooldown.Value;
            cooldownAmmountInverse = cooldownInverse.Value;
            cooldownEnd = cooldownEndDay.Value;
            SetKeepList(keepList.Value, false);
            SetKeepList(keepListInverse.Value, true);
            doDrainItems = doDrain.Value;
            drainItemsPercent = drainPercent.Value;
        }

        public static void SetKeepList(string list, bool inverse = false)
        {
            if (inverse)
            {
                keepListItemsInverse = list.Split(',');
                for (int i = 0; i < keepListItemsInverse.Length; i++)
                {
                    keepListItemsInverse[i] = keepListItemsInverse[i].Trim();
                }
            }
            else
            {
                keepListItems = list.Split(',');
                for (int i = 0; i < keepListItems.Length; i++)
                {
                    keepListItems[i] = keepListItems[i].Trim();
                }
            }
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB))]
    public class ConfigSync
    {
        [HarmonyPatch("ConnectClientToPlayerObject"), HarmonyPostfix]
        public static void InitializeLocalPlayer()
        {
            if (NetworkManager.Singleton.IsServer)
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("BetterTeleporterConfigSync", new HandleNamedMessageDelegate(OnReceiveConfigSyncRequest));
            }
            else
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("BetterTeleporterReceiveConfigSync", new HandleNamedMessageDelegate(OnReceiveConfigSync));
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("BetterTeleporterReceiveConfigSync_KeepList", new HandleNamedMessageDelegate(OnReceiveConfigSync_KeepList));
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("BetterTeleporterReceiveConfigSync_KeepListInverse", new HandleNamedMessageDelegate(OnReceiveConfigSync_KeepListInverse));
                RequestConfigSync();
            }
        }

        public static void RequestConfigSync()
        {
            if (NetworkManager.Singleton.IsClient)
            {
                Plugin.log.LogInfo("Sending config sync request to server.");
                FastBufferWriter val = new FastBufferWriter(16, Unity.Collections.Allocator.Temp, -1);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("BetterTeleporterConfigSync", 0ul, val, NetworkDelivery.ReliableSequenced);
            }
            else
            {
                Plugin.log.LogWarning("Failed to send config sync request.");
            }
        }

        public static void OnReceiveConfigSyncRequest(ulong clientId, FastBufferReader reader)
        {
            if (NetworkManager.Singleton.IsServer)
            {
                Plugin.log.LogInfo("Receiving sync request from client with id: " + clientId + ". Sending config sync to client.");
                FastBufferWriter val = new FastBufferWriter((sizeof(int) * 2) + (sizeof(bool) * 2) + sizeof(float), Unity.Collections.Allocator.Temp, -1);
                val.WriteValueSafe<int>(ConfigSettings.cooldown.Value, default(FastBufferWriter.ForPrimitives));
                val.WriteValueSafe<int>(ConfigSettings.cooldownInverse.Value, default(FastBufferWriter.ForPrimitives));
                val.WriteValueSafe<bool>(ConfigSettings.cooldownEndDay.Value, default(FastBufferWriter.ForPrimitives));
                val.WriteValueSafe<bool>(ConfigSettings.doDrain.Value, default(FastBufferWriter.ForPrimitives));
                val.WriteValueSafe<float>(ConfigSettings.drainPercent.Value, default(FastBufferWriter.ForPrimitives));
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("BetterTeleporterReceiveConfigSync", clientId, val, NetworkDelivery.ReliableSequenced);

                FastBufferWriter val2 = new FastBufferWriter((ConfigSettings.keepList.Value.Length) * sizeof(char), Unity.Collections.Allocator.Temp, -1);
                val2.WriteValueSafe(ConfigSettings.keepList.Value, true);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("BetterTeleporterReceiveConfigSync_KeepList", clientId, val2, NetworkDelivery.ReliableSequenced);

                FastBufferWriter val3 = new FastBufferWriter((ConfigSettings.keepListInverse.Value.Length) * sizeof(char), Unity.Collections.Allocator.Temp, -1);
                val3.WriteValueSafe(ConfigSettings.keepListInverse.Value, true);
                NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("BetterTeleporterReceiveConfigSync_KeepListInverse", clientId, val3, NetworkDelivery.ReliableSequenced);
            }
        }

        public static void OnReceiveConfigSync(ulong clientId, FastBufferReader reader)
        {
            if (((FastBufferReader)reader).TryBeginRead(4))
            {
                Plugin.log.LogInfo("Receiving sync from server.");

                ((FastBufferReader)reader).ReadValueSafe<int>(out int coolA);
                ConfigSettings.cooldownAmmount = coolA;
                Plugin.log.LogInfo($"Recieved 'cooldownAmmount = {coolA}");
                ((FastBufferReader)reader).ReadValueSafe<int>(out int coolB);
                ConfigSettings.cooldownAmmountInverse = coolB;
                Plugin.log.LogInfo($"Recieved 'cooldownAmmountInverse = {coolB}");
                ((FastBufferReader)reader).ReadValueSafe<bool>(out bool coolDay);
                ConfigSettings.cooldownEnd = coolDay;
                Plugin.log.LogInfo($"Recieved 'cooldownEnd = {coolDay}");
                ((FastBufferReader)reader).ReadValueSafe<bool>(out bool doDrain);
                ConfigSettings.doDrainItems = doDrain;
                Plugin.log.LogInfo($"Recieved 'doDrainItems = {doDrain}");
                ((FastBufferReader)reader).ReadValueSafe<float>(out float drainPercent);
                ConfigSettings.drainItemsPercent = drainPercent;
                Plugin.log.LogInfo($"Recieved 'drainItemsPercent = {drainPercent}");
            }
            else
            {
                Plugin.log.LogWarning("Error receiving config sync from server.");
            }
        }

        public static void OnReceiveConfigSync_KeepList(ulong clientId, FastBufferReader reader)
        {
            if (((FastBufferReader)reader).TryBeginRead(4))
            {
                Plugin.log.LogInfo("Receiving sync from server.");

                ((FastBufferReader)reader).ReadValueSafe(out string list, true);
                ConfigSettings.SetKeepList(list, false);
                Plugin.log.LogInfo($"Recieved 'keepList = {list}");
            }
            else
            {
                Plugin.log.LogWarning("Error receiving keepList config sync from server.");
            }
        }

        public static void OnReceiveConfigSync_KeepListInverse(ulong clientId, FastBufferReader reader)
        {
            if (((FastBufferReader)reader).TryBeginRead(4))
            {
                Plugin.log.LogInfo("Receiving sync from server.");

                ((FastBufferReader)reader).ReadValueSafe(out string list, true);
                ConfigSettings.SetKeepList(list, true);
                Plugin.log.LogInfo($"Recieved 'keepListInverse = {list}");
            }
            else
            {
                Plugin.log.LogWarning("Error receiving keepListInverse config sync from server.");
            }
        }
    }
}