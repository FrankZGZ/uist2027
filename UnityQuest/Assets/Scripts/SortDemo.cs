using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Management;

namespace MysterySort
{
    // A panel that holds notes. rot.forward points INTO the panel, i.e. the way a viewer looks at it.
    public sealed class Panel
    {
        public int id; public string name;
        public Transform root;
        public Vector3 pos => root.position;
        public Quaternion rot => root.rotation;
        public float w, h, dx, dy;
        public Vector3 Forward => rot * Vector3.forward;
        public Vector3 Right => rot * Vector3.right;
        public Vector3 Slot(int note) => pos + rot * new Vector3((note % 3 - 1) * dx, (0.5f - note / 3) * dy, -0.012f);
        public bool Contains(Vector3 world)
        {
            Vector3 local = Quaternion.Inverse(rot) * (world - pos);
            return Mathf.Abs(local.x) <= w / 2 && Mathf.Abs(local.y) <= h / 2;
        }
        // Where the avatar stands to reach a slot with its right hand.
        public Vector3 Stand(Vector3 slot) { Vector3 p = slot - Forward * 0.45f - Right * 0.17f; p.y = 0; return p; }
    }

    public sealed class SortDemo : MonoBehaviour
    {
        public enum Role { A, B }
        public Role role = Role.A;
        public string peerIP = "127.0.0.1";
        public int port = 9000;
        public GameObject avatarPrefab;
        public RuntimeAnimatorController controller;
        public Material surface;
        public Camera cam;
        // Headset build: the rig starts disabled and is switched on once OpenXR is running.
        public bool vr;
        public GameObject questRig;
        public OVRHand rightHand;
        // Mixed reality: passthrough background, no virtual room, panels placed in the real room.
        public bool mr;
        public const int RelayPort = 9000, RoomBPort = 9002;

        const int N = SortTransport.Notes;
        const float NoteW = 0.20f, NoteH = 0.14f;
        float noteScale = 1f; // B shows the same notes larger: its panels are across the room
        static readonly string[] Letter = { "A", "B", "C", "D", "E", "F" };
        static readonly string[] Header = { "Kelley", "Smith", "Scott", "Knife", "Bullet", "Elevator" };
        static readonly string[] Clue =
        {
            "Mr. Kelley had destroyed Mr. Jones' business.",
            "Miss Smith often followed Mr. Kelley.",
            "Police could not locate Mr. Scott after the murder.",
            "A knife with Mr. Kelley's blood was found in Miss Smith's yard.",
            "The bullet in Mr. Kelley's thigh matched Mr. Jones' gun.",
            "The elevator man saw Mr. Kelley's wife go up at 12:15 a.m."
        };
        static readonly Color NoteColor = new Color(1f, 0.62f, 0.25f), HeldColor = new Color(1f, 0.9f, 0.3f);

        Panel[] panels;
        readonly Transform[] notes = new Transform[N];
        readonly Renderer[] noteFaces = new Renderer[N];
        readonly int[] loc = new int[N];       // A: authoritative. B: where each note is in B's room (-2 = in the avatar's hand)
        SortTransport network;
        AvatarActor actor;
        TextMesh wallText, captionText;
        Font font;
        RelayDiscovery discovery;
        LineRenderer rayLine;
        TextMesh clueText;
        bool wasPressed, gripWas;
        float aimDistance = 1.5f, moveDistance, resetHeld;
        int movingPanel = -1;
        Vector3[] defaultPos; Quaternion[] defaultRot;
        string inputNote = "", netNote = "";

        // A
        int held = -1, hover = -1, focus = -1, eventCount;
        string eventText = "";
        readonly List<string> eventLog = new List<string>();
        int lastFocus = -1, lastHover = -1, grabbedFrom;
        float focusSince, hoverSince;
        readonly List<string> traceLog = new List<string>();
        int traceCount, traceHeld = -1; float nextTrace, lastTraceTime; Vector3 tracePos; string lastTraceBody = "";
        float nextSend, nextScene;
        // B
        int carrying = -1, watching = -1;
        float idleSince, watchingSince;
        Vector3 lookPoint;
        // Model-driven hesitation. undecided[n] = panels the model said A was torn between while holding note n.
        readonly int[][] undecided = new int[N][];
        readonly float[] waverShown = new float[N];
        int tornSeen, liveTornNote = -1; float liveTornUntil;
        string avatarDoing = "idle";
        // automated run
        bool smoke, clockStarted; string smokeDir; float smokeStart; int shot; float nextShot = 1;
        string logPath;

        public static string Arg(string key, string fallback = null)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1];
            return fallback;
        }

        IEnumerator Start()
        {
            role = Arg("-role", role.ToString()).ToUpperInvariant() == "B" ? Role.B : Role.A;
            peerIP = Arg("-peer", peerIP);
            if (int.TryParse(Arg("-port"), out int parsed) && parsed > 0 && parsed < 65536) port = parsed;
            smokeDir = Arg("-smokeDir"); smoke = smokeDir != null;
            if (smoke) { vr = false; Directory.CreateDirectory(smokeDir); }
            Application.runInBackground = true; Application.targetFrameRate = vr ? 72 : 60; QualitySettings.vSyncCount = 0;
            logPath = Path.Combine(smoke ? smokeDir : Application.persistentDataPath, "events-" + role + ".jsonl");
            File.WriteAllText(logPath, "");
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (vr)
            {
                // Start OpenXR by hand, then switch from the desktop camera to the headset rig.
                var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
                if (manager != null)
                {
                    yield return manager.InitializeLoader();
                    if (manager.activeLoader != null) { manager.StartSubsystems(); xrStarted = true; }
                }
                if (xrStarted && questRig != null)
                {
                    // A stands just in front of the desk panels; B stands back to take in the whole room.
                    // In MR the rig stays at the tracking origin, so virtual metres are real metres from where the user stands.
                    questRig.transform.position = mr ? Vector3.zero : role == Role.A ? new Vector3(0, 0, -0.1f) : new Vector3(0, 0, -0.9f);
                    questRig.SetActive(true);
                    cam.gameObject.SetActive(false);
                    cam = questRig.GetComponent<OVRCameraRig>().centerEyeAnchor.GetComponent<Camera>();
                    // Passthrough is composited under the scene: anything the camera leaves transparent shows the real room.
                    if (mr) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0, 0, 0, 0); }
                }
                else vr = false; // no headset runtime: fall back to the flat view so the app is not black
            }

            if (!vr) mr = false;
            noteScale = role == Role.B ? (mr ? 1.3f : 1.7f) : 1f;
            BuildRoom();
            for (int i = 0; i < N; i++) { notes[i] = BuildNote(i); loc[i] = 0; Snap(i, 0); }

            // A sees the collaborator standing still (one-way prototype). B sees it act.
            Vector3 start = role == Role.A ? new Vector3(0, 0, mr ? 2.3f : 2.5f) : new Vector3(0, 0, mr ? 1.8f : 2.2f);
            GameObject avatar = Instantiate(avatarPrefab, start, Quaternion.Euler(0, 180, 0));
            avatar.name = "Collaborator";
            actor = avatar.AddComponent<AvatarActor>(); actor.Viewer = cam.transform; actor.Initialize(controller);
            if (mr)
            {
                // No virtual floor to catch a shadow: a soft dark disc under the feet keeps the avatar from looking afloat.
                Shader blob = Shader.Find("Sprites/Default");
                if (blob != null)
                {
                    var disc = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(disc.GetComponent<Collider>());
                    disc.name = "Contact shadow"; disc.transform.SetParent(avatar.transform, false);
                    disc.transform.localPosition = new Vector3(0, 0.01f, 0); disc.transform.localRotation = Quaternion.Euler(90, 0, 0);
                    disc.transform.localScale = new Vector3(0.75f, 0.55f, 1);
                    var shade = new Material(blob); shade.color = new Color(0, 0, 0, 0.4f); disc.GetComponent<Renderer>().material = shade;
                }
            }

            if (vr)
            {
                // Headsets do not know the relay's address yet; RelayDiscovery fills it in.
                discovery = new RelayDiscovery();
                var transport = new SortTransport();
                if (role == Role.A) transport.Start(true, "", RelayPort); else transport.Start(false, "", RoomBPort);
                network = transport;
                rayLine = new GameObject("Pointer ray").AddComponent<LineRenderer>();
                rayLine.material = new Material(surface); rayLine.material.color = new Color(0.3f, 1f, 0.85f);
                rayLine.startWidth = 0.004f; rayLine.endWidth = 0.002f; rayLine.positionCount = 2; rayLine.enabled = false;
            }
            else { network = new SortTransport(); network.Start(role == Role.A, peerIP, port); }
            if (role == Role.B) StartCoroutine(AvatarLoop());
            smokeStart = Time.unscaledTime;
        }

        bool xrStarted;

        // ---------- scene ----------

        GameObject Box(string name, Vector3 pos, Quaternion rot, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.transform.SetPositionAndRotation(pos, rot); go.transform.localScale = scale;
            Destroy(go.GetComponent<Collider>());
            var material = new Material(surface); material.color = color; go.GetComponent<Renderer>().material = material;
            return go;
        }

        TextMesh Label(string name, Vector3 pos, Quaternion rot, float size, Color color, Transform parent = null)
        {
            var text = new GameObject(name).AddComponent<TextMesh>();
            text.transform.SetPositionAndRotation(pos, rot); if (parent != null) text.transform.SetParent(parent, true);
            text.font = font; text.GetComponent<MeshRenderer>().material = font.material;
            text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
            text.fontSize = 64; text.characterSize = size; text.color = color;
            return text;
        }

        Panel MakePanel(int id, string name, Vector3 pos, float yaw, float w, float h, float dx, float dy, Color color)
        {
            var root = new GameObject(name + " root").transform;
            root.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            var p = new Panel { id = id, name = name, root = root, w = w, h = h, dx = dx, dy = dy };
            Box(name, pos + p.Forward * 0.02f, p.rot, new Vector3(w, h, 0.03f), color).transform.SetParent(root, true);
            Label(name + " label", pos + Vector3.up * (h / 2 + 0.07f * noteScale) - p.Forward * 0.01f, p.rot, 0.011f * noteScale, new Color(0.85f, 0.93f, 1f), root).text = name;
            return p;
        }

        void BuildRoom()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.62f, 0.66f, 0.72f);
            var sun = new GameObject("Key light").AddComponent<Light>(); sun.type = LightType.Directional;
            sun.intensity = 1.1f; sun.transform.rotation = Quaternion.Euler(45, -35, 0); sun.shadows = LightShadows.Soft;
            if (!mr)
            {
                Box("Floor", new Vector3(0, -0.05f, 2), Quaternion.identity, new Vector3(10, 0.1f, 10), new Color(0.12f, 0.17f, 0.23f));
                Box("Back wall", new Vector3(0, 1.7f, 4.6f), Quaternion.identity, new Vector3(10, 3.5f, 0.1f), new Color(0.20f, 0.27f, 0.35f));
            }
            Color home = new Color(0.30f, 0.33f, 0.38f), one = new Color(0.20f, 0.42f, 0.33f), two = new Color(0.45f, 0.27f, 0.30f);
            Color statusColor = new Color(0.75f, 0.94f, 1), noteTextColor = new Color(1f, 0.93f, 0.6f);
            if (role == Role.A)
            {
                // Compact desk layout: three panels side by side within arm's reach.
                if (!mr) Box("Desk stand", new Vector3(0, 0.40f, 1.24f), Quaternion.identity, new Vector3(2.3f, 0.8f, 0.04f), new Color(0.16f, 0.20f, 0.26f));
                panels = new[]
                {
                    MakePanel(0, "Clue tray", new Vector3(0, 1.0f, 1.2f), 0, 0.70f, 0.34f, 0.22f, 0.16f, home),
                    MakePanel(1, "Group 1", new Vector3(-0.76f, 1.0f, 1.2f), 0, 0.70f, 0.34f, 0.22f, 0.16f, one),
                    MakePanel(2, "Group 2", new Vector3(0.76f, 1.0f, 1.2f), 0, 0.70f, 0.34f, 0.22f, 0.16f, two)
                };
                if (!vr) { cam.transform.position = new Vector3(0, 1.5f, -0.45f); cam.transform.LookAt(new Vector3(0, 1.08f, 1.2f)); }
            }
            else if (mr)
            {
                // Fits a clear floor of about 3.2 m x 3.2 m in front of the observer.
                panels = new[]
                {
                    MakePanel(0, "Clue wall", new Vector3(0, 1.35f, 3.0f), 0, 1.12f, 0.58f, 0.34f, 0.25f, home),
                    MakePanel(1, "West panel", new Vector3(-1.5f, 1.35f, 1.7f), -65, 1.12f, 0.58f, 0.34f, 0.25f, one),
                    MakePanel(2, "East panel", new Vector3(1.5f, 1.35f, 1.7f), 65, 1.12f, 0.58f, 0.34f, 0.25f, two)
                };
            }
            else
            {
                // Room-scale layout: clue wall at the back, group panels on opposite side walls.
                panels = new[]
                {
                    MakePanel(0, "Clue wall", new Vector3(0, 1.35f, 4.0f), 0, 1.45f, 0.74f, 0.44f, 0.32f, home),
                    MakePanel(1, "West panel", new Vector3(-2.3f, 1.35f, 2.4f), -55, 1.45f, 0.74f, 0.44f, 0.32f, one),
                    MakePanel(2, "East panel", new Vector3(2.3f, 1.35f, 2.4f), 55, 1.45f, 0.74f, 0.44f, 0.32f, two)
                };
                if (!vr) { cam.transform.position = new Vector3(0, 1.65f, -0.9f); cam.transform.LookAt(new Vector3(0, 1.3f, 2.5f)); cam.fieldOfView = 72; }
            }

            Transform anchor = panels[0].root; float top = panels[0].h / 2;
            if (mr)
            {
                // No wall to write on: status sits under the first panel, clue text or AI caption above it, and they move with it.
                wallText = Label("Status", anchor.position, anchor.rotation, 0.0065f * noteScale, statusColor, anchor);
                wallText.transform.localPosition = new Vector3(0, -top - 0.04f, -0.01f); wallText.anchor = TextAnchor.UpperCenter;
                var above = Label(role == Role.A ? "Clue" : "Caption", anchor.position, anchor.rotation, 0.0065f * noteScale, noteTextColor, anchor);
                above.transform.localPosition = new Vector3(0, top + 0.15f * noteScale, -0.01f); above.anchor = TextAnchor.LowerCenter;
                if (role == Role.A) clueText = above; else captionText = above;
            }
            else if (role == Role.A)
            {
                // In the headset there is no screen overlay: the clue under the pointer is written above the tray.
                clueText = Label("Clue", new Vector3(0, 1.48f, 1.19f), Quaternion.identity, 0.006f, noteTextColor);
                wallText = Label("Status", new Vector3(0, 2.75f, 4.5f), Quaternion.identity, 0.02f, statusColor);
            }
            else
            {
                wallText = Label("Status", new Vector3(0, 3.05f, 4.5f), Quaternion.identity, 0.026f, statusColor);
                captionText = Label("Caption", new Vector3(0, 2.78f, 4.5f), Quaternion.identity, 0.018f, noteTextColor);
                captionText.anchor = TextAnchor.UpperCenter;
            }

            defaultPos = new Vector3[panels.Length]; defaultRot = new Quaternion[panels.Length];
            for (int i = 0; i < panels.Length; i++) { defaultPos[i] = panels[i].pos; defaultRot[i] = panels[i].rot; }
            if (vr) LoadLayout();
        }

        // ---------- panel placement (headset only): where each user put the panels in their own room ----------

        [Serializable] class Layout { public Vector3[] pos; public float[] yaw; }

        string LayoutPath => Path.Combine(Application.persistentDataPath, "layout-" + role + (mr ? "-mr" : "-vr") + ".json");

        void SaveLayout()
        {
            var layout = new Layout { pos = new Vector3[panels.Length], yaw = new float[panels.Length] };
            for (int i = 0; i < panels.Length; i++) { layout.pos[i] = panels[i].pos; layout.yaw[i] = panels[i].rot.eulerAngles.y; }
            try { File.WriteAllText(LayoutPath, JsonUtility.ToJson(layout)); } catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        void LoadLayout()
        {
            try
            {
                if (!File.Exists(LayoutPath)) return;
                var layout = JsonUtility.FromJson<Layout>(File.ReadAllText(LayoutPath));
                if (layout?.pos == null || layout.yaw == null || layout.pos.Length != panels.Length || layout.yaw.Length != panels.Length) return;
                for (int i = 0; i < panels.Length; i++) panels[i].root.SetPositionAndRotation(layout.pos[i], Quaternion.Euler(0, layout.yaw[i], 0));
            }
            catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        void ResetLayout()
        {
            for (int i = 0; i < panels.Length; i++) panels[i].root.SetPositionAndRotation(defaultPos[i], defaultRot[i]);
            for (int i = 0; i < N; i++) if (loc[i] >= 0 && i != held) Snap(i, loc[i]);
            try { if (File.Exists(LayoutPath)) File.Delete(LayoutPath); } catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        // Grip on a panel to carry it at the end of the ray; thumbstick pushes it away or pulls it closer.
        // The panel always turns to face the user. Holding B for two seconds restores the default layout.
        void MovePanels(Ray ray, bool valid, bool grip, float stick, bool resetButton)
        {
            if (movingPanel < 0)
            {
                if (valid && grip && !gripWas && held < 0 && Aim(ray, out _, out int target)) { movingPanel = target; moveDistance = aimDistance; }
            }
            else if (!valid || !grip) { SaveLayout(); movingPanel = -1; }
            else
            {
                moveDistance = Mathf.Clamp(moveDistance + stick * Time.deltaTime * 1.2f, 0.4f, 6f);
                Panel moved = panels[movingPanel]; Vector3 at = ray.GetPoint(moveDistance);
                Vector3 facing = at - cam.transform.position; facing.y = 0;
                if (facing.sqrMagnitude > 0.01f) moved.root.rotation = Quaternion.LookRotation(facing);
                moved.root.position = at;
                for (int i = 0; i < N; i++) if (loc[i] == movingPanel && i != held) Snap(i, movingPanel);
            }
            gripWas = grip && valid;
            resetHeld = resetButton && movingPanel < 0 ? resetHeld + Time.deltaTime : 0;
            if (resetHeld > 2f) { ResetLayout(); resetHeld = 0; }
        }

        Transform BuildNote(int i)
        {
            var root = new GameObject("Note " + Letter[i]).transform;
            var face = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(face.GetComponent<Collider>());
            face.transform.SetParent(root, false); face.transform.localScale = new Vector3(NoteW, NoteH, 1);
            var material = new Material(surface); material.color = NoteColor; face.GetComponent<Renderer>().material = material;
            noteFaces[i] = face.GetComponent<Renderer>();
            var back = Instantiate(face, root); back.transform.localRotation = Quaternion.Euler(0, 180, 0); // visible from behind while carried
            var title = Label("Title", Vector3.zero, Quaternion.identity, 0.0065f, new Color(0.15f, 0.08f, 0.02f), root);
            title.transform.localPosition = new Vector3(0, 0.02f, -0.002f); title.text = Letter[i] + "\n" + Header[i]; title.fontStyle = FontStyle.Bold;
            root.localScale = Vector3.one * noteScale;
            return root;
        }

        void Snap(int note, int panel)
        {
            notes[note].SetPositionAndRotation(panels[panel].Slot(note), panels[panel].rot);
        }

        // ---------- A: input, events, scene text ----------

        void Event(string sentence)
        {
            // The last few events travel with every state packet, so the relay sees the whole process
            // (inspecting, hesitating, putting back), not only where notes ended up.
            eventCount++;
            eventLog.Add("#" + eventCount + " t=" + Stamp() + " " + sentence);
            if (eventLog.Count > 8) eventLog.RemoveAt(0);
            eventText = string.Join("\n", eventLog);
            Log("{\"t\":" + (Time.unscaledTime - smokeStart).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"role\":\"" + role + "\",\"event\":\"" + sentence.Replace("\"", "'") + "\"}");
        }

        void Log(string line) { File.AppendAllText(logPath, line + "\n"); Debug.Log(line); }

        string Members(int panel)
        {
            var list = new List<string>();
            for (int i = 0; i < N; i++) if (loc[i] == panel) list.Add(Letter[i]);
            return list.Count == 0 ? "empty" : string.Join(", ", list);
        }

        void Grab(int note)
        {
            held = note; hover = loc[note]; grabbedFrom = loc[note];
            lastHover = hover; hoverSince = Time.unscaledTime; lastFocus = -1;
            Event("grab note " + Letter[note] + " [" + Header[note] + "] from " + panels[loc[note]].name);
        }

        void Release(int panel)
        {
            int note = held; loc[note] = panel; Snap(note, panel); held = -1; hover = -1; lastHover = -1;
            string label = "note " + Letter[note] + " [" + Header[note] + "]";
            if (panel == grabbedFrom) Event("put " + label + " back on " + panels[panel].name + " without sorting it");
            else Event((grabbedFrom == 0 ? "place " : "move ") + label + (grabbedFrom == 0 ? "" : " from " + panels[grabbedFrom].name) + " on " + panels[panel].name + "; " + panels[panel].name + " now holds: " + Members(panel));
        }

        // Raw motion sample for the intent reading. No interpretation here: only what is held, how far
        // it is from each panel, and how it is moving. Whether that amounts to hesitation is for the model to say.
        void Sample(float now)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string body;
            if (held >= 0)
            {
                Vector3 at = notes[held].position; float dt = Mathf.Max(0.05f, now - lastTraceTime);
                bool continuing = traceHeld == held;
                var parts = new List<string>(); float bestGain = 0.001f; int toward = -1;
                foreach (var panel in panels)
                {
                    float d = Vector3.Distance(at, panel.Slot(held));
                    parts.Add(panel.name + " " + d.ToString("F2", inv) + " m");
                    float gain = Vector3.Distance(tracePos, panel.Slot(held)) - d;
                    if (continuing && gain > bestGain) { bestGain = gain; toward = panel.id; }
                }
                float speed = continuing ? Vector3.Distance(at, tracePos) / dt : 0;
                body = "holding " + Letter[held] + " [" + Header[held] + "] | distance to " + string.Join(", ", parts) + " | " +
                    (speed < 0.05f || toward < 0 ? "still" : "moving toward " + panels[toward].name + " at " + speed.ToString("F2", inv) + " m/s") +
                    " | " + (hover >= 0 ? "over " + panels[hover].name : "over no panel");
                tracePos = at;
            }
            else body = "hand empty | " + (focus >= 0 ? "looking at " + Letter[focus] + " [" + Header[focus] + "] on " + panels[loc[focus]].name : "looking at no note");
            traceHeld = held; lastTraceTime = now;
            if (held < 0 && body == lastTraceBody) return; // nothing new while the hand is empty
            lastTraceBody = body;
            traceCount++;
            traceLog.Add("#" + traceCount + " t=" + Stamp() + " " + body);
            if (traceLog.Count > 5) traceLog.RemoveAt(0);
            network.SendTrace(traceCount, string.Join("\n", traceLog));
        }

        static string Seconds(float value) => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s";

        static string Direction(Transform viewer, Vector3 point)
        {
            Vector3 to = point - viewer.position; to.y = 0; Vector3 forward = viewer.forward; forward.y = 0;
            float angle = Vector3.SignedAngle(forward.normalized, to.normalized, Vector3.up), abs = Mathf.Abs(angle);
            string side = angle >= 0 ? "right" : "left";
            return abs <= 12 ? "straight ahead" : abs <= 65 ? "front-" + side : abs <= 115 ? side : "behind-" + side;
        }

        // Static scene turned into text: what the relay (and the LLM behind it) knows about this room.
        string SceneText()
        {
            var text = new StringBuilder();
            text.AppendLine("room=" + (role == Role.A ? "A, compact desk layout, user sorts notes by hand" : "B, room-scale layout, observer watches the partner's avatar"));
            foreach (var p in panels)
            {
                Vector3 to = p.pos - cam.transform.position; to.y = 0;
                text.AppendLine("panel id=" + p.id + " name=\"" + p.name + "\" role=" + (p.id == 0 ? "unsorted clues" : "group") +
                    " | " + Direction(cam.transform, p.pos) + " of the viewer, " + to.magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " m away");
            }
            for (int i = 0; i < N; i++) text.AppendLine("note " + Letter[i] + " [" + Header[i] + "]: " + Clue[i]);
            return text.ToString();
        }

        int PanelAt(Vector3 point) { foreach (var p in panels) if (p.Contains(point)) return p.id; return -1; }

        int NoteAt(Vector3 point)
        {
            for (int i = 0; i < N; i++)
            {
                Vector3 local = Quaternion.Inverse(notes[i].rotation) * (point - notes[i].position);
                if (Mathf.Abs(local.x) <= NoteW * noteScale / 2 && Mathf.Abs(local.y) <= NoteH * noteScale / 2) return i;
            }
            return -1;
        }

        // Nearest panel whose rectangle the ray crosses. Panels no longer share one plane once users move them.
        bool Aim(Ray ray, out Vector3 point, out int panel)
        {
            point = Vector3.zero; panel = -1; float best = 8f;
            foreach (var p in panels)
            {
                var plane = new Plane(-p.Forward, p.pos - p.Forward * 0.05f);
                if (!plane.Raycast(ray, out float d) || d >= best) continue;
                Vector3 at = ray.GetPoint(d);
                if (p.Contains(at)) { best = d; point = at; panel = p.id; }
            }
            if (panel >= 0) aimDistance = best;
            return panel >= 0;
        }

        // One pointer for every device: a ray and a pressed flag. Mouse, controller trigger or hand pinch.
        void PointerInput(Ray ray, bool valid, bool pressed)
        {
            Vector3 point = Vector3.zero; int over = -1;
            bool onPanel = valid && Aim(ray, out point, out over);
            if (valid && !onPanel) point = ray.GetPoint(aimDistance); // between panels: keep the last depth
            if (held < 0)
            {
                focus = onPanel ? NoteAt(point) : -1;
                if (pressed && !wasPressed && focus >= 0) Grab(focus);
            }
            else
            {
                focus = held;
                if (valid)
                {
                    notes[held].position = point; hover = over;
                    if (onPanel) notes[held].rotation = panels[over].rot;
                }
                // Losing tracking mid-carry must not drop the note somewhere random: it goes back where it was.
                if (!pressed || !valid) Release(valid && hover >= 0 ? hover : loc[held]);
            }
            wasPressed = pressed && valid;
            ShowRay(ray, valid, point);
        }

        void ShowRay(Ray ray, bool valid, Vector3 end)
        {
            if (rayLine == null) return;
            rayLine.enabled = valid;
            if (valid) { rayLine.SetPosition(0, ray.origin); rayLine.SetPosition(1, end); }
        }

        void MouseInput()
        {
            PointerInput(cam.ScreenPointToRay(Input.mousePosition), true, Input.GetMouseButton(0));
        }

        // Right Touch controller if one is tracked (trigger = notes, grip = panels), otherwise the right hand (index pinch = notes).
        bool HeadsetRay(out Ray ray, out bool trigger, out bool grip, out float stick, out bool resetButton)
        {
            ray = default; trigger = grip = resetButton = false; stick = 0;
            var rig = questRig.GetComponent<OVRCameraRig>();
            bool controller = (OVRInput.GetConnectedControllers() & OVRInput.Controller.RTouch) != 0 &&
                OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch);
            if (controller)
            {
                Transform anchor = rig.rightControllerAnchor;
                ray = new Ray(anchor.position, anchor.forward);
                trigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
                grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
                stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;
                resetButton = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);
                inputNote = role == Role.A ? "Trigger: move a note.  Grip: move a panel (stick = nearer/farther).  Hold B: reset panels"
                                           : "Grip: move a panel (stick = nearer/farther).  Hold B: reset panels";
                return true;
            }
            if (rightHand != null && rightHand.IsTracked && rightHand.IsPointerPoseValid)
            {
                // OVRHand reports its pointer pose in tracking space.
                Transform space = rig.trackingSpace, pose = rightHand.PointerPose;
                ray = new Ray(space.TransformPoint(pose.localPosition), space.rotation * pose.localRotation * Vector3.forward);
                trigger = rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                inputNote = role == Role.A ? "Right hand: pinch a note and release it over a group.  Panels need the controller" : "Pick up the right controller to move panels";
                return true;
            }
            inputNote = "No right controller or right hand tracked";
            return false;
        }

        void HeadsetInput()
        {
            bool valid = HeadsetRay(out Ray ray, out bool trigger, out bool grip, out float stick, out bool resetButton);
            MovePanels(ray, valid, grip, stick, resetButton);
            if (role == Role.A && movingPanel < 0) PointerInput(ray, valid, trigger);
            else ShowRay(ray, valid, valid ? ray.GetPoint(movingPanel >= 0 ? moveDistance : 2.5f) : Vector3.zero);
        }

        // Scripted input for the automated run. f = look at note, g = grab note, m = carry toward panel, r = release on panel.
        // Looks at D then C, sorts C into Group 1; carries E back and forth between the groups before Group 2; sorts A into Group 1; looks at B.
        static readonly float[] ScriptTime = { 0.8f, 2.0f, 3.0f, 3.0f, 5.5f, 7.0f, 8.5f, 8.5f, 10.5f, 12.0f, 13.5f, 17.0f, 18.0f, 19.5f, 19.5f, 22.0f, 23.5f, 25.5f };
        static readonly char[] ScriptKind = { 'f', 'f', 'g', 'm', 'r', 'f', 'g', 'm', 'm', 'm', 'm', 'r', 'f', 'g', 'm', 'r', 'f', 'f' };
        static readonly int[] ScriptArg = { 3, 2, 2, 1, 1, 4, 4, 1, 2, 1, 2, 2, 0, 0, 1, 1, 1, -1 };
        int scriptStep, scriptPanel = -1, scriptFocus = -1;

        void ScriptedInput(float t)
        {
            while (scriptStep < ScriptTime.Length && t >= ScriptTime[scriptStep])
            {
                int arg = ScriptArg[scriptStep];
                switch (ScriptKind[scriptStep])
                {
                    case 'f': scriptFocus = arg; break;
                    case 'g': Grab(arg); break;
                    case 'm': scriptPanel = arg; break;
                    case 'r': Release(arg); scriptPanel = -1; scriptFocus = -1; break;
                }
                scriptStep++;
            }
            if (held >= 0)
            {
                if (scriptPanel >= 0)
                {
                    Panel target = panels[scriptPanel];
                    notes[held].position = Vector3.MoveTowards(notes[held].position, target.Slot(held) - target.Forward * 0.05f, 0.5f * Time.deltaTime);
                }
                hover = PanelAt(notes[held].position); focus = held;
            }
            else focus = scriptFocus;
        }

        // ---------- B: the avatar acts out A's state in B's own layout ----------

        IEnumerator AvatarLoop()
        {
            while (!actor.Ready) yield return null;
            idleSince = Time.time; lookPoint = cam.transform.position;
            while (true)
            {
                int[] wanted = network.Loc; int wantHeld = network.Held, wantHover = network.Hover, wantFocus = network.Focus;
                if (carrying < 0)
                {
                    int note = -1;
                    if (wantHeld >= 0 && loc[wantHeld] >= 0) note = wantHeld;                      // A just picked something up
                    else for (int i = 0; i < N; i++) if (loc[i] >= 0 && loc[i] != wanted[i]) { note = i; break; } // catch up on a missed move
                    if (note < 0)
                    {
                        if (wantFocus >= 0 && loc[wantFocus] >= 0)
                        {
                            // Observe: A is looking at a note. Turn the head to it at once; if A keeps looking, walk over.
                            if (wantFocus != watching) { watching = wantFocus; watchingSince = Time.time; }
                            Panel p = panels[loc[wantFocus]]; Vector3 at = p.Slot(wantFocus);
                            lookPoint = at;
                            if (Time.time - watchingSince > 0.5f)
                            {
                                Vector3 stand = at - p.Forward * 0.95f - p.Right * 0.10f; stand.y = 0;
                                bool there = actor.Step(stand, p.Forward);
                                avatarDoing = (there ? "reading note " : "walking over to look at note ") + Letter[wantFocus];
                            }
                            else { actor.Stop(); avatarDoing = "glancing at note " + Letter[wantFocus]; }
                            idleSince = Time.time;
                        }
                        else
                        {
                            watching = -1; lookPoint = cam.transform.position; avatarDoing = "idle";
                            Vector3 toViewer = cam.transform.position - actor.transform.position; toViewer.y = 0;
                            if (Time.time - idleSince > 1.5f) actor.Step(actor.transform.position, toViewer); else actor.Stop();
                        }
                        yield return null; continue;
                    }
                    watching = -1;
                    Panel src = panels[loc[note]]; Vector3 slot = src.Slot(note);
                    avatarDoing = "walking to " + src.name + " for note " + Letter[note];
                    lookPoint = slot;
                    yield return actor.WalkTo(src.Stand(slot), src.Forward);
                    // Find it: eyes pass over a neighbouring note before settling on the right one.
                    int neighbour = -1; float nearest = float.MaxValue;
                    for (int i = 0; i < N; i++)
                    {
                        if (i == note || loc[i] != loc[note]) continue;
                        float d = (notes[i].position - slot).sqrMagnitude;
                        if (d < nearest) { nearest = d; neighbour = i; }
                    }
                    if (neighbour >= 0)
                    {
                        avatarDoing = "searching for note " + Letter[note];
                        lookPoint = notes[neighbour].position; yield return new WaitForSeconds(0.35f);
                    }
                    lookPoint = slot; yield return new WaitForSeconds(0.25f);
                    avatarDoing = "picking up note " + Letter[note];
                    yield return actor.Reach(slot, () =>
                    {
                        actor.Carried = notes[note]; carrying = note; loc[note] = -2;
                        noteFaces[note].material.color = HeldColor;
                        Log("{\"t\":" + Stamp() + ",\"role\":\"B\",\"avatar\":\"pick " + Letter[note] + " from " + src.name + "\",\"handError\":" + actor.ReachError.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}");
                    });
                }
                else
                {
                    int note = carrying;
                    if (wantHeld == note)
                    {
                        if (liveTornNote == note && Time.unscaledTime < liveTornUntil && undecided[note] != null)
                        {
                            // The model reads A as undecided right now: act that out instead of chasing A's hand.
                            Waver(note); yield return null; continue;
                        }
                        // A is still holding it. Re-decided every frame, so when A wavers between panels the avatar wavers too.
                        if (wantHover >= 0)
                        {
                            Panel ahead = panels[wantHover]; lookPoint = ahead.Slot(note);
                            bool there = actor.Step(ahead.Stand(ahead.Slot(note)), ahead.Forward);
                            avatarDoing = (there ? "holding note " + Letter[note] + " at " : "carrying note " + Letter[note] + " toward ") + ahead.name;
                        }
                        else { actor.Stop(); lookPoint = notes[note].position; avatarDoing = "holding note " + Letter[note] + ", undecided"; }
                        yield return null; continue;
                    }
                    // A has let go. If A wavered while the avatar was still catching up, show a short version of it
                    // before the placement, so the observer does not see a decisiveness that was not there.
                    if (undecided[note] != null && waverShown[note] < 1.6f)
                    {
                        float until = Time.time + 3.2f;
                        while (Time.time < until) { Waver(note); yield return null; }
                    }
                    undecided[note] = null; waverShown[note] = 0;
                    int destination = wanted[note]; Panel to = panels[destination]; Vector3 slot = to.Slot(note);
                    avatarDoing = "carrying note " + Letter[note] + " to " + to.name;
                    lookPoint = slot;
                    yield return actor.WalkTo(to.Stand(slot), to.Forward);
                    avatarDoing = "placing note " + Letter[note] + " on " + to.name;
                    yield return actor.Reach(slot, () =>
                    {
                        actor.Carried = null; Snap(note, destination); loc[note] = destination; carrying = -1;
                        noteFaces[note].material.color = NoteColor;
                        Log("{\"t\":" + Stamp() + ",\"role\":\"B\",\"avatar\":\"place " + Letter[note] + " on " + to.name + "\",\"handError\":" + actor.ReachError.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}");
                    });
                    idleSince = Time.time;
                }
            }
        }

        // Stand between the two candidate panels and turn from one to the other every 1.6 s,
        // shifting a step toward whichever is being considered.
        void Waver(int note)
        {
            int[] pair = undecided[note];
            int phase = (int)(Time.time / 1.6f) % 2;
            Panel considered = panels[pair[phase]], other = panels[pair[1 - phase]];
            Vector3 a = considered.Stand(considered.Slot(note)), b = other.Stand(other.Slot(note));
            Vector3 stand = Vector3.Lerp((a + b) / 2, a, 0.10f);
            actor.Step(stand, considered.pos - stand);
            lookPoint = considered.Slot(note);
            waverShown[note] += Time.deltaTime;
            avatarDoing = "undecided with note " + Letter[note] + ": " + panels[pair[0]].name + " or " + panels[pair[1]].name + "?";
        }

        string Stamp() => (Time.unscaledTime - smokeStart).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // ---------- frame ----------

        void Update()
        {
            if (network == null) return;
            float now = Time.unscaledTime;
            if (!clockStarted) { clockStarted = true; smokeStart = now; }
            if (discovery != null)
            {
                discovery.Tick(now);
                network.SetDestination(discovery.RelayIP, RelayPort);
                netNote = discovery.RelayIP != "" ? "Relay " + discovery.RelayIP : "Looking for the relay PC on this Wi-Fi...";
                if (discovery.Error != "") netNote += "  (" + discovery.Error + ")";
                if (network.Error != "") netNote += "  (" + network.Error + ")";
            }
            if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
            if (role == Role.A)
            {
                if (smoke) ScriptedInput(now - smokeStart); else if (vr) HeadsetInput(); else MouseInput();
                clueText.text = focus >= 0 ? Wrap("Note " + Letter[focus] + ": " + Clue[focus], 46) : "";
                if (now >= nextTrace) { Sample(now); nextTrace = now + 0.4f; }
                actor.LookPoint = cam.transform.position;
                for (int i = 0; i < N; i++) noteFaces[i].material.color = i == held ? HeldColor : NoteColor;
                if (now >= nextSend) { network.SendState(held, hover, held >= 0 ? held : focus, loc, eventCount, eventText); nextSend = now + 0.05f; }
                if (now >= nextScene) { network.SendScene("A", SceneText()); nextScene = now + 1; }
                network.Poll(now);
                wallText.text = "ROOM A  |  you sort the clues\n" + (held >= 0 ? "Holding " + Letter[held] + (hover >= 0 ? " over " + panels[hover].name : "") : "Drag a note onto Group 1 or Group 2");
            }
            else
            {
                network.Poll(now);
                if (vr) HeadsetInput();
                actor.LookPoint = lookPoint;
                if (network.TornStamp != tornSeen)
                {
                    tornSeen = network.TornStamp; int about = network.TornNote;
                    if (network.Torn.Length >= 2)
                    {
                        if (undecided[about] == null) Log("{\"t\":" + Stamp() + ",\"role\":\"B\",\"intent\":\"note " + Letter[about] + " torn between " + panels[network.Torn[0]].name + " and " + panels[network.Torn[1]].name + "\"}");
                        undecided[about] = new[] { network.Torn[0], network.Torn[1] };
                        liveTornNote = about; liveTornUntil = now + 6f;
                    }
                    else if (about == liveTornNote) liveTornNote = -1; // the model no longer sees hesitation
                }
                if (now >= nextScene) { network.SendScene("B", SceneText()); nextScene = now + 1; }
                bool live = now - network.LastReceived <= 1f;
                wallText.text = "ROOM B  |  partner's avatar\n" + (live ? "Avatar: " + avatarDoing : "Waiting for A...");
                captionText.text = Wrap(network.Caption, 70);
            }
            if (vr) wallText.text += "\n" + netNote + "\n" + inputNote;
            if (smoke)
            {
                float t = now - smokeStart;
                if (t >= nextShot) { nextShot += 2; StartCoroutine(Capture(role + "-" + (shot++).ToString("00"))); }
                if (t > (role == Role.A ? 40 : 42))
                {
                    var state = new StringBuilder();
                    for (int i = 0; i < N; i++) state.Append(Letter[i]).Append('=').Append(loc[i] >= 0 ? panels[loc[i]].name : "in hand").Append(i < N - 1 ? ", " : "");
                    File.WriteAllText(Path.Combine(smokeDir, "final-" + role + ".txt"), state + "\naccepted packets: " + network.Accepted + "\n");
                    Application.Quit();
                }
            }
        }

        static string Wrap(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var result = new StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                if (result.Length > 0) result.Append('\n');
                int column = 0;
                foreach (string word in line.Split(' '))
                {
                    if (column > 0 && column + word.Length > width) { result.Append('\n'); column = 0; }
                    else if (column > 0) { result.Append(' '); column++; }
                    result.Append(word); column += word.Length;
                }
            }
            return result.ToString();
        }

        IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            // Render the scene explicitly: hidden test windows may have a black swapchain.
            var texture = new RenderTexture(1000, 800, 24); var previous = RenderTexture.active;
            cam.targetTexture = texture; cam.Render(); RenderTexture.active = texture;
            var pixels = new Texture2D(1000, 800, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, 1000, 800), 0, 0); pixels.Apply();
            File.WriteAllBytes(Path.Combine(smokeDir, name + ".png"), pixels.EncodeToPNG());
            cam.targetTexture = null; RenderTexture.active = previous;
            Destroy(pixels); texture.Release(); Destroy(texture);
        }

        void OnGUI()
        {
            if (vr || network == null) return; // no screen overlay inside a headset
            GUI.skin.label.fontSize = 15; GUI.skin.label.wordWrap = true;
            GUILayout.BeginArea(new Rect(14, 12, 470, 150), GUI.skin.box);
            GUILayout.Label("Room " + role + (role == Role.A ? "  ->  " + peerIP + ":" + port : "  listening on UDP " + port));
            if (role == Role.A)
            {
                GUILayout.Label("Hold the left mouse button on a note and drop it on a group panel. Esc quits.");
                if (focus >= 0) GUILayout.Label("Note " + Letter[focus] + ": " + Clue[focus]);
            }
            else GUILayout.Label("The avatar replays your partner's sorting in this room's own layout. Esc quits.");
            if (network.Error != "") GUILayout.Label("Network: " + network.Error);
            GUILayout.EndArea();
        }

        void OnDestroy()
        {
            network?.Dispose(); discovery?.Dispose();
            if (xrStarted && XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager.activeLoader != null)
            { XRGeneralSettings.Instance.Manager.StopSubsystems(); XRGeneralSettings.Instance.Manager.DeinitializeLoader(); }
        }
    }
}
