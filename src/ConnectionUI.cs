using System.Collections.Generic;
using UnityEngine;

namespace FloatingPointArchipelago
{
    /// <summary>
    /// In-game connection UI and HUD overlay for Archipelago.
    /// Rendered with Unity's legacy OnGUI (same as the base game).
    ///
    /// F1 toggles the connection panel.
    /// The panel only shows connection status + a Connect/Disconnect button.
    /// All auth details (host/slot/password) are owned by APProxy — not shown here.
    /// </summary>
    public class ConnectionUI : MonoBehaviour
    {
        public static ConnectionUI Instance { get; private set; }

        // Panel visibility
        private bool _showConnectionPanel = false;

        // HUD messages
        private class HUDMessage
        {
            public string Text;
            public float ExpiresAt;
        }
        private readonly List<HUDMessage> _hudMessages = new List<HUDMessage>();

        // AP status ticker
        private const int MAX_TICKER_LINES = 6;
        private readonly Queue<string> _ticker = new Queue<string>();

        // GUI state helpers
        private bool _connected  => ArchipelagoClient.Instance != null && ArchipelagoClient.Instance.IsConnected;
        private bool _connecting => ArchipelagoClient.Instance != null && ArchipelagoClient.Instance.IsConnecting;

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (ArchipelagoClient.Instance != null)
            {
                ArchipelagoClient.Instance.OnConnected    += () => ShowMessage("Connected to Archipelago!", 5f);
                ArchipelagoClient.Instance.OnDisconnected += () => ShowMessage("Disconnected from Archipelago.", 5f);
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
                _showConnectionPanel = !_showConnectionPanel;
        }

        public void ShowMessage(string text, float duration = 3f)
        {
            _hudMessages.Add(new HUDMessage { Text = text, ExpiresAt = Time.time + duration });
        }

        private void OnGUI()
        {
            // ── Progress HUD (top-left) ─────────────────────────────────────────
            DrawProgressHUD();

            // ── Connection panel (center, F1) ───────────────────────────────────
            if (_showConnectionPanel)
                DrawConnectionPanel();

            // ── Status indicator (top-right) ────────────────────────────────────
            DrawStatusIndicator();

            // ── HUD messages (center-top) ────────────────────────────────────────
            DrawHUDMessages();

            // ── AP message ticker (bottom-right) ─────────────────────────────────
            DrawTicker();
        }

        // ── Progress HUD ─────────────────────────────────────────────────────────

        private void DrawProgressHUD()
        {
            var client = ArchipelagoClient.Instance;
            if (client == null || !client.IsConnected) return;

            var lm = LocationManager.Instance;
            if (lm == null) return;

            int goalType = client.GoalType;
            float x = 6f, y = 6f, w = 220f, lh = 18f;
            GUI.color = new Color(1f, 1f, 1f, 0.85f);

            if (goalType == GoalType.LevelsCompleted)
            {
                GUI.Label(new Rect(x, y, w, lh),
                    $"Level {lm.CompletedLevels} / {client.LevelsRequired}");
                y += lh;
                GUI.Label(new Rect(x, y, w, lh),
                    $"Bars {lm.TotalBarsCollected}");
            }
            else if (goalType == GoalType.BarsCollected)
            {
                GUI.Label(new Rect(x, y, w, lh),
                    $"Bars {lm.TotalBarsCollected} / {client.BarsRequired}");
                y += lh;
                GUI.Label(new Rect(x, y, w, lh),
                    $"Levels {lm.CompletedLevels}");
            }
            else if (goalType == GoalType.AllLocations)
            {
                GUI.Label(new Rect(x, y, w, lh),
                    $"Locations {lm.CheckedLocationsCount} / {lm.TotalLocations}");
            }

            GUI.color = Color.white;
        }

        // ── Connection panel ──────────────────────────────────────────────────────

        private void DrawConnectionPanel()
        {
            float pw = 280f, ph = 110f;
            float px = (Screen.width  - pw) / 2f;
            float py = (Screen.height - ph) / 2f;
            GUI.Box(new Rect(px, py, pw, ph), "Archipelago");

            float lx = px + 10f;
            float y  = py + 30f;
            float lh = 22f, gap = 30f;

            if (_connected)
            {
                GUI.Label(new Rect(lx, y, pw - 20f, lh), "<color=lime>Connected</color>");
                y += gap;
                if (GUI.Button(new Rect(lx, y, 120f, lh), "Disconnect"))
                    ArchipelagoClient.Instance.Disconnect();
            }
            else if (_connecting)
            {
                GUI.Label(new Rect(lx, y, pw - 20f, lh), "Connecting...");
            }
            else
            {
                string err = ArchipelagoClient.Instance?.LastError;
                if (!string.IsNullOrEmpty(err))
                {
                    GUI.Label(new Rect(lx, y, pw - 20f, 40f),
                        "<color=red>" + err + "</color>");
                    y += gap;
                }
                if (GUI.Button(new Rect(lx, y, 120f, lh), "Connect"))
                    ArchipelagoClient.Instance?.Connect();
            }

            // Close button
            if (GUI.Button(new Rect(px + pw - 30f, py + 2f, 26f, 20f), "X"))
                _showConnectionPanel = false;
        }

        // ── Status indicator ──────────────────────────────────────────────────────

        private void DrawStatusIndicator()
        {
            string label;
            if (_connected)
                label = "<color=lime>[AP]</color>";
            else if (_connecting)
                label = "<color=yellow>[AP connecting...]</color>";
            else
                label = "<color=grey>[AP offline — F1]</color>";

            GUI.Label(new Rect(Screen.width - 200f, 4f, 196f, 20f), label);
        }

        // ── HUD messages ──────────────────────────────────────────────────────────

        private void DrawHUDMessages()
        {
            _hudMessages.RemoveAll(m => Time.time > m.ExpiresAt);
            float y = 40f;
            foreach (var msg in _hudMessages)
            {
                float alpha = Mathf.Clamp01((msg.ExpiresAt - Time.time) / 1f);
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect((Screen.width - 400f) / 2f, y, 400f, 24f), msg.Text);
                y += 26f;
            }
            GUI.color = Color.white;
        }

        // ── Ticker ───────────────────────────────────────────────────────────────

        private void DrawTicker()
        {
            string[] lines;
            lock (_ticker) lines = _ticker.ToArray();
            float y = Screen.height - 20f - lines.Length * 16f;
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            foreach (var line in lines)
            {
                GUI.Label(new Rect(4f, y, Screen.width - 8f, 16f), line);
                y += 16f;
            }
            GUI.color = Color.white;
        }
    }
}
