using System.Collections;
using System.Collections.Generic;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.Models;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FloatingPointArchipelago
{
    /// <summary>
    /// Receives items from Archipelago and applies their effects to the game.
    /// Effects that need a live game object are queued and applied next Update.
    /// </summary>
    public class ItemManager : MonoBehaviour
    {
        private static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource("FP.Items");

        public static ItemManager Instance { get; private set; }

        // Pending items to apply on the main thread
        private readonly Queue<ItemInfo> _pendingItems = new Queue<ItemInfo>();

        // Trap state
        private float _decaySpikeTimer = 0f;
        private bool _gravitySpikePending = false;

        // Water access state
        public bool WaterAccessUnlocked { get; private set; } = false;

        // Grapple unlock state
        public bool GrappleUnlocked     { get; private set; } = false;

        // Level skip stock
        public int LevelSkipsAvailable  { get; private set; } = 0;
        public bool LevelSkipRequired   { get; private set; } = true;

        // References to live game scripts - set by Plugin patches
        public SwingFromRope SwingScript { get; set; }
        public GenerateLevel GenerateScript { get; set; }

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (ArchipelagoClient.Instance != null)
                ArchipelagoClient.Instance.OnItemReceived += EnqueueItem;

            // If this slot doesn't use the water_access gate, unlock immediately.
            if (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.WaterAccessRequired)
                WaterAccessUnlocked = true;

            // If this slot doesn't use the grapple_unlock gate, unlock immediately.
            if (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.GrappleUnlockRequired)
                GrappleUnlocked = true;

            // If this slot doesn't use the level_skip gate, mark it as not required.
            if (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.LevelSkipRequired)
                LevelSkipRequired = false;
        }

        private void OnDestroy()
        {
            if (ArchipelagoClient.Instance != null)
                ArchipelagoClient.Instance.OnItemReceived -= EnqueueItem;
        }

        public void EnqueueItem(ItemInfo item)
        {
            lock (_pendingItems)
                _pendingItems.Enqueue(item);
        }

        /// <summary>
        /// Spends one Level Skip. Returns true if a skip was available and consumed.
        /// </summary>
        public bool UseSkip()
        {
            if (LevelSkipsAvailable <= 0) return false;
            LevelSkipsAvailable--;
            Logger.LogInfo($"Level Skip used — {LevelSkipsAvailable} skip(s) remaining.");
            ConnectionUI.Instance?.ShowMessage(
                LevelSkipsAvailable > 0
                    ? $"Level skipped! ({LevelSkipsAvailable} remaining)"
                    : "Level skipped! (no skips remaining)", 3f);
            return true;
        }

        private void Update()
        {
            // Apply queued items on the main thread
            lock (_pendingItems)
            {
                while (_pendingItems.Count > 0)
                    ApplyItem(_pendingItems.Dequeue());
            }

            // Tick active traps
            if (_decaySpikeTimer > 0f && SwingScript != null)
            {
                _decaySpikeTimer -= Time.deltaTime;
                if (_decaySpikeTimer <= 0f)
                {
                    SwingScript.pointDecayRate -= 10f;
                    Logger.LogInfo("Decay Spike trap expired.");
                }
            }

            if (_gravitySpikePending && SwingScript != null)
            {
                _gravitySpikePending = false;
                StartCoroutine(GravitySpikeCoroutine());
            }
        }

        private void ApplyItem(ItemInfo item)
        {
            long id = item.ItemId;
            string name = LocationData.GetItemName(id);
            Logger.LogInfo($"Applying item: {name} (id={id})");

            // Show a brief HUD message
            ConnectionUI.Instance?.ShowMessage($"Received: {name}", 4f);

            if (SwingScript == null || GenerateScript == null)
            {
                // Game not loaded yet — retry next frame via queue
                Logger.LogWarning($"Game scripts not ready, re-queuing item {name}");
                lock (_pendingItems) _pendingItems.Enqueue(item);                return;
            }

            switch (id)
            {
                // ── Score bonuses ────────────────────────────────────────────
                case LocationData.ITEM_SCORE_BONUS_SMALL:
                    SwingScript.score += 500f;
                    SwingScript.accumulatedPoints += 200f;
                    break;
                case LocationData.ITEM_SCORE_BONUS_MEDIUM:
                    SwingScript.score += 2000f;
                    SwingScript.accumulatedPoints += 800f;
                    break;
                case LocationData.ITEM_SCORE_BONUS_LARGE:
                    SwingScript.score += 5000f;
                    SwingScript.accumulatedPoints += 2000f;
                    break;

                // ── Physics upgrades ────────────────────────────────────────
                case LocationData.ITEM_RETRACT_SPEED_UP:
                    SwingScript.retractSpeedBase += 5f;
                    break;
                case LocationData.ITEM_RETRACT_BONUS_UP:
                    SwingScript.retractSpeedBonus += 5f;
                    break;
                case LocationData.ITEM_DECAY_RATE_DOWN:
                    SwingScript.pointDecayRate = Mathf.Max(1f, SwingScript.pointDecayRate - 2f);
                    break;
                case LocationData.ITEM_DECAY_FACTOR_DOWN:
                    // pointDecayFactor is close to 1 (0.998); increase it slightly to slow decay
                    SwingScript.pointDecayFactor = Mathf.Min(0.9999f, SwingScript.pointDecayFactor + 0.0005f);
                    break;
                case LocationData.ITEM_IMPACT_PENALTY_DOWN:
                    SwingScript.pointImpactPenalty = Mathf.Max(0f, SwingScript.pointImpactPenalty - 5f);
                    break;
                case LocationData.ITEM_BAR_THRESHOLD_DOWN:
                    SwingScript.barHeightConsideredGood = Mathf.Max(500f, SwingScript.barHeightConsideredGood - 500f);
                    break;
                case LocationData.ITEM_EXTRA_LEVEL:
                    LocationManager.Instance?.OnLevelComplete();
                    break;
                case LocationData.ITEM_WATER_ACCESS:
                    WaterAccessUnlocked = true;
                    Logger.LogInfo("Water Access unlocked — water surface is no longer solid.");
                    break;
                case LocationData.ITEM_GRAPPLE_UNLOCK:
                    GrappleUnlocked = true;
                    Logger.LogInfo("Grapple Unlock received — grapple is now available.");
                    break;
                case LocationData.ITEM_LEVEL_SKIP:
                    LevelSkipsAvailable++;
                    Logger.LogInfo($"Level Skip received — {LevelSkipsAvailable} skip(s) available.");
                    break;
                case LocationData.ITEM_GRAPPLE_PAYOUT_UP:
                {
                    var t = Traverse.Create(SwingScript).Field("grapplePayoutSpeed");
                    float cur = t.GetValue<float>();
                    float next = Mathf.Min(cur + 2f, 14f);
                    t.SetValue(next);
                    Logger.LogInfo($"Grapple Payout Speed Up: {cur} → {next}");
                    break;
                }

                // ── Traps ────────────────────────────────────────────────────
                case LocationData.ITEM_TRAP_GRAVITY_SPIKE:
                    _gravitySpikePending = true;
                    break;
                case LocationData.ITEM_TRAP_DECAY_SPIKE:
                    SwingScript.pointDecayRate += 10f;
                    _decaySpikeTimer = 10f;
                    break;
                case LocationData.ITEM_TRAP_DISCONNECT_GRAPPLE:
                    SwingScript.TellGunToDisconnect();
                    break;
            }
        }

        private IEnumerator GravitySpikeCoroutine()
        {
            // Unity 4.3 doesn't have Physics.gravity as a settable property via script easily,
            // but we can punch the rigidbody downward directly.
            if (SwingScript != null)
            {
                var rb = SwingScript.rigidbody;
                if (rb != null)
                    rb.AddForce(Vector3.down * 500f, ForceMode.Impulse);
            }
            yield return new WaitForSeconds(0.1f);
        }
    }
}
