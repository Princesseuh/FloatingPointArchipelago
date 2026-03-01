using System.Collections.Generic;
using UnityEngine;

namespace FloatingPointArchipelago
{
    /// <summary>
    /// In-game connection UI and HUD overlay for Archipelago.
    /// Rendered with Unity's legacy OnGUI (same as the base game).
    /// </summary>
    public class ConnectionUI : MonoBehaviour
    {
        public static ConnectionUI Instance { get; private set; }

        // Connection form state
        private bool _showConnectionPanel = false;
        private string _host = "archipelago.gg";
        private string _port = "38281";
        private string _slotName = "";
        private string _password = "";

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

        // GUI state
        private bool _connected => ArchipelagoClient.Instance != null && ArchipelagoClient.Instance.IsConnected;
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
                ArchipelagoClient.Instance.OnMessageReceived += msg =>
                {
                    lock (_ticker)
                    {
                        _ticker.Enqueue(msg);
                        while (_ticker.Count > MAX_TICKER_LINES) _ticker.Dequeue();
                    }
                };
                ArchipelagoClient.Instance.OnConnected += () =>
                    ShowMessage("Connected to Archipelago!", 5f);
                ArchipelagoClient.Instance.OnDisconnected += () =>
                    ShowMessage("Disconnected from Archipelago.", 5f);
            }
        }

        private void Update()
        {
            // F1 toggles the connection panel
            if (Input.GetKeyDown(KeyCode.F1))
                _showConnectionPanel = !_showConnectionPanel;
        }

        public void ShowMessage(string text, float duration = 3f)
        {
            _hudMessages.Add(new HUDMessage { Text = text, ExpiresAt = Time.time + duration });
        }

        private void OnGUI()
        {
            // ── Connection panel ─────────────────────────────────────────────
            if (_showConnectionPanel)
                DrawConnectionPanel();

            // ── Status indicator (top-right corner) ──────────────────────────
            DrawStatusIndicator();

            // ── HUD messages (center-top) ────────────────────────────────────
            DrawHUDMessages();

            // ── AP message ticker (bottom-right) ────────────────────────────
            DrawTicker();
        }

        private void DrawConnectionPanel()
        {
            float pw = 340f, ph = 230f;
            float px = (Screen.width - pw) / 2f;
            float py = (Screen.height - ph) / 2f;
            GUI.Box(new Rect(px, py, pw, ph), "Archipelago Connection");

            float lx = px + 10f, rx = px + 140f, w = pw - 150f;
            float y = py + 30f, lh = 22f, gap = 28f;

            GUI.Label(new Rect(lx, y, 125f, lh), "Host:port");
            _host = GUI.TextField(new Rect(rx, y, 140f, lh), _host);
            GUI.Label(new Rect(rx + 145f, y, 10f, lh), ":");
            _port = GUI.TextField(new Rect(rx + 158f, y, 45f, lh), _port);
            y += gap;

            GUI.Label(new Rect(lx, y, 125f, lh), "Slot name");
            _slotName = GUI.TextField(new Rect(rx, y, w, lh), _slotName);
            y += gap;

            GUI.Label(new Rect(lx, y, 125f, lh), "Password");
            _password = GUI.PasswordField(new Rect(rx, y, w, lh), _password, '*');
            y += gap + 6f;

            if (_connected)
            {
                GUI.Label(new Rect(lx, y, pw - 20f, lh), "<color=lime>Connected</color>");
                y += gap;
                if (GUI.Button(new Rect(lx, y, 100f, lh), "Disconnect"))
                    ArchipelagoClient.Instance.Disconnect();
            }
            else if (_connecting)
            {
                GUI.Label(new Rect(lx, y, pw - 20f, lh), "Connecting...");
            }
            else
            {
                if (!string.IsNullOrEmpty(ArchipelagoClient.Instance?.LastError))
                    GUI.Label(new Rect(lx, y, pw - 20f, 40f), "<color=red>" + ArchipelagoClient.Instance.LastError + "</color>");
                y += gap;
                if (GUI.Button(new Rect(lx, y, 100f, lh), "Connect"))
                {
                    string host = _host + ":" + _port;
                    ArchipelagoClient.Instance?.Connect(host, _slotName, _password);
                }
            }

            // Close button
            if (GUI.Button(new Rect(px + pw - 30f, py + 2f, 26f, 20f), "X"))
                _showConnectionPanel = false;
        }

        private void DrawStatusIndicator()
        {
            string label;
            if (_connected)
            {
                var itemMgr = ItemManager.Instance;
                string skipInfo = (itemMgr != null && itemMgr.LevelSkipRequired)
                    ? $"  skips:{itemMgr.LevelSkipsAvailable}"
                    : "";
                label = $"<color=lime>[AP]{skipInfo}</color>";
            }
            else if (_connecting)
                label = "<color=yellow>[AP connecting...]</color>";
            else
                label = "<color=grey>[AP offline — F1]</color>";

            GUI.Label(new Rect(Screen.width - 200f, 4f, 196f, 20f), label);
        }

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
