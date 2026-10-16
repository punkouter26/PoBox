"""The cornerman's proxy: holds the Claude API key so the game never ships one.

    set ANTHROPIC_API_KEY=sk-ant-...
    python server/corner_proxy.py                        on this PC (the editor)
    python server/corner_proxy.py --host 0.0.0.0 --token SECRET     for a phone on the same Wi-Fi (CORNER: http://PC:8787/corner?token=SECRET)

The game (Broadcast/CornerAI.cs) POSTs one fight's numbers to /corner as JSON:
    {"kind": "corner" | "interview", "boxer": "MATT", "opponent": "ZOMBIE", "round": 2, ...numbers...}
and gets back {"text": "..."}: one short line, the corner's advice between rounds, or the winner's word after the
bell. Every number in it is the game's; the prompt tells the model to use only those. Any failure (no key, the API
down, a refusal, too slow) is a 502 with an empty text, and the game says its own rule-based tip instead.

Standard library only (this machine's Pythons have no pip), so the Messages API is called over plain HTTP.
Claude Opus 5.5 at low effort: a one-line answer does not need more, and the round break is short.
Refusals fall back server-side (fallbacks "default"). A request bigger than 4 KB, a wrong --token, or more than one
request every 2 seconds is turned away, so a proxy left open on a network cannot be used to spend the key.
"""
from __future__ import annotations

import argparse
import hmac
import json
import os
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

API = os.environ.get("ANTHROPIC_BASE_URL", "https://api.anthropic.com").rstrip("/") + "/v1/messages"
MODEL = "claude-opus-5-5"
MAX_BODY = 4096
MIN_GAP_S = 2.0

SYSTEM = {
    "corner": (
        "You are the cornerman of a boxer in a physics-simulated boxing broadcast, talking to your boxer on the stool "
        "between rounds. You get the round's numbers as JSON. Say ONE thing to your boxer, at most 22 words, in the "
        "voice of a gruff boxing trainer: the single most useful change for the next round, backed by one of the "
        "numbers. Use only numbers that are in the JSON; never invent one. No preamble, no quotes, no stage directions."
    ),
    "interview": (
        "You are the winning boxer of a physics-simulated boxing broadcast, giving the ringside interviewer one line "
        "right after the decision. You get the fight's numbers as JSON. At most 24 words, confident and a little funny, "
        "naming one real number from the JSON about how you won. Use only numbers that are in the JSON; never invent "
        "one. No preamble, no quotes, no stage directions."
    ),
}

_lock = threading.Lock()
_last = [0.0]


def ask(kind: str, facts: dict, timeout_s: float) -> str:
    key = os.environ.get("ANTHROPIC_API_KEY", "")
    if not key:
        raise RuntimeError("ANTHROPIC_API_KEY is not set")
    body = {
        "model": MODEL,
        "max_tokens": 2000,                       # thinking is always on for this model and counts towards it
        "output_config": {"effort": "low"},
        "fallbacks": "default",
        "system": SYSTEM[kind],
        "messages": [{"role": "user", "content": json.dumps(facts, sort_keys=True)}],
    }
    req = urllib.request.Request(API, data=json.dumps(body).encode("utf-8"), method="POST", headers={
        "content-type": "application/json",
        "x-api-key": key,
        "anthropic-version": "2023-06-01",
        "anthropic-beta": "server-side-fallback-2026-07-01",
    })
    with urllib.request.urlopen(req, timeout=timeout_s) as r:
        msg = json.loads(r.read().decode("utf-8"))
    if msg.get("stop_reason") == "refusal":
        raise RuntimeError(f"refused: {(msg.get('stop_details') or {}).get('category')}")
    text = " ".join(b.get("text", "") for b in msg.get("content", []) if b.get("type") == "text").strip()
    if not text:
        raise RuntimeError(f"no text (stop_reason {msg.get('stop_reason')})")
    return text.strip('"')


class Handler(BaseHTTPRequestHandler):
    token = ""
    timeout_s = 8.0

    def reply(self, code: int, text: str = "", error: str = "") -> None:
        data = json.dumps({"text": text, "error": error}).encode("utf-8")
        self.send_response(code)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self) -> None:  # noqa: N802 (http.server's name)
        url = urllib.parse.urlsplit(self.path)
        if url.path != "/corner":
            return self.reply(404, error="not found")
        given = self.headers.get("x-pobox-token", "") or urllib.parse.parse_qs(url.query).get("token", [""])[0]
        if self.token and not hmac.compare_digest(given, self.token):
            return self.reply(403, error="bad token")
        n = int(self.headers.get("content-length") or 0)
        if n <= 0 or n > MAX_BODY:
            return self.reply(413, error="body too big or empty")
        try:
            facts = json.loads(self.rfile.read(n).decode("utf-8"))
            kind = facts.get("kind", "corner")
            if not isinstance(facts, dict) or kind not in SYSTEM:
                raise ValueError("kind must be corner or interview")
        except (ValueError, AttributeError) as e:
            return self.reply(400, error=str(e))
        with _lock:
            if time.time() - _last[0] < MIN_GAP_S:
                return self.reply(429, error="too soon")
            _last[0] = time.time()
        try:
            text = ask(kind, facts, self.timeout_s)
        except (OSError, RuntimeError, ValueError, urllib.error.URLError) as e:
            self.log_message("upstream: %s", e)
            return self.reply(502, error=str(e)[:200])
        self.log_message("%s %s: %s", kind, facts.get("boxer", "?"), text)
        self.reply(200, text=text)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8787)
    ap.add_argument("--token", default="", help="the game must send it: the settings' CORNER address ending ?token=...")
    ap.add_argument("--timeout", type=float, default=8.0)
    args = ap.parse_args()
    Handler.token, Handler.timeout_s = args.token, args.timeout
    if args.host != "127.0.0.1" and not args.token:
        print("warning: open on the network with no --token: anyone who can reach it can spend the key")
    print(f"corner proxy on http://{args.host}:{args.port}/corner ({MODEL}); key {'set' if os.environ.get('ANTHROPIC_API_KEY') else 'NOT SET'}")
    ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
