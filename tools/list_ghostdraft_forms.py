"""List every form fact-docgen routes to GhostDraft and match it to a legacy Documaker FAP.

Sources:
  fact-docgen rules  src/MoE.Commercial.Documents.Generation.Core/Rules/**/*.json (formSystem == GhostDraft)
  FORM.DAT           FaCT-DocProd-Development/mstrres/MOEC0/DEFLIB/FORM.DAT  (;GROUP;LOB;CODE EDITION;desc;..;FAP|opts/FAP|opts;)
  FAPs               FaCT-DocProd-Development/mstrres/MOEC0/FORMS
Output: ghostdraft-forms.csv next to this script's parent folder.
"""
import csv
import json
import os
import re
from collections import defaultdict

RULES = r"C:\src\fact-docgen\src\MoE.Commercial.Documents.Generation.Core\Rules"
FORM_DAT = r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DEFLIB\FORM.DAT"
FORMS_DIR = r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS"
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "ghostdraft-forms.csv")


def norm(s):
    return re.sub(r"\s+", "", s or "").upper()


# ---- GhostDraft forms (deduplicated by package + GhostDraft form name) ----
forms = {}
for root, _, files in os.walk(RULES):
    for f in files:
        if not f.lower().endswith(".json"):
            continue
        with open(os.path.join(root, f), encoding="utf-8-sig") as fh:
            r = json.load(fh)
        if r.get("formSystem") != "GhostDraft":
            continue
        key = (r.get("ghostDraftPackageName"), r.get("ghostDraftFormName"))
        e = forms.setdefault(key, {
            "Package": r.get("ghostDraftPackageName"),
            "PackageVersion": set(),
            "GhostDraftForm": r.get("ghostDraftFormName"),
            "FormCode": r.get("formCode"),
            "FormName": r.get("formName"),
            "LOBs": set(),
            "Enabled": set(),
            "Static": set(),
            "Interactive": set(),
            "Recipients": set(),
            "Rules": 0,
        })
        e["PackageVersion"].add(str(r.get("ghostDraftPackageVersion")))
        e["LOBs"].add(r.get("lineOfBusiness"))
        e["Enabled"].add(bool(r.get("enabled")))
        e["Static"].add(bool(r.get("isStatic")))
        e["Interactive"].add(bool(r.get("isInteractive")))
        e["Recipients"].update(r.get("recipients") or [])
        e["Rules"] += 1

# ---- FORM.DAT: (code, edition) -> FAP names ----
with open(FORM_DAT, encoding="latin-1") as fh:
    text = re.sub(r"\\\r?\n", "", fh.read())  # join continuation lines
by_code_ed = defaultdict(set)
by_code = defaultdict(set)
for line in text.splitlines():
    if not line.startswith(";"):
        continue
    parts = line.split(";")
    if len(parts) < 8:
        continue
    name = parts[3].split()
    if not name:
        continue
    code = norm(name[0])
    ed = name[1] if len(name) > 1 and re.fullmatch(r"\d{4}", name[1]) else ""
    faps = [p.split("|")[0].strip() for p in parts[7].split("/") if p.strip()]
    by_code_ed[(code, ed)].update(faps)
    if ed:
        by_code[code].add(ed)

fap_files = {os.path.splitext(n)[0].upper() for n in os.listdir(FORMS_DIR) if n.lower().endswith(".fap")}

rows = []
for e in forms.values():
    code = norm(e["FormCode"])
    m = re.search(r"(\d{4})\s*$", e["FormName"] or "")
    ed = m.group(1) if m else ""
    faps = sorted(by_code_ed.get((code, ed), set()))
    if faps:
        match = "Same edition"
    elif by_code.get(code):
        match = "Other edition only"
        faps = sorted(set().union(*(by_code_ed[(code, x)] for x in by_code[code])))
    else:
        # State-variant dec pages are stored with an 'x' placeholder: DA0093 -> DAx093P1A.FAP
        variant = code[:2] + "X" + code[3:] if len(code) > 3 else code
        guess = sorted(n for n in fap_files if n.startswith(code) or n.startswith(variant))
        match = "FAP by name only" if guess else "None (GhostDraft only)"
        faps = guess
    rows.append({
        "LOB": ",".join(sorted(x for x in e["LOBs"] if x)),
        "Package": e["Package"],
        "PackageVersion": ",".join(sorted(e["PackageVersion"])),
        "GhostDraftForm": e["GhostDraftForm"],
        "FormCode": e["FormCode"],
        "Edition": ed,
        "Enabled": "/".join(sorted({str(x) for x in e["Enabled"]})),
        "Static": "/".join(sorted({str(x) for x in e["Static"]})),
        "Recipients": ",".join(sorted(e["Recipients"])),
        "RuleFiles": e["Rules"],
        "LegacyMatch": match,
        "LegacyEditions": ",".join(sorted(by_code.get(code, set()))),
        "LegacyFAPs": " ".join(faps),
        "FAPsOnDisk": sum(1 for x in faps if x.upper() in fap_files),
    })

rows.sort(key=lambda r: (r["Package"] or "", r["GhostDraftForm"] or ""))
with open(OUT, "w", newline="", encoding="utf-8") as fh:
    w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
    w.writeheader()
    w.writerows(rows)

print(f"wrote {len(rows)} forms -> {OUT}")
summary = defaultdict(int)
for r in rows:
    summary[r["LegacyMatch"]] += 1
for k, v in sorted(summary.items()):
    print(f"  {k}: {v}")
by_pkg = defaultdict(lambda: defaultdict(int))
for r in rows:
    by_pkg[r["Package"]][r["LegacyMatch"]] += 1
for p, d in sorted(by_pkg.items()):
    print(f"  {p}: " + ", ".join(f"{k}={v}" for k, v in sorted(d.items())))
