using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace MysterySort
{
    // One packet shape for everything. kind = state | scene | trace | caption.
    //  trace   A -> relay, every 0.4 s. Raw motion samples for the intent reading; B ignores them.
    //  state   A -> (relay) -> B, 20 Hz. Full state, so a lost packet costs nothing.
    //  scene   A -> relay and B -> relay, 1 Hz. Text description of the local layout.
    //  caption relay -> B. Text shown above the avatar.
    [Serializable]
    public class SortPacket
    {
        public string protocol = SortTransport.Protocol;
        public string kind = "state";
        public string role = "A";
        public string session;
        public long seq;
        public int held = -1;      // note id in A's hand, -1 = none
        public int hover = -1;     // panel id A is holding the note over, -1 = none
        public int focus = -1;     // note id A is attending to (mouse over; gaze target in VR), -1 = none
        public int[] loc;          // panel id per note (0 = home panel)
        public int ev;             // event counter, increases with every grab/place
        public int[] torn;         // caption only: panels the model says A is undecided between, for note `focus`
        public string text;        // recent event lines ("#n t=sec sentence"), scene description, or caption
    }

    // Main-thread polling keeps all Unity API usage on the Unity thread.
    public sealed class SortTransport : IDisposable
    {
        public const string Protocol = "mystery-sort-v1";
        public const int Notes = 6, Panels = 3;

        UdpClient socket;
        IPEndPoint destination, lastSender;
        readonly string session = Guid.NewGuid().ToString("N");
        string peer;
        long outgoing, incoming = -1;

        public float LastReceived { get; private set; } = -1000;
        public int Held { get; private set; } = -1;
        public int Hover { get; private set; } = -1;
        public int Focus { get; private set; } = -1;
        public int[] Loc { get; private set; } = new int[Notes];
        public string Caption { get; private set; } = "";
        // Latest intent reading from the relay: which note, and the panels A is torn between (may be empty).
        public int TornNote { get; private set; } = -1;
        public int[] Torn { get; private set; } = new int[0];
        public int TornStamp { get; private set; }
        public string Error { get; private set; } = "";
        public long Accepted { get; private set; }

        public void Start(bool sender, string host, int port)
        {
            Dispose();
            try
            {
                if (sender)
                {
                    if (!string.IsNullOrEmpty(host)) destination = new IPEndPoint(IPAddress.Parse(host), port);
                    socket = new UdpClient(AddressFamily.InterNetwork);
                    socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                }
                else socket = new UdpClient(port);
                socket.Client.Blocking = false;
                Error = "";
            }
            catch (Exception e) { Error = e.Message; Dispose(); }
        }

        // Headsets learn the relay's address after start-up (discovery), so it can be set or changed later.
        public string Destination => destination != null ? destination.ToString() : "";
        public void SetDestination(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) { destination = null; return; }
            if (destination != null && destination.Address.ToString() == host && destination.Port == port) return;
            try { destination = new IPEndPoint(IPAddress.Parse(host), port); } catch (FormatException e) { Error = e.Message; }
        }

        void Send(SortPacket packet, IPEndPoint to)
        {
            if (socket == null || to == null) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(packet));
                socket.Send(data, data.Length, to);
                Error = "";
            }
            catch (SocketException e) { Error = e.Message; }
        }

        public void SendState(int held, int hover, int focus, int[] loc, int ev, string text)
        {
            Send(new SortPacket { kind = "state", role = "A", session = session, seq = ++outgoing,
                held = held, hover = hover, focus = focus, loc = loc, ev = ev, text = text }, destination);
        }

        // Each trace packet repeats the last few samples, so one lost packet loses nothing.
        public void SendTrace(int count, string text)
        {
            Send(new SortPacket { kind = "trace", role = "A", session = session, ev = count, text = text }, destination);
        }

        // A sends its scene to its destination; B answers whoever sent it the last packet (the relay).
        public void SendScene(string role, string text)
        {
            Send(new SortPacket { kind = "scene", role = role, session = session, text = text },
                destination ?? lastSender);
        }

        public static bool ValidState(SortPacket p)
        {
            if (p.loc == null || p.loc.Length != Notes) return false;
            foreach (int l in p.loc) if (l < 0 || l >= Panels) return false;
            return p.held >= -1 && p.held < Notes && p.hover >= -1 && p.hover < Panels && p.focus >= -1 && p.focus < Notes;
        }

        public void Poll(float now)
        {
            if (socket == null) return;
            try
            {
                for (int i = 0; i < 128 && socket.Available > 0; i++)
                {
                    IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = socket.Receive(ref endpoint);
                    if (data.Length > 4096) continue;
                    SortPacket packet;
                    try { packet = JsonUtility.FromJson<SortPacket>(Encoding.UTF8.GetString(data)); }
                    catch (ArgumentException) { continue; }
                    if (packet == null || packet.protocol != Protocol) continue;
                    if (packet.kind == "caption")
                    {
                        Caption = packet.text ?? ""; lastSender = endpoint;
                        if (packet.focus >= 0 && packet.focus < Notes)
                        {
                            var valid = new System.Collections.Generic.List<int>();
                            if (packet.torn != null) foreach (int id in packet.torn) if (id >= 0 && id < Panels && !valid.Contains(id)) valid.Add(id);
                            TornNote = packet.focus; Torn = valid.ToArray(); TornStamp++;
                        }
                        continue;
                    }
                    if (packet.kind != "state" || string.IsNullOrEmpty(packet.session) || packet.seq < 1 || !ValidState(packet)) continue;
                    string identity = endpoint + "/" + packet.session;
                    if (identity != peer)
                    {
                        if (now - LastReceived <= 0.5f) continue;
                        peer = identity; incoming = -1;
                    }
                    if (packet.seq <= incoming) continue;
                    incoming = packet.seq;
                    Held = packet.held; Hover = packet.hover; Focus = packet.focus; Loc = packet.loc;
                    LastReceived = now; lastSender = endpoint; Accepted++;
                    Error = "";
                }
            }
            catch (SocketException e) { Error = e.Message; }
            // A stopped talking: nobody is holding anything any more. Placements stay as last known.
            if (now - LastReceived > 1f) { Held = -1; Hover = -1; Focus = -1; }
        }

        public void Dispose() { socket?.Close(); socket = null; destination = null; }
    }
}
