using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Packets;

// Usage: APProxy <host:port> <slot> [password]
const string GAME       = "Floating Point";
const string PIPE_S2C   = "FloatingPointArchipelago_S2C"; // proxy writes, game reads
const string PIPE_C2S   = "FloatingPointArchipelago_C2S"; // game writes, proxy reads

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: APProxy <host:port> <slot> [password]");
    Environment.Exit(1);
}

string hostArg  = args[0];
string slot     = args[1];
string password = args.Length > 2 ? args[2] : "";

if (!hostArg.StartsWith("ws://") && !hostArg.StartsWith("wss://"))
    hostArg = "wss://" + hostArg;

Console.WriteLine($"AP Proxy");
Console.WriteLine($"  Server : {hostArg}");
Console.WriteLine($"  Slot   : {slot}");
Console.WriteLine($"  S2C    : \\\\.\\pipe\\{PIPE_S2C}");
Console.WriteLine($"  C2S    : \\\\.\\pipe\\{PIPE_C2S}");
Console.WriteLine();

while (true)
{
    Console.WriteLine("[Proxy] Waiting for game to connect...");

    // Create both pipe servers before waiting — game connects to both
    using var pipeS2C = new NamedPipeServerStream(PIPE_S2C, PipeDirection.Out, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var pipeC2S = new NamedPipeServerStream(PIPE_C2S, PipeDirection.In,  1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    // Wait for both connections (game connects to S2C first, then C2S)
    await pipeS2C.WaitForConnectionAsync();
    Console.WriteLine("[Proxy] S2C pipe connected.");
    await pipeC2S.WaitForConnectionAsync();
    Console.WriteLine("[Proxy] C2S pipe connected.");

    await RunSession(pipeS2C, pipeC2S, hostArg, slot, password);

    Console.WriteLine("[Proxy] Session ended.\n");
}

static async Task RunSession(
    NamedPipeServerStream pipeS2C,
    NamedPipeServerStream pipeC2S,
    string host, string slot, string password)
{
    using var cts = new CancellationTokenSource();
    var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // S2C: proxy → game  (Out pipe, dedicated write thread)
    var writer = new StreamWriter(pipeS2C, enc, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

    var sendQueue = new Queue<string?>();
    var sendLock  = new object();

    void EnqueueLine(string line)
    {
        lock (sendLock) { sendQueue.Enqueue(line); Monitor.Pulse(sendLock); }
    }

    void EnqueueJson(JsonNode msg)
    {
        string line = msg.ToJsonString();
        Console.WriteLine($"[->Game] {line}");
        EnqueueLine(line);
    }

    var writeThread = new Thread(() =>
    {
        while (true)
        {
            string? line;
            lock (sendLock)
            {
                while (sendQueue.Count == 0) Monitor.Wait(sendLock);
                line = sendQueue.Dequeue();
            }
            if (line == null) return;
            try   { writer.WriteLine(line); }
            catch (Exception ex) { Console.Error.WriteLine($"[Proxy] Write error: {ex.Message}"); cts.Cancel(); return; }
        }
    }) { IsBackground = true };
    writeThread.Start();

    // C2S: game → proxy  (In pipe, async read loop)
    var reader = new StreamReader(pipeC2S, enc, leaveOpen: true);

    // ── Connect to AP ────────────────────────────────────────────────────────
    ArchipelagoSession session;
    LoginSuccessful loginOk;
    try
    {
        Console.WriteLine("[AP] Connecting...");
        session = ArchipelagoSessionFactory.CreateSession(host);
        var result = session.TryConnectAndLogin(
            GAME, slot, ItemsHandlingFlags.AllItems,
            new Version(0, 6, 0),
            password: string.IsNullOrEmpty(password) ? null : password);

        if (!result.Successful)
        {
            var fail = (LoginFailure)result;
            string err = string.Join(", ", fail.Errors);
            Console.Error.WriteLine($"[AP] Login failed: {err}");
            EnqueueJson(new JsonObject { ["type"] = "error", ["message"] = err });
            await Task.Delay(500); EnqueueLine(null!); return;
        }

        loginOk = (LoginSuccessful)result;
        Console.WriteLine($"[AP] Connected as '{slot}'.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[AP] Exception: {ex.Message}");
        EnqueueJson(new JsonObject { ["type"] = "error", ["message"] = ex.Message });
        await Task.Delay(500); EnqueueLine(null!); return;
    }

    // ── Send "connected" ─────────────────────────────────────────────────────
    var slotDataNode = new JsonObject();
    foreach (var kv in loginOk.SlotData)
    {
        slotDataNode[kv.Key] = kv.Value switch
        {
            long   l => JsonValue.Create(l),
            int    i => JsonValue.Create((long)i),
            double d => JsonValue.Create(d),
            bool   b => JsonValue.Create(b),
            string s => JsonValue.Create(s),
            _        => JsonValue.Create(kv.Value?.ToString() ?? "")
        };
    }

    var checkedArr = new JsonArray();
    foreach (var locId in session.Locations.AllLocationsChecked)
        checkedArr.Add(locId);

    var itemsArr = new JsonArray();
    foreach (var item in session.Items.AllItemsReceived)
        itemsArr.Add(item.ItemId);

    EnqueueJson(new JsonObject
    {
        ["type"]              = "connected",
        ["slot_data"]         = slotDataNode,
        ["checked_locations"] = checkedArr,
        ["items_received"]    = itemsArr,
    });

    // Wire new items
    int alreadySent = itemsArr.Count;
    int itemIndex   = 0;
    session.Items.ItemReceived += helper =>
    {
        while (helper.Any())
        {
            var item = helper.DequeueItem();
            if (itemIndex < alreadySent) { itemIndex++; continue; }
            itemIndex++;
            Console.WriteLine($"[AP] New item: {item.ItemName} ({item.ItemId})");
            EnqueueJson(new JsonObject { ["type"] = "item", ["item_id"] = item.ItemId });
        }
    };

    session.Socket.SocketClosed += reason =>
    {
        Console.WriteLine($"[AP] Socket closed: {reason}");
        EnqueueJson(new JsonObject { ["type"] = "disconnected" });
        cts.Cancel();
    };

    // ── Read commands from game ───────────────────────────────────────────────
    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try   { msg = JsonNode.Parse(line); }
            catch { Console.Error.WriteLine($"[Proxy] Bad JSON: {line}"); continue; }

            switch (msg?["type"]?.GetValue<string>())
            {
                case "check":
                {
                    long id = msg!["location_id"]!.GetValue<long>();
                    Console.WriteLine($"[AP] Check location: {id}");
                    session.Locations.CompleteLocationChecks(id);
                    break;
                }
                case "goal":
                    Console.WriteLine("[AP] Goal complete.");
                    session.Socket.SendPacket(new StatusUpdatePacket
                        { Status = ArchipelagoClientState.ClientGoal });
                    break;
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) when (!cts.IsCancellationRequested)
    {
        Console.Error.WriteLine($"[Proxy] Read error: {ex.Message}");
    }
    finally
    {
        EnqueueLine(null!); // stop write thread
        try { await session.Socket.DisconnectAsync(); } catch { }
        cts.Cancel();
    }
}
