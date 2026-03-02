using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;

namespace FloatingPointArchipelago
{
    /// <summary>
    /// Talks to APProxy.exe over a named pipe.
    /// The proxy holds the real Archipelago.MultiClient.Net session and handles all WSS/TLS.
    /// Protocol: newline-delimited JSON in both directions.
    ///
    /// Proxy -> Plugin:
    ///   {"type":"connected","slot_data":{...},"checked_locations":[...]}
    ///   {"type":"item","item_id":45000001}
    ///   {"type":"disconnected"}
    ///   {"type":"error","message":"..."}
    ///
    /// Plugin -> Proxy:
    ///   {"type":"check","location_id":45000100}
    ///   {"type":"goal"}
    /// </summary>
    public class ArchipelagoClient
    {
        public const string GAME_NAME = "Floating Point";
        const string PIPE_S2C = "FloatingPointArchipelago_S2C"; // proxy→game
        const string PIPE_C2S = "FloatingPointArchipelago_C2S"; // game→proxy

        private static readonly ManualLogSource Logger =
            BepInEx.Logging.Logger.CreateLogSource("FP.APClient");

        public static ArchipelagoClient Instance { get; private set; }

        public bool   IsConnected  { get; private set; }
        public bool   IsConnecting { get; private set; }
        public string LastError    { get; private set; }

        // Slot data
        public int  GoalType               { get; private set; } = 0;
        public int  LevelsRequired         { get; private set; } = 50;
        public int  BarsRequired           { get; private set; } = 400;
        public int  NumLevels              { get; private set; } = 50;
        public bool WaterAccessRequired    { get; private set; } = true;
        public bool GrappleUnlockRequired  { get; private set; } = true;
        /// <summary>0=very_slow, 1=moderate, 2=near_default</summary>
        public int  StartingRetractSpeed   { get; private set; } = 0;

        // Events
        public event Action<long> OnItemReceived;
        public event Action       OnConnected;
        public event Action       OnDisconnected;

        // Checked locations (populated from proxy on connect)
        private readonly HashSet<long> _checkedLocations = new HashSet<long>();

        private NamedPipeClientStream _pipeRead;   // S2C: proxy→game
        private NamedPipeClientStream _pipeWrite;  // C2S: game→proxy
        private StreamWriter          _writer;
        private Thread                _readThread;

        public static void Init()
        {
            if (Instance == null)
                Instance = new ArchipelagoClient();
        }

        /// <summary>
        /// Connects to the named pipe served by APProxy.exe.
        /// All auth details (host/slot/password) are held by the proxy — no arguments needed.
        /// </summary>
        public void Connect()
        {
            if (IsConnecting || IsConnected) return;
            IsConnecting = true;
            LastError    = null;

            var t = new Thread(ConnectInternal);
            t.IsBackground = true;
            t.Start();
        }

        private void ConnectInternal()
        {
            try
            {
                Logger.LogInfo($"Connecting to APProxy pipes...");

                var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

                // Connect S2C first (proxy is listening on both before we connect)
                _pipeRead = new NamedPipeClientStream(".", PIPE_S2C, PipeDirection.In, PipeOptions.None);
                _pipeRead.Connect(5000);
                Logger.LogInfo("S2C pipe connected.");

                _pipeWrite = new NamedPipeClientStream(".", PIPE_C2S, PipeDirection.Out, PipeOptions.None);
                _pipeWrite.Connect(5000);
                Logger.LogInfo("C2S pipe connected.");

                _writer = new StreamWriter(_pipeWrite, enc) { AutoFlush = true, NewLine = "\n" };

                // Wait for first message on the read pipe — must be "connected" or "error"
                var reader = new StreamReader(_pipeRead, enc);
                string firstLine = reader.ReadLine();
                if (string.IsNullOrEmpty(firstLine))
                {
                    LastError = "Proxy closed connection immediately";
                    Logger.LogError($"AP connection failed: {LastError}");
                    IsConnecting = false;
                    return;
                }

                var msg = JObject.Parse(firstLine);
                string type = (string)msg["type"];

                if (type == "error")
                {
                    LastError = (string)msg["message"] ?? "Unknown error";
                    Logger.LogError($"AP connection failed: {LastError}");
                    IsConnecting = false;
                    return;
                }

                if (type != "connected")
                {
                    LastError = $"Unexpected first message: {firstLine}";
                    Logger.LogError($"AP connection failed: {LastError}");
                    IsConnecting = false;
                    return;
                }

                // Parse slot data
                var sd = msg["slot_data"] as JObject;
                if (sd != null)
                {
                    if (sd["goal_type"]               != null) GoalType               = (int)sd["goal_type"];
                    if (sd["levels_required"]         != null) LevelsRequired         = (int)sd["levels_required"];
                    if (sd["bars_required"]           != null) BarsRequired           = (int)sd["bars_required"];
                    if (sd["num_levels"]              != null) NumLevels              = (int)sd["num_levels"];
                    if (sd["water_access_required"]   != null) WaterAccessRequired    = (int)sd["water_access_required"] != 0;
                    if (sd["grapple_unlock_required"] != null) GrappleUnlockRequired  = (int)sd["grapple_unlock_required"] != 0;
                    if (sd["starting_retract_speed"]  != null) StartingRetractSpeed   = (int)sd["starting_retract_speed"];
                }

                // Parse already-checked locations
                var checkedArr = msg["checked_locations"] as JArray;
                if (checkedArr != null)
                    foreach (var v in checkedArr)
                        _checkedLocations.Add((long)v);

                IsConnected  = true;
                IsConnecting = false;

                Logger.LogInfo(
                    $"Connected via APProxy. GoalType={GoalType}, NumLevels={NumLevels}, LevelsRequired={LevelsRequired}, " +
                    $"BarsRequired={BarsRequired}, GrappleUnlockRequired={GrappleUnlockRequired}, " +
                    $"StartingRetractSpeed={StartingRetractSpeed}");

                LocationManager.Instance?.OnConnected();
                OnConnected?.Invoke();

                // Replay all items received so far
                var itemsArr = msg["items_received"] as JArray;
                if (itemsArr != null)
                {
                    Logger.LogInfo($"Replaying {itemsArr.Count} previously received item(s).");
                    foreach (var v in itemsArr)
                        OnItemReceived?.Invoke((long)v);
                }

                // Start background writer and reader threads
                StartWriteThread();
                _readThread = new Thread(() => ReadLoop(reader));
                _readThread.IsBackground = true;
                _readThread.Start();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.LogError($"AP connection exception: {ex}");
                IsConnecting = false;
            }
        }

        private void ReadLoop(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line) || line.Trim().Length == 0) continue;
                    try
                    {
                        var msg  = JObject.Parse(line);
                        string t = (string)msg["type"];
                        switch (t)
                        {
                            case "item":
                                long itemId = (long)msg["item_id"];
                                Logger.LogInfo($"Item received from proxy: {itemId}");
                                OnItemReceived?.Invoke(itemId);
                                break;

                            case "disconnected":
                                Logger.LogWarning("Proxy reports AP disconnected.");
                                IsConnected = false;
                                OnDisconnected?.Invoke();
                                return;

                            case "error":
                                Logger.LogError($"Proxy error: {(string)msg["message"]}");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Bad message from proxy: {ex.Message} — {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsConnected)
                    Logger.LogError($"Proxy read loop error: {ex.Message}");
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    StopWriteThread();
                    OnDisconnected?.Invoke();
                }
            }
        }

        public void Disconnect()
        {
            if (!IsConnected) return;
            IsConnected = false;
            StopWriteThread();
            try { _pipeRead?.Close(); } catch { }
            try { _pipeWrite?.Close(); } catch { }
            OnDisconnected?.Invoke();
        }

        public void SendLocation(long locationId)
        {
            SendRaw($"{{\"type\":\"check\",\"location_id\":{locationId}}}");
            Logger.LogInfo($"Sent location check: {locationId}");
        }

        public void SendGoalComplete()
        {
            SendRaw("{\"type\":\"goal\"}");
            Logger.LogInfo("Goal complete sent.");
        }

        public bool IsLocationChecked(long locationId)
        {
            return _checkedLocations.Contains(locationId);
        }

        // Write queue — SendRaw enqueues; a background thread does the actual pipe write
        // so the main thread (Unity Update) is never blocked by pipe I/O.
        private readonly Queue<string>  _sendQueue  = new Queue<string>();
        private readonly object         _sendLock   = new object();
        private Thread                  _writeThread;

        private void StartWriteThread()
        {
            _writeThread = new Thread(WriteLoop) { IsBackground = true };
            _writeThread.Start();
        }

        private void WriteLoop()
        {
            while (true)
            {
                string line = null;
                lock (_sendLock)
                {
                    while (_sendQueue.Count == 0)
                    {
                        // If disconnected and queue is empty, exit
                        if (!IsConnected) return;
                        Monitor.Wait(_sendLock);
                    }
                    line = _sendQueue.Dequeue();
                }

                // Null sentinel: caller wants the thread to stop
                if (line == null) return;

                try
                {
                    _writer.WriteLine(line);
                    Logger.LogInfo($"[WriteThread] wrote: {line}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Pipe write error: {ex.Message}");
                    return;
                }
            }
        }

        private void SendRaw(string json)
        {
            if (!IsConnected) return;
            lock (_sendLock)
            {
                _sendQueue.Enqueue(json);
                Monitor.Pulse(_sendLock);
            }
            Logger.LogInfo($"[SendRaw] enqueued (queue size now ~{_sendQueue.Count}): {json}");
        }

        private void StopWriteThread()
        {
            lock (_sendLock)
            {
                _sendQueue.Enqueue(null); // sentinel
                Monitor.Pulse(_sendLock);
            }
        }
    }
}
