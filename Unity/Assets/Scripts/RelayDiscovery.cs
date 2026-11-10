using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace MysterySort
{
    // The relay PC broadcasts a beacon once a second; both headsets listen for it and learn the relay's
    // address, so nobody types an IP inside VR. A file named relay.txt in the app's data folder with
    // one IPv4 address overrides discovery (for networks that block broadcast).
    public sealed class RelayDiscovery : IDisposable
    {
        public const string Beacon = "mystery-sort-relay-v1";
        public const int Port = 9001;

        UdpClient socket;
        float lastSeen = -100;
        readonly string fixedIP = "";
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject multicastLock;
#endif
        public string RelayIP { get; private set; } = "";
        public string Error { get; private set; } = "";

        public RelayDiscovery()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "relay.txt");
                if (File.Exists(path) && IPAddress.TryParse(File.ReadAllText(path).Trim(), out IPAddress parsed))
                { fixedIP = parsed.ToString(); RelayIP = fixedIP; }
            }
            catch (Exception e) { Error = e.Message; }
            try
            {
                socket = new UdpClient(Port);
                socket.EnableBroadcast = true; socket.Client.Blocking = false;
#if UNITY_ANDROID && !UNITY_EDITOR
                // Quest drops broadcast packets unless the app holds a multicast lock.
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
                using (var wifi = context.Call<AndroidJavaObject>("getSystemService", "wifi"))
                {
                    multicastLock = wifi.Call<AndroidJavaObject>("createMulticastLock", "MysterySortDiscovery");
                    multicastLock.Call("setReferenceCounted", false);
                    multicastLock.Call("acquire");
                }
#endif
            }
            catch (Exception e) { Error = e.Message; }
        }

        public void Tick(float now)
        {
            if (socket == null || fixedIP != "") return;
            try
            {
                if (now - lastSeen > 4) RelayIP = "";
                for (int i = 0; i < 32 && socket.Available > 0; i++)
                {
                    var endpoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = socket.Receive(ref endpoint);
                    if (bytes.Length != Beacon.Length || Encoding.ASCII.GetString(bytes) != Beacon) continue;
                    string ip = endpoint.Address.ToString();
                    if (RelayIP != "" && RelayIP != ip) continue; // stay with the first relay until it goes quiet
                    RelayIP = ip; lastSeen = now;
                }
                Error = "";
            }
            catch (SocketException e) { Error = e.Message; }
        }

        public void Dispose()
        {
            socket?.Close(); socket = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (multicastLock != null)
            {
                if (multicastLock.Call<bool>("isHeld")) multicastLock.Call("release");
                multicastLock.Dispose(); multicastLock = null;
            }
#endif
        }
    }
}
