"""A tiny OpenAI-compatible upstream for load and chaos tests (standard library only).

Answers POST /v1/chat/completions, streaming or not, after LATENCY_MS to imitate a real provider.
"""
import json
import os
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LATENCY = int(os.environ.get("LATENCY_MS", "50")) / 1000
WORDS = "This is a simulated answer from the mock upstream provider.".split()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_GET(self):
        self._send(200, b'{"status":"ok"}')

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        time.sleep(LATENCY)
        model = body.get("model", "mock")
        usage = {"prompt_tokens": 20, "completion_tokens": len(WORDS), "total_tokens": 20 + len(WORDS)}
        cid = "chatcmpl-" + uuid.uuid4().hex[:12]

        if not body.get("stream"):
            answer = {
                "id": cid, "object": "chat.completion", "model": model,
                "choices": [{"index": 0, "message": {"role": "assistant", "content": " ".join(WORDS)}, "finish_reason": "stop"}],
                "usage": usage,
            }
            self._send(200, json.dumps(answer).encode())
            return

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        for i, word in enumerate(WORDS):
            chunk = {"id": cid, "object": "chat.completion.chunk", "model": model,
                     "choices": [{"index": 0, "delta": {"content": (" " if i else "") + word}, "finish_reason": None}]}
            self._chunk(f"data: {json.dumps(chunk)}\n\n")
        if (body.get("stream_options") or {}).get("include_usage"):
            self._chunk(f"data: {json.dumps({'id': cid, 'object': 'chat.completion.chunk', 'choices': [], 'usage': usage})}\n\n")
        self._chunk("data: [DONE]\n\n")
        self.wfile.write(b"0\r\n\r\n")

    def _chunk(self, text):
        data = text.encode()
        self.wfile.write(f"{len(data):x}\r\n".encode() + data + b"\r\n")
        self.wfile.flush()

    def _send(self, status, payload):
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


if __name__ == "__main__":
    ThreadingHTTPServer.daemon_threads = True
    ThreadingHTTPServer(("0.0.0.0", 8000), Handler).serve_forever()
