using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace FloatingPointArchipelago
{
    // Must stay in sync with goal type constants in __init__.py
    public static class GoalType
    {
        public const int LevelsCompleted = 0;
        public const int Score           = 1;
        public const int BarsCollected   = 2;
        public const int AllLocations    = 3;
    }

    // Must stay in sync with level_complete_condition constants in __init__.py
    public static class LevelCompleteCondition
    {
        public const int AllBars   = 0;
        public const int PressEnter = 1;
    }

    /// <summary>
    /// Tracks bar collection per level and sends location checks to Archipelago.
    /// Supports all four goal types: levels_completed, score, bars_collected, all_locations.
    /// Sends a "Level N - Complete" location check when a level is finished,
    /// with the definition of "finished" controlled by LevelCompleteCondition.
    /// </summary>
    public class LocationManager
    {
        private static readonly ManualLogSource Logger =
            BepInEx.Logging.Logger.CreateLogSource("FP.Locations");

        public static LocationManager Instance { get; private set; }

        // Current level index (increments each time a level is advanced)
        public int CurrentLevelIndex { get; private set; } = 0;

        // How many bars have been collected on the current level
        private int _barsCollectedThisLevel = 0;

        // Set of location IDs already checked (avoids re-sending on level revisit)
        private readonly HashSet<long> _checkedLocations = new HashSet<long>();

        // Count of fully-completed levels
        public int CompletedLevels { get; private set; } = 0;

        // Cumulative bars collected across all levels
        public int TotalBarsCollected { get; private set; } = 0;

        // Whether the overall goal has been sent
        private bool _goalSent = false;

        public static void Init()
        {
            if (Instance == null)
                Instance = new LocationManager();
        }

        /// <summary>
        /// Called by the Enter-key Harmony patch when the player presses Enter to advance.
        /// Handles level-complete logic for both condition modes:
        ///   - press_enter: fires OnLevelComplete immediately
        ///   - all_bars: just advances the level index (bars path handles completion separately)
        /// </summary>
        public void OnLevelAdvancedByEnter()
        {
            var client = ArchipelagoClient.Instance;
            int condition = client != null ? client.LevelCompleteCondition : LevelCompleteCondition.AllBars;

            if (condition == LevelCompleteCondition.PressEnter)
            {
                Logger.LogInfo("Enter pressed — press_enter condition: firing level complete.");
                OnLevelComplete();
            }

            // Advance the level counter and reset bar tracking regardless of condition
            if (_barsCollectedThisLevel > 0 || CurrentLevelIndex > 0)
            {
                CurrentLevelIndex++;
                Logger.LogInfo($"Advanced to level index {CurrentLevelIndex} (via Enter key)");
            }
            _barsCollectedThisLevel = 0;
        }

        /// <summary>
        /// Called by Harmony patch when GenerateLevel.Regenerate fires
        /// (menu-driven level reset, not the Enter-key path).
        /// </summary>
        public void OnLevelRegenerated()
        {
            if (_barsCollectedThisLevel > 0 || CurrentLevelIndex > 0)
            {
                CurrentLevelIndex++;
                Logger.LogInfo($"Advanced to level index {CurrentLevelIndex} (via Regenerate)");
            }
            _barsCollectedThisLevel = 0;
        }

        /// <summary>
        /// Called by Harmony patch when a bar (Points) is collected.
        /// barIndex is the sequential bar number within this level (0-based).
        /// </summary>
        public void OnBarCollected(int barIndex)
        {
            _barsCollectedThisLevel = barIndex + 1;
            TotalBarsCollected++;

            int effectiveLevelIndex = CurrentLevelIndex % LocationData.NumLevels;
            int effectiveBarIndex   = barIndex % LocationData.BARS_PER_LEVEL;

            long locationId = LocationData.GetLocationId(effectiveLevelIndex, effectiveBarIndex);

            if (!_checkedLocations.Contains(locationId) &&
                (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.IsLocationChecked(locationId)))
            {
                _checkedLocations.Add(locationId);
                Logger.LogInfo(
                    $"Bar collected -> location {locationId} " +
                    $"({LocationData.GetLocationName(effectiveLevelIndex, effectiveBarIndex)}) " +
                    $"[total bars: {TotalBarsCollected}]");
                ArchipelagoClient.Instance?.SendLocation(locationId);
            }

            CheckGoalOnBarCollected();
        }

        /// <summary>
        /// Called when all bars on a level are collected (all_bars condition) or
        /// immediately when Enter is pressed (press_enter condition).
        /// Sends the Level N - Complete location check and checks goal state.
        /// </summary>
        public void OnLevelComplete()
        {
            int effectiveLevelIndex = CurrentLevelIndex % LocationData.NumLevels;

            // Send the level completion location check
            long completionLocationId = LocationData.GetLevelCompleteLocationId(effectiveLevelIndex);
            if (!_checkedLocations.Contains(completionLocationId) &&
                (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.IsLocationChecked(completionLocationId)))
            {
                _checkedLocations.Add(completionLocationId);
                Logger.LogInfo(
                    $"Level complete -> location {completionLocationId} " +
                    $"({LocationData.GetLevelCompleteLocationName(effectiveLevelIndex)})");
                ArchipelagoClient.Instance?.SendLocation(completionLocationId);
            }

            CompletedLevels++;
            Logger.LogInfo($"Level complete. Total completed levels: {CompletedLevels}");
            CheckGoalOnLevelComplete();
        }

        /// <summary>
        /// Called by ArchipelagoClient immediately after a successful connection.
        /// Sends the "Connected to Archipelago" location check (BASE + 20_000).
        /// This location lives in the Menu region with no access rule — it is always
        /// reachable and acts as the sphere-0 anchor that lets the generator place
        /// Grapple Unlock in sphere 1, preventing deadlock in solo games.
        /// Deduplicated with both the local set and server-side IsLocationChecked,
        /// so reconnects don't re-send.
        /// </summary>
        public void OnConnected()
        {
            long id = LocationData.LOCATION_CONNECTED;
            if (!_checkedLocations.Contains(id) &&
                (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.IsLocationChecked(id)))
            {
                _checkedLocations.Add(id);
                Logger.LogInfo("Connected to Archipelago — sending connection location check.");
                ArchipelagoClient.Instance?.SendLocation(id);
            }
        }

        /// <summary>
        /// Called every frame by the GenerateLevel.Update patch so the score goal
        /// can be evaluated continuously. Pass the current SwingFromRope.score value.
        /// </summary>
        public void OnScoreTick(float currentScore)
        {
            if (_goalSent) return;
            if (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.IsConnected) return;

            var client = ArchipelagoClient.Instance;
            if (client.GoalType == GoalType.Score && currentScore >= client.GoalScore)
            {
                Logger.LogInfo($"Score goal reached ({currentScore} >= {client.GoalScore})! Sending completion.");
                client.SendGoalComplete();
                _goalSent = true;
            }
        }

        // ── Private goal checks ──────────────────────────────────────────────────

        private void CheckGoalOnBarCollected()
        {
            if (_goalSent) return;
            if (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.IsConnected) return;

            var client = ArchipelagoClient.Instance;

            if (client.GoalType == GoalType.BarsCollected && TotalBarsCollected >= client.BarsRequired)
            {
                Logger.LogInfo(
                    $"Bars-collected goal reached ({TotalBarsCollected} >= {client.BarsRequired})! Sending completion.");
                client.SendGoalComplete();
                _goalSent = true;
            }
            else if (client.GoalType == GoalType.AllLocations &&
                     _checkedLocations.Count >= LocationData.TotalLocations)
            {
                Logger.LogInfo("All-locations goal reached! Sending completion.");
                client.SendGoalComplete();
                _goalSent = true;
            }
        }

        private void CheckGoalOnLevelComplete()
        {
            if (_goalSent) return;
            if (ArchipelagoClient.Instance == null || !ArchipelagoClient.Instance.IsConnected) return;

            var client = ArchipelagoClient.Instance;

            if (client.GoalType == GoalType.LevelsCompleted && CompletedLevels >= client.LevelsRequired)
            {
                Logger.LogInfo(
                    $"Levels-completed goal reached ({CompletedLevels} >= {client.LevelsRequired})! Sending completion.");
                client.SendGoalComplete();
                _goalSent = true;
            }
        }
    }
}
