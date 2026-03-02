namespace FloatingPointArchipelago
{
    /// <summary>
    /// Central registry of all Archipelago location and item IDs for Floating Point.
    ///
    /// Location layout (dynamic based on num_levels N, default 50):
    ///   N  cumulative-bar milestones : fire when total bars crosses 8, 16, ..., N*8.
    ///                                  IDs = BASE + 0 .. BASE + N-1
    ///   N  level completion checks   : fire on each level completed (water-gated).
    ///                                  IDs = BASE + 10_000 .. BASE + 10_000 + N-1
    ///    1 "Connected"               : ID  = BASE + 20_000
    ///   16 single-level best-bar     : fire once when best-single-level bar count hits 2/4/.../32.
    ///                                  IDs = BASE + 30_000 .. BASE + 30_015
    ///                                  Thresholds 26-32 require Water Access.
    ///    6 score milestones          : fire once when running score crosses 50k/100k/250k/500k/750k/1M.
    ///                                  IDs = BASE + 40_000 .. BASE + 40_005
    ///
    ///   Total = 2*N + 23  (default N=50 → 123 locations)
    /// </summary>
    public static class LocationData
    {
        // ── Base offsets ────────────────────────────────────────────────────────
        public const long BASE_LOCATION_ID = 45_000_000;
        public const long BASE_ITEM_ID     = 45_000_000;

        // ── Runtime level count (from slot data, default 50) ────────────────────
        /// <summary>
        /// Number of levels for this slot — drives both the cumulative-bar milestone
        /// count and the level-completion check count. Falls back to 50 if no AP
        /// session is active.
        /// </summary>
        public static int NumLevels => ArchipelagoClient.Instance?.NumLevels ?? 50;

        // ── Cumulative-bar milestones ────────────────────────────────────────────
        /// <summary>Number of cumulative-bar milestone locations (== NumLevels).</summary>
        public static int TotalBarMilestoneCount => NumLevels;
        /// <summary>Bar step between consecutive cumulative milestones.</summary>
        public const int BAR_MILESTONE_STEP = 8;

        public static int    GetCumulativeBarThreshold(int i)   => (i + 1) * BAR_MILESTONE_STEP;
        public static long   GetCumulativeBarMilestoneId(int i)  => BASE_LOCATION_ID + i;
        public static string GetCumulativeBarMilestoneName(int i)
            => $"Total Bars - {GetCumulativeBarThreshold(i)} Collected";

        // ── Level completion locations ───────────────────────────────────────────
        /// <summary>Total level-completion check slots (== NumLevels).</summary>
        public static int TotalLevelCompletions => NumLevels;
        public const long LEVEL_COMPLETE_OFFSET = 10_000;

        public static long   GetLevelCompleteLocationId(int i)   => BASE_LOCATION_ID + LEVEL_COMPLETE_OFFSET + i;
        public static string GetLevelCompleteLocationName(int i) => $"Level Complete {i + 1}";

        // ── "Connected to Archipelago" ───────────────────────────────────────────
        public const long LOCATION_CONNECTED = BASE_LOCATION_ID + 20_000;

        // ── Single-level best-bar milestones ────────────────────────────────────
        public const long BAR_MILESTONE_OFFSET = 30_000;

        /// <summary>Thresholds for single-level best-bar milestones (every 2 bars, 2–32).</summary>
        public static readonly int[] SINGLE_LEVEL_BAR_MILESTONES =
            { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32 };

        /// <summary>
        /// Single-level milestones with threshold strictly above this value require Water Access.
        /// Bars 0-23 are above water; bars 24-31 are below. So 26 is the first water-gated threshold.
        /// </summary>
        public const int WATER_GATED_BAR_START = 24;

        public static long   GetSingleLevelMilestoneId(int i)   => BASE_LOCATION_ID + BAR_MILESTONE_OFFSET + i;
        public static string GetSingleLevelMilestoneName(int i) => $"Best Single Level - {SINGLE_LEVEL_BAR_MILESTONES[i]} Bars";

        // ── Score milestones ─────────────────────────────────────────────────────
        public const long SCORE_MILESTONE_OFFSET = 40_000;

        /// <summary>Score thresholds for score milestone checks.</summary>
        public static readonly float[] SCORE_MILESTONES = { 50_000f, 100_000f, 250_000f, 500_000f, 750_000f, 1_000_000f };

        public static long   GetScoreMilestoneId(int i)   => BASE_LOCATION_ID + SCORE_MILESTONE_OFFSET + i;
        public static string GetScoreMilestoneName(int i) => $"Score - {FormatScore(SCORE_MILESTONES[i])}";

        private static string FormatScore(float score)
        {
            if (score >= 1_000_000f) return $"{(int)(score / 1_000_000f)}M";
            if (score >= 1_000f)     return $"{(int)(score / 1_000f)}k";
            return $"{(int)score}";
        }

        // ── Item IDs ────────────────────────────────────────────────────────────
        public const long ITEM_SCORE_BONUS_SMALL       = BASE_ITEM_ID + 0;
        public const long ITEM_SCORE_BONUS_MEDIUM      = BASE_ITEM_ID + 1;
        public const long ITEM_SCORE_BONUS_LARGE       = BASE_ITEM_ID + 2;
        public const long ITEM_RETRACT_SPEED_UP        = BASE_ITEM_ID + 3;
        public const long ITEM_RETRACT_BONUS_UP        = BASE_ITEM_ID + 4;
        public const long ITEM_DECAY_RATE_DOWN         = BASE_ITEM_ID + 5;
        public const long ITEM_DECAY_FACTOR_DOWN       = BASE_ITEM_ID + 6;
        public const long ITEM_IMPACT_PENALTY_DOWN     = BASE_ITEM_ID + 7;
        public const long ITEM_BAR_THRESHOLD_DOWN      = BASE_ITEM_ID + 8;
        public const long ITEM_WATER_ACCESS            = BASE_ITEM_ID + 10;
        public const long ITEM_LEVEL_SKIP              = BASE_ITEM_ID + 11;
        public const long ITEM_GRAPPLE_PAYOUT_UP       = BASE_ITEM_ID + 12;
        public const long ITEM_GRAPPLE_UNLOCK          = BASE_ITEM_ID + 13;
        public const long ITEM_TRAP_GRAVITY_SPIKE      = BASE_ITEM_ID + 20;
        public const long ITEM_TRAP_DECAY_SPIKE        = BASE_ITEM_ID + 21;
        public const long ITEM_TRAP_DISCONNECT_GRAPPLE = BASE_ITEM_ID + 22;

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
                case ITEM_WATER_ACCESS:            return "Water Access";
                case ITEM_LEVEL_SKIP:              return "Level Skip (Trap)";
                case ITEM_GRAPPLE_PAYOUT_UP:       return "Grapple Payout Speed Up";
                case ITEM_GRAPPLE_UNLOCK:          return "Grapple Unlock";
                case ITEM_TRAP_GRAVITY_SPIKE:      return "Gravity Spike (Trap)";
                case ITEM_TRAP_DECAY_SPIKE:        return "Decay Spike (Trap)";
                case ITEM_TRAP_DISCONNECT_GRAPPLE: return "Grapple Disconnect (Trap)";
                default: return $"Unknown Item ({itemId})";
            }
        }
    }
}
