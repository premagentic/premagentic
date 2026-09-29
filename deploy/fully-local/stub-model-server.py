#!/usr/bin/env python3
"""A stand-in for a local model server, for proving the fully local loop.

    python3 stub-model-server.py [port]        (default 11434, loopback only)

It is NOT a model. It answers the chat completions request shape that local
model servers commonly offer (POST /v1/chat/completions) by naming the
passages it was handed and quoting the first, so a test can tell that the
passages PremAgentic returned reached the model and came back in its answer.
It generates nothing and knows nothing. It exists so that the offline proof in
scripts/clean-install/run.sh can run the whole loop, a client asking
PremAgentic and then a model, where there is no network and no model to
download.

The standard library only. It listens on 127.0.0.1 and opens no connection of
its own. PREM_STUB_REACH_OUT=1 makes it try one connection out at startup,
which is how the proof's control shows the proof fails when the model server
reaches for the network.
"""
import json
import os
import socket
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 11434
MARK = "--- ["


def passages(text):
    """The passage blocks of a message, as PremAgentic's search tool writes them."""
    blocks = []
    for part in text.split(MARK)[1:]:
        head, _, body = part.partition("\n")
        citation = head.split("] ", 1)[1] if "] " in head else head
        lines = [line for line in body.splitlines() if line.strip()]
        # The first line after the citation is the fields line; the text follows it.
        blocks.append((citation.strip(), lines[1] if len(lines) > 1 else ""))
    return blocks


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/v1/models":
            self.reply({"object": "list", "data": [{"id": "stub", "object": "model", "owned_by": "nobody"}]})
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path != "/v1/chat/completions":
            self.send_error(404)
            return
        request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        text = "\n".join(m.get("content", "") for m in request.get("messages", []) if isinstance(m.get("content"), str))
        found = passages(text)
        if found:
            answer = "From " + "; ".join(c for c, _ in found) + ". " + found[0][1]
        else:
            answer = "No passage was given, so there is nothing to answer from."
        self.reply({
            "id": "stub-1", "object": "chat.completion", "model": request.get("model", "stub"),
            "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": answer}}],
        })

    def reply(self, body):
        data = json.dumps(body).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


if os.environ.get("PREM_STUB_REACH_OUT") == "1":
    try:
        socket.create_connection(("1.1.1.1", 443), timeout=3).close()
    except OSError:
        pass

HTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
