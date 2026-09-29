#!/usr/bin/env python3
"""The fully local loop, as an MCP-capable chat client runs it: ask PremAgentic,
then hand what it returned to a local model and print the answer.

    python3 loop-client.py <question> --mcp https://host:8443/mcp --ca https.crt
                           --token-file agent.token --model http://127.0.0.1:11434
                           [--any-answer]

1. Calls PremAgentic's search_knowledge tool over MCP with the agent's token,
   read from a file, never from an argument.
2. Sends the cited passages, with the question, to the model server's chat
   completions endpoint.
3. Prints the model's answer, and exits 1 if either step fails or the answer
   fails its check.

The check is the stand-in's by default: the answer names a passage, which the
stand-in always does. With --any-answer, for a real model, whose answer need
not name a passage path, the check is that the request carried at least one
passage and the answer is not empty.

A real client does the same with a real model; this one exists for the offline
proof in scripts/clean-install/run.sh. The standard library only.
"""
import argparse
import json
import ssl
import sys
import urllib.request


def arguments():
    parser = argparse.ArgumentParser()
    parser.add_argument("question")
    parser.add_argument("--mcp", required=True)
    parser.add_argument("--ca", required=True)
    parser.add_argument("--token-file", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--any-answer", action="store_true",
                        help="a real model: the request carried a passage and the answer is not empty")
    return parser.parse_args()


# How PremAgentic's search tool opens each passage it returns.
PASSAGE_MARK = "--- ["


def answered(answer, evidence, any_answer):
    """Whether the answer passes its check: the stand-in's, or with any_answer a real model's."""
    if any_answer:
        return PASSAGE_MARK in evidence and answer.strip() != ""
    return answer.startswith("From ")


def search(args):
    token = open(args.token_file, encoding="utf-8").read().strip()
    # A stateless call: the protocol version and the client's capabilities
    # travel with it, since there is no session to have agreed them in.
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                       "params": {"name": "search_knowledge", "arguments": {"query": args.question, "topK": 5},
                                  "_meta": {"io.modelcontextprotocol/protocolVersion": "2026-07-28",
                                            "io.modelcontextprotocol/clientCapabilities": {}}}}).encode()
    request = urllib.request.Request(args.mcp, data=body, method="POST", headers={
        "Authorization": f"Bearer {token}", "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": "2026-07-28", "Mcp-Method": "tools/call", "Mcp-Name": "search_knowledge"})
    with urllib.request.urlopen(request, context=ssl.create_default_context(cafile=args.ca), timeout=30) as response:
        raw = response.read().decode("utf-8")
    # The answer comes as one JSON object, or as server-sent events whose data lines hold it.
    payload = next((line[5:].strip() for line in raw.splitlines() if line.startswith("data:")), raw)
    result = json.loads(payload)["result"]
    return "\n".join(part.get("text", "") for part in result.get("content", []))


def ask(args, evidence):
    # A short answer is enough to prove the loop, and bounds how long a small
    # model on a CPU spends writing it.
    body = json.dumps({"model": "local", "max_tokens": 256, "messages": [
        {"role": "system", "content": "Answer only from the passages below, and name the ones you used."},
        {"role": "user", "content": f"{args.question}\n\n{evidence}"},
    ]}).encode()
    request = urllib.request.Request(args.model.rstrip("/") + "/v1/chat/completions", data=body, method="POST",
                                     headers={"Content-Type": "application/json"})
    # A real model reads the passages before it writes a word, which on a
    # busy machine can take far longer than the stand-in's answer.
    with urllib.request.urlopen(request, timeout=180) as response:
        return json.loads(response.read())["choices"][0]["message"]["content"]


def main():
    args = arguments()
    evidence = search(args)
    answer = ask(args, evidence)
    print(answer)
    return 0 if answered(answer, evidence, args.any_answer) else 1


if __name__ == "__main__":
    sys.exit(main())
