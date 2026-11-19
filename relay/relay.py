"""Relay between room A and room B. Both directions are handled the same way.

For each room (the source) and the other room (the destination):
  1. Both rooms send a text description of their own layout (kind=scene).
  2. The relay decides which panel in B stands for which panel in A (OpenAI if a key is set,
     otherwise same-id mapping) and rewrites every state packet into the destination's panel ids.
  3. Every action sentence from the source (grab, place, move, put back) is rewritten in the
     destination's vocabulary, kept in a timeline, and sent to the destination as a caption.
  4. The source also sends a raw motion trace every 0.4 s: what is held, its distance to each panel,
     how it is moving, what the user is looking at. Nothing in it is labelled as hesitation.
  5. OpenAI (if available) reads events + trace + current groups + its own previous reading and
     returns a structured intent reading: after every placement, and every few seconds while the
     user is still holding a note. The reading goes to the destination room.

Standard library only. Set OPENAI_API_KEY in the environment or in a .env file next to this script.
"""
import argparse
import json
import os
import re
import socket
import threading
import time
import urllib.request
from pathlib import Path

PROTOCOL = "mystery-sort-v2"
BEACON = b"mystery-sort-relay-v1"
HERE = Path(__file__).resolve().parent


def load_env():
    env = HERE / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if "=" in line and not line.strip().startswith("#"):
                key, value = line.split("=", 1)
                os.environ.setdefault(key.strip(), value.strip().strip('"').strip("'"))


def ask_openai(system, user, timeout=20):
    """Returns the reply text, or None when there is no key or the call fails."""
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        return None
    body = json.dumps({
        "model": os.environ.get("OPENAI_MODEL", "gpt-5.4-mini"),
        "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
    }).encode("utf-8")
    request = urllib.request.Request(
        "https://api.openai.com/v1/chat/completions", data=body,
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read())["choices"][0]["message"]["content"].strip()
    except Exception as error:  # network, auth, model name, quota
        detail = ""
        if hasattr(error, "read"):
            try:
                detail = error.read().decode("utf-8", "replace")[:400]
            except Exception:
                pass
        message = f"[openai] failed: {type(error).__name__}: {error} {detail}"
        print(message)
        with (HERE / "openai-errors.log").open("a", encoding="utf-8") as handle:
            handle.write(time.strftime("%H:%M:%S ") + message + "\n")
        return None


def panel_names(scene_text):
    return {int(i): name for i, name in re.findall(r'panel id=(\d+) name="([^"]+)"', scene_text or "")}


class Side:
    """Everything the relay knows about one room and about what its user has been doing."""

    def __init__(self, name):
        self.name = name
        self.addr = None
        self.scene = None
        self.last_event = 0
        self.history = []          # (seconds, sentence in the other room's vocabulary), whole session
        self.reading = None        # last structured intent reading about this room's user
        self.reading_request = 0   # only the newest request may publish its answer
        self.trace = []            # (seconds, raw motion sample in the other room's vocabulary)
        self.last_trace = 0
        self.holding_samples = 0   # samples with a note in hand since the last model call
        self.last_loc = [0] * 6
        self.busy = False
        self.lock = threading.Lock()


class Relay:
    def __init__(self, args):
        self.args = args
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("0.0.0.0", args.listen))
        self.sides = {"A": Side("A"), "B": Side("B")}
        self.mapping = {0: 0, 1: 1, 2: 2}  # A panel id -> B panel id
        self.mapping_started = False
        self.lock = threading.Lock()
        self.log_path = Path(args.log)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_path.write_text("", encoding="utf-8")

    def other(self, side):
        return self.sides["B" if side.name == "A" else "A"]

    def panel_map(self, src):
        """Panel ids of room `src` -> panel ids of the other room."""
        return self.mapping if src.name == "A" else {b: a for a, b in self.mapping.items()}

    def log(self, kind, **fields):
        record = {"t": round(time.time(), 3), "kind": kind, **fields}
        with self.lock:
            with self.log_path.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        print(f"[{kind}] " + ", ".join(f"{k}={v}" for k, v in fields.items()))

    def send_caption(self, dst, text, note=-1, torn=None):
        # note >= 0 marks this caption as an intent reading about that note; torn = panel ids of the receiving room.
        if dst.addr is None:
            return
        packet = {"protocol": PROTOCOL, "kind": "caption", "role": "relay", "text": text, "focus": note, "torn": torn or []}
        self.sock.sendto(json.dumps(packet).encode("utf-8"), dst.addr)
        self.log("caption", to=dst.name, text=text)

    # ---- panel mapping -------------------------------------------------------------------

    def build_mapping(self):
        scene_a, scene_b = self.sides["A"].scene, self.sides["B"].scene
        reply = ask_openai(
            "You align two differently arranged rooms used for the same note-sorting task. "
            "Each room has one panel of unsorted clues and group panels. Map every panel id in room A "
            "to the panel id in room B that plays the same role for a person standing in that room "
            "(same function first, then same side relative to the viewer). "
            'Answer with JSON only, e.g. {"0": 0, "1": 1, "2": 2}. Every B id must be used exactly once.',
            "ROOM A\n" + scene_a + "\nROOM B\n" + scene_b)
        source = "default same-id (no OpenAI key or call failed)"
        mapping = {0: 0, 1: 1, 2: 2}
        if reply:
            try:
                parsed = json.loads(re.search(r"\{.*\}", reply, re.S).group(0))
                candidate = {int(k): int(v) for k, v in parsed.items()}
                if sorted(candidate) == [0, 1, 2] and sorted(candidate.values()) == [0, 1, 2]:
                    mapping, source = candidate, "openai"
                else:
                    source = "default same-id (OpenAI answer was not a valid one-to-one mapping)"
            except Exception:
                source = "default same-id (OpenAI answer was not JSON)"
        self.mapping = mapping
        a, b = panel_names(scene_a), panel_names(scene_b)
        self.log("mapping", source=source,
                 pairs="; ".join(f"{a.get(k, k)} <-> {b.get(v, v)}" for k, v in sorted(mapping.items())))

    # ---- events ----------------------------------------------------------------------------

    def translate_sentence(self, src, sentence):
        """Rewrite panel names of room `src` into the names the other room uses for the matching panels."""
        own, theirs, mapping = panel_names(src.scene), panel_names(self.other(src).scene), self.panel_map(src)
        table = {name: theirs.get(mapping.get(index, index), name) for index, name in own.items()}
        if not table:
            return sentence
        pattern = "|".join(re.escape(name) for name in sorted(table, key=len, reverse=True))
        return re.sub(pattern, lambda match: table[match.group(0)], sentence)

    INTENT_SYSTEM = (
        "You observe a person (the partner) sorting murder-mystery clue notes into groups in another room, "
        "and you explain their thinking to a collaborator who works on the same notes from their own room. You receive: EVENTS, the discrete "
        "actions of the whole session; TRACE, raw samples of the last seconds taken every 0.4 s (what the partner "
        "holds, the distance from that note to every panel, its motion, or what they look at with an empty hand); "
        "the CURRENT GROUPS; and YOUR PREVIOUS READING. Nobody has interpreted the trace for you. Read the "
        "process yourself: approaching a panel and backing off, pausing, going back and forth between panels, "
        "or looking at several notes before taking one all mean something. Distances are in the partner's own room, "
        "whose panels may be an arm's length or a few steps apart, so judge by how the distances change, not by their size; "
        "panel names are the collaborator's names for the matching panels. The collaborator may also move notes; the groups show the joint result. "
        "Update your previous reading instead of starting over, and say so when the evidence is too thin. "
        "Reply with JSON only: "
        '{"state": "exploring" | "deciding" | "confident" | "revising", '
        '"confidence": number 0-1, '
        '"reading": "one sentence, max 22 words: what the partner is trying to do right now, citing what in the process shows it", '
        '"torn_between": [panel names the partner is undecided between right now. Fill it only when the trace shows '
        'the held note reversing direction between panels or stalling between them; one direct move to a panel is NOT torn. Empty otherwise], '
        '"group_labels": {"<group panel name>": "2-4 word label, or ? if unclear"}, '
        '"next": "note letter the partner will most likely handle next, or ?"}')

    def on_event(self, src, seconds, sentence, loc):
        dst = self.other(src)
        translated = self.translate_sentence(src, sentence)
        src.history.append((seconds, translated))
        self.log("event", room=src.name, t=seconds, said=sentence, translated=translated)
        action = "Partner: " + translated.split(";")[0]
        tail = self.reading_line(src)
        self.send_caption(dst, action + ("\n" + tail if tail else ""))
        if sentence.startswith("grab"):
            return  # picking up is evidence, not yet a decision; the trace will show what follows
        self.read_intent(src, action, loc, "event")

    def on_trace(self, src, fresh):
        for _, seconds, sample in fresh:
            src.trace.append((seconds, self.translate_sentence(src, sample)))
            src.holding_samples = src.holding_samples + 1 if sample.startswith("holding") else 0
        src.trace = src.trace[-60:]
        # About every 2.4 s of carrying, read again: this is when wavering can be seen while it happens.
        if src.holding_samples >= 6 and not src.busy:
            holding = re.match(r"holding (\w) \[([^\]]+)\]", src.trace[-1][1])
            action = "Partner: holding note " + (f"{holding.group(1)} [{holding.group(2)}]" if holding else "?")
            self.read_intent(src, action, list(src.last_loc), "trace")

    def read_intent(self, src, action, loc, trigger):
        dst = self.other(src)
        src.reading_request += 1
        request = src.reading_request
        src.holding_samples = 0
        src.busy = True
        try:
            names, mapping = panel_names(dst.scene), self.panel_map(src)
            notes = dict(re.findall(r"note (\w) \[[^\]]+\]: (.+)", src.scene or dst.scene or ""))
            groups = {}
            for index, panel in enumerate(loc):
                letter = "ABCDEF"[index]
                groups.setdefault(names.get(mapping.get(panel, panel), str(panel)), []).append(f"{letter}: {notes.get(letter, '')}")
            latest = max([t for t, _ in src.trace[-1:]] + [t for t, _ in src.history[-1:]] + [0])
            recent = [(t, line) for t, line in src.trace if t >= latest - 12]
            prompt = (
                "EVENTS (seconds since start)\n" + ("\n".join(f"{t:6.1f}  {line}" for t, line in src.history[-20:]) or "none") +
                "\n\nTRACE (last 12 s)\n" + ("\n".join(f"{t:6.1f}  {line}" for t, line in recent) or "none") +
                "\n\nCURRENT GROUPS\n" + "\n".join(f"{name}: " + " | ".join(items) for name, items in groups.items()) +
                "\n\nYOUR PREVIOUS READING\n" + (json.dumps(src.reading, ensure_ascii=False) if src.reading else "none yet"))
            reply = ask_openai(self.INTENT_SYSTEM, prompt)
        finally:
            src.busy = False
        if not reply or request != src.reading_request:
            return
        try:
            parsed = json.loads(re.search(r"\{.*\}", reply, re.S).group(0))
            reading = {
                "state": str(parsed.get("state", "exploring")),
                "confidence": max(0.0, min(1.0, float(parsed.get("confidence", 0)))),
                "reading": str(parsed.get("reading", "")).strip(),
                "torn_between": [str(x) for x in (parsed.get("torn_between") or [])],
                "group_labels": {str(k): str(v) for k, v in (parsed.get("group_labels") or {}).items()},
                "next": str(parsed.get("next", "?")),
            }
        except Exception:
            self.log("intent_error", reply=reply[:200])
            return
        src.reading = reading
        self.log("intent", about=src.name, trigger=trigger, after=action, trace_lines=len(recent), **reading)
        ids = {name: index for index, name in names.items()}
        torn = [ids[name] for name in reading["torn_between"] if name in ids]
        about = re.search(r"note (\w)", action)
        note = "ABCDEF".index(about.group(1)) if about and about.group(1) in "ABCDEF" else -1
        self.send_caption(dst, action + "\n" + self.reading_line(src), note, torn)

    def on_trace_locked(self, src, fresh):
        with src.lock:
            self.on_trace(src, fresh)

    def on_events(self, src, fresh, loc):
        with src.lock:
            for _, seconds, sentence in fresh:
                self.on_event(src, seconds, sentence, loc)

    @staticmethod
    def reading_line(src):
        if not src.reading:
            return ""
        r = src.reading
        # Group labels and the predicted next note stay in the log; the caption has room for one sentence.
        return f"AI reading [{r['state']}, {r['confidence']:.1f}]: {r['reading']}"

    @staticmethod
    def local_addresses():
        found = set()
        try:
            found.update(socket.gethostbyname_ex(socket.gethostname())[2])
        except OSError:
            pass
        # macOS often resolves its own host name to 127.0.0.1 only. Asking the OS which address it would use
        # to reach the outside finds the Wi-Fi address without sending anything.
        for probe in ("8.8.8.8", "192.168.1.1", "10.255.255.255"):
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
                    s.connect((probe, 9))
                    found.add(s.getsockname()[0])
            except OSError:
                pass
        return sorted(a for a in found if not a.startswith("127.") and not a.startswith("169.254."))

    def beacon_loop(self):
        """Tell headsets on the Wi-Fi where the relay is. One socket per local address, so the
        broadcast leaves through every network adapter (wired and Wi-Fi), not only the default one.
        The address list is re-read every few seconds: Wi-Fi may connect after the relay started."""
        senders, known, checked = {}, None, 0.0
        while True:
            if time.time() - checked > 5:
                checked = time.time()
                current = self.local_addresses()
                if current != known:
                    known = current
                    for sender in senders.values():
                        sender.close()
                    senders = {}
                    for address in current:
                        try:
                            sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                            sender.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
                            sender.bind((address, 0))
                            senders[address] = sender
                        except OSError as error:
                            print(f"[beacon] cannot use {address}: {error}")
                    self.log("beacon", addresses=", ".join(senders) or "none")
            for address, sender in senders.items():
                # Limited broadcast plus the /24 directed broadcast: some access points pass only one of them.
                for target in ("255.255.255.255", address.rsplit(".", 1)[0] + ".255"):
                    try:
                        sender.sendto(BEACON, (target, 9001))
                    except OSError:
                        pass
            time.sleep(1)

    # ---- main loop ------------------------------------------------------------------------

    def run(self):
        print(f"Relay listening on UDP {self.args.listen}. Rooms A and B introduce themselves; both directions are forwarded.")
        print("OpenAI: " + ("enabled, model " + os.environ.get("OPENAI_MODEL", "gpt-5.4-mini")
                            if os.environ.get("OPENAI_API_KEY") else "no OPENAI_API_KEY, using built-in rules"))
        if self.args.local:
            print("Local preview: no beacon, only this PC is accepted")
        else:
            threading.Thread(target=self.beacon_loop, daemon=True).start()
        while True:
            try:
                data, sender = self.sock.recvfrom(8192)
            except ConnectionResetError:
                continue  # Windows reports "the other room is not listening yet" on the next receive
            try:
                packet = json.loads(data.decode("utf-8"))
            except ValueError:
                continue
            if packet.get("protocol") != PROTOCOL:
                continue
            kind, role = packet.get("kind"), packet.get("role")
            if role not in self.sides:
                continue
            if self.args.local and sender[0] != "127.0.0.1":
                continue  # desktop preview: ignore headsets that happen to be on the network
            src = self.sides[role]
            dst = self.other(src)
            if sender != src.addr:
                src.addr = sender  # rooms introduce themselves; no IP needs to be typed anywhere
                self.log("peer", room=role, address=f"{sender[0]}:{sender[1]}")
            if kind == "scene":
                text = packet.get("text")
                # The description changes whenever the user walks; only a change of panels is worth a log line.
                if panel_names(src.scene) != panel_names(text):
                    self.log("scene", room=role, text=text)
                src.scene = text
                if self.sides["A"].scene and self.sides["B"].scene and not self.mapping_started:
                    self.mapping_started = True
                    threading.Thread(target=self.build_mapping, daemon=True).start()
            elif kind == "trace":
                count = packet.get("ev", 0)
                if count < src.last_trace:
                    src.last_trace, src.trace = 0, []
                if count > src.last_trace:
                    lines = re.findall(r"^#(\d+) t=([\d.]+) (.+)$", packet.get("text") or "", re.M)
                    fresh = sorted((int(i), float(t), text) for i, t, text in lines if int(i) > src.last_trace)
                    src.last_trace = count
                    threading.Thread(target=self.on_trace_locked, args=(src, fresh), daemon=True).start()
            elif kind == "state":
                loc = packet.get("loc") or []
                src.last_loc = list(loc)
                event = packet.get("ev", 0)
                if event < src.last_event:  # this room restarted: new timeline
                    src.last_event, src.history, src.reading = 0, [], None
                if event > src.last_event:
                    # The packet carries the last few events; take the ones not seen yet, in order.
                    lines = re.findall(r"^#(\d+) t=([\d.]+) (.+)$", packet.get("text") or "", re.M)
                    fresh = [(int(i), float(t), text) for i, t, text in lines if int(i) > src.last_event]
                    src.last_event = event
                    threading.Thread(target=self.on_events, args=(src, sorted(fresh), list(loc)), daemon=True).start()
                if dst.addr is None:
                    continue
                mapping = self.panel_map(src)
                packet["loc"] = [mapping.get(panel, panel) for panel in loc]
                packet["hover"] = mapping.get(packet.get("hover", -1), -1)
                packet["text"] = ""  # event lines are for the relay; the other room gets captions instead
                self.sock.sendto(json.dumps(packet).encode("utf-8"), dst.addr)


if __name__ == "__main__":
    load_env()
    parser = argparse.ArgumentParser()
    parser.add_argument("--listen", type=int, default=9000, help="UDP port both rooms send to")
    parser.add_argument("--local", action="store_true", help="desktop preview on this PC only: no beacon, ignore other machines")
    parser.add_argument("--log", default=str(HERE / "relay-log.jsonl"))
    Relay(parser.parse_args()).run()
