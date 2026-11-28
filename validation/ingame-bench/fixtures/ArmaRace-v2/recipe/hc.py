"""Minimal stdio MCP client for the installed housecarl-mcp.exe.
usage: python hc.py tools OUT.json
       python hc.py run CALLS.json OUT.json     (CALLS = [{"tool":..., "args":{...}}, ...])
"""
import json, os, subprocess, sys
EXE = os.environ.get("HC_EXE") or os.path.join(os.path.expanduser("~"), ".claude", "skills", "housecarl", "server", "housecarl-mcp.exe")
HERE = os.path.dirname(os.path.abspath(__file__))
INSTANCE = os.environ.get("HC_INSTANCE", r"X:\Skyrim Conflict Lab")

def rpc(p, i, method, params):
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": i, "method": method, "params": params}) + "\n"); p.stdin.flush()
    while True:
        line = p.stdout.readline()
        if not line: raise SystemExit(f"server closed stdout waiting for id {i}")
        line = line.strip()
        if not line: continue
        try: msg = json.loads(line)
        except json.JSONDecodeError: continue
        if msg.get("id") == i: return msg

def main():
    env = dict(os.environ, HOUSECARL_DATA_DIR=os.path.join(HERE, "hcdata"), HouseCarl__Mo2InstanceDir=INSTANCE)
    p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env, text=True, encoding="utf-8")
    try:
        rpc(p, 1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "npc2bench-fixture", "version": "0"}})
        p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); p.stdin.flush()
        if sys.argv[1] == "tools":
            json.dump(rpc(p, 2, "tools/list", {}), open(sys.argv[2], "w", encoding="utf-8"), indent=1)
        else:
            calls = json.load(open(sys.argv[2], encoding="utf-8")); out = []
            for n, c in enumerate(calls):
                r = rpc(p, 10 + n, "tools/call", {"name": c["tool"], "arguments": c.get("args", {})})
                out.append({"tool": c["tool"], "result": r.get("result"), "error": r.get("error")})
                res = r.get("result") or {}
                if r.get("error") or res.get("isError"):
                    if not c.get("continue"): break
            json.dump(out, open(sys.argv[3], "w", encoding="utf-8"), indent=1)
    finally:
        try: p.stdin.close()
        except Exception: pass
        try: p.wait(timeout=20)
        except Exception: p.kill()
main()
