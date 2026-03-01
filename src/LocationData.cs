namespace FloatingPointArchipelago
{
    /// <summary>
    /// Central registry of all Archipelago location and item IDs for Floating Point.
    ///
    /// Design:
    ///   Locations = collecting red bars across levels.
    ///   We support up to MAX_LEVELS levels, each with BARS_PER_LEVEL bar locations.
    ///   The actual number of active levels comes from the AP slot (ArchipelagoClient.NumLevels).
    ///   Location ID = BASE_LOCATION_ID + (levelIndex * BARS_PER_LEVEL) + barIndex
    ///
    ///   Items = physics upgrades, score bonuses, and traps.
    ///   Item ID = BASE_ITEM_ID + itemIndex
    /// </summary>
    public static class LocationData
    {
        // ── Base offsets ────────────────────────────────────────────────────────
        public const long BASE_LOCATION_ID = 45_000_000;
        public const long BASE_ITEM_ID     = 45_000_000;

        // ── Location layout ─────────────────────────────────────────────────────
        public const int MAX_LEVELS     = 30;   // hard ceiling; LOCATION_TABLE in apworld covers all 30
        public const int BARS_PER_LEVEL = 32;   // matches GenerateLevel.barsOnThisLevel default

        /// <summary>
        /// Runtime number of levels for this slot.
        /// Falls back to 10 (the default) if no AP session is active.
        /// </summary>
        public static int NumLevels
            => ArchipelagoClient.Instance?.NumLevels ?? 10;

        /// <summary>Total AP location count for the current slot (bar + completion checks).</summary>
        public static int TotalLocations
            => NumLevels * BARS_PER_LEVEL + NumLevels;

        // Bar locations: BASE + (levelIndex * BARS_PER_LEVEL) + barIndex  → 0..959 (up to MAX_LEVELS)
        public static long GetLocationId(int levelIndex, int barIndex)
            => BASE_LOCATION_ID + (levelIndex * BARS_PER_LEVEL) + barIndex;

        public static string GetLocationName(int levelIndex, int barIndex)
            => $"Level {levelIndex + 1} - Bar {barIndex + 1}";

        // Level completion locations: BASE + 10_000 + levelIndex  → 10000..10029
        public const long LEVEL_COMPLETE_OFFSET = 10_000;

        public static long GetLevelCompleteLocationId(int levelIndex)
            => BASE_LOCATION_ID + LEVEL_COMPLETE_OFFSET + levelIndex;

        public static string GetLevelCompleteLocationName(int levelIndex)
            => $"Level {levelIndex + 1} - Complete";

        // "Connected to Archipelago" location: BASE + 20_000
        // Lives in the Menu region with no access rule — always reachable.
        // Sent the moment a connection succeeds; guarantees Grapple Unlock lands in sphere 0
        // (i.e. another player can receive it immediately), preventing deadlock in solo games.
        public const long LOCATION_CONNECTED = BASE_LOCATION_ID + 20_000;

        // ── Item IDs ────────────────────────────────────────────────────────────
        // Progression / useful items
        public const long ITEM_SCORE_BONUS_SMALL       = BASE_ITEM_ID + 0;   // +500 score
        public const long ITEM_SCORE_BONUS_MEDIUM      = BASE_ITEM_ID + 1;   // +2000 score
        public const long ITEM_SCORE_BONUS_LARGE       = BASE_ITEM_ID + 2;   // +5000 score
        public const long ITEM_RETRACT_SPEED_UP        = BASE_ITEM_ID + 3;   // +5 retract speed
        public const long ITEM_RETRACT_BONUS_UP        = BASE_ITEM_ID + 4;   // +5 retract bonus
        public const long ITEM_DECAY_RATE_DOWN         = BASE_ITEM_ID + 5;   // -2 bar shrink (constant)
        public const long ITEM_DECAY_FACTOR_DOWN       = BASE_ITEM_ID + 6;   // bar shrink % reduced
        public const long ITEM_IMPACT_PENALTY_DOWN     = BASE_ITEM_ID + 7;   // -5% impact penalty
        public const long ITEM_BAR_THRESHOLD_DOWN      = BASE_ITEM_ID + 8;   // -500 music threshold (easier)
        public const long ITEM_EXTRA_LEVEL             = BASE_ITEM_ID + 9;   // counts toward level goal
        public const long ITEM_WATER_ACCESS           = BASE_ITEM_ID + 10;  // unlocks going below the water surface
        public const long ITEM_LEVEL_SKIP             = BASE_ITEM_ID + 11;  // skips the current level (like pressing Enter)
        public const long ITEM_GRAPPLE_PAYOUT_UP      = BASE_ITEM_ID + 12;  // +2 grapple payout speed (starts at 4, cap 14)
        public const long ITEM_GRAPPLE_UNLOCK          = BASE_ITEM_ID + 13;  // unlocks the grapple entirely (hard block without it)

        // Traps
        public const long ITEM_TRAP_GRAVITY_SPIKE      = BASE_ITEM_ID + 20;  // briefly double gravity
        public const long ITEM_TRAP_DECAY_SPIKE        = BASE_ITEM_ID + 21;  // +10 decay rate for 10s
        public const long ITEM_TRAP_DISCONNECT_GRAPPLE = BASE_ITEM_ID + 22;  // force-release grapple

        // ── Item names (must match apworld) ─────────────────────────────────────
        public static string GetItemName(long itemId)
        {
            switch (itemId)
            {
                case ITEM_SCORE_BONUS_SMALL:       return "Score Bonus (Small)";
                case ITEM_SCORE_BONUS_MEDIUM:      return "Score Bonus (Medium)";
                case ITEM_SCORE_BONUS_LARGE:       return "Score Bonus (Large)";
                case ITEM_RETRACT_SPEED_UP:        return "Retract Speed Up";
                case ITEM_RETRACT_BONUS_UP:        return "Retract Bonus Up";
                case ITEM_DECAY_RATE_DOWN:         return "Bar Decay Rate Down";
                case ITEM_DECAY_FACTOR_DOWN:       return "Bar Decay Factor Down";
                case ITEM_IMPACT_PENALTY_DOWN:     return "Impact Penalty Down";
                case ITEM_BAR_THRESHOLD_DOWN:      return "Bar Threshold Down";
                case ITEM_EXTRA_LEVEL:             return "Extra Level";
                case ITEM_WATER_ACCESS:           return "Water Access";
                case ITEM_LEVEL_SKIP:             return "Level Skip";
                case ITEM_GRAPPLE_PAYOUT_UP:      return "Grapple Payout Speed Up";
                case ITEM_GRAPPLE_UNLOCK:          return "Grapple Unlock";
                case ITEM_TRAP_GRAVITY_SPIKE:      return "Gravity Spike (Trap)";
                case ITEM_TRAP_DECAY_SPIKE:        return "Decay Spike (Trap)";
                case ITEM_TRAP_DISCONNECT_GRAPPLE: return "Grapple Disconnect (Trap)";
                default: return $"Unknown Item ({itemId})";
            }
        }
    }
}
