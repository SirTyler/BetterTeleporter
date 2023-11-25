using BepInEx;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using System.Reflection;
using UnityEngine;

using BetterTeleporter.Patches;

namespace BetterTeleporter
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        internal ManualLogSource log;
        public static Plugin instance { get; private set; }

        private void Awake()
        {
            instance = this;
            log = this.Logger;
            harmony.PatchAll(typeof(BetterInversePatch));
            log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }
    }
}
namespace BetterTeleporter.Patches
{
    [HarmonyPatch(typeof(ShipTeleporter))] 
    internal class BetterInversePatch
    {
        [HarmonyPatch("TeleportPlayerOutWithInverseTeleporter")]
        [HarmonyPrefix]
        private static bool TeleportPlayerOutWithInverseTeleporter(ShipTeleporter __instance, ref int[] ___playersBeingTeleported, int playerObj, Vector3 teleportPos)
        {
            if (StartOfRound.Instance.allPlayerScripts[playerObj].isPlayerDead)
            {
                return true;
            }

            PlayerControllerB playerControllerB = StartOfRound.Instance.allPlayerScripts[playerObj];
            SetPlayerTeleporterId(__instance, ___playersBeingTeleported, playerControllerB, -1);
            DropSomeItems(playerControllerB);
            if ((bool)Object.FindObjectOfType<AudioReverbPresets>())
            {
                Object.FindObjectOfType<AudioReverbPresets>().audioPresets[2].ChangeAudioReverbForPlayer(playerControllerB);
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
            if ((Object)(object)playerControllerB == (Object)(object)GameNetworkManager.Instance.localPlayerController)
            {
                Debug.Log("Teleporter shaking camera");
                HUDManager.Instance.ShakeCamera(ScreenShakeType.Big);
            }

            return false;
        }

        private static void SetPlayerTeleporterId(ShipTeleporter __instance, int[] playersBeingTeleported, PlayerControllerB playerScript, int teleporterId)
        {
            playerScript.shipTeleporterId = teleporterId;
            playersBeingTeleported[playerScript.playerClientId] = (int)playerScript.playerClientId;
        }

        private static void DropSomeItems(PlayerControllerB __instance, bool itemsFall = true, bool disconnecting = false)
        {
            for (int i = 0; i < __instance.ItemSlots.Length; i++)
            {
                GrabbableObject grabbableObject = __instance.ItemSlots[i];
                if (!((UnityEngine.Object)(object)grabbableObject != null))
                {
                    continue;
                }

                if (grabbableObject is KeyItem)
                {
                    continue;
                }

                if (grabbableObject is FlashlightItem || grabbableObject is WalkieTalkie)
                {
                    float new_charge = grabbableObject.insertedBattery.charge * 0.5f;
                    if (new_charge < 0) new_charge = 0; 

                    grabbableObject.insertedBattery = new Battery(isEmpty: (new_charge != 0f), new_charge);
                    grabbableObject.SyncBatteryServerRpc((int) (new_charge * 100f));
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
}