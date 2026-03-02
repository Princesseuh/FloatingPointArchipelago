using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FloatingPointArchipelago
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID    = "com.archipelago.floatingpoint";
        public const string PLUGIN_NAME    = "Floating Point Archipelago";
        public const string PLUGIN_VERSION = "1.0.0";

        internal static new ManualLogSource Logger;
        private Harmony _harmony;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"{PLUGIN_NAME} {PLUGIN_VERSION} loading...");

            // Init singletons
            ArchipelagoClient.Init();
            LocationManager.Init();

            // Attach MonoBehaviour managers to a persistent GameObject
            var managerGO = new GameObject("ArchipelagoManagers");
            DontDestroyOnLoad(managerGO);
            managerGO.AddComponent<ConnectionUI>();
            managerGO.AddComponent<ItemManager>();

            // Apply Harmony patches
            _harmony = new Harmony(PLUGIN_GUID);
            _harmony.PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} loaded. Press F1 in-game to connect to Archipelago.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // ────────────────────────────────────────────────────────────────────────
        // Harmony Patches
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Track bar collection by watching GenerateLevel.barsCollected, and
        /// detect when the player presses Enter (transition flips to "Harder").
        /// We use a Prefix to snapshot the transition state before Update runs,
        /// then a Postfix to detect the flip and forward bar/score events.
        /// </summary>
        [HarmonyPatch(typeof(GenerateLevel), "Update")]
        static class Patch_GenerateLevel_Update
        {
            static float s_prevBarsCollected = -1f;
            static string s_prevTransition = null;

            static void Prefix(GenerateLevel __instance)
            {
                s_prevTransition = __instance.transition;
            }

            static void Postfix(GenerateLevel __instance)
            {
                // Detect Enter key: transition flips None → Harder
                if (s_prevTransition == "None" && __instance.transition == "Harder")
                {
                    Logger.LogInfo("Detected Enter key (transition None→Harder).");
                    LocationManager.Instance?.OnLevelAdvancedByEnter();
                    s_prevBarsCollected = -1f;
                    return;
                }

                float current = __instance.barsCollected;

                if (s_prevBarsCollected < 0f)
                {
                    s_prevBarsCollected = current;
                    return;
                }

                while (s_prevBarsCollected < current)
                {
                    int barIndex = Mathf.RoundToInt(s_prevBarsCollected);
                    LocationManager.Instance?.OnBarCollected(barIndex);
                    s_prevBarsCollected++;
                }

                var swing = ItemManager.Instance != null ? ItemManager.Instance.SwingScript : null;
                if (swing != null)
                    LocationManager.Instance?.OnScoreTick(swing.score);
            }

            public static void Reset() { s_prevBarsCollected = -1f; }
        }

        /// <summary>
        /// When Water Access has not been unlocked, treat y=0 as a solid floor.
        /// </summary>
        [HarmonyPatch(typeof(SwingFromRope), "FixedUpdate")]
        static class Patch_SwingFromRope_FixedUpdate
        {
            static void Prefix(SwingFromRope __instance)
            {
                if (ItemManager.Instance == null) return;
                if (ItemManager.Instance.WaterAccessUnlocked) return;

                var rb = __instance.rigidbody;
                if (rb == null) return;

                if (rb.position.y < 0f)
                {
                    rb.position = new Vector3(rb.position.x, 0f, rb.position.z);
                    if (rb.velocity.y < 0f)
                        rb.velocity = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
                }
            }
        }

        /// <summary>
        /// Patch GenerateLevel.Regenerate to track level transitions.
        /// </summary>
        [HarmonyPatch(typeof(GenerateLevel), "Regenerate")]
        static class Patch_GenerateLevel_Regenerate
        {
            static void Prefix()
            {
                LocationManager.Instance?.OnLevelRegenerated();
                Patch_GenerateLevel_Update.Reset();
            }
        }

        /// <summary>
        /// Patch SwingFromRope.Start to capture the reference for ItemManager
        /// and apply hobbled starting physics values.
        /// </summary>
        [HarmonyPatch(typeof(SwingFromRope), "Start")]
        static class Patch_SwingFromRope_Start
        {
            static void Postfix(SwingFromRope __instance)
            {
                if (ItemManager.Instance != null)
                {
                    ItemManager.Instance.SwingScript = __instance;
                    ItemManager.Instance.ApplyPhysicsToScript(__instance);
                }
            }
        }

        /// <summary>
        /// Patch GenerateLevel.Start to capture the reference for ItemManager.
        /// </summary>
        [HarmonyPatch(typeof(GenerateLevel), "Start")]
        static class Patch_GenerateLevel_Start
        {
            static void Postfix(GenerateLevel __instance)
            {
                if (ItemManager.Instance != null)
                    ItemManager.Instance.GenerateScript = __instance;
                Logger.LogInfo("GenerateLevel started — ItemManager reference captured.");
            }
        }

        /// <summary>
        /// When the water gate is active, replaces GenerateLevel.Generate entirely to
        /// run two separate passes: one that places exactly WATER_GATED_BAR_START bars
        /// above water (y > 0), and one that places the remaining bars below (y &lt; 0).
        /// Filler blocks (no bar) go in whichever half they belong to.
        /// When the water gate is inactive the original method runs unchanged.
        /// </summary>
        [HarmonyPatch(typeof(GenerateLevel), "Generate")]
        static class Patch_GenerateLevel_Generate
        {
            static bool Prefix(GenerateLevel __instance, float depth)
            {
                if (ItemManager.Instance == null || !ItemManager.Instance.WaterGateActive)
                    return true; // run original

                var t = Traverse.Create(__instance);

                t.Method("DestroyEverythingAt", depth).GetValue();
                t.Method("PositionFloorAndWater").GetValue();
                __instance.barsCollected = 0f;

                float levelHalfHeight  = __instance.levelHalfHeight;
                float levelWidth       = __instance.levelWidth;
                float blockWidth       = __instance.blockWidth;
                float blockHeight      = __instance.blockHeight;
                float blockDensityFactor = __instance.blockDensityFactor;
                float blockDensity     = t.Field("blockDensity").GetValue<float>();
                float blockDepth       = t.Field("blockDepth").GetValue<float>();
                float barsOnThisLevel  = __instance.barsOnThisLevel;

                int totalBars   = Mathf.FloorToInt(barsOnThisLevel);
                int aboveBars   = LocationData.WATER_GATED_BAR_START;
                int belowBars   = totalBars - aboveBars;

                int totalBlocks = (int)(blockDensity * levelWidth * levelHalfHeight * blockDensityFactor);
                if (totalBlocks < totalBars) totalBlocks = totalBars;

                // Split filler blocks proportionally between halves.
                int fillerTotal  = totalBlocks - totalBars;
                int aboveFillerExtra = fillerTotal / 2;
                int belowFillerExtra = fillerTotal - aboveFillerExtra;

                SpawnHalf(__instance, depth, levelHalfHeight,  levelWidth, blockWidth, blockHeight, blockDepth, aboveBars,  aboveFillerExtra, above: true);
                SpawnHalf(__instance, depth, levelHalfHeight,  levelWidth, blockWidth, blockHeight, blockDepth, belowBars,  belowFillerExtra, above: false);

                return false; // skip original
            }

            static void SpawnHalf(GenerateLevel gen, float depth, float halfHeight,
                float levelWidth, float blockWidth, float blockHeight, float blockDepth,
                int barCount, int fillerCount, bool above)
            {
                int total = barCount + fillerCount;
                int num2  = barCount; // bars remaining to place
                float xMin = levelWidth * -0.5f;
                float xMax = levelWidth *  0.5f;
                float yMin = above ?  1f : -halfHeight;
                float yMax = above ?  halfHeight : -1f;

                for (int i = 0; i < total; i++)
                {
                    float num3 = Random.Range(5f, blockHeight);
                    float blockY = Random.Range(yMin, yMax);
                    float blockX = Random.Range(xMin + num3 / 2f, xMax - num3 / 2f);

                    var block = Object.Instantiate(gen.blockType,
                        new Vector3(blockX, blockY, depth),
                        Quaternion.identity) as GameObject;
                    block.transform.localScale = new Vector3(
                        Random.Range(5f, blockWidth), num3, Random.Range(5f, blockDepth));

                    float num6 = blockY > 0f
                        ? blockY + num3 / 2f
                        : blockY - num3 / 2f;
                    float lightY = blockY > 0f ? num6 + 1f : num6 - 1f;

                    Object.Instantiate(gen.lightType,
                        new Vector3(Random.Range(blockX - block.transform.localScale.x / 2f,
                                                 blockX + block.transform.localScale.x / 2f),
                                    lightY,
                                    depth + block.transform.localScale.z * -1f),
                        Quaternion.identity);

                    // Place bar on last num2 blocks (same reservoir logic as original).
                    int remaining = total - i;
                    if (num2 > 0 && Random.Range(0f, 1f) < (float)num2 / remaining)
                    {
                        Object.Instantiate(gen.pickupType,
                            new Vector3(blockX, num6, depth),
                            Quaternion.identity);
                        num2--;
                    }
                }
            }
        }

        /// <summary>
        /// Block grapple firing when GrappleBlocked: spoof grappleDeployed=true on the
        /// click frame so the game's own !grappleDeployed guard blocks the fire,
        /// then restore the real value afterwards.
        /// </summary>
        [HarmonyPatch(typeof(GrappleGun), "Update")]
        static class Patch_GrappleGun_Update
        {
            static bool s_loggedOnce = false;
            static bool s_wasDeployed = false;
            static bool s_spoofed = false;

            static void Prefix(GrappleGun __instance)
            {
                s_spoofed = false;
                if (ItemManager.Instance == null) return;
                if (!ItemManager.Instance.GrappleBlocked) { s_loggedOnce = false; return; }

                if (!Input.GetButtonDown("Click")) return;

                s_wasDeployed = __instance.grappleDeployed;
                __instance.grappleDeployed = true;
                s_spoofed = true;

                if (!s_loggedOnce)
                {
                    Logger.LogInfo("Grapple fire blocked — Grapple Unlock not yet received or trap active.");
                    s_loggedOnce = true;
                }
                string msg = ItemManager.Instance.GrappleUnlocked
                    ? "Grapple disconnected! (locked for 2s)"
                    : "Grapple locked! Find the Grapple Unlock item.";
                ConnectionUI.Instance?.ShowMessage(msg, 3f);
            }

            static void Postfix(GrappleGun __instance)
            {
                if (s_spoofed)
                {
                    __instance.grappleDeployed = s_wasDeployed;
                    s_spoofed = false;
                }
            }
        }

        /// <summary>
        /// Secondary block on FireGrappleTo as a safety net for any direct callers.
        /// </summary>
        [HarmonyPatch(typeof(SwingFromRope), "FireGrappleTo")]
        static class Patch_SwingFromRope_FireGrappleTo
        {
            static bool Prefix()
            {
                if (ItemManager.Instance == null) return true;
                if (!ItemManager.Instance.GrappleBlocked) return true;
                return false;
            }
        }

        /// <summary>
        /// After Central.RestoreDefaults resets all physics to game defaults,
        /// re-assert our hobbled/upgraded values so the mod stays in full control.
        /// </summary>
        [HarmonyPatch(typeof(Central), "RestoreDefaults")]
        static class Patch_Central_RestoreDefaults
        {
            static void Postfix()
            {
                var im = ItemManager.Instance;
                if (im?.SwingScript == null) return;
                im.ApplyPhysicsToScript(im.SwingScript);
                Logger.LogInfo("[RestoreDefaults] Physics re-applied after Central.RestoreDefaults.");
            }
        }

        /// <summary>
        /// After Central.ReadTweaksFile potentially overwrites physics from a file,
        /// re-assert our hobbled/upgraded values so the mod stays in full control.
        /// </summary>
        [HarmonyPatch(typeof(Central), "ReadTweaksFile")]
        static class Patch_Central_ReadTweaksFile
        {
            static void Postfix()
            {
                var im = ItemManager.Instance;
                if (im?.SwingScript == null) return;
                im.ApplyPhysicsToScript(im.SwingScript);
                Logger.LogInfo("[ReadTweaksFile] Physics re-applied after Central.ReadTweaksFile.");
            }
        }
    }
}
