# يرجّع حسابات قديمة من ملف البيانات القديم (work\harmoni\profiles.json) ويصلّح قائمة «أتابعهم».
import json, os, shutil, sys
D = os.path.join(os.environ["LOCALAPPDATA"], "HarmoniData", "profiles.json")
OLD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "harmoni", "profiles.json")
ME = "un9xrsb86t1gmt1u7gai"
BRING = ("ukwsz098gynmt1um9x8", "u097d8c6d83044be", "ud53e32493b1d4e6")
REMAP = {"ufb541c5e512b437": "uf441efba7fad4ee"}   # md5n سوّا حساب جديد
cur = json.load(open(D, encoding="utf-8-sig"))
old = json.load(open(OLD, encoding="utf-8-sig"))
taken = {(p.get("username") or "").lower() for p in cur.values()}
for u in BRING:
    if u not in cur and u in old and (old[u].get("username") or "").lower() not in taken:
        cur[u] = old[u]
        print("restored:", old[u].get("name"), "@" + str(old[u].get("username")))
me = cur.get(ME)
if me:
    out = []
    for x in me.get("following") or []:
        x = REMAP.get(x, x)
        if x in cur and x not in out and x != ME:
            out.append(x)
    me["following"] = out
    print("following now:", len(out))
shutil.copy2(D, D.replace("profiles.json", "profiles.before-restore.json"))
data = json.dumps(cur, ensure_ascii=False)
for target in (D + ".bak", D):
    with open(target, "w", encoding="utf-8") as f:
        f.write(data)
print("done")
