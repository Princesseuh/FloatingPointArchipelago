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
            static bool s_blockedThisFrame = false;

            // Gate + snapshot before Update runs
            static void Prefix(GenerateLevel __instance)
            {
                s_prevTransition = __instance.transition;
                s_blockedThisFrame = false;

                // Only intervene when the player is pressing Enter on an idle level
                if (__instance.transition != "None" || !Input.GetButtonDown("Harder"))
                    return;

                var itemMgr = ItemManager.Instance;

                // If level skip gating is off, let the game handle it normally
                if (itemMgr == null || !itemMgr.LevelSkipRequired)
                    return;

                // Determine whether the level is already complete
                bool levelComplete = false;
                var client = ArchipelagoClient.Instance;
                int condition = client != null
                    ? client.LevelCompleteCondition
                    : LevelCompleteCondition.AllBars;

                if (condition == LevelCompleteCondition.PressEnter)
                {
                    // press_enter mode: Enter itself is the completion trigger — always allowed
                    levelComplete = true;
                }
                else
                {
                    // all_bars mode: complete when every bar has been collected
                    levelComplete = __instance.barsCollected >= __instance.barsOnThisLevel;
                }

                if (levelComplete)
                    return; // level done — let the game advance normally

                // Level not complete — try to spend a skip
                if (itemMgr.UseSkip())
                    return; // skip consumed — let the game advance

                // No skip available — block the advance this frame
                Logger.LogInfo("Enter blocked: level not complete and no skips available.");
                ConnectionUI.Instance?.ShowMessage("Need a Level Skip to advance! (level not complete)", 3f);
                __instance.transition = "Blocked";
                s_blockedThisFrame = true;
            }

            static void Postfix(GenerateLevel __instance)
            {
                // Restore transition if we blocked it, so nothing else breaks
                if (s_blockedThisFrame)
                {
                    __instance.transition = "None";
                    s_blockedThisFrame = false;
                    return;
                }

                // ── Enter key: transition "None" → "Harder" ──────────────────
                if (s_prevTransition == "None" && __instance.transition == "Harder")
                {
                    Logger.LogInfo("Detected Enter key (transition None→Harder).");
                    LocationManager.Instance?.OnLevelAdvancedByEnter();
                    // Bar counter resets because a new level is generated — reset tracker too
                    s_prevBarsCollected = -1f;
                    return; // barsCollected also reset this frame; skip bar logic
                }

                float current = __instance.barsCollected;

                // First frame: initialise without firing
                if (s_prevBarsCollected < 0f)
                {
                    s_prevBarsCollected = current;
                    return;
                }

                // One or more bars were collected since last frame
                while (s_prevBarsCollected < current)
                {
                    int barIndex = Mathf.RoundToInt(s_prevBarsCollected); // 0-based index
                    LocationManager.Instance?.OnBarCollected(barIndex);
                    s_prevBarsCollected++;
                }

                // Score goal: tick every frame with the current score
                var swing = ItemManager.Instance != null ? ItemManager.Instance.SwingScript : null;
                if (swing != null)
                    LocationManager.Instance?.OnScoreTick(swing.score);

                // Level completion (all_bars condition)
                if (current >= __instance.barsOnThisLevel && s_prevBarsCollected >= __instance.barsOnThisLevel - 1f)
                {
                    // Only fire once per level
                    if (s_prevBarsCollected < __instance.barsOnThisLevel + 0.5f)
                    {
                        var client = ArchipelagoClient.Instance;
                        int condition = client != null
                            ? client.LevelCompleteCondition
                            : LevelCompleteCondition.AllBars;

                        if (condition == LevelCompleteCondition.AllBars)
                            LocationManager.Instance?.OnLevelComplete();

                        s_prevBarsCollected = __instance.barsOnThisLevel + 1f; // prevent re-fire
                    }
                }
            }

            /// <summary>Reset the bar counter when a new level begins.</summary>
            public static void Reset()
            {
                s_prevBarsCollected = -1f;
            }
        }

        /// <summary>
        /// When Water Access has not been unlocked, treat y=0 as a solid floor.
        /// If the player would cross below the water surface, snap them back and
        /// cancel any downward velocity — the water behaves like a hard ceiling.
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
                    // Snap back to just above the water surface
                    rb.position = new Vector3(rb.position.x, 0f, rb.position.z);

                    // Cancel downward velocity so the player bounces off cleanly
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
        /// and set grapplePayoutSpeed to its locked starting value (4f).
        /// The player earns back full payout speed via Grapple Payout Speed Up items.
        /// </summary>
        [HarmonyPatch(typeof(SwingFromRope), "Start")]
        static class Patch_SwingFromRope_Start
        {
            static void Postfix(SwingFromRope __instance)
            {
                if (ItemManager.Instance != null)
                    ItemManager.Instance.SwingScript = __instance;

                // Reduce initial payout speed from the game default (14) to near-zero.
                // Grapple Payout Speed Up items restore it in +2 steps back up to 14.
                Traverse.Create(__instance).Field("grapplePayoutSpeed").SetValue(4f);

                Logger.LogInfo("SwingFromRope started — ItemManager reference captured, grapplePayoutSpeed set to 4.");
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
        /// Block grapple firing until Grapple Unlock has been received.
        /// SwingFromRope.FireGrappleTo is the single point all grapple-fire paths go through,
        /// so blocking it here covers both the player clicking and any other trigger.
        /// The player can still fall, reach any level region, and receive items —
        /// they just cannot hook onto anything until unlocked.
        /// </summary>
        [HarmonyPatch(typeof(SwingFromRope), "FireGrappleTo")]
        static class Patch_SwingFromRope_FireGrappleTo
        {
            static bool Prefix()
            {
                if (ItemManager.Instance == null) return true;
                if (ItemManager.Instance.GrappleUnlocked) return true;

                Logger.LogInfo("Grapple fire blocked — Grapple Unlock not yet received.");
                ConnectionUI.Instance?.ShowMessage("Grapple locked! Find the Grapple Unlock item.", 3f);
                return false; // skip original — grapple does not fire
            }
        }
    }
}
