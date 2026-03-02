using System.Collections;
using System.Collections.Generic;
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
        private readonly Queue<long> _pendingItems = new Queue<long>();

        // Trap state
        private float _decaySpikeTimer = 0f;
        private bool _gravitySpikePending = false;
        private float _grappleLockTimer = 0f;  // > 0 means grapple is temporarily locked by trap

        // Upgrade counts received so far (for re-applying after level transitions)
        private int _retractSpeedUpCount    = 0;
        private int _retractBonusUpCount    = 0;
        private int _decayRateDownCount     = 0;
        private int _decayFactorDownCount   = 0;
        private int _impactPenaltyDownCount = 0;
        private int _barThresholdDownCount  = 0;
        private int _grapplePayoutUpCount   = 0;

        // Water access state
        public bool WaterAccessUnlocked { get; private set; } = false;

        /// <summary>
        /// True when the water gate is active for this slot (water_access option is enabled).
        /// Used by the Generate patch to decide whether to enforce the above/below layout.
        /// </summary>
        public bool WaterGateActive =>
            ArchipelagoClient.Instance != null && ArchipelagoClient.Instance.WaterAccessRequired;

        // Grapple unlock state
        public bool GrappleUnlocked { get; private set; } = false;

        /// <summary>
        /// True when the grapple should be blocked — either not yet unlocked, or
        /// temporarily locked by the Grapple Disconnect trap.
        /// </summary>
        public bool GrappleBlocked => !GrappleUnlocked || _grappleLockTimer > 0f;

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
        }

        private void OnDestroy()
        {
            if (ArchipelagoClient.Instance != null)
                ArchipelagoClient.Instance.OnItemReceived -= EnqueueItem;
        }

        public void EnqueueItem(long itemId)
        {
            lock (_pendingItems)
                _pendingItems.Enqueue(itemId);
        }

        /// <summary>
        /// Returns the starting retractSpeedBase for the configured tier.
        /// 0=very_slow → 2, 1=moderate → 8, 2=near_default → 13
        /// </summary>
        private float RetractBaseStart()
        {
            int tier = ArchipelagoClient.Instance?.StartingRetractSpeed ?? 0;
            switch (tier) { case 1: return 8f; case 2: return 13f; default: return 2f; }
        }

        /// <summary>
        /// Returns the starting retractSpeedBonus for the configured tier.
        /// 0=very_slow → 3, 1=moderate → 12, 2=near_default → 22
        /// </summary>
        private float RetractBonusStart()
        {
            int tier = ArchipelagoClient.Instance?.StartingRetractSpeed ?? 0;
            switch (tier) { case 1: return 12f; case 2: return 22f; default: return 3f; }
        }

        /// <summary>
        /// Formats a progress message for stackable upgrades.
        /// e.g. "Retract Speed Up (8/20): 10 → 11"
        /// </summary>
        private static string UpgradeMsg(string name, int count, int max, float oldVal, float newVal, string fmt = "0.##")
            => $"{name} ({count}/{max}): {oldVal.ToString(fmt)} \u2192 {newVal.ToString(fmt)}";
        public void ApplyPhysicsToScript(SwingFromRope swing)
        {
            // retractSpeedBase:        start=tier-dependent, step=+1.0, count=20
            swing.retractSpeedBase        = RetractBaseStart()  + _retractSpeedUpCount  * 1.0f;
            // retractSpeedBonus:       start=tier-dependent,  step=+2.0, count=15
            swing.retractSpeedBonus       = RetractBonusStart() + _retractBonusUpCount  * 2.0f;
            // pointDecayRate:          start=26, step=-0.6, count=10 → floor 20
            swing.pointDecayRate          = Mathf.Max(20f, 26f - _decayRateDownCount     * 0.6f);
            // pointDecayFactor:        start=0.9955, step=+0.00025, count=10 → ceil 0.998
            swing.pointDecayFactor        = Mathf.Min(0.998f, 0.9955f + _decayFactorDownCount * 0.00025f);
            // pointImpactPenalty:      start=26, step=-0.6, count=10 → floor 20
            swing.pointImpactPenalty      = Mathf.Max(20f, 26f - _impactPenaltyDownCount  * 0.6f);
            // barHeightConsideredGood: start=6500, step=-500, count=8 → floor 2500
            swing.barHeightConsideredGood = Mathf.Max(2500f, 6500f - _barThresholdDownCount * 500f);
            // grapplePayoutSpeed:      start=1, step=+1.5, count=10 → ceil 16
            float grapplePayout           = Mathf.Min(16f, 1f + _grapplePayoutUpCount * 1.5f);
            Traverse.Create(swing).Field("grapplePayoutSpeed").SetValue(grapplePayout);
            Logger.LogInfo($"Physics applied: retractBase={swing.retractSpeedBase} retractBonus={swing.retractSpeedBonus} " +
                           $"decayRate={swing.pointDecayRate} decayFactor={swing.pointDecayFactor} " +
                           $"impactPenalty={swing.pointImpactPenalty} barThreshold={swing.barHeightConsideredGood} " +
                           $"grapplePayout={grapplePayout}");
        }

        private void Update()
        {
            // Apply queued items on the main thread.
            int toProcess;
            lock (_pendingItems) toProcess = _pendingItems.Count;
            while (toProcess-- > 0)
            {
                long id;
                lock (_pendingItems)
                {
                    if (_pendingItems.Count == 0) break;
                    id = _pendingItems.Dequeue();
                }
                ApplyItem(id);
            }

            // Tick active traps
            if (_decaySpikeTimer > 0f && SwingScript != null)
            {
                _decaySpikeTimer -= Time.deltaTime;
                if (_decaySpikeTimer <= 0f)
                {
                    // Recompute physics cleanly from upgrade counts rather than
                    // delta-subtracting, so we're robust against ApplyPhysicsToScript
                    // having fired during the trap and already overwriting the +10f.
                    ApplyPhysicsToScript(SwingScript);
                    Logger.LogInfo("Decay Spike trap expired.");
                }
            }

            if (_gravitySpikePending && SwingScript != null)
            {
                _gravitySpikePending = false;
                StartCoroutine(GravitySpikeCoroutine());
            }

            if (_grappleLockTimer > 0f)
            {
                _grappleLockTimer -= Time.deltaTime;
                if (_grappleLockTimer <= 0f)
                    Logger.LogInfo("Grapple Disconnect trap expired — grapple restored.");
            }
        }

        private void ApplyItem(long id)
        {
            string name = LocationData.GetItemName(id);
            Logger.LogInfo($"Applying item: {name} (id={id})");

            if (SwingScript == null || GenerateScript == null)
            {
                // Game not loaded yet — retry next frame via queue
                Logger.LogWarning($"Game scripts not ready, re-queuing item {name}");
                lock (_pendingItems) _pendingItems.Enqueue(id);
                return;
            }

            switch (id)
            {
                // ── Score bonuses ────────────────────────────────────────────
                case LocationData.ITEM_SCORE_BONUS_SMALL:
                    SwingScript.score += 10_000f;
                    ConnectionUI.Instance?.ShowMessage("Score Bonus (Small): +10,000", 4f);
                    break;
                case LocationData.ITEM_SCORE_BONUS_MEDIUM:
                    SwingScript.score += 40_000f;
                    ConnectionUI.Instance?.ShowMessage("Score Bonus (Medium): +40,000", 4f);
                    break;
                case LocationData.ITEM_SCORE_BONUS_LARGE:
                    SwingScript.score += 100_000f;
                    ConnectionUI.Instance?.ShowMessage("Score Bonus (Large): +100,000", 4f);
                    break;

                // ── Physics upgrades ────────────────────────────────────────
                // Starts at tier-dependent base, +1.0 per item, 20 items → max tier+20
                case LocationData.ITEM_RETRACT_SPEED_UP:
                {
                    float oldVal = SwingScript.retractSpeedBase;
                    _retractSpeedUpCount++;
                    float newVal = RetractBaseStart() + _retractSpeedUpCount * 1.0f;
                    SwingScript.retractSpeedBase = newVal;
                    ConnectionUI.Instance?.ShowMessage(UpgradeMsg("Retract Speed Up", _retractSpeedUpCount, 20, oldVal, newVal));
                    Logger.LogInfo($"Retract Speed Up ({_retractSpeedUpCount}/20): retractSpeedBase {oldVal} -> {newVal}");
                    break;
                }
                // Starts at tier-dependent base, +2.0 per item, 15 items → max tier+30
                case LocationData.ITEM_RETRACT_BONUS_UP:
                {
                    float oldVal = SwingScript.retractSpeedBonus;
                    _retractBonusUpCount++;
                    float newVal = RetractBonusStart() + _retractBonusUpCount * 2.0f;
                    SwingScript.retractSpeedBonus = newVal;
                    ConnectionUI.Instance?.ShowMessage(UpgradeMsg("Retract Bonus Up", _retractBonusUpCount, 15, oldVal, newVal));
                    Logger.LogInfo($"Retract Bonus Up ({_retractBonusUpCount}/15): retractSpeedBonus {oldVal} -> {newVal}");
                    break;
                }
                // Starts at 26, -0.6 per item over 10 items → floor 20
                case LocationData.ITEM_DECAY_RATE_DOWN:
                {
                    float oldVal = SwingScript.pointDecayRate;
                    _decayRateDownCount++;
                    float newVal = Mathf.Max(20f, 26f - _decayRateDownCount * 0.6f);
                    SwingScript.pointDecayRate = newVal;
                    ConnectionUI.Instance?.ShowMessage(UpgradeMsg("Bar Decay Rate Down", _decayRateDownCount, 10, oldVal, newVal));
                    Logger.LogInfo($"Bar Decay Rate Down ({_decayRateDownCount}/10): pointDecayRate {oldVal} -> {newVal}");
                    break;
                }
                // Starts at 0.9955, +0.00025 per item over 10 items → ceil 0.998
                case LocationData.ITEM_DECAY_FACTOR_DOWN:
                {
                    float oldVal = SwingScript.pointDecayFactor;
                    _decayFactorDownCount++;
                    float newVal = Mathf.Min(0.998f, 0.9955f + _decayFactorDownCount * 0.00025f);
                    SwingScript.pointDecayFactor = newVal;
                    ConnectionUI.Instance?.ShowMessage(UpgradeMsg("Bar Decay Factor Down", _decayFactorDownCount, 10, oldVal, newVal, "0.####"));
                    Logger.LogInfo($"Bar Decay Factor Down ({_decayFactorDownCount}/10): pointDecayFactor {oldVal} -> {newVal}");
                    break;
                }
                // Starts at 26, -0.6 per item over 10 items → floor 20
                case LocationData.ITEM_IMPACT_PENALTY_DOWN:
                {
                    float oldVal = SwingScript.pointImpactPenalty;
                    _impactPenaltyDownCount++;
                    float newVal = Mathf.Max(20f, 26f - _impactPenaltyDownCount * 0.6f);
                    SwingScript.pointImpactPenalty = newVal;
                    ConnectionUI.Instance?.ShowMessage(UpgradeMsg("Impact Penalty Down", _impactPenaltyDownCount, 10, oldVal, newVal));
                    Logger.LogInfo($"Impact Penalty Down ({_impactPenaltyDownCount}/10): pointImpactPenalty {oldVal} -> {newVal}");
                    break;
                }
                // Starts at 6500, -500 per item over 8 items → floor 2500
                case LocationData.ITEM_BAR_THRESHOLD_DOWN:
                {
                    float oldVal = SwingScript.barHeightConsideredGood;
                    _barThresholdDownCount++;
                    float newVal = Mathf.Max(2500f, 6500f - _barThresholdDownCount * 500f);
                    SwingScript.barHeightConsideredGood = newVal;
                    ConnectionUI.Instance?.ShowMessage(UpgradeMsg("Bar Threshold Down", _barThresholdDownCount, 8, oldVal, newVal));
                    Logger.LogInfo($"Bar Threshold Down ({_barThresholdDownCount}/8): barHeightConsideredGood {oldVal} -> {newVal}");
                    break;
                }

                case LocationData.ITEM_WATER_ACCESS:
                    WaterAccessUnlocked = true;
                    ConnectionUI.Instance?.ShowMessage("Water Access unlocked!", 4f);
                    Logger.LogInfo("Water Access unlocked — water surface is no longer solid.");
                    break;
                case LocationData.ITEM_GRAPPLE_UNLOCK:
                    GrappleUnlocked = true;
                    ConnectionUI.Instance?.ShowMessage("Grapple Unlock received!", 4f);
                    Logger.LogInfo("Grapple Unlock received — grapple is now available.");
                    break;

                // ── Level Skip (Trap) ────────────────────────────────────────
                // Forces the player to advance to the next level immediately.
                // Replicates the full Enter-key code path from GenerateLevel.Update():
                //   1. transition = "Harder"
                //   2. RandomiseParameters()
                //   3. Generate(backgroundDepth * 2f)
                //   4. AudioSource.PlayClipAtPoint(levelChangeSound, ...)
                //   5. WriteScoresFile()
                //   6. SwingScript.ResetScore()
                // Also notifies LocationManager so the level counts as completed.
                case LocationData.ITEM_LEVEL_SKIP:
                {
                    Logger.LogInfo("Level Skip (Trap) received — forcing level advance.");
                    ConnectionUI.Instance?.ShowMessage("Level Skip (Trap)!", 4f);

                    var genTraverse = Traverse.Create(GenerateScript);

                    // Step 1: set tutorial flag (matches Enter key handler)
                    GenerateScript.centralScript.tutorial = false;

                    // Step 2: transition flag (triggers the scroll animation)
                    GenerateScript.transition = "Harder";

                    // Step 3: RandomiseParameters() — picks new levelHalfHeight and blockDensity
                    genTraverse.Method("RandomiseParameters").GetValue();

                    // Step 4: Generate(backgroundDepth * 2f) — pre-generates next level geometry
                    float bgDepth = genTraverse.Field("backgroundDepth").GetValue<float>();
                    genTraverse.Method("Generate", bgDepth * 2f).GetValue();

                    // Step 5: play the level-change sound
                    AudioClip levelChangeSound = genTraverse.Field("levelChangeSound").GetValue<AudioClip>();
                    if (levelChangeSound != null)
                        AudioSource.PlayClipAtPoint(levelChangeSound, Camera.main != null ? Camera.main.transform.position : Vector3.zero);

                    // Step 6: persist high score
                    GenerateScript.WriteScoresFile();

                    // Step 7: reset the player's bar score
                    SwingScript.ResetScore();

                    // Reset per-level bar tracking without crediting a level completion
                    // (Level Skip is a trap — the player didn't earn this transition)
                    LocationManager.Instance?.ResetBarsThisLevel();

                    break;
                }

                case LocationData.ITEM_GRAPPLE_PAYOUT_UP:
                {
                    float oldVal = Traverse.Create(SwingScript).Field("grapplePayoutSpeed").GetValue<float>();
                    _grapplePayoutUpCount++;
                    // Starts at 1, +1.5 per item over 10 items → ceil 16
                    float newVal = Mathf.Min(16f, 1f + _grapplePayoutUpCount * 1.5f);
                    Traverse.Create(SwingScript).Field("grapplePayoutSpeed").SetValue(newVal);
                    ConnectionUI.Instance?.ShowMessage(UpgradeMsg("Grapple Payout Speed Up", _grapplePayoutUpCount, 10, oldVal, newVal));
                    Logger.LogInfo($"Grapple Payout Speed Up ({_grapplePayoutUpCount}/10): grapplePayoutSpeed {oldVal} -> {newVal}");
                    break;
                }

                // ── Traps ────────────────────────────────────────────────────
                case LocationData.ITEM_TRAP_GRAVITY_SPIKE:
                    _gravitySpikePending = true;
                    ConnectionUI.Instance?.ShowMessage("Gravity Spike (Trap)!", 4f);
                    break;
                case LocationData.ITEM_TRAP_DECAY_SPIKE:
                    SwingScript.pointDecayRate += 10f;
                    _decaySpikeTimer = 10f;
                    ConnectionUI.Instance?.ShowMessage("Decay Spike (Trap)! (10s)", 4f);
                    break;
                case LocationData.ITEM_TRAP_DISCONNECT_GRAPPLE:
                    SwingScript.TellGunToDisconnect();
                    _grappleLockTimer = 2f;
                    Logger.LogInfo("Grapple Disconnect trap — grapple locked for 2 seconds.");
                    ConnectionUI.Instance?.ShowMessage("Grapple disconnected! (2s lockout)", 3f);
                    break;
            }
        }

        private IEnumerator GravitySpikeCoroutine()
        {
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
