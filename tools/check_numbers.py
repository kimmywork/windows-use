#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
WinUse 数字一致性守卫
====================

存在理由
--------
本项目连续出现过 4 处「同一数字在多份文档里写死、互相不一致」的问题：
    02 §5.14 的 ~133  ->  07 §0.7 的 162  ->  实际表行 167（98/39/30）
    06 §3.1 的 "total": 108          ->  应为 98
    08 §2.6 的「v1 有 108 个能力」    ->  应为 98
    05 / 00 / 09 的 78 / 38 / v1=108 ->  已作废但未同步
共同根因：数字被人工抄写进多处，没有可追溯的单一来源。

本脚本做的事
------------
1. 以 docs/07-capability-registry.md §0.7 的「归属 / tier」两列为**唯一权威源**，
   实际数表行，算出 v1 / v1.5 / v2 条数与 core / extended / advanced / future 条数。
2. 扫描其余文档里**写死的能力条数**，与权威源比对，不一致就报错。
3. 校验内部一致性：future 条数 应等于 v1.5 + v2 条数。

用法
----
    python tools/check_numbers.py          # 只报告，退出码 0/1
    python tools/check_numbers.py --strict # 报告 + 不一致时非零退出（CI 用）
    python tools/check_numbers.py --emit   # 额外输出可直接粘贴的权威数字块

约定（供文档作者遵守）
----------------------
任何文档**不得**写死能力条数。要写就写成引用式：
    「条数见 07 §0.7 tier 列（权威源）；以 tools/check_numbers.py 复核为准」
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
REGISTRY = DOCS / "07-capability-registry.md"

# §0.7 权威表的区间：从 "### 0.7" 起，到 "#### 0.7.12" 止（0.7.12 之后是统计/口径，不再是表）
SEC_START = re.compile(r"^###\s+0\.7\s")
SEC_END = "#### 0.7.12"

# 表格数据行：以 | 开头，且不是分隔行（|---），也不是表头
ROW_RE = re.compile(r"^\|(.+)\|\s*$")
SEP_RE = re.compile(r"^\|[\s:|-]+\|\s*$")

VALID_VERSIONS = {"v1", "v1.5", "v2"}
VALID_TIERS = {"core", "extended", "advanced", "future"}


def _clean(cell: str) -> str:
    """去掉 markdown 强调、行内代码、引用标记与空白。"""
    c = cell.strip()
    c = c.replace("**", "").replace("__", "")
    c = re.sub(r"`([^`]*)`", r"\1", c)
    c = re.sub(r"~~([^~]*)~~", r"\1", c)
    c = c.replace("⭐", "").replace("🔴", "")
    return c.strip()


def _norm_version(cell: str) -> str:
    """归属列可能写成 `v2（⚠️需 PoC）`、`**v1.5**（team-lead 裁定：…）`，取括号前的版本号。"""
    c = cell.strip().lower()
    c = re.split(r"[（(\[【]", c)[0]
    return c.strip().rstrip("。").replace(" ", "")


def parse_registry() -> tuple[list[dict], list[str]]:
    """解析 07 §0.7 权威表，返回 (行记录, 警告)。

    列序**按每节的真实表头定位**（不是写死索引）——
    §0.7.1~§0.7.10 是 8 列（含 P级），§0.7.11 `office.*` 是 7 列（无 P级）。
    """
    if not REGISTRY.exists():
        sys.exit(f"[FATAL] 找不到权威源文件：{REGISTRY}")

    lines = REGISTRY.read_text(encoding="utf-8").splitlines()

    start = None
    end = len(lines)
    for i, ln in enumerate(lines):
        if start is None and SEC_START.match(ln):
            start = i
        elif start is not None and ln.startswith(SEC_END):
            end = i
            break
    if start is None:
        sys.exit("[FATAL] 07 中找不到 §0.7 权威表起始标记 '### 0.7'")

    rows: list[dict] = []
    warns: list[str] = []
    v_idx: int | None = None
    t_idx: int | None = None

    for lineno in range(start, end):
        raw = lines[lineno]
        # 每进入一个 #### 子节就重置表头（各节列数可能不同）
        if raw.startswith("#### "):
            v_idx = t_idx = None
            continue

        m = ROW_RE.match(raw)
        if not m:
            continue
        cells = [_clean(c) for c in m.group(1).split("|")]
        if SEP_RE.match(raw):
            continue

        # 表头行：定位「归属」与「tier」两列
        if "权威 ID" in cells[0]:
            try:
                v_idx = cells.index("归属")
                t_idx = cells.index("tier")
            except ValueError:
                warns.append(f"[表头] 第 {lineno + 1} 行表头缺「归属」或「tier」列：{cells}")
                v_idx = t_idx = None
            continue

        if not cells or not cells[0]:
            continue
        if v_idx is None or t_idx is None:
            warns.append(f"[跳过] 第 {lineno + 1} 行出现在表头之前，无法定位列：{cells[:2]}")
            continue
        if max(v_idx, t_idx) >= len(cells):
            warns.append(f"[跳过] 第 {lineno + 1} 行列数 {len(cells)} 不足，无法解析：{cells[:2]}")
            continue

        cid = cells[0]
        version = _norm_version(cells[v_idx])
        tier = cells[t_idx].lower().replace(" ", "")

        if version not in VALID_VERSIONS:
            warns.append(f"[归属异常] {cid}: 归属='{cells[v_idx]}' 不在 {sorted(VALID_VERSIONS)}")
            continue
        if tier not in VALID_TIERS:
            warns.append(f"[tier 异常] {cid}: tier='{cells[t_idx]}' 不在 {sorted(VALID_TIERS)}")
            continue

        rows.append({"id": cid, "version": version, "tier": tier, "line": lineno + 1})

    return rows, warns


def tally(rows: list[dict]) -> dict:
    by_version = {v: 0 for v in VALID_VERSIONS}
    by_tier = {t: 0 for t in VALID_TIERS}
    for r in rows:
        by_version[r["version"]] += 1
        by_tier[r["tier"]] += 1
    return {"version": by_version, "tier": by_tier, "total": len(rows)}


# ---------------------------------------------------------------------------
# 其它文档里「写死的能力条数」扫描
# ---------------------------------------------------------------------------

# (正则, 捕获组名, 期望对照的权威键, 说明)
HARDCODED_PATTERNS = [
    (re.compile(r"core\s*(?:条数)?\s*[=＝:：]\s*\*{0,2}(\d+)\*{0,2}"), "core", "core 条数"),
    (re.compile(r"\*\*core\*\*\s*\|\s*\*{0,2}(\d+)\*{0,2}"), "core", "core 条数（tier 表）"),
    (re.compile(r"v1\s*能力\s*[=＝:：]\s*\*{0,2}(\d+)\*{0,2}"), "v1", "v1 条数"),
    (re.compile(r"v1\s*有\s*\*{0,2}(\d+)\*{0,2}\s*个?能力"), "v1", "v1 条数"),
    (re.compile(r"\"total\"\s*:\s*(\d+)"), "v1", "协议 total 字段（应等于 v1 条数）"),
    (re.compile(r"能力总数\s*[=＝:：]\s*\*{0,2}(\d+)\*{0,2}"), "total", "能力总数"),
    (re.compile(r"共\s*\*{0,2}(\d+)\*{0,2}\s*(?:条|个)能力"), "total", "能力总数"),
]

# 07 自己是权威源，不参与「写死」扫描；12 是实测报告，里面的数字是 PoC 数据不是能力条数
SCAN_SKIP = {"07-capability-registry.md", "12-poc-findings.md", "01-market-research.md"}


# 已确认为过期的能力条数：出现在任何文档里都应被视为未同步的残留
STALE_NUMBERS = {
    162: "旧总数（未计入 office.* 5 条）",
    108: "旧 v1 条数",
    133: "02 §5.14 按表行自报、未展开的旧口径",
    78: "旧数（已作废）",
    38: "旧 candidate 清单条数（该清单从未落盘）",
}
STALE_CONTEXT = re.compile(r"能力|capabilit|总数|条数|个工具|合计")

# 合法引用旧值的行（"更正记录"里必然要写旧值），不算残留
# 删除线 `~~108~~` 与「旧稿/原写/已更正」同义：都是**显式标记为历史值**
STALE_EXEMPT = re.compile(r"旧稿|旧写|原写|旧统计|已更正|已作废|更正记录|作废|~~\d")


# --- 09 任务 ID 完整性（防"定义在编辑中悄悄消失"） ---
# 存在理由：09 的任务表曾因一次 Edit 的锚点包含了要保留的正文，把 T-S1 整行吞掉，
# 而在甘特图/依赖图/正文里 T-S1 仍被引用 —— 引用还在、定义没了，肉眼极难发现。
ROADMAP = DOCS / "09-roadmap.md"
TASK_ID_RE = re.compile(r"\b(T-[A-Z]{1,3}\d{1,3})\b")
# 定义行形如：| **T-S1** | ... |
TASK_DEF_RE = re.compile(r"^\|\s*\*\*(T-[A-Z]{1,3}\d{1,3})\*\*\s*\|")


def scan_task_ids() -> tuple[set[str], set[str], list[str]]:
    """返回 (已定义 ID, 被引用 ID, 编号断号提示)。"""
    text = ROADMAP.read_text(encoding="utf-8")
    defined: set[str] = set()
    for ln in text.splitlines():
        m = TASK_DEF_RE.match(ln.strip())
        if m:
            defined.add(m.group(1))
    referenced = set(TASK_ID_RE.findall(text))

    # 同一前缀内的编号断号（只提示，不算错——可能确为有意留空）。
    # 注意 ID 形如 T-A8 / T-C12：前缀是字母段，不能按 "-" 切分。
    gaps: list[str] = []
    by_prefix: dict[str, list[int]] = {}
    for tid in defined:
        m = TASK_ID_RE.fullmatch(tid)
        if not m:
            continue
        pm = re.fullmatch(r"T-([A-Z]{1,3})(\d{1,3})", tid)
        if not pm:
            continue
        by_prefix.setdefault("T-" + pm.group(1), []).append(int(pm.group(2)))
    for p, nums in sorted(by_prefix.items()):
        nums.sort()
        missing = [n for n in range(nums[0], nums[-1] + 1) if n not in nums]
        if missing:
            gaps.append(f"{p}-* 缺号 {missing}（已有 {nums[0]}~{nums[-1]}）")
    return defined, referenced, gaps


def scan_stale() -> list[dict]:
    """扫描已过期但可能残留在文档里的数字。

    两条降噪规则：
      1. 必须与「能力/条数/合计」等词相邻（避免把「108 个长工作流」这类无关数字算进来）；
      2. 显式标注为「旧稿/原写/已更正」的行是**合法的历史记录**，豁免。
    """
    out: list[dict] = []
    for path in sorted(DOCS.glob("*.md")):
        if path.name in SCAN_SKIP:
            continue
        text = path.read_text(encoding="utf-8")
        for lineno, ln in enumerate(text.splitlines(), 1):
            if not STALE_CONTEXT.search(ln) or STALE_EXEMPT.search(ln):
                continue
            # 删除线 `~~...~~` 里的内容是显式标注的历史值 —— 整个跨度都不参与扫描。
            # 注意不能用 `~~\d` 匹配：删除线开头与数字之间常隔有其他文字（如 `~~v1 的 108 个~~`）。
            eff = re.sub(r"~~[^~]*~~", "", ln)
            for num, why in STALE_NUMBERS.items():
                # 数字必须与上下文词相邻（前后 12 字符内），避免误伤无关数字
                near = (
                    re.search(rf"(?:能力|条数|总数|合计)[^\d。；]{{0,12}}{num}(?![\d.%])", eff)
                    or re.search(rf"(?<![\d.]){num}[^\d。；]{{0,10}}(?:条能力|个能力|条\b|个工具)", eff)
                )
                if near:
                    out.append({"file": path.name, "line": lineno, "num": num, "why": why, "snippet": ln.strip()[:140]})
    return out


# --- core 逐条清单 vs tier 列 对账 ---
CORE_LIST_START_RE = re.compile(r"core\s*\d+\s*条逐条清单")
CORE_LIST_END = "**advanced 6 条"
CORE_ID_RE = re.compile(r"`([a-z][a-z_]*\.[a-z_*][^`]*)`")


def parse_declared_core_list() -> tuple[set[str], str | None]:
    """读 07 §0.7.13 里「core N 条逐条清单」，返回 (清单里的 ID 集合, 章节起始行文本)。"""
    lines = REGISTRY.read_text(encoding="utf-8").splitlines()
    start = None
    for i, ln in enumerate(lines):
        if CORE_LIST_START_RE.search(ln):
            start = i
            break
    if start is None:
        return set(), None
    end = len(lines)
    for i in range(start, len(lines)):
        if lines[i].startswith(CORE_LIST_END):
            end = i
            break
    ids: set[str] = set()
    for ln in lines[start:end]:
        m = ROW_RE.match(ln.strip())
        if not m:
            continue
        for cid in CORE_ID_RE.findall(m.group(1)):
            ids.add(cid.strip())
    return ids, lines[start].strip()


# --- 00 文档索引表的行数对账 ---
DOC_ROW_RE = re.compile(r"^\|\s*\**(\d{2})\**\s*\|\s*`(\d{2}-[a-z\-]+\.md)`\s*\|\s*([~\d]+)\s*\|")


def scan_doc_index() -> list[dict]:
    """00-executive-summary.md 的文档索引表里写了每篇的行数，与磁盘实际行数对账。

    同一根因：行数也是人工抄写的，改完文档没人回改索引表。
    表里写 `~400` / `—` 的是约数，跳过。
    """
    idx = DOCS / "00-executive-summary.md"
    if not idx.exists():
        return []
    out: list[dict] = []
    for lineno, ln in enumerate(idx.read_text(encoding="utf-8").splitlines(), 1):
        m = DOC_ROW_RE.match(ln.strip())
        if not m:
            continue
        _, fname, declared = m.groups()
        if declared.startswith("~") or not declared.isdigit():
            continue
        fpath = DOCS / fname
        if not fpath.exists():
            out.append({"file": fname, "line": lineno, "declared": int(declared), "actual": None})
            continue
        actual = len(fpath.read_text(encoding="utf-8").splitlines())
        if actual != int(declared):
            out.append({"file": fname, "line": lineno, "declared": int(declared), "actual": actual})
    return out


def scan_hardcoded(truth: dict) -> list[dict]:
    """扫描 docs/ 下其它文档里写死的能力条数。"""
    findings: list[dict] = []
    expect = {
        "core": truth["tier"]["core"],
        "v1": truth["version"]["v1"],
        "total": truth["total"],
    }
    for path in sorted(DOCS.glob("*.md")):
        if path.name in SCAN_SKIP:
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except OSError as e:
            findings.append({"file": path.name, "line": 0, "kind": "读取失败", "found": str(e), "expect": "-", "ok": False})
            continue

        for lineno, ln in enumerate(text.splitlines(), 1):
            # 注释掉的行（引用本脚本为权威）不算违规
            if "check_numbers.py" in ln and ("权威" in ln or "为准" in ln):
                continue
            for rx, key, label in HARDCODED_PATTERNS:
                for m in rx.finditer(ln):
                    got = int(m.group(1))
                    want = expect[key]
                    findings.append(
                        {
                            "file": path.name,
                            "line": lineno,
                            "kind": label,
                            "found": got,
                            "expect": want,
                            "ok": got == want,
                            "snippet": ln.strip()[:120],
                        }
                    )
    return findings


def main() -> int:
    strict = "--strict" in sys.argv
    emit = "--emit" in sys.argv

    rows, warns = parse_registry()
    if not rows:
        sys.exit("[FATAL] §0.7 表里没解析出任何数据行，列序可能变了，请人工核对 07 §0.7 图例")

    t = tally(rows)
    v, ti = t["version"], t["tier"]

    print("=" * 68)
    print(" WinUse 数字一致性守卫 —— 权威源：docs/07-capability-registry.md §0.7")
    print("=" * 68)
    print(f" 解析数据行：{t['total']} 行\n")

    print(" [按版本 归属]")
    for k in ("v1", "v1.5", "v2"):
        print(f"   {k:<8} {v[k]:>4}")
    print(f"   {'合计':<8} {t['total']:>4}")

    print("\n [按 tier]")
    for k in ("core", "extended", "advanced", "future"):
        print(f"   {k:<10} {ti[k]:>4}")
    print(f"   {'合计':<10} {sum(ti.values()):>4}")

    # --- 内部一致性 ---
    print("\n [内部一致性检查]")
    problems: list[str] = []
    if sum(ti.values()) != t["total"]:
        problems.append(f"tier 合计 {sum(ti.values())} != 总行数 {t['total']}")
    if ti["future"] != v["v1.5"] + v["v2"]:
        problems.append(f"future({ti['future']}) != v1.5+v2({v['v1.5']}+{v['v2']}={v['v1.5'] + v['v2']})：口径是「v1.5/v2 全部标 future」")
    if ti["core"] + ti["extended"] + ti["advanced"] != v["v1"]:
        problems.append(f"core+extended+advanced({ti['core']}+{ti['extended']}+{ti['advanced']}) != v1({v['v1']})：v1 之外不应有非 future tier")

    if problems:
        for p in problems:
            print(f"   ❌ {p}")
    else:
        print("   ✅ 全部通过")

    # --- 解析警告 ---
    if warns:
        print("\n [解析警告]")
        for w in warns[:20]:
            print(f"   ⚠️  {w}")
        if len(warns) > 20:
            print(f"   ... 另有 {len(warns) - 20} 条")

    # --- 跨文档写死数字扫描 ---
    findings = scan_hardcoded(t)
    bad = [f for f in findings if not f["ok"]]
    print("\n [跨文档写死数字扫描]")
    if not findings:
        print("   ✅ 未发现写死的能力条数（或都已与权威源一致）")
    else:
        for f in findings:
            mark = "✅" if f["ok"] else "❌"
            print(f"   {mark} {f['file']}:{f['line']}  {f['kind']}：写死 {f['found']}，权威 {f['expect']}")
            if not f["ok"]:
                print(f"        └─ {f.get('snippet','')}")
    # --- 00 索引表行数对账 ---
    print("\n [00 文档索引表行数对账]")
    idx_bad = scan_doc_index()
    if not idx_bad:
        print("   ✅ 索引表行数与实际一致")
    for d in idx_bad:
        print(f"   ❌ 00:{d['line']}  {d['file']}：索引表写 {d['declared']} 行，实际 {d['actual']} 行")
    if idx_bad:
        print("   （建议：索引表行数也改成引用式，或干脆不写行数）")

    print("\n [规则] 文档里不得写死能力条数，一律写成引用式：")
    print("        「条数见 07 §0.7 tier 列（权威源），以 tools/check_numbers.py 复核为准」")

    # --- 09 任务 ID 完整性 ---
    print("\n [09 任务 ID 完整性]")
    task_bad: list[str] = []
    if not ROADMAP.exists():
        print("   ⚠️  找不到 09-roadmap.md，跳过")
    else:
        defined, referenced, gaps = scan_task_ids()
        dangling = sorted(referenced - defined)
        print(f"   已定义 {len(defined)} 个任务 ID ｜ 全文引用 {len(referenced)} 个")
        if dangling:
            task_bad.append("dangling")
            print(f"   ❌ 被引用但没有定义行（{len(dangling)} 个）—— 定义在编辑中消失了：")
            for t in dangling:
                print(f"        - {t}")
        else:
            print("   ✅ 所有被引用的任务 ID 都有定义行")
        for g in gaps:
            print(f"   ⚠️  编号断号：{g}")

    # --- core 清单 vs tier 列 对账 ---
    print("\n [core 清单 vs tier 列 对账]")
    declared, head = parse_declared_core_list()
    actual = {r["id"] for r in rows if r["tier"] == "core"}
    core_drift: list[str] = []
    if not declared:
        print(f"   ⚠️  未在 07 中找到「core N 条逐条清单」小节，跳过对账")
    else:
        only_table = sorted(actual - declared)
        only_list = sorted(declared - actual)
        if only_table:
            print(f"   ❌ 表里标 core、但没进逐条清单（{len(only_table)} 条）：{', '.join(only_table)}")
            core_drift.append("table-only")
        if only_list:
            print(f"   ❌ 清单里有、但表里没标 core（{len(only_list)} 条）：{', '.join(only_list)}")
            core_drift.append("list-only")
        if not only_table and not only_list:
            print(f"   ✅ 一致：tier 列 core {len(actual)} 条 == 逐条清单 {len(declared)} 条")
        elif head:
            print(f"   （对照的清单标题行：{head}）")

    # --- 过期数字残留扫描 ---
    stale = scan_stale()
    print("\n [过期数字残留扫描]")
    if not stale:
        print("   ✅ 未发现已作废的旧条数残留")
    for s in stale:
        print(f"   ❌ {s['file']}:{s['line']}  残留 {s['num']}（{s['why']}）")
        print(f"        └─ {s['snippet']}")

    if emit:
        print("\n" + "-" * 68)
        print(" 可直接粘贴的权威数字块：")
        print("-" * 68)
        print(f"   能力总数 {t['total']}（v1 {v['v1']} / v1.5 {v['v1.5']} / v2 {v['v2']}）")
        print(f"   tier：core {ti['core']} / extended {ti['extended']} / advanced {ti['advanced']} / future {ti['future']}")

    print("\n" + "=" * 68)
    n_bad = len(problems) + len(bad) + len(stale) + len(core_drift) + len(idx_bad) + len(task_bad)
    if n_bad == 0:
        print(" 结论：✅ 通过 —— 所有写死的数字都与 07 §0.7 权威表一致")
        return 0
    print(f" 结论：❌ 发现 {n_bad} 处不一致")
    return 1 if strict else 0


if __name__ == "__main__":
    sys.exit(main())
