"""Self-check of corner_proxy.py against a stand-in for the Messages API (no key, no network):

    python server/test_corner_proxy.py
"""
import json
import os
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

seen = []
answer = {"stop_reason": "end_turn", "content": [{"type": "thinking", "thinking": ""}, {"type": "text", "text": "Jab first."}]}


class Fake(BaseHTTPRequestHandler):
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["content-length"])))
        seen.append((self.headers, body))
        data = json.dumps(answer).encode()
        self.send_response(200)
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


fake = ThreadingHTTPServer(("127.0.0.1", 0), Fake)
threading.Thread(target=fake.serve_forever, daemon=True).start()
os.environ["ANTHROPIC_BASE_URL"] = f"http://127.0.0.1:{fake.server_port}"
os.environ["ANTHROPIC_API_KEY"] = "test-key"
import corner_proxy as P  # noqa: E402  (reads the base URL on import)

P.Handler.log_message = lambda *a: None
proxy = ThreadingHTTPServer(("127.0.0.1", 0), P.Handler)
threading.Thread(target=proxy.serve_forever, daemon=True).start()
url = f"http://127.0.0.1:{proxy.server_port}/corner"


def post(obj, raw=None):
    req = urllib.request.Request(url, data=raw or json.dumps(obj).encode(), method="POST", headers={"content-type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read())


code, out = post({"kind": "corner", "boxer": "MATT", "landed": 4})
assert (code, out["text"]) == (200, "Jab first."), (code, out)
h, body = seen[-1]
assert body["model"] == "claude-opus-5-5" and body["fallbacks"] == "default" and body["output_config"] == {"effort": "low"}
assert h["x-api-key"] == "test-key" and h["anthropic-version"] == "2023-06-01" and h["anthropic-beta"] == "server-side-fallback-2026-07-01"
assert json.loads(body["messages"][0]["content"])["landed"] == 4
assert post({"kind": "corner"})[0] == 429                      # under 2 s since the last
time.sleep(P.MIN_GAP_S)
answer = {"stop_reason": "refusal", "stop_details": {"category": "x"}, "content": []}
assert post({"kind": "interview"})[0] == 502                   # a refusal is the game's fallback, not a line
assert post(None, raw=b"x" * 5000)[0] == 413
assert post({"kind": "speech"})[0] == 400
print("corner proxy: all checks pass")
P.Handler.token = "s3cret"
time.sleep(P.MIN_GAP_S)
assert post({"kind": "corner"})[0] == 403
url += "?token=s3cret"
answer = {"stop_reason": "end_turn", "content": [{"type": "text", "text": "ok"}]}
assert post({"kind": "corner"}) == (200, {"text": "ok", "error": ""})
print("corner proxy: token checks pass")
