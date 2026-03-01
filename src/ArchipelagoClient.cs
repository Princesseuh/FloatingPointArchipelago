using System;
using System.Collections.Generic;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using BepInEx.Logging;

namespace FloatingPointArchipelago
{
    public class ArchipelagoClient
    {
        public const string GAME_NAME = "Floating Point";

        private static readonly ManualLogSource Logger =
            BepInEx.Logging.Logger.CreateLogSource("FP.APClient");

        public static ArchipelagoClient Instance { get; private set; }

        public ArchipelagoSession Session  { get; private set; }
        public bool IsConnected            { get; private set; }
        public bool IsConnecting           { get; private set; }
        public string LastError            { get; private set; }

        // Slot data
        public int GoalType               { get; private set; } = 0;   // 0=levels_completed, 1=score, 2=bars_collected, 3=all_locations
        public int GoalScore              { get; private set; } = 10_000;
        public int NumLevels              { get; private set; } = 10;
        public int LevelsRequired         { get; private set; } = 3;
        public int BarsRequired           { get; private set; } = 96;
        public int LevelCompleteCondition { get; private set; } = 0;   // 0=all_bars, 1=press_enter
        public bool WaterAccessRequired   { get; private set; } = true; // false = water always open
        public bool LevelSkipRequired     { get; private set; } = true; // false = Enter always works
        public bool GrappleUnlockRequired { get; private set; } = true; // false = grapple always available

        // Callbacks wired up by ItemManager / LocationManager
        public event Action<ItemInfo> OnItemReceived;
        public event Action<string>   OnMessageReceived;
        public event Action           OnConnected;
        public event Action           OnDisconnected;

        public static void Init()
        {
            if (Instance == null)
                Instance = new ArchipelagoClient();
        }

        /// <summary>Kicks off a background thread that connects to the AP server.</summary>
        public void Connect(string host, string slotName, string password)
        {
            if (IsConnecting || IsConnected) return;
            IsConnecting = true;
            LastError = null;

            var thread = new Thread(() => ConnectInternal(host, slotName, password));
            thread.IsBackground = true;
            thread.Start();
        }

        private void ConnectInternal(string host, string slotName, string password)
        {
            try
            {
                Session = ArchipelagoSessionFactory.CreateSession(host);

                var result = Session.TryConnectAndLogin(
                    GAME_NAME,
                    slotName,
                    ItemsHandlingFlags.AllItems,
                    new Version(0, 5, 1),
                    password: string.IsNullOrEmpty(password) ? null : password
                );

                if (result.Successful)
                {
                    var ok = (LoginSuccessful)result;

                    if (ok.SlotData.ContainsKey("goal_type"))
                        GoalType = Convert.ToInt32(ok.SlotData["goal_type"]);
                    if (ok.SlotData.ContainsKey("goal_score"))
                        GoalScore = Convert.ToInt32(ok.SlotData["goal_score"]);
                    if (ok.SlotData.ContainsKey("num_levels"))
                        NumLevels = Convert.ToInt32(ok.SlotData["num_levels"]);
                    if (ok.SlotData.ContainsKey("levels_required"))
                        LevelsRequired = Convert.ToInt32(ok.SlotData["levels_required"]);
                    if (ok.SlotData.ContainsKey("bars_required"))
                        BarsRequired = Convert.ToInt32(ok.SlotData["bars_required"]);
                    if (ok.SlotData.ContainsKey("level_complete_condition"))
                        LevelCompleteCondition = Convert.ToInt32(ok.SlotData["level_complete_condition"]);
                    if (ok.SlotData.ContainsKey("water_access_required"))
                        WaterAccessRequired = Convert.ToInt32(ok.SlotData["water_access_required"]) != 0;
                    if (ok.SlotData.ContainsKey("level_skip_required"))
                        LevelSkipRequired = Convert.ToInt32(ok.SlotData["level_skip_required"]) != 0;
                    if (ok.SlotData.ContainsKey("grapple_unlock_required"))
                        GrappleUnlockRequired = Convert.ToInt32(ok.SlotData["grapple_unlock_required"]) != 0;

                    // Wire up events
                    Session.Items.ItemReceived += OnItemReceivedHandler;
                    Session.MessageLog.OnMessageReceived += OnMessageReceivedHandler;
                    Session.Socket.SocketClosed += OnSocketClosed;

                    IsConnected = true;
                    IsConnecting = false;

                    Logger.LogInfo(
                        $"Connected to Archipelago as '{slotName}'. " +
                        $"GoalType={GoalType}, GoalScore={GoalScore}, " +
                        $"LevelsRequired={LevelsRequired}, BarsRequired={BarsRequired}, " +
                        $"LevelCompleteCondition={LevelCompleteCondition}");

                    // Send the "Connected to Archipelago" location check before notifying
                    // other systems — this is sphere 0 and must arrive at the server first
                    // so that Grapple Unlock (sphere 1) is released to the player.
                    LocationManager.Instance?.OnConnected();

                    OnConnected?.Invoke();
                }
                else
                {
                    var fail = (LoginFailure)result;
                    LastError = string.Join(", ", fail.Errors);
                    Logger.LogError($"AP connection failed: {LastError}");
                    IsConnecting = false;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.LogError($"AP connection exception: {ex}");
                IsConnecting = false;
            }
        }

        public void Disconnect()
        {
            if (!IsConnected) return;
            try
            {
                Session?.Socket?.Disconnect();
            }
            catch { }
            IsConnected = false;
            Session = null;
            OnDisconnected?.Invoke();
        }

        public void SendLocation(long locationId)
        {
            if (!IsConnected) return;
            try
            {
                Session.Locations.CompleteLocationChecks(locationId);
                Logger.LogInfo($"Sent location check: {locationId}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to send location {locationId}: {ex.Message}");
            }
        }

        public void SendGoalComplete()
        {
            if (!IsConnected) return;
            try
            {
                Session.Socket.SendPacket(new StatusUpdatePacket
                {
                    Status = ArchipelagoClientState.ClientGoal
                });
                Logger.LogInfo("Goal complete sent to Archipelago.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to send goal: {ex.Message}");
            }
        }

        public bool IsLocationChecked(long locationId)
        {
            if (!IsConnected) return false;
            return Session.Locations.AllLocationsChecked.Contains(locationId);
        }

        // ── Event handlers ───────────────────────────────────────────────────

        private void OnItemReceivedHandler(ReceivedItemsHelper helper)
        {
            // Dequeue all pending items and forward them
            while (helper.Any())
            {
                var item = helper.DequeueItem();
                Logger.LogInfo($"Received item: {item.ItemName ?? item.ItemId.ToString()}");
                OnItemReceived?.Invoke(item);
            }
        }

        private void OnMessageReceivedHandler(LogMessage message)
        {
            OnMessageReceived?.Invoke(message.ToString());
        }

        private void OnSocketClosed(string reason)
        {
            Logger.LogWarning($"AP socket closed: {reason}");
            IsConnected = false;
            OnDisconnected?.Invoke();
        }
    }
}
