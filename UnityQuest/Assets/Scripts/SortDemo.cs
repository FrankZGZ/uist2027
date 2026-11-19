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
        public Vector3 Up => rot * Vector3.up;
        // The avatar works on this panel from the far side (across a desk) instead of the near side.
        public bool across;
        // 0 = upright (wall), 90 = lying flat (desk).
        public float Tilt => Mathf.Asin(Mathf.Clamp(-Forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        // Horizontal direction a person working at the panel faces. Holds for upright, tilted and flat panels.
        public Vector3 Facing { get { Vector3 f = Forward + Up; f.y = 0; return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward; } }
        public Vector3 Face => across ? -Facing : Facing;
        // Closer to a desk than to a wall: the arm reaches down and forward.
        public float ReachBack => Mathf.Lerp(0.45f, 0.25f, Mathf.Clamp01(-Forward.y));
        public Vector3 Slot(int note) => pos + rot * new Vector3((note % 3 - 1) * dx, (0.5f - note / 3) * dy, -0.012f);
        public bool Contains(Vector3 world)
        {
            Vector3 local = Quaternion.Inverse(rot) * (world - pos);
            return Mathf.Abs(local.x) <= w / 2 && Mathf.Abs(local.y) <= h / 2;
        }
        // Where the avatar stands to reach a point with its right hand: `back` metres beyond the panel's edge
        // on its own side, a little to the left. For an upright panel the edge distance is zero.
        public Vector3 Stand(Vector3 point, float back = -1f, float side = 0.17f)
        {
            if (back < 0) back = ReachBack;
            Vector3 face = Face, edge = pos + Up * (across ? h / 2 : -h / 2);
            float toEdge = Mathf.Max(0f, Vector3.Dot(point - edge, face));
            Vector3 p = point - face * (toEdge + back) - Vector3.Cross(Vector3.up, face) * side;
            p.y = 0; return p;
        }
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
        public OVRHand rightHand, leftHand;
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
        readonly int[] loc = new int[N];       // where each note physically is in THIS room (-2 = in the avatar's hand)
        // What both rooms agree on: panel per note and how often it has been placed. A placement raises the version;
        // the higher version wins. When agreed differs from loc, the partner moved the note and the avatar acts it out here.
        readonly int[] agreed = new int[N], ver = new int[N];
        int clock, listenPort;
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
        Vector3[] defaultPos; Quaternion[] defaultRot; bool[] defaultAcross;
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
            peerIP = Arg("-peer", peerIP); if (peerIP == "none") peerIP = "";
            if (int.TryParse(Arg("-listen"), out int listen) && listen > 0 && listen < 65536) listenPort = listen;
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
            noteScale = role == Role.B ? (mr ? 1f : 1.7f) : 1f;
            BuildRoom();
            for (int i = 0; i < N; i++) { notes[i] = BuildNote(i); loc[i] = 0; Snap(i, 0); }

            // The partner's avatar. In room A it starts beside the tray, on the side the user stands on.
            Vector3 start = role == Role.A ? (vr ? new Vector3(-1.0f, 0, 1.0f) : new Vector3(-1.5f, 0, 0.9f)) : new Vector3(0, 0, mr ? 1.4f : 2.2f);
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
                Shader marker = Shader.Find("Sprites/Default");
                if (marker != null)
                {
                    var ring = GameObject.CreatePrimitive(PrimitiveType.Quad); Destroy(ring.GetComponent<Collider>());
                    ring.name = "Avatar stand marker"; ring.transform.localScale = new Vector3(0.45f, 0.45f, 1);
                    var paint = new Material(marker); paint.color = new Color(0.3f, 1f, 0.85f, 0.45f); ring.GetComponent<Renderer>().material = paint;
                    standMarker = ring.transform; ring.SetActive(false);
                }
                if (mr) StartCoroutine(LoadAnchors());
            }
            if (vr)
            {
                // Headsets do not know the relay's address yet; RelayDiscovery fills it in.
                discovery = new RelayDiscovery();
                network = new SortTransport(role.ToString()); network.Start(0, "", RelayPort);
                rayLine = new GameObject("Pointer ray").AddComponent<LineRenderer>();
                rayLine.material = new Material(surface); rayLine.material.color = new Color(0.3f, 1f, 0.85f);
                rayLine.startWidth = 0.004f; rayLine.endWidth = 0.002f; rayLine.positionCount = 2; rayLine.enabled = false;
            }
            else { network = new SortTransport(role.ToString()); network.Start(listenPort, peerIP, port); }
            StartCoroutine(AvatarLoop());
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

        Panel MakePanel(int id, string name, Vector3 pos, float yaw, float w, float h, float dx, float dy, Color color, float tilt = 0)
        {
            var root = new GameObject(name + " root").transform;
            root.SetPositionAndRotation(pos, Quaternion.Euler(tilt, yaw, 0));
            var p = new Panel { id = id, name = name, root = root, w = w, h = h, dx = dx, dy = dy };
            Box(name, pos + p.Forward * 0.02f, p.rot, new Vector3(w, h, 0.03f), color).transform.SetParent(root, true);
            Label(name + " label", pos + p.Up * (h / 2 + 0.07f * noteScale) - p.Forward * 0.01f, p.rot, 0.011f * noteScale, new Color(0.85f, 0.93f, 1f), root).text = name;
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
                if (vr)
                {
                    // Headset: notes are taken by hand, so the panels are spread out and the user walks between them.
                    // The walk is what gives the avatar in room B time to follow.
                    panels = new[]
                    {
                        MakePanel(0, "Clue tray", new Vector3(0, 1.25f, 1.0f), 0, 0.70f, 0.34f, 0.22f, 0.16f, home),
                        MakePanel(1, "Group 1", new Vector3(-1.4f, 1.25f, 2.2f), -50, 0.70f, 0.34f, 0.22f, 0.16f, one),
                        MakePanel(2, "Group 2", new Vector3(1.4f, 1.25f, 2.2f), 50, 0.70f, 0.34f, 0.22f, 0.16f, two)
                    };
                }
                else
                {
                    // Desktop: three panels side by side, mouse drag.
                    Box("Desk stand", new Vector3(0, 0.40f, 1.24f), Quaternion.identity, new Vector3(2.3f, 0.8f, 0.04f), new Color(0.16f, 0.20f, 0.26f));
                    panels = new[]
                    {
                        MakePanel(0, "Clue tray", new Vector3(0, 1.0f, 1.2f), 0, 0.70f, 0.34f, 0.22f, 0.16f, home),
                        MakePanel(1, "Group 1", new Vector3(-0.76f, 1.0f, 1.2f), 0, 0.70f, 0.34f, 0.22f, 0.16f, one),
                        MakePanel(2, "Group 2", new Vector3(0.76f, 1.0f, 1.2f), 0, 0.70f, 0.34f, 0.22f, 0.16f, two)
                    };
                }
                if (!vr) { cam.transform.position = new Vector3(0, 1.5f, -0.45f); cam.transform.LookAt(new Vector3(0, 1.08f, 1.2f)); }
            }
            else if (mr)
            {
                // Desk layout: three mats lying flat in a row on a table about 2.2 m long, 0.74 m high,
                // its near edge about 0.35 m in front of the user. The partner's avatar works from the far side.
                panels = new[]
                {
                    MakePanel(0, "Clue mat", new Vector3(0, 0.745f, 0.55f), 0, 0.70f, 0.34f, 0.22f, 0.16f, home, 90),
                    MakePanel(1, "Left mat", new Vector3(-0.74f, 0.745f, 0.55f), 0, 0.70f, 0.34f, 0.22f, 0.16f, one, 90),
                    MakePanel(2, "Right mat", new Vector3(0.74f, 0.745f, 0.55f), 0, 0.70f, 0.34f, 0.22f, 0.16f, two, 90)
                };
                foreach (var mat in panels) mat.across = true;
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
                // No wall to write on: status sits under the first panel; caption and clue share one text above it and move with it.
                wallText = Label("Status", anchor.position, anchor.rotation, 0.0065f * noteScale, statusColor, anchor);
                wallText.transform.localPosition = new Vector3(0, -top - 0.04f, -0.01f); wallText.anchor = TextAnchor.UpperCenter;
                var above = Label("Info", anchor.position, anchor.rotation, 0.0065f * noteScale, noteTextColor, anchor);
                above.transform.localPosition = new Vector3(0, top + 0.15f * noteScale, -0.01f); above.anchor = TextAnchor.LowerCenter;
                clueText = captionText = above;
            }
            else
            {
                bool small = role == Role.A;
                wallText = Label("Status", new Vector3(0, small ? 2.75f : 3.05f, 4.5f), Quaternion.identity, small ? 0.02f : 0.026f, statusColor);
                captionText = Label("Caption", new Vector3(0, small ? 2.5f : 2.78f, 4.5f), Quaternion.identity, small ? 0.014f : 0.018f, noteTextColor);
                captionText.anchor = TextAnchor.UpperCenter;
                clueText = Label("Clue", anchor.position, anchor.rotation, 0.006f * noteScale, noteTextColor, anchor);
                clueText.transform.localPosition = new Vector3(0, top + 0.15f * noteScale, -0.01f); clueText.anchor = TextAnchor.LowerCenter;
            }

            defaultPos = new Vector3[panels.Length]; defaultRot = new Quaternion[panels.Length]; defaultAcross = new bool[panels.Length];
            for (int i = 0; i < panels.Length; i++) { defaultPos[i] = panels[i].pos; defaultRot[i] = panels[i].rot; defaultAcross[i] = panels[i].across; }
            if (vr) LoadLayout();
        }

        // ---------- panel placement (headset only): where each user put the panels in their own room ----------

        [Serializable] class Layout { public Vector3[] pos; public Quaternion[] rot; public bool[] across; }

        // Room B in MR: mats lie on a real table. They always stay flat and only slide on the table plane.
        bool Desk => role == Role.B && mr;
        // Room A in MR: panels hang on real walls. They always stay upright; only position and heading change.
        bool Wall => role == Role.A && mr;
        string DeskTag => Desk ? "-desk" : "";

        // A flat mat with the same heading as q, whatever q's tilt (also repairs a mat saved upright by an earlier build).
        static Quaternion Upright(Quaternion q)
        {
            Vector3 f = q * Vector3.forward + q * Vector3.up; f.y = 0;
            return Quaternion.LookRotation(f.sqrMagnitude < 1e-6f ? Vector3.forward : f.normalized);
        }

        Quaternion Locked(Quaternion q) => Desk ? Flat(q) : Wall ? Upright(q) : q;

        static Quaternion Flat(Quaternion q)
        {
            Vector3 f = q * Vector3.forward + q * Vector3.up; f.y = 0;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            return Quaternion.LookRotation(f.normalized) * Quaternion.Euler(90, 0, 0);
        }

        string LayoutPath => Path.Combine(Application.persistentDataPath, "layout-" + role + (mr ? "-mr" : "-vr") + DeskTag + "-v3.json");

        void SaveLayout()
        {
            var layout = new Layout { pos = new Vector3[panels.Length], rot = new Quaternion[panels.Length], across = new bool[panels.Length] };
            for (int i = 0; i < panels.Length; i++) { layout.pos[i] = panels[i].pos; layout.rot[i] = panels[i].rot; layout.across[i] = panels[i].across; }
            try { File.WriteAllText(LayoutPath, JsonUtility.ToJson(layout)); } catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        void LoadLayout()
        {
            try
            {
                if (!File.Exists(LayoutPath)) return;
                var layout = JsonUtility.FromJson<Layout>(File.ReadAllText(LayoutPath));
                if (layout?.pos == null || layout.rot == null || layout.pos.Length != panels.Length || layout.rot.Length != panels.Length) return;
                for (int i = 0; i < panels.Length; i++)
                {
                    panels[i].root.SetPositionAndRotation(layout.pos[i], Locked(layout.rot[i]));
                    if (layout.across != null && layout.across.Length == panels.Length) panels[i].across = layout.across[i];
                }
            }
            catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        void ResetLayout()
        {
            for (int i = 0; i < panels.Length; i++) { panels[i].root.SetPositionAndRotation(defaultPos[i], defaultRot[i]); panels[i].across = defaultAcross[i]; }
            for (int i = 0; i < N; i++) if (loc[i] >= 0 && i != held) Snap(i, loc[i]);
            try { if (File.Exists(LayoutPath)) File.Delete(LayoutPath); } catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        // ---------- spatial anchors (MR): each panel is pinned to the real room, not to where the headset was recentred ----------
        // The anchors live on this headset. Placing a panel replaces its anchor; holding B erases them all.

        [Serializable] class AnchorFile { public string[] uuid; }
        readonly OVRSpatialAnchor[] anchors = new OVRSpatialAnchor[SortTransport.Panels];
        readonly bool[] anchorBusy = new bool[SortTransport.Panels];
        string[] anchorIds = new string[SortTransport.Panels];
        string anchorNote = "";

        string AnchorPath => Path.Combine(Application.persistentDataPath, "anchors-" + role + DeskTag + ".json");

        void WriteAnchorFile()
        {
            try { File.WriteAllText(AnchorPath, JsonUtility.ToJson(new AnchorFile { uuid = anchorIds })); } catch (Exception e) { Debug.LogWarning(e.Message); }
        }

        int AnchorCount() { int n = 0; foreach (var a in anchors) if (a != null && a.Localized) n++; return n; }

        IEnumerator LoadAnchors()
        {
            AnchorFile file = null;
            try { if (File.Exists(AnchorPath)) file = JsonUtility.FromJson<AnchorFile>(File.ReadAllText(AnchorPath)); }
            catch (Exception e) { Debug.LogWarning(e.Message); }
            if (file?.uuid != null && file.uuid.Length == panels.Length) anchorIds = file.uuid;
            var ids = new List<Guid>(); var panelOf = new Dictionary<Guid, int>();
            for (int i = 0; i < panels.Length; i++)
                if (Guid.TryParse(anchorIds[i] ?? "", out Guid id) && id != Guid.Empty) { ids.Add(id); panelOf[id] = i; }
            if (ids.Count == 0) { anchorNote = "Anchors: none yet. Grip each panel and put it on the real desk or wall"; yield break; }

            anchorNote = "Anchors: looking for this room...";
            var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
            var load = OVRSpatialAnchor.LoadUnboundAnchorsAsync(ids, unbound);
            while (!load.IsCompleted) yield return null;
            var result = load.GetResult();
            if (!result.Success) { anchorNote = "Anchors: could not load (" + result.Status + "). Panels are at their last place"; yield break; }

            // Localise all at once; a room the headset does not recognise must not hold up the others.
            var pending = new List<(OVRSpatialAnchor.UnboundAnchor anchor, OVRTask<bool> task, int panel)>();
            foreach (var u in result.Value)
                if (panelOf.TryGetValue(u.Uuid, out int i)) pending.Add((u, u.Localized ? OVRTask.FromResult(true) : u.LocalizeAsync(20), i));
            float giveUp = Time.unscaledTime + 25f;
            while (pending.Count > 0 && Time.unscaledTime < giveUp)
            {
                for (int k = pending.Count - 1; k >= 0; k--)
                {
                    var (u, task, i) = pending[k];
                    if (!task.IsCompleted) continue;
                    pending.RemoveAt(k);
                    if (!task.GetResult() || movingPanel == i || anchors[i] != null) continue;
                    var holder = new GameObject("Anchor " + panels[i].name);
                    if (u.TryGetPose(out Pose pose)) holder.transform.SetPositionAndRotation(pose.position, pose.rotation);
                    var bound = holder.AddComponent<OVRSpatialAnchor>();
                    u.BindTo(bound);
                    anchors[i] = bound;
                }
                yield return null;
            }
            int found = AnchorCount(), missing = ids.Count - found;
            anchorNote = missing == 0 ? "Anchors: " + found + " panels pinned to this room"
                                      : "Anchors: " + missing + " panel(s) not found in this room. Grip them back into place";
        }

        // Every frame: an anchored panel sits exactly on its anchor, which the headset keeps aligned with the room.
        void FollowAnchors()
        {
            for (int i = 0; i < panels.Length; i++)
            {
                var a = anchors[i];
                if (a == null || !a.Localized || i == movingPanel || (Desk && movingPanel >= 0)) continue; // desk: height changes move all mats
                Transform t = a.transform; Quaternion want = Locked(t.rotation);
                if ((panels[i].pos - t.position).sqrMagnitude < 1e-7f && Quaternion.Angle(panels[i].rot, want) < 0.05f) continue;
                panels[i].root.SetPositionAndRotation(t.position, want);
                for (int n = 0; n < N; n++) if (loc[n] == i && n != held) Snap(n, i);
            }
        }

        IEnumerator AnchorPanel(int i)
        {
            while (anchorBusy[i]) yield return null;
            anchorBusy[i] = true;
            anchorNote = "Anchors: pinning " + panels[i].name + "...";
            var old = anchors[i]; anchors[i] = null;
            if (old != null)
            {
                var erase = old.EraseAnchorAsync();
                while (!erase.IsCompleted) yield return null;
                Destroy(old.gameObject);
            }
            else if (Guid.TryParse(anchorIds[i] ?? "", out Guid stale) && stale != Guid.Empty)
            {
                var erase = OVRSpatialAnchor.EraseAnchorsAsync(new List<OVRSpatialAnchor>(), new[] { stale });
                while (!erase.IsCompleted) yield return null;
            }
            anchorIds[i] = ""; WriteAnchorFile();
            var holder = new GameObject("Anchor " + panels[i].name);
            holder.transform.SetPositionAndRotation(panels[i].pos, panels[i].rot);
            var anchor = holder.AddComponent<OVRSpatialAnchor>();
            var created = anchor.WhenCreatedAsync();
            while (!created.IsCompleted) yield return null;
            if (!created.GetResult()) { anchorNote = "Anchors: could not pin " + panels[i].name + ". Try again with more light"; Destroy(holder); anchorBusy[i] = false; yield break; }
            var save = anchor.SaveAnchorAsync();
            while (!save.IsCompleted) yield return null;
            var saved = save.GetResult();
            if (!saved.Success) { anchorNote = "Anchors: " + panels[i].name + " pinned for now but not saved (" + saved.Status + ")"; }
            else { anchorIds[i] = anchor.Uuid.ToString(); WriteAnchorFile(); anchorNote = "Anchors: " + AnchorCountAfter(anchor, i) + " panels pinned to this room"; }
            anchors[i] = anchor;
            anchorBusy[i] = false;
        }

        int AnchorCountAfter(OVRSpatialAnchor fresh, int i) { int n = 1; for (int k = 0; k < anchors.Length; k++) if (k != i && anchors[k] != null && anchors[k].Localized) n++; return n; }

        IEnumerator EraseAnchors()
        {
            var live = new List<OVRSpatialAnchor>(); var ids = new List<Guid>();
            for (int i = 0; i < panels.Length; i++)
            {
                if (anchors[i] != null) live.Add(anchors[i]);
                else if (Guid.TryParse(anchorIds[i] ?? "", out Guid id) && id != Guid.Empty) ids.Add(id);
                anchors[i] = null; anchorIds[i] = "";
            }
            WriteAnchorFile();
            if (live.Count + ids.Count == 0) yield break;
            var erase = OVRSpatialAnchor.EraseAnchorsAsync(live, ids);
            while (!erase.IsCompleted) yield return null;
            foreach (var a in live) if (a != null) Destroy(a.gameObject);
            anchorNote = "Anchors: erased. Grip each panel and put it on the real desk or wall";
        }

        // Where the avatar will stand: shown while a panel is being placed and for a few seconds after.
        Transform standMarker; int markerPanel = -1; float markerUntil;

        void ShowStandMarker()
        {
            if (standMarker == null) return;
            int k = movingPanel >= 0 ? movingPanel : Time.unscaledTime < markerUntil ? markerPanel : -1;
            standMarker.gameObject.SetActive(k >= 0);
            if (k < 0) return;
            Panel p = panels[k];
            standMarker.position = p.Stand(p.pos) + Vector3.up * 0.01f;
            standMarker.rotation = Quaternion.LookRotation(Vector3.down, p.Face);
        }

        // Grip on a panel to carry it at the end of the ray; thumbstick pushes it away or pulls it closer.
        // The panel always turns to face the user. Holding B for two seconds restores the default layout.
        void MovePanels(Ray ray, bool valid, bool grip, Vector2 stick, bool resetButton, bool flip)
        {
            if (movingPanel < 0)
            {
                if (valid && grip && !gripWas && held < 0 && Aim(ray, out Vector3 grabbed, out int target))
                {
                    movingPanel = target; moveDistance = aimDistance; moveTilt = panels[target].Tilt;
                    deskOffset = panels[target].pos - grabbed; deskOffset.y = 0; deskHeightChanged = false; moveYaw = 0;
                }
                else if (valid && flip && held < 0 && Aim(ray, out _, out int side))
                {
                    panels[side].across = !panels[side].across; SaveLayout();
                    markerPanel = side; markerUntil = Time.unscaledTime + 4f;
                }
            }
            else if (!valid || !grip)
            {
                SaveLayout();
                if (mr)
                {
                    if (Desk && deskHeightChanged) for (int k = 0; k < panels.Length; k++) StartCoroutine(AnchorPanel(k));
                    else StartCoroutine(AnchorPanel(movingPanel));
                }
                markerPanel = movingPanel; markerUntil = Time.unscaledTime + 4f;
                movingPanel = -1;
            }
            else if (Desk)
            {
                // Slide on the table: the mat follows where the ray meets the table plane. Stick left/right turns it,
                // up/down raises or lowers the table height for all mats together.
                Panel moved = panels[movingPanel]; float y = moved.pos.y;
                if (Mathf.Abs(stick.y) > 0.2f)
                {
                    y = Mathf.Clamp(y + stick.y * 0.15f * Time.deltaTime, 0.3f, 1.4f); deskHeightChanged = true;
                    foreach (var mat in panels) { Vector3 at2 = mat.pos; at2.y = y; mat.root.position = at2; }
                }
                if (Mathf.Abs(stick.x) > 0.2f) moved.root.rotation = Quaternion.Euler(0, stick.x * 60f * Time.deltaTime, 0) * moved.rot;
                moved.root.rotation = Flat(moved.rot);
                var table = new Plane(Vector3.up, new Vector3(0, y, 0));
                if (table.Raycast(ray, out float d) && d < 6f) { Vector3 at = ray.GetPoint(d) + deskOffset; at.y = y; moved.root.position = at; }
                for (int k = 0; k < panels.Length; k++) for (int i = 0; i < N; i++) if (loc[i] == k && i != held) Snap(i, k);
            }
            else
            {
                // Up/down on the stick: nearer or farther. Left/right: tilt from upright (wall) to flat (desk), with a snap at both ends.
                moveDistance = Mathf.Clamp(moveDistance + stick.y * Time.deltaTime * 1.2f, 0.4f, 6f);
                if (Wall)
                {
                    // Upright only. Left/right on the stick turns the panel to sit flush on a wall that is not square to the user.
                    moveTilt = 0f;
                    if (Mathf.Abs(stick.x) > 0.2f) moveYaw = Mathf.Clamp(moveYaw + stick.x * 60f * Time.deltaTime, -90f, 90f);
                }
                else if (Mathf.Abs(stick.x) > 0.2f) moveTilt = Mathf.Clamp(moveTilt + stick.x * 70f * Time.deltaTime, 0f, 90f);
                else if (moveTilt < 6f) moveTilt = 0f; else if (moveTilt > 84f) moveTilt = 90f;
                Panel moved = panels[movingPanel]; Vector3 at = ray.GetPoint(moveDistance);
                Vector3 facing = at - cam.transform.position; facing.y = 0;
                if (facing.sqrMagnitude > 0.01f) moved.root.rotation = Quaternion.Euler(0, moveYaw, 0) * Quaternion.LookRotation(facing) * Quaternion.Euler(moveTilt, 0, 0);
                moved.root.position = at;
                for (int i = 0; i < N; i++) if (loc[i] == movingPanel && i != held) Snap(i, movingPanel);
            }
            gripWas = grip && valid;
            resetHeld = resetButton && movingPanel < 0 ? resetHeld + Time.deltaTime : 0;
            if (resetHeld > 2f) { if (mr) StartCoroutine(EraseAnchors()); ResetLayout(); resetHeld = 0; }
        }

        float moveTilt, moveYaw; Vector3 deskOffset; bool deskHeightChanged;

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
            for (int i = 0; i < N; i++) if (agreed[i] == panel) list.Add(Letter[i]);
            return list.Count == 0 ? "empty" : string.Join(", ", list);
        }

        bool Live => Time.unscaledTime - network.LastReceived <= 1f;

        // A note can be taken only when it is at rest here: not in the partner's hand, not carried by the avatar,
        // and not waiting for the avatar to move it.
        bool CanGrab(int note) => loc[note] >= 0 && loc[note] == agreed[note] && carrying != note && !(Live && network.Held == note);

        void Grab(int note)
        {
            held = note; hover = loc[note]; grabbedFrom = loc[note];
            lastHover = hover; hoverSince = Time.unscaledTime; lastFocus = -1;
            Event("grab note " + Letter[note] + " [" + Header[note] + "] from " + panels[loc[note]].name);
        }

        void Release(int panel)
        {
            int note = held; loc[note] = panel; Snap(note, panel); held = -1; hover = -1; lastHover = -1;
            if (panel != grabbedFrom) { agreed[note] = panel; ver[note] = ++clock; }
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
            text.AppendLine("room=" + (role == Role.A ? (vr ? "A, room-scale layout, user walks between the panels and moves notes by hand" : "A, compact desk layout, user sorts notes by hand") : (mr ? "B, desk layout, notes spread out on a table, user reaches for them by hand" : "B, room-scale layout, user walks between the panels and moves notes by hand")));
            foreach (var p in panels)
            {
                Vector3 to = p.pos - cam.transform.position; to.y = 0;
                text.AppendLine("panel id=" + p.id + " name=\"" + p.name + "\" role=" + (p.id == 0 ? "unsorted clues" : "group") +
                    (p.Tilt > 45 ? " lying on a desk" : " upright") +
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
                if (pressed && !wasPressed && focus >= 0 && CanGrab(focus)) Grab(focus);
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

        // ---------- A in the headset: notes are taken by hand, at arm's length ----------
        // The sender walks up to a note and pinches it; there is no pointing from a distance.
        // The walk covers the seconds the avatar in room B needs to follow.

        const float GrabReach = 0.09f, NoticeReach = 0.16f, DropDepth = 0.20f;
        readonly Transform[][] tips = new Transform[2][];
        readonly bool[] pressWas = new bool[3];
        readonly Transform[] dots = new Transform[3];
        int grabSource = -1, reachNote = -1; float openSince = -1, lostSince = -1; Vector3 heldInHead;

        bool ControllerTracked => (OVRInput.GetConnectedControllers() & OVRInput.Controller.RTouch) != 0 &&
            OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch);

        bool Tips(int side, OVRHand hand)
        {
            if (tips[side] != null) return true;
            var skeleton = hand.GetComponent<OVRSkeleton>();
            if (skeleton == null || !skeleton.IsInitialized || skeleton.Bones == null) return false;
            var type = skeleton.GetSkeletonType();
            bool xr = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
            var thumbId = xr ? OVRSkeleton.BoneId.XRHand_ThumbTip : OVRSkeleton.BoneId.Hand_ThumbTip;
            var indexId = xr ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
            Transform thumb = null, index = null;
            foreach (var bone in skeleton.Bones)
            {
                if (bone.Id == thumbId) thumb = bone.Transform; else if (bone.Id == indexId) index = bone.Transform;
            }
            if (thumb == null || index == null) return false;
            tips[side] = new[] { thumb, index };
            return true;
        }

        // 0 = left hand, 1 = right hand (point between thumb tip and index tip, pinch), 2 = right controller (its position, trigger).
        bool Source(int source, out Vector3 point, out bool pressed)
        {
            point = default; pressed = false;
            var rig = questRig.GetComponent<OVRCameraRig>();
            if (source == 2)
            {
                if (!ControllerTracked) return false;
                point = rig.rightControllerAnchor.position;
                pressed = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
                return true;
            }
            OVRHand hand = source == 0 ? leftHand : rightHand;
            if (hand == null || !hand.IsTracked) return false;
            pressed = hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
            if (Tips(source, hand)) point = (tips[source][0].position + tips[source][1].position) / 2;
            else
            {
                // Skeleton not ready yet: the pointer pose origin sits by the hand and is close enough to start with.
                Transform space = rig.trackingSpace;
                point = hand.IsPointerPoseValid ? space.TransformPoint(hand.PointerPose.localPosition) : (source == 0 ? rig.leftHandAnchor : rig.rightHandAnchor).position;
            }
            return true;
        }

        float NoteDistance(int note, Vector3 point)
        {
            Vector3 local = Quaternion.Inverse(notes[note].rotation) * (point - notes[note].position);
            float x = Mathf.Max(Mathf.Abs(local.x) - NoteW * noteScale / 2, 0), y = Mathf.Max(Mathf.Abs(local.y) - NoteH * noteScale / 2, 0);
            return new Vector3(x, y, local.z).magnitude;
        }

        // The panel a hand is at: inside its rectangle (a little margin) and within reach of its surface.
        int PanelNear(Vector3 point)
        {
            int found = -1; float best = DropDepth;
            foreach (var p in panels)
            {
                Vector3 local = Quaternion.Inverse(p.rot) * (point - p.pos);
                if (Mathf.Abs(local.x) > p.w / 2 + 0.05f || Mathf.Abs(local.y) > p.h / 2 + 0.05f || Mathf.Abs(local.z) >= best) continue;
                best = Mathf.Abs(local.z); found = p.id;
            }
            return found;
        }

        void TouchInput(float now)
        {
            var point = new Vector3[3]; var valid = new bool[3]; var pressed = new bool[3];
            for (int s = 0; s < 3; s++) valid[s] = Source(s, out point[s], out pressed[s]);
            reachNote = -1;
            if (held < 0)
            {
                int near = -1, nearSource = -1; float best = NoticeReach;
                for (int s = 0; s < 3; s++)
                {
                    if (!valid[s]) continue;
                    for (int i = 0; i < N; i++)
                    {
                        float d = NoteDistance(i, point[s]);
                        if (d < best) { best = d; near = i; nearSource = s; }
                    }
                }
                if (near >= 0)
                {
                    focus = near;
                    if (best <= GrabReach && CanGrab(near))
                    {
                        reachNote = near;
                        if (pressed[nearSource] && !pressWas[nearSource]) { Grab(near); grabSource = nearSource; openSince = lostSince = -1; }
                    }
                }
                else
                {
                    // No hand at a note: the note the head is turned toward counts as the one being looked at.
                    focus = Aim(new Ray(cam.transform.position, cam.transform.forward), out Vector3 seen, out _) ? NoteAt(seen) : -1;
                }
            }
            else
            {
                focus = held; int s = grabSource;
                if (valid[s])
                {
                    lostSince = -1;
                    notes[held].position = point[s]; heldInHead = cam.transform.InverseTransformPoint(point[s]);
                    hover = PanelNear(point[s]);
                    Vector3 away = point[s] - cam.transform.position; away.y = 0;
                    if (hover >= 0) notes[held].rotation = panels[hover].rot;
                    else if (away.sqrMagnitude > 0.0004f) notes[held].rotation = Quaternion.LookRotation(away);
                    // A pinch that flickers open for a frame is not a release.
                    if (pressed[s]) openSince = -1; else if (openSince < 0) openSince = now;
                    if (openSince >= 0 && now - openSince > 0.12f) { Release(hover >= 0 ? hover : loc[held]); grabSource = -1; }
                }
                else
                {
                    // The hand left the cameras' view, which happens while walking with the arm down.
                    // The note stays with the user; only a long loss puts it back.
                    if (lostSince < 0) lostSince = now;
                    notes[held].position = cam.transform.TransformPoint(heldInHead); hover = -1;
                    if (now - lostSince > 4f) { Release(loc[held]); grabSource = -1; }
                }
            }
            for (int s = 0; s < 3; s++)
            {
                pressWas[s] = valid[s] && pressed[s];
                // A small dot shows where the system thinks the pinch is.
                if (dots[s] == null)
                {
                    var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere); Destroy(dot.GetComponent<Collider>());
                    dot.name = "Pinch point " + s; dot.transform.localScale = Vector3.one * 0.012f;
                    dot.GetComponent<Renderer>().material = new Material(surface);
                    dots[s] = dot.transform;
                }
                dots[s].gameObject.SetActive(valid[s] && held < 0);
                if (valid[s])
                {
                    dots[s].position = point[s];
                    dots[s].GetComponent<Renderer>().material.color = pressed[s] ? new Color(1f, 0.9f, 0.3f) : new Color(0.3f, 1f, 0.85f);
                }
            }
        }

        // Right Touch controller: grip on a panel moves it (both rooms). In room A the hands, or the controller held at a note, move notes.
        void HeadsetInput(float now)
        {
            var rig = questRig.GetComponent<OVRCameraRig>();
            bool controller = ControllerTracked, grip = false, resetButton = false, flip = false; Vector2 stick = Vector2.zero; Ray ray = default;
            if (controller)
            {
                Transform anchor = rig.rightControllerAnchor;
                ray = new Ray(anchor.position, anchor.forward);
                grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
                stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
                resetButton = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);
                flip = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
            }
            MovePanels(ray, controller, grip, stick, resetButton, flip);
            ShowStandMarker();
            bool show = controller && (grip || movingPanel >= 0);
            ShowRay(ray, show, show ? ray.GetPoint(movingPanel >= 0 ? moveDistance : 2.5f) : Vector3.zero);
            if (movingPanel < 0) TouchInput(now);
            inputNote = !controller ? "Hands: pinch a note with thumb and index finger, let go at a group panel.  Panels need the controller"
                : Wall ? "Trigger at a note: take it.  Grip on a panel: move it (stick up/down = distance, left/right = turn).  A: avatar side.  Hold B: reset"
                : Desk ? "Trigger at a note: take it.  Grip on a mat: slide it on the table (stick left/right = turn, up/down = table height).  A: avatar side.  Hold B: reset"
                : "Trigger at a note: take it.  Grip on a panel: move it (stick up/down = distance, left/right = tilt to desk).  A: avatar side.  Hold B: reset";
        }

        // Scripted input for the automated run. f = look at note, g = grab note, m = carry toward panel, r = release on panel.
        // Looks at D then C, sorts C into Group 1; carries E back and forth between the groups before Group 2; sorts A into Group 1; looks at B.
        static readonly float[] ScriptTime = { 0.8f, 2.0f, 3.0f, 3.0f, 5.5f, 7.0f, 8.5f, 8.5f, 10.5f, 12.0f, 13.5f, 17.0f, 18.0f, 19.5f, 19.5f, 22.0f, 23.5f, 25.5f };
        static readonly char[] ScriptKind = { 'f', 'f', 'g', 'm', 'r', 'f', 'g', 'm', 'm', 'm', 'm', 'r', 'f', 'g', 'm', 'r', 'f', 'f' };
        static readonly int[] ScriptArg = { 3, 2, 2, 1, 1, 4, 4, 1, 2, 1, 2, 2, 0, 0, 1, 1, 1, -1 };
        // Room B: looks at F, then sorts it into its panel 2.
        static readonly float[] ScriptTimeB = { 29.0f, 30.5f, 30.5f, 33.5f };
        static readonly char[] ScriptKindB = { 'f', 'g', 'm', 'r' };
        static readonly int[] ScriptArgB = { 5, 5, 2, 2 };
        int scriptStep, scriptPanel = -1, scriptFocus = -1;

        void ScriptedInput(float t)
        {
            float[] times = role == Role.A ? ScriptTime : ScriptTimeB;
            char[] kinds = role == Role.A ? ScriptKind : ScriptKindB;
            int[] args = role == Role.A ? ScriptArg : ScriptArgB;
            while (scriptStep < times.Length && t >= times[scriptStep])
            {
                int arg = args[scriptStep];
                switch (kinds[scriptStep])
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

        // ---------- the avatar acts out the partner's state in this room's own layout ----------

        IEnumerator AvatarLoop()
        {
            while (!actor.Ready) yield return null;
            idleSince = Time.time; lookPoint = cam.transform.position;
            while (true)
            {
                int[] wanted = agreed; int wantHeld = network.Held, wantHover = network.Hover, wantFocus = network.Focus;
                if (carrying < 0)
                {
                    int note = -1;
                    if (wantHeld >= 0 && wantHeld != held && loc[wantHeld] >= 0) note = wantHeld;                      // A just picked something up
                    else for (int i = 0; i < N; i++) if (i != held && loc[i] >= 0 && loc[i] != wanted[i]) { note = i; break; } // catch up on a missed move
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
                                Vector3 stand = p.Stand(at, p.ReachBack + 0.5f, 0.10f);
                                bool there = actor.Step(stand, p.Face);
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
                    yield return actor.WalkTo(src.Stand(slot), src.Face);
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
                        Log("{\"t\":" + Stamp() + ",\"role\":\"" + role + "\",\"avatar\":\"pick " + Letter[note] + " from " + src.name + "\",\"handError\":" + actor.ReachError.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}");
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
                            bool there = actor.Step(ahead.Stand(ahead.Slot(note)), ahead.Face);
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
                    yield return actor.WalkTo(to.Stand(slot), to.Face);
                    avatarDoing = "placing note " + Letter[note] + " on " + to.name;
                    yield return actor.Reach(slot, () =>
                    {
                        actor.Carried = null; Snap(note, destination); loc[note] = destination; carrying = -1;
                        noteFaces[note].material.color = NoteColor;
                        Log("{\"t\":" + Stamp() + ",\"role\":\"" + role + "\",\"avatar\":\"place " + Letter[note] + " on " + to.name + "\",\"handError\":" + actor.ReachError.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "}");
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
            network.Poll(now);
            if (mr) FollowAnchors();
            bool live = Live;
            // Take over the partner's placements: a higher version wins; on a tie room A's view stands.
            for (int i = 0; i < N; i++)
            {
                int theirs = network.Ver[i];
                if (theirs > clock) clock = theirs;
                if (theirs > ver[i] || (theirs == ver[i] && network.Loc[i] != agreed[i] && role == Role.B)) { agreed[i] = network.Loc[i]; ver[i] = theirs; }
            }
            // Both reached for the same note at the same moment: room B lets go.
            if (held >= 0 && live && network.Held == held && role == Role.B) { Release(loc[held]); grabSource = -1; }

            if (smoke) ScriptedInput(now - smokeStart); else if (vr) HeadsetInput(now); else MouseInput();
            if (now >= nextTrace) { Sample(now); nextTrace = now + 0.4f; }
            actor.LookPoint = lookPoint;
            for (int i = 0; i < N; i++)
                noteFaces[i].material.color = i == held || i == carrying ? HeldColor : i == reachNote ? Color.Lerp(NoteColor, Color.white, 0.45f) : NoteColor;
            if (network.TornStamp != tornSeen)
            {
                tornSeen = network.TornStamp; int about = network.TornNote;
                if (network.Torn.Length >= 2)
                {
                    if (undecided[about] == null) Log("{\"t\":" + Stamp() + ",\"role\":\"" + role + "\",\"intent\":\"note " + Letter[about] + " torn between " + panels[network.Torn[0]].name + " and " + panels[network.Torn[1]].name + "\"}");
                    undecided[about] = new[] { network.Torn[0], network.Torn[1] };
                    liveTornNote = about; liveTornUntil = now + 6f;
                }
                else if (about == liveTornNote) liveTornNote = -1; // the model no longer sees hesitation
            }
            if (now >= nextSend) { network.SendState(held, hover, held >= 0 ? held : focus, agreed, ver, eventCount, eventText); nextSend = now + 0.05f; }
            if (now >= nextScene) { network.SendScene(SceneText()); nextScene = now + 1; }

            string hint = vr ? "Walk to a note, pinch it, carry it to " + panels[1].name + " or " + panels[2].name + " and let go"
                             : "Drag a note onto " + panels[1].name + " or " + panels[2].name;
            wallText.text = "ROOM " + role + "  |  " + (held >= 0 ? "Holding " + Letter[held] + (hover >= 0 ? " over " + panels[hover].name : "") : hint) +
                "\nPartner: " + (live ? avatarDoing : "not connected yet");
            string clue = focus >= 0 ? Wrap("Note " + Letter[focus] + ": " + Clue[focus], 46) : "", caption = Wrap(network.Caption, 70);
            if (clueText == captionText) clueText.text = caption + (clue != "" && caption != "" ? "\n\n" : "") + clue;
            else { clueText.text = clue; captionText.text = caption; }
            if (vr) wallText.text += "\n" + netNote + "\n" + inputNote + (mr ? "\n" + anchorNote : "");
            if (smoke)
            {
                float t = now - smokeStart;
                if (t >= nextShot) { nextShot += 2; StartCoroutine(Capture(role + "-" + (shot++).ToString("00"))); }
                if (t > (role == Role.A ? 46 : 48))
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
            GUILayout.Label("Room " + role + (peerIP != "" ? "  <->  " + peerIP + ":" + port : "  listening on UDP " + listenPort));
            GUILayout.Label("Hold the left mouse button on a note and drop it on a group panel. The avatar does what your partner does. Esc quits.");
            if (focus >= 0) GUILayout.Label("Note " + Letter[focus] + ": " + Clue[focus]);
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
