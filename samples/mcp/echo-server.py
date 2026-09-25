#!/usr/bin/env python3
"""最小 MCP server（stdio + 换行分隔 JSON-RPC 2.0），仅用于验证 AgentFramework 的 MCP 通道。

暴露两个工具：
  - echo(text)      原样回显
  - add(a, b)       两数相加

protocol: initialize / tools/list / tools/call
"""
import json
import sys


def send(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


TOOLS = [
    {
        "name": "echo",
        "description": "原样回显传入的文本。（测试用最小工具）",
        "inputSchema": {
            "type": "object",
            "properties": {"text": {"type": "string", "description": "要回显的文本"}},
            "required": ["text"],
        },
    },
    {
        "name": "add",
        "description": "计算两个数之和。",
        "inputSchema": {
            "type": "object",
            "properties": {
                "a": {"type": "number", "description": "加数"},
                "b": {"type": "number", "description": "被加数"},
            },
            "required": ["a", "b"],
        },
    },
]


def call_tool(name, args):
    if name == "echo":
        return "echo: " + str(args.get("text", ""))
    if name == "add":
        try:
            total = float(args.get("a", 0)) + float(args.get("b", 0))
        except (TypeError, ValueError):
            return {"error": "参数不是数字"}
        return int(total) if total.is_integer() else total
    return {"error": "unknown tool: " + name}


def main():
    # 注意：这里刻意用 readline() 逐行读，而不是 `for line in sys.stdin` ——
    # 后者带 read-ahead 缓冲，在长连接（stdio 会话）里会攒着不返回，导致对端永远等不到响应。
    while True:
        line = sys.stdin.readline()
        if not line:
            break
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue

        method = msg.get("method")
        msg_id = msg.get("id")

        if method == "initialize":
            send({"jsonrpc": "2.0", "id": msg_id, "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "echo-mcp", "version": "0.1.0"},
            }})
        elif method == "notifications/initialized":
            pass  # 通知，无需响应
        elif method == "tools/list":
            send({"jsonrpc": "2.0", "id": msg_id, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            params = msg.get("params") or {}
            result = call_tool(params.get("name"), params.get("arguments") or {})
            if isinstance(result, str):
                text, is_err = result, False
            elif isinstance(result, dict) and "error" in result:
                text, is_err = str(result["error"]), True
            else:
                text, is_err = json.dumps(result, ensure_ascii=False), False
            send({"jsonrpc": "2.0", "id": msg_id, "result": {
                "content": [{"type": "text", "text": text}],
                "isError": is_err,
            }})
        else:
            if msg_id is not None:
                send({"jsonrpc": "2.0", "id": msg_id, "error": {
                    "code": -32601, "message": "method not found: " + str(method)}})


if __name__ == "__main__":
    main()
