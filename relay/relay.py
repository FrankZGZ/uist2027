"""Relay between room A and room B.

A -> relay -> B. The relay is where "translation" happens:
  1. Both rooms send a text description of their own layout (kind=scene).
  2. The relay decides which panel in B stands for which panel in A (OpenAI if a key is set,
     otherwise same-id mapping) and rewrites every state packet into B's panel ids.
  3. Every action sentence from A (grab, place, move, put back) is rewritten in B's vocabulary,
     kept in a timeline, and sent to B as a caption.
  4. A also sends a raw motion trace every 0.4 s: what is held, its distance to each panel, how it
     is moving, what A is looking at. Nothing in it is labelled as hesitation.
  5. OpenAI (if available) reads events + trace + current groups + its own previous reading and
     returns a structured intent reading: after every placement, and every few seconds while A is
     still holding a note.

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

PROTOCOL = "mystery-sort-v1"
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


class Relay:
    def __init__(self, args):
        self.args = args
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("0.0.0.0", args.listen))
        self.b_addr = (args.b_host, args.b_port)
        self.a_addr = None
        self.scene = {"A": None, "B": None}
        self.mapping = {0: 0, 1: 1, 2: 2}
        self.mapping_source = "default same-id (waiting for both scene descriptions)"
        self.mapping_started = False
        self.last_event = 0
        self.history = []          # (seconds, sentence in B's vocabulary), whole session
        self.reading = None        # last structured intent reading from the model
        self.reading_request = 0   # only the newest request may publish its answer
        self.trace = []            # (seconds, raw motion sample in B's vocabulary)
        self.last_trace = 0
        self.holding_samples = 0   # samples with a note in hand since the last model call
        self.last_loc = [0] * 6
        self.busy = False
        self.forwarded = 0
        self.lock = threading.Lock()
        self.event_lock = threading.Lock()
        self.log_path = Path(args.log)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_path.write_text("", encoding="utf-8")

    def log(self, kind, **fields):
        record = {"t": round(time.time(), 3), "kind": kind, **fields}
        with self.lock:
            with self.log_path.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        print(f"[{kind}] " + ", ".join(f"{k}={v}" for k, v in fields.items()))

    def send_caption(self, text, note=-1, torn=None):
        # note >= 0 marks this caption as an intent reading about that note; torn = B panel ids.
        packet = {"protocol": PROTOCOL, "kind": "caption", "role": "relay", "text": text, "focus": note, "torn": torn or []}
        self.sock.sendto(json.dumps(packet).encode("utf-8"), self.b_addr)
        self.log("caption", text=text)

    # ---- panel mapping -------------------------------------------------------------------

    def build_mapping(self):
        reply = ask_openai(
            "You align two differently arranged rooms used for the same note-sorting task. "
            "Each room has one panel of unsorted clues and group panels. Map every panel id in room A "
            "to the panel id in room B that plays the same role for a person standing in that room "
            "(same function first, then same side relative to the viewer). "
            'Answer with JSON only, e.g. {"0": 0, "1": 1, "2": 2}. Every B id must be used exactly once.',
            "ROOM A\n" + self.scene["A"] + "\nROOM B\n" + self.scene["B"])
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
        self.mapping, self.mapping_source = mapping, source
        a, b = panel_names(self.scene["A"]), panel_names(self.scene["B"])
        self.log("mapping", source=source,
                 pairs="; ".join(f"{a.get(k, k)} -> {b.get(v, v)}" for k, v in sorted(mapping.items())))

    # ---- events ----------------------------------------------------------------------------

    def translate_sentence(self, sentence):
        a, b = panel_names(self.scene["A"]), panel_names(self.scene["B"])
        for a_id, a_name in a.items():
            sentence = sentence.replace(a_name, b.get(self.mapping.get(a_id, a_id), a_name))
        return sentence

    INTENT_SYSTEM = (
        "You observe a person (the partner) sorting murder-mystery clue notes into groups in another room, "
        "and you explain their thinking to a collaborator who can only watch. You receive: EVENTS, the discrete "
        "actions of the whole session; TRACE, raw samples of the last seconds taken every 0.4 s (what the partner "
        "holds, the distance from that note to every panel, its motion, or what they look at with an empty hand); "
        "the CURRENT GROUPS; and YOUR PREVIOUS READING. Nobody has interpreted the trace for you. Read the "
        "process yourself: approaching a panel and backing off, pausing, going back and forth between panels, "
        "or looking at several notes before taking one all mean something. Distances are in the partner's own room, "
        "where panels sit about 0.8 m apart; panel names are the collaborator's names for the matching panels. "
        "Update your previous reading instead of starting over, and say so when the evidence is too thin. "
        "Reply with JSON only: "
        '{"state": "exploring" | "deciding" | "confident" | "revising", '
        '"confidence": number 0-1, '
        '"reading": "one sentence, max 22 words: what the partner is trying to do right now, citing what in the process shows it", '
        '"torn_between": [panel names the partner is undecided between right now. Fill it only when the trace shows '
        'the held note reversing direction between panels or stalling between them; one direct move to a panel is NOT torn. Empty otherwise], '
        '"group_labels": {"<group panel name>": "2-4 word label, or ? if unclear"}, '
        '"next": "note letter the partner will most likely handle next, or ?"}')

    def on_event(self, seconds, sentence, loc):
        translated = self.translate_sentence(sentence)
        self.history.append((seconds, translated))
        self.log("event", t_a=seconds, room_a=sentence, room_b=translated)
        action = "Partner: " + translated.split(";")[0]
        tail = self.reading_line()
        self.send_caption(action + ("\n" + tail if tail else ""))
        if sentence.startswith("grab"):
            return  # picking up is evidence, not yet a decision; the trace will show what follows
        self.read_intent(action, loc, "event")

    def on_trace(self, fresh):
        for _, seconds, sample in fresh:
            self.trace.append((seconds, self.translate_sentence(sample)))
            self.holding_samples = self.holding_samples + 1 if sample.startswith("holding") else 0
        self.trace = self.trace[-60:]
        # About every 2.4 s of carrying, read again: this is when wavering can be seen while it happens.
        if self.holding_samples >= 6 and not self.busy:
            holding = re.match(r"holding (\w) \[([^\]]+)\]", self.trace[-1][1])
            action = "Partner: holding note " + (f"{holding.group(1)} [{holding.group(2)}]" if holding else "?")
            self.read_intent(action, list(self.last_loc), "trace")

    def read_intent(self, action, loc, trigger):
        self.reading_request += 1
        request = self.reading_request
        self.holding_samples = 0
        self.busy = True
        try:
            b = panel_names(self.scene["B"])
            notes = dict(re.findall(r"note (\w) \[[^\]]+\]: (.+)", self.scene["A"] or ""))
            groups = {}
            for index, panel in enumerate(loc):
                letter = "ABCDEF"[index]
                groups.setdefault(b.get(self.mapping.get(panel, panel), str(panel)), []).append(f"{letter}: {notes.get(letter, '')}")
            latest = max([t for t, _ in self.trace[-1:]] + [t for t, _ in self.history[-1:]] + [0])
            recent = [(t, line) for t, line in self.trace if t >= latest - 12]
            prompt = (
                "EVENTS (seconds since start)\n" + ("\n".join(f"{t:6.1f}  {line}" for t, line in self.history[-20:]) or "none") +
                "\n\nTRACE (last 12 s)\n" + ("\n".join(f"{t:6.1f}  {line}" for t, line in recent) or "none") +
                "\n\nCURRENT GROUPS\n" + "\n".join(f"{name}: " + " | ".join(items) for name, items in groups.items()) +
                "\n\nYOUR PREVIOUS READING\n" + (json.dumps(self.reading, ensure_ascii=False) if self.reading else "none yet"))
            reply = ask_openai(self.INTENT_SYSTEM, prompt)
        finally:
            self.busy = False
        if not reply or request != self.reading_request:
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
        self.reading = reading
        self.log("intent", trigger=trigger, after=action, trace_lines=len(recent), **reading)
        ids = {name: index for index, name in panel_names(self.scene["B"]).items()}
        torn = [ids[name] for name in reading["torn_between"] if name in ids]
        about = re.search(r"note (\w)", action)
        note = "ABCDEF".index(about.group(1)) if about and about.group(1) in "ABCDEF" else -1
        self.send_caption(action + "\n" + self.reading_line(), note, torn)

    def on_trace_locked(self, fresh):
        with self.event_lock:
            self.on_trace(fresh)

    def on_events(self, fresh, loc):
        with self.event_lock:
            for _, seconds, sentence in fresh:
                self.on_event(seconds, sentence, loc)

    def reading_line(self):
        if not self.reading:
            return ""
        r = self.reading
        # Group labels and the predicted next note stay in the log; the wall caption has room for one sentence.
        return f"AI reading [{r['state']}, {r['confidence']:.1f}]: {r['reading']}"

    @staticmethod
    def local_addresses():
        found = set()
        try:
            found.update(socket.gethostbyname_ex(socket.gethostname())[2])
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
        print(f"Relay listening on UDP {self.args.listen}, forwarding to B at {self.b_addr[0]}:{self.b_addr[1]}")
        print("OpenAI: " + ("enabled, model " + os.environ.get("OPENAI_MODEL", "gpt-5.4-mini")
                            if os.environ.get("OPENAI_API_KEY") else "no OPENAI_API_KEY, using built-in rules"))
        threading.Thread(target=self.beacon_loop, daemon=True).start()
        while True:
            try:
                data, sender = self.sock.recvfrom(8192)
            except ConnectionResetError:
                continue  # Windows reports "B is not listening yet" on the next receive
            try:
                packet = json.loads(data.decode("utf-8"))
            except ValueError:
                continue
            if packet.get("protocol") != PROTOCOL:
                continue
            kind, role = packet.get("kind"), packet.get("role")
            if role == "B" and sender != self.b_addr:
                self.b_addr = sender  # room B introduces itself; no IP needs to be typed anywhere
                self.log("peer", room="B", address=f"{sender[0]}:{sender[1]}")
            if role == "A" and sender != self.a_addr:
                self.a_addr = sender
                self.log("peer", room="A", address=f"{sender[0]}:{sender[1]}")
            if kind == "scene" and role in self.scene:
                if self.scene[role] != packet.get("text"):
                    self.scene[role] = packet.get("text")
                    self.log("scene", room=role, text=self.scene[role])
                if self.scene["A"] and self.scene["B"] and not self.mapping_started:
                    self.mapping_started = True
                    threading.Thread(target=self.build_mapping, daemon=True).start()
            elif kind == "trace" and role == "A":
                count = packet.get("ev", 0)
                if count < self.last_trace:
                    self.last_trace, self.trace = 0, []
                if count > self.last_trace:
                    lines = re.findall(r"^#(\d+) t=([\d.]+) (.+)$", packet.get("text") or "", re.M)
                    fresh = sorted((int(i), float(t), text) for i, t, text in lines if int(i) > self.last_trace)
                    self.last_trace = count
                    threading.Thread(target=self.on_trace_locked, args=(fresh,), daemon=True).start()
            elif kind == "state" and role == "A":
                loc = packet.get("loc") or []
                self.last_loc = list(loc)
                event = packet.get("ev", 0)
                if event < self.last_event:  # A restarted: new session, new timeline
                    self.last_event, self.history, self.reading = 0, [], None
                if event > self.last_event:
                    # The packet carries the last few events; take the ones not seen yet, in order.
                    lines = re.findall(r"^#(\d+) t=([\d.]+) (.+)$", packet.get("text") or "", re.M)
                    fresh = [(int(i), float(t), text) for i, t, text in lines if int(i) > self.last_event]
                    self.last_event = event
                    threading.Thread(target=self.on_events, args=(sorted(fresh), list(loc)), daemon=True).start()
                mapping = self.mapping
                packet["loc"] = [mapping.get(panel, panel) for panel in loc]
                packet["hover"] = mapping.get(packet.get("hover", -1), -1)
                self.sock.sendto(json.dumps(packet).encode("utf-8"), self.b_addr)
                self.forwarded += 1


if __name__ == "__main__":
    load_env()
    parser = argparse.ArgumentParser()
    parser.add_argument("--listen", type=int, default=9000, help="UDP port room A sends to")
    parser.add_argument("--b-host", default="127.0.0.1")
    parser.add_argument("--b-port", type=int, default=9002, help="UDP port room B listens on")
    parser.add_argument("--log", default=str(HERE / "relay-log.jsonl"))
    Relay(parser.parse_args()).run()
