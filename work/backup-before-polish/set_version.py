"""يكتب version.json قبل البناء: set_version.py <version> [owner/repo]
لو ما انعطى repo نخلي الموجود."""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
path = os.path.join(HERE, "version.json")
try:
    with open(path, encoding="utf-8") as f:
        data = json.load(f) or {}
except Exception:
    data = {}
version = sys.argv[1] if len(sys.argv) > 1 else data.get("version", "0")
if not re.match(r"^\d+(\.\d+){1,5}$", version):
    sys.exit("bad version: " + version)
data["version"] = version
if len(sys.argv) > 2 and sys.argv[2].strip():
    data["repo"] = sys.argv[2].strip()
data.setdefault("repo", "")
with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f)
print("version.json ->", data["version"], data["repo"] or "(no repo)")
