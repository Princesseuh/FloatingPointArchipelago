using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace FloatingPointArchipelago
{
    // Must stay in sync with goal type constants in __init__.py
    public static class GoalType
    {
        public const int LevelsCompleted = 0;
        public const int BarsCollected   = 2;
        public const int AllLocations    = 3;
    }

    /// <summary>
    /// Tracks bar collection and level completions, sends location checks to Archipelago.
    ///
    /// Location sources:
    ///   - Cumulative-bar milestones: fire when total bars crosses 8, 16, ..., N*8 (N = num_levels).
    ///   - Level completions (water-gated): fire on each level completed, up to num_levels.
    ///   - Single-level best-bar milestones: fire once when best-in-a-level bar count hits 2/4/.../32.
    ///   - Score milestones: fire once when running score crosses 100k/250k/500k/1M.
    ///   - Connected: sent immediately on connection.
    /// </summary>
    public class LocationManager
    {
        private static readonly ManualLogSource Logger =
            BepInEx.Logging.Logger.CreateLogSource("FP.Locations");

        public static LocationManager Instance { get; private set; }

        // Cumulative bars collected across all levels (ever)
        public int TotalBarsCollected { get; private set; } = 0;

        // Bars collected on the current level (reset each level transition)
        private int _barsThisLevel = 0;

        // Best single-level bar count ever achieved this session
        private int _bestSingleLevelBars = 0;

        // How many level-complete checks have been sent (up to TOTAL_LEVEL_COMPLETIONS)
        public int CompletedLevels { get; private set; } = 0;

        // Set of location IDs already checked this session (avoids re-sending)
        private readonly HashSet<long> _checkedLocations = new HashSet<long>();

        /// <summary>Number of location checks sent this session.</summary>
        public int CheckedLocationsCount => _checkedLocations.Count;

        // Whether the overall goal has been sent
        private bool _goalSent = false;

        // Total locations for the current slot (depends on NumLevels from slot data)
        public int TotalLocations =>
            LocationData.TotalBarMilestoneCount +
            LocationData.TotalLevelCompletions +
            1 + // Connected
            LocationData.SINGLE_LEVEL_BAR_MILESTONES.Length +
            LocationData.SCORE_MILESTONES.Length;

        public static void Init()
        {
            if (Instance == null)
                Instance = new LocationManager();
        }

        /// <summary>
        /// Called by the Enter-key patch when the player presses Enter to advance.
        /// Completes the current level and resets per-level bar tracking.
        /// </summary>
        public void OnLevelAdvancedByEnter()
        {
            Logger.LogInfo("Enter pressed — completing current level.");
            OnLevelComplete();
            _barsThisLevel = 0;
        }

        /// <summary>
        /// Resets per-level bar tracking without crediting a level completion.
        /// Used by the Level Skip trap (forced transition, not earned by the player).
        /// </summary>
        public void ResetBarsThisLevel()
        {
            _barsThisLevel = 0;
        }

        /// <summary>
        /// Called by the Regenerate patch (menu-driven level reset).
        /// Counts as a level completion and resets per-level bar tracking.
        /// </summary>
        public void OnLevelRegenerated()
        {
            Logger.LogInfo("Regenerate fired — completing current level.");
            OnLevelComplete();
            _barsThisLevel = 0;
        }

        /// <summary>
        /// Called by the GenerateLevel.Update patch each time a bar is collected.
        /// barIndex is 0-based sequential bar number within the current level.
        /// </summary>
        public void OnBarCollected(int barIndex)
        {
            _barsThisLevel = barIndex + 1;
            TotalBarsCollected++;

            // Cumulative bar milestones
            CheckCumulativeBarMilestones();

            // Single-level best-bar milestones
            if (_barsThisLevel > _bestSingleLevelBars)
            {
                _bestSingleLevelBars = _barsThisLevel;
                CheckSingleLevelBarMilestones();
            }

            CheckGoal();
        }

        /// <summary>
        /// Called every frame by the GenerateLevel.Update patch with the current score.
        /// Checks score milestones.
        /// </summary>
        public void OnScoreTick(float currentScore)
        {
            CheckScoreMilestones(currentScore);
        }

        /// <summary>
        /// Called immediately after a successful Archipelago connection.
        /// </summary>
        public void OnConnected()
        {
            long id = LocationData.LOCATION_CONNECTED;
            SendOnce(id, "Connected to Archipelago");
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        public void OnLevelComplete()
        {
            // Water Access required before any completion check counts
            if (ItemManager.Instance != null && !ItemManager.Instance.WaterAccessUnlocked)
            {
                Logger.LogInfo("Level complete — but Water Access not yet received; skipping completion check.");
                return;
            }

            if (CompletedLevels >= LocationData.TotalLevelCompletions)
            {
                Logger.LogInfo($"Level complete — all {LocationData.TotalLevelCompletions} completion checks already sent.");
                return;
            }

            long id     = LocationData.GetLevelCompleteLocationId(CompletedLevels);
            string name = LocationData.GetLevelCompleteLocationName(CompletedLevels);
            if (SendOnce(id, name))
                CompletedLevels++;

            Logger.LogInfo($"Completed levels: {CompletedLevels}/{LocationData.TotalLevelCompletions}");
            CheckGoal();
        }

        private void CheckCumulativeBarMilestones()
        {
            for (int i = 0; i < LocationData.TotalBarMilestoneCount; i++)
            {
                int threshold = LocationData.GetCumulativeBarThreshold(i);
                if (TotalBarsCollected < threshold) break; // sorted ascending
                SendOnce(LocationData.GetCumulativeBarMilestoneId(i),
                         LocationData.GetCumulativeBarMilestoneName(i));
            }
        }

        private void CheckSingleLevelBarMilestones()
        {
            int[] milestones = LocationData.SINGLE_LEVEL_BAR_MILESTONES;
            for (int i = 0; i < milestones.Length; i++)
            {
                if (_bestSingleLevelBars < milestones[i]) break;
                SendOnce(LocationData.GetSingleLevelMilestoneId(i),
                         LocationData.GetSingleLevelMilestoneName(i));
            }
        }

        private void CheckScoreMilestones(float score)
        {
            float[] milestones = LocationData.SCORE_MILESTONES;
            for (int i = 0; i < milestones.Length; i++)
            {
                if (score < milestones[i]) break; // sorted ascending
                SendOnce(LocationData.GetScoreMilestoneId(i),
                         LocationData.GetScoreMilestoneName(i));
            }
        }

        private void CheckGoal()
        {
            if (_goalSent) return;
            if (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.IsConnected) return;

            var client = ArchipelagoClient.Instance;

            if (client.GoalType == GoalType.LevelsCompleted && CompletedLevels >= client.LevelsRequired)
            {
                Logger.LogInfo($"Levels goal reached ({CompletedLevels} >= {client.LevelsRequired})! Sending completion.");
                client.SendGoalComplete();
                _goalSent = true;
            }
            else if (client.GoalType == GoalType.BarsCollected && TotalBarsCollected >= client.BarsRequired)
            {
                Logger.LogInfo($"Bars goal reached ({TotalBarsCollected} >= {client.BarsRequired})! Sending completion.");
                client.SendGoalComplete();
                _goalSent = true;
            }
            else if (client.GoalType == GoalType.AllLocations && _checkedLocations.Count >= TotalLocations)
            {
                Logger.LogInfo("All-locations goal reached! Sending completion.");
                client.SendGoalComplete();
                _goalSent = true;
            }
        }

        /// <summary>
        /// Sends a location check exactly once (deduped against both local set and server state).
        /// Returns true if it was newly sent.
        /// </summary>
        private bool SendOnce(long id, string name)
        {
            if (_checkedLocations.Contains(id)) return false;
            if (ArchipelagoClient.Instance != null && ArchipelagoClient.Instance.IsLocationChecked(id)) return false;

            _checkedLocations.Add(id);
            Logger.LogInfo($"Check -> {id} ({name})");
            ArchipelagoClient.Instance?.SendLocation(id);
            ShowCheckMessage(id, name);
            return true;
        }

        private void ShowCheckMessage(long id, string name)
        {
            string msg;

            // Level complete: "Level complete! (3/10)"
            if (id >= LocationData.BASE_LOCATION_ID + LocationData.LEVEL_COMPLETE_OFFSET &&
                id <  LocationData.BASE_LOCATION_ID + LocationData.LEVEL_COMPLETE_OFFSET + LocationData.TotalLevelCompletions)
            {
                int num   = CompletedLevels + 1; // +1 because CompletedLevels increments after SendOnce returns
                int total = LocationData.TotalLevelCompletions;
                msg = $"Level complete! ({num}/{total})";
            }
            // Cumulative bar milestone: "200 bars collected!"
            else if (id >= LocationData.BASE_LOCATION_ID &&
                     id <  LocationData.BASE_LOCATION_ID + LocationData.TotalBarMilestoneCount)
            {
                int idx       = (int)(id - LocationData.BASE_LOCATION_ID);
                int threshold = LocationData.GetCumulativeBarThreshold(idx);
                msg = $"{threshold} bars collected!";
            }
            // Single-level best-bar milestone: "Best level: 14 bars!"
            else if (id >= LocationData.BASE_LOCATION_ID + LocationData.BAR_MILESTONE_OFFSET &&
                     id <  LocationData.BASE_LOCATION_ID + LocationData.BAR_MILESTONE_OFFSET + LocationData.SINGLE_LEVEL_BAR_MILESTONES.Length)
            {
                int idx       = (int)(id - (LocationData.BASE_LOCATION_ID + LocationData.BAR_MILESTONE_OFFSET));
                int threshold = LocationData.SINGLE_LEVEL_BAR_MILESTONES[idx];
                msg = $"Best level: {threshold} bars!";
            }
            // Score milestone: "200,000 score!"
            else if (id >= LocationData.BASE_LOCATION_ID + LocationData.SCORE_MILESTONE_OFFSET &&
                     id <  LocationData.BASE_LOCATION_ID + LocationData.SCORE_MILESTONE_OFFSET + LocationData.SCORE_MILESTONES.Length)
            {
                int idx   = (int)(id - (LocationData.BASE_LOCATION_ID + LocationData.SCORE_MILESTONE_OFFSET));
                float thr = LocationData.SCORE_MILESTONES[idx];
                string label = thr >= 1_000_000f ? $"{(int)(thr / 1_000_000f)}M"
                             : thr >= 1_000f     ? $"{(int)(thr / 1_000f)}k"
                             : $"{(int)thr}";
                msg = $"{label} score!";
            }
            else
            {
                msg = $"Check: {name}";
            }

            ConnectionUI.Instance?.ShowMessage(msg, 4f);
        }
    }
}
