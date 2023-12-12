using System;
using System.Reflection;
using System.Linq;

using GameNetcodeStuff;
using UnityEngine;

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
            harmony.PatchAll(typeof(ShipTeleporterPatch));
            harmony.PatchAll(typeof(StartOfRoundPatch));
            log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }
    }
}
namespace BetterTeleporter.Patches
{
    [HarmonyPatch(typeof(ShipTeleporter))] 
    internal class ShipTeleporterPatch
    {
        [HarmonyPatch("Awake")]
        [HarmonyPrefix]
        private static void Awake(ref bool ___isInverseTeleporter, ref float ___cooldownAmount)
        {
            if(___isInverseTeleporter) ___cooldownAmount = ConfigSettings.cooldownAmmountInverse;
            else ___cooldownAmount = ConfigSettings.cooldownAmmount;
        }

        [HarmonyPatch("TeleportPlayerOutWithInverseTeleporter")]
        [HarmonyPrefix]
        private static bool TeleportPlayerOutWithInverseTeleporter(ShipTeleporter __instance, ref int[] ___playersBeingTeleported, int playerObj, Vector3 teleportPos)
        {
            if (StartOfRound.Instance.allPlayerScripts[playerObj].isPlayerDead)
            {
                return true;
            }

            PlayerControllerB playerControllerB = StartOfRound.Instance.allPlayerScripts[playerObj];
            SetPlayerTeleporterId(___playersBeingTeleported, playerControllerB, -1);
            DropSomeItems(playerControllerB, true);
            if ((bool)UnityEngine.Object.FindObjectOfType<AudioReverbPresets>())
            {
                UnityEngine.Object.FindObjectOfType<AudioReverbPresets>().audioPresets[2].ChangeAudioReverbForPlayer(playerControllerB);
            }

            playerControllerB.isInElevator = false;
            playerControllerB.isInHangarShipRoom = false;
            playerControllerB.isInsideFactory = true;
            playerControllerB.averageVelocity = 0f;
            playerControllerB.velocityLastFrame = Vector3.zero;
            StartOfRound.Instance.allPlayerScripts[playerObj].TeleportPlayer(teleportPos);
            StartOfRound.Instance.allPlayerScripts[playerObj].beamOutParticle.Play();
            __instance.shipTeleporterAudio.PlayOneShot(__instance.teleporterBeamUpSFX);
            StartOfRound.Instance.allPlayerScripts[playerObj].movementAudio.PlayOneShot(__instance.teleporterBeamUpSFX);
            if ((UnityEngine.Object)(object)playerControllerB == (UnityEngine.Object)(object)GameNetworkManager.Instance.localPlayerController)
            {
                UnityEngine.Debug.Log("Teleporter shaking camera");
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            }

            return false;
        }



        private static void SetPlayerTeleporterId(int[] ___playersBeingTeleported, PlayerControllerB playerScript, int teleporterId)
        {
            playerScript.shipTeleporterId = teleporterId;
            ___playersBeingTeleported[playerScript.playerClientId] = (int)playerScript.playerClientId;
        }

        private static void DropSomeItems(PlayerControllerB __instance, bool inverse = false, bool itemsFall = true, bool disconnecting = false)
        {
            for (int i = 0; i < __instance.ItemSlots.Length; i++)
            {
                GrabbableObject grabbableObject = __instance.ItemSlots[i];
                if (!((UnityEngine.Object)(object)grabbableObject != null))
                {
                    continue;
                }

                var keepList = ConfigSettings.keepListItems;
                if (inverse) keepList = ConfigSettings.keepListItemsInverse;

                if (keepList.Contains(grabbableObject.GetType().ToString()))
                {
                    if (grabbableObject.insertedBattery != null && ConfigSettings.doDrainItems)
                    {
                        float new_charge = grabbableObject.insertedBattery.charge * ConfigSettings.drainItemsPercent;
                        if (new_charge < 0) new_charge = 0;

                        grabbableObject.insertedBattery = new Battery(isEmpty: (new_charge != 0f), new_charge);
                        grabbableObject.SyncBatteryServerRpc((int)(new_charge * 100f));
                    }
                    continue;
                }


                if (itemsFall)
                {
                    grabbableObject.parentObject = null;
                    grabbableObject.heldByPlayerOnServer = false;
                    if (__instance.isInElevator)
                    {
                        ((Component)(object)grabbableObject).transform.SetParent(__instance.playersManager.elevatorTransform, worldPositionStays: true);
                    }
                    else
                    {
                        ((Component)(object)grabbableObject).transform.SetParent(__instance.playersManager.propsContainer, worldPositionStays: true);
                    }

                    __instance.SetItemInElevator(__instance.isInHangarShipRoom, __instance.isInElevator, grabbableObject);
                    grabbableObject.EnablePhysics(enable: true);
                    grabbableObject.EnableItemMeshes(enable: true);
                    ((Component)(object)grabbableObject).transform.localScale = grabbableObject.originalScale;
                    grabbableObject.isHeld = false;
                    grabbableObject.isPocketed = false;
                    grabbableObject.startFallingPosition = ((Component)(object)grabbableObject).transform.parent.InverseTransformPoint(((Component)(object)grabbableObject).transform.position);
                    grabbableObject.FallToGround(randomizePosition: true);
                    grabbableObject.fallTime = UnityEngine.Random.Range(-0.3f, 0.05f);
                    if (__instance.IsOwner)
                    {
                        grabbableObject.DiscardItemOnClient();
                    }
                    else if (!grabbableObject.itemProperties.syncDiscardFunction)
                    {
                        grabbableObject.playerHeldBy = null;
                    }
                }

                if (__instance.IsOwner && !disconnecting)
                {
                    ((Behaviour)(object)HUDManager.Instance.holdingTwoHandedItem).enabled = false;
                    ((Behaviour)(object)HUDManager.Instance.itemSlotIcons[i]).enabled = false;
                    HUDManager.Instance.ClearControlTips();
                    __instance.activatingItem = false;
                }

                __instance.ItemSlots[i] = null;
            }

            if (__instance.isHoldingObject)
            {
                __instance.isHoldingObject = false;
                if ((UnityEngine.Object)(object)__instance.currentlyHeldObjectServer != null)
                {
                    MethodInfo methodInfo = __instance.GetType().GetMethod("SetSpecialGrabAnimationBool", BindingFlags.NonPublic | BindingFlags.Instance);
                    methodInfo.Invoke(__instance, new object[] { false, __instance.currentlyHeldObjectServer });
                }

                __instance.playerBodyAnimator.SetBool("cancelHolding", value: true);
                __instance.playerBodyAnimator.SetTrigger("Throw");
            }

            __instance.twoHanded = false;
            __instance.carryWeight = 1f;
            __instance.currentlyHeldObjectServer = null;
        }
    }

    [HarmonyPatch(typeof(StartOfRound))]
    internal class StartOfRoundPatch
    {
        private static FieldInfo cooldownProp = typeof(ShipTeleporter).GetField("cooldownTime", BindingFlags.Instance | BindingFlags.NonPublic);
        
        [HarmonyPatch("EndOfGame")]
        [HarmonyPrefix]
        private static void EndGame(StartOfRound __instance)
        {
            if(ConfigSettings.cooldownEnd) ResetCooldown(__instance);
        }

        [HarmonyPatch("StartGame")]
        [HarmonyPrefix]
        private static void StartGame(StartOfRound __instance)
        {
            if(ConfigSettings.cooldownEnd) ResetCooldown(__instance);
        }

        private static void ResetCooldown(StartOfRound instance)
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
            if(inverse)
            {
                keepListItemsInverse = list.Split(',');
                for (int i = 0; i < keepListItemsInverse.Length; i++)
                {
                    keepListItemsInverse[i] = keepListItemsInverse[i].Trim();
                }
            } else
            {
                keepListItems = list.Split(',');
                for (int i = 0; i < keepListItems.Length; i++)
                {
                    keepListItems[i] = keepListItems[i].Trim();
                }
            }
        }
    }
}