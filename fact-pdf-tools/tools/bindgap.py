"""How far does the EXISTING model reach across the FAP library, and what should it grow next?

There is no need to invent a binding model. Three layers already exist and are in production:

    DB2  --(fact-commercial-api: SELECT BYBRNB AS LocationNumber FROM {0}.ASBYCPP)-->  CDM
    CDM  --(fact-docgen: 61 ISectionBuilders, e.g. agency.FullName -> <Agency><Name>)-->  Server XML
    Server XML --(GhostDraft templates)-->  Form

The GhostDraft Server XML element paths ARE the logical binding namespace FORM-STUDIO-PLAN
section 3 asks for ("never store a CDM path in a form"), and the section builders ARE the
CdmDataSource adapter. So a Form Studio field should bind to a Server XML path, and the only
open question is COVERAGE: the model was built for three packages, and the FAP library is 4,478
forms.

This joins demand to supply:

    FAP field -> DDT rule -> DB2 column -> hydration alias -> CDM name -> Server XML element
      POLNUM     concat      PMSP0200.SYMBOL   "AS PolicySymbol"    PolicySymbol   <PolicyNumber>

and reports, per form, how much of it the current model can already feed -- plus a backlog of
the attributes that block the most forms.

READ THIS BEFORE QUOTING THE COVERAGE NUMBER. It is a FLOOR, not a measurement.

The chain has more naming layers than it looks:

    DB2 column -> query-result property -> CDM property -> XML element

and each is renamed by hand. `POLICY0NUM` becomes `POLICYNO` on the query result and `Number`
on `Policy`; nothing in the source text links the last pair. Coverage was measured at 1%, then
5%, then 20% as the join was corrected three times -- first reading CDM properties out of
builder expressions instead of the CDM types, then reading DB2->CDM out of SQL `AS` aliases
instead of the 148 mapper classes that actually do it. Each correction was a real improvement
and each previous number was an artefact. Assume this one is too.

What IS trustworthy here:

  * The DEMAND side is exact. It comes from the DDT, which declares a source for 99.7% of field
    records, and from DB2 schema lookups for the table/column semantics.
  * The BACKLOG RANKING is therefore trustworthy, because it depends only on demand: how many
    forms need a given attribute is a fact about the FAP library, not about the join.
  * The per-form percentage is a lower bound. Use it to sort, not to report progress.

To turn the floor into a measurement, resolve the last hop from data rather than source text:
call the API for a real policy, walk the returned CDM JSON, and match on VALUES rather than
names. That is the honest next step and it is not done here.

Usage:  python tools/bindgap.py [--top N]
Writes output/bindgap-attributes.csv and output/bindgap-forms.csv
"""
import collections
import csv
import glob
import os
import re
import sys

DDT = r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\DDTLIB"
API = r"C:\src\fact-commercial-api"
DOCGEN = (r"C:\src\fact-docgen\src\MoE.Commercial.Documents.Generation.GhostDraft"
          r"\Components\Serialization")
CDM = r"C:\src\fact-commercial-api\MoE.Commercial.CommonDataModel"
MAPPERS = r"C:\src\fact-commercial-api\MoE.Commercial.Data.Provider\Mapping\Db2"
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "output")

# SQL CASTs look like an alias and are not one.
CAST_TYPES = {"VARCHAR", "CHAR", "DECIMAL", "INTEGER", "DATE", "TIME", "TIMESTAMP",
              "NUMERIC", "SMALLINT", "BIGINT", "DOUBLE", "REAL", "CLOB", "BLOB"}

MECHANICAL = {"move_it", "movenum", "movedate", "JustFld", "concat", "tbllook", "noopfunc"}
MANUAL = {"powtype"}


def norm_table(t):
    t = t.upper()
    return t[:-4] + "CPP" if t.endswith(("CPL1", "CPL0")) else t


def load_hydration():
    """DB2 column -> {CDM alias}. Read out of the SELECT ... AS ... in the API's SQL."""
    alias = collections.defaultdict(set)
    tables = set()
    pat = re.compile(r"\b([A-Z][A-Z0-9_]{3,})\s+AS\s+([A-Za-z][A-Za-z0-9_]*)")
    for dirpath, _d, names in os.walk(API):
        if any(x in dirpath for x in (".git", "worktrees", os.sep + "obj", os.sep + "bin")):
            continue
        for n in names:
            if not n.endswith(".cs"):
                continue
            try:
                txt = open(os.path.join(dirpath, n), encoding="utf-8", errors="replace").read()
            except Exception:
                continue
            for t in re.findall(r"\{0\}\.([A-Z0-9_]{4,})", txt):
                tables.add(norm_table(t))
            for col, name in pat.findall(txt):
                if name.upper() in CAST_TYPES:
                    continue
                alias[col.upper()].add(name)
    # The SQL `AS` aliases are only a fraction of the DB2->CDM mapping. The authoritative
    # source is Mapping/Db2: 148 mapper classes whose Map() assigns
    #     target.<CdmProperty> = source.<DB2COLUMN>
    # Reading those instead of inferring from SELECT text took the joinable attribute
    # count from 54 to what the report now shows. Fourth time in this analysis that
    # reading the authority beat inferring from a proxy.
    assign = re.compile(r"target\.([A-Za-z_]\w*)\s*=(.*?);", re.S)
    srccol = re.compile(r"source\.([A-Z][A-Z0-9_]{2,})")
    for dirpath, _d, names in os.walk(MAPPERS):
        for n in names:
            if not n.endswith(".cs"):
                continue
            try:
                txt = open(os.path.join(dirpath, n), encoding="utf-8",
                           errors="replace").read()
            except Exception:
                continue
            for prop, rhs in assign.findall(txt):
                for col in srccol.findall(rhs):
                    alias[col.upper()].add(prop)
    return alias, tables


def load_model():
    """The Server XML vocabulary: leaf element names, and CDM properties the builders read."""
    elems, props, sections = set(), set(), set()
    elem_re = re.compile(r'new XElement\(\s*"([A-Za-z0-9_]+)"')
    leaf_re = re.compile(r'new XElement\(\s*"([A-Za-z0-9_]+)"\s*,\s*([^,)]*\.[A-Za-z0-9_?.]+)')
    sect_re = re.compile(r'\[SectionBuilder\(\s*[A-Za-z.]+\s*,\s*"([^"]+)"')
    for dirpath, _d, names in os.walk(DOCGEN):
        for n in names:
            if not n.endswith(".cs"):
                continue
            try:
                txt = open(os.path.join(dirpath, n), encoding="utf-8", errors="replace").read()
            except Exception:
                continue
            sections.update(sect_re.findall(txt))
            elems.update(elem_re.findall(txt))
            for _e, expr in leaf_re.findall(txt):
                p = expr.strip().split(".")[-1].strip("? )")
                if p and p[0].isupper():
                    props.add(p)
    # The CDM's own type definitions are the authoritative supply side. Scraping property
    # names out of builder EXPRESSIONS caught only what one regex happened to see and
    # reported 1% coverage -- a measurement artefact, not a finding, and the third of its
    # kind in this analysis. Read the model instead of inferring it.
    cdm_prop = re.compile(r"public\s+[\w<>?\[\],\s]+?\s+([A-Z]\w*)\s*\{\s*get")
    for dirpath, _d, names in os.walk(CDM):
        if os.sep + "obj" in dirpath or os.sep + "bin" in dirpath:
            continue
        for n in names:
            if not n.endswith(".cs"):
                continue
            try:
                props.update(cdm_prop.findall(open(
                    os.path.join(dirpath, n), encoding="utf-8", errors="replace").read()))
            except Exception:
                pass
    return elems, props, sections


def load_demand():
    """form -> Counter((entity, value column)) plus per-form method classification."""
    tbl_re = re.compile(r"TBLOFF,\s*([A-Z0-9_]+)[ ,]?(.*)", re.I)
    collen_re = re.compile(r"([A-Za-z][A-Za-z0-9_]{1,14})\s*,\s*(\d+)")
    demand = collections.defaultdict(collections.Counter)
    kinds = collections.defaultdict(collections.Counter)
    for path in glob.glob(os.path.join(DDT, "*.DDT")):
        form = os.path.splitext(os.path.basename(path))[0].upper()
        try:
            lines = open(path, encoding="latin-1").read().split("\n")
        except Exception:
            continue
        inov = False
        for raw in lines:
            line = raw.strip()
            if line.lower().startswith("<image field rules override>"):
                inov = True
                continue
            if not inov or not line.startswith(";"):
                continue
            parts = line.split(";")
            if len(parts) < 12:
                continue
            srccol, method, src = parts[3].strip(), parts[10].strip(), parts[11]
            if method in MANUAL:
                kinds[form]["manual"] += 1
                continue
            m = tbl_re.search(src)
            if not m:
                kinds[form]["computed/DAL"] += 1
                continue
            ent = norm_table(m.group(1))
            cols = [srccol.upper()] if srccol else [c.upper() for c, _ in
                                                    collen_re.findall(m.group(2))]
            if not cols:
                kinds[form]["selector only"] += 1
                continue
            for c in cols:
                demand[form][(ent, c)] += 1
                kinds[form]["data"] += 1
    return demand, kinds


def main(top):
    alias, hyd_tables = load_hydration()
    elems, props, sections = load_model()
    demand, kinds = load_demand()
    model_names = {x.lower() for x in elems | props}

    print(f"hydration: {len(alias)} DB2 columns aliased, {len(hyd_tables)} tables")
    print(f"model    : {len(sections)} sections, {len(elems)} XML elements, "
          f"{len(props)} CDM properties read")
    print(f"demand   : {len(demand)} forms with data-bound fields\n")

    # ---- attribute level ----------------------------------------------------
    attr_forms = collections.defaultdict(set)
    attr_uses = collections.Counter()
    for form, c in demand.items():
        for key, n in c.items():
            attr_forms[key].add(form)
            attr_uses[key] += n

    rows = []
    for (ent, col), n in attr_uses.items():
        names = alias.get(col, set())
        reaches = any(x.lower() in model_names for x in names)
        rows.append({"entity": ent, "column": col, "uses": n,
                     "forms": len(attr_forms[(ent, col)]),
                     "entity_hydrated": ent in hyd_tables,
                     "cdm_alias": ";".join(sorted(names)),
                     "in_model": reaches})
    rows.sort(key=lambda r: -r["forms"])

    reach = sum(1 for r in rows if r["in_model"])
    hyd = sum(1 for r in rows if r["entity_hydrated"])
    print(f"ATTRIBUTES the FAP library needs: {len(rows)}")
    print(f"   on a CDM-hydrated entity      : {hyd} ({100*hyd/len(rows):.0f}%)")
    print(f"   with a DB2->CDM alias         : {sum(1 for r in rows if r['cdm_alias'])}")
    print(f"   reaching the Server XML model : {reach} ({100*reach/len(rows):.0f}%)")

    # ---- form level ---------------------------------------------------------
    frows = []
    for form, c in demand.items():
        tot = sum(c.values())
        ok = sum(n for key, n in c.items()
                 if any(x.lower() in model_names for x in alias.get(key[1], set())))
        k = kinds[form]
        frows.append({"form": form, "data_fields": tot, "bindable_today": ok,
                      "pct": round(100 * ok / tot, 1) if tot else 0,
                      "manual": k["manual"], "computed_dal": k["computed/DAL"]})
    frows.sort(key=lambda r: -r["pct"])
    buckets = collections.Counter()
    for r in frows:
        p = r["pct"]
        buckets["100%" if p == 100 else ">=75%" if p >= 75 else ">=50%" if p >= 50
                else ">=25%" if p >= 25 else ">0%" if p > 0 else "0%"] += 1
    print(f"\nFORMS by share of data fields the model can feed TODAY:")
    for b in ("100%", ">=75%", ">=50%", ">=25%", ">0%", "0%"):
        if buckets[b]:
            print(f"   {buckets[b]:>5}  {b}")

    print(f"\nBACKLOG -- attributes blocking the most forms (not in the model):")
    print(f"   {'forms':>6} {'uses':>7}  {'entity.column':<26} hydrated  alias")
    for r in [x for x in rows if not x["in_model"]][:top]:
        print(f"   {r['forms']:>6} {r['uses']:>7}  {r['entity']+'.'+r['column']:<26} "
              f"{'yes' if r['entity_hydrated'] else 'no ':<9} {r['cdm_alias'][:28]}")

    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "bindgap-attributes.csv"), "w", newline="",
              encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    with open(os.path.join(OUT, "bindgap-forms.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(frows[0].keys()))
        w.writeheader()
        w.writerows(frows)
    print(f"\nWrote {OUT}\\bindgap-attributes.csv and bindgap-forms.csv")


if __name__ == "__main__":
    n = 20
    if "--top" in sys.argv:
        n = int(sys.argv[sys.argv.index("--top") + 1])
    main(n)
