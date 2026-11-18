"""R8, the performance budgets: the benchmark's budget records against each
preset's thresholds (docs/running.md, "Budgets").

Usage: budget.py OUT.json RECORD.json...

Each RECORD is one benchmark run's `--bench-json` (BenchTest: the map, the
preset, the render path, the GI drawn, the resolution, the renderer and the
adapter; frame and GPU ms, draw calls, memory). Prints a table per map and
preset and writes OUT.json (the thresholds, every record with its verdicts,
the GI's cost: the remastered desktop run with VoxelGI against the same
without GI), which the editor's status dashboard reads. Exit 1 when a record
is over a budget (llvmpipe and lavapipe runs have none: the CPU renders).

The budgets are numbers for the target hardware. The Steam Deck is not at
hand: its records here come from another GPU at the Deck's settings and are
held against the Deck's budgets scaled by DECK_PROXY (the reference GPU's
throughput over the Deck's, an estimate: see docs/running.md). Real Deck
numbers need the device.
"""
import json
import sys

# per preset: frame ms (uncapped), GPU ms, draw calls (mean per frame), texture,
# video memory (MiB), the GI's GPU cost (ms)
BUDGETS = {
    "deck": {"frame_ms": 16.7, "gpu_ms": 16.7, "draw_calls": 2000, "texture_mib": 1536, "video_mib": 3072, "gi_ms": 0.5},
    "desktop": {"frame_ms": 16.7, "gpu_ms": 16.7, "draw_calls": 4000, "texture_mib": 3072, "video_mib": 6144, "gi_ms": 4.0},
}
# Deck numbers measured on the reference GPU (the development machine's Intel Arc
# 140T, Vulkan): its time budgets divided by these factors (an estimate from the
# GPUs' FP32 throughput, ~1.6 TFLOPS for the Deck's against ~4.5 for the Arc's,
# and the CPUs' single-thread speed, roughly 2x)
DECK_PROXY = {"gpu_ms": 3.0, "frame_ms": 2.0, "gi_ms": 3.0}
SOFTWARE = ("llvmpipe", "lavapipe", "softpipe", "swiftshader")


def software(r):
    return any(s in (r.get("adapter") or "").lower() for s in SOFTWARE)


def limits(r):
    b = dict(BUDGETS.get(r.get("preset"), BUDGETS["desktop"]))
    if r.get("preset") == "deck":
        for k, f in DECK_PROXY.items():
            b[k] = round(b[k] / f, 2)
    return b


def main():
    out, files = sys.argv[1], sys.argv[2:]
    recs = []
    for f in files:
        try:
            with open(f, encoding="utf-8") as fh:
                recs.append(json.load(fh))
        except (OSError, ValueError) as e:
            print(f"budget: {f}: {e}", file=sys.stderr)
    # the GI's cost: the remastered run with GI against the same without (gpu ms, else frame ms)
    for r in recs:
        if r.get("path") != "remastered" or r.get("gi", "None") == "None":
            continue
        base = [x for x in recs if x.get("key") == r.get("key") and x.get("preset") == r.get("preset")
                and x.get("path") == "remastered" and x.get("gi") == "None" and x.get("adapter") == r.get("adapter")]
        if base:
            m = "gpu_ms" if r.get("gpu_ms", 0) > 0 and base[0].get("gpu_ms", 0) > 0 else "frame_ms"
            r["gi_ms"] = round(r[m] - base[0][m], 3)
            r["gi_measure"] = m
    over = 0
    for r in recs:
        sw = software(r)
        lim = limits(r)
        verdicts = {}
        for k, v in lim.items():
            if k not in r or (k == "gpu_ms" and r.get("gpu_ms", 0) <= 0):
                continue
            verdicts[k] = "n/a" if sw else ("ok" if r[k] <= v else "over")
        r["limits"] = lim
        r["verdicts"] = verdicts
        r["software"] = sw
        r["ok"] = sw or all(v != "over" for v in verdicts.values())
        if not r["ok"]:
            over += 1
    recs.sort(key=lambda r: (r.get("key", ""), r.get("preset", ""), r.get("path", ""), r.get("gi", "")))
    with open(out, "w", encoding="utf-8") as fh:
        json.dump({"budgets": BUDGETS, "deck_proxy": DECK_PROXY, "records": recs}, fh, indent=1)
    head = f"{'map':8} {'preset':8} {'path':10} {'GI':8} {'size':10} {'frame ms':>9} {'p99':>7} {'GPU ms':>7} {'p99':>7} {'draws':>6} {'tex MiB':>8} {'vid MiB':>8} {'GI ms':>6}  verdict"
    print(head)
    for r in recs:
        gpu = f"{r.get('gpu_ms', 0):7.2f}" if r.get("gpu_ms", 0) > 0 else "    n/a"
        gp99 = f"{r.get('gpu_p99_ms', 0):7.2f}" if r.get("gpu_ms", 0) > 0 else "    n/a"
        gi = f"{r['gi_ms']:6.2f}" if "gi_ms" in r else "     -"
        bad = [k for k, v in r["verdicts"].items() if v == "over"]
        verdict = "llvmpipe/lavapipe: no budget" if r["software"] else ("ok" if not bad else "OVER: " + ", ".join(bad))
        print(f"{r.get('key', '?'):8} {r.get('preset', '?'):8} {r.get('path', '?'):10} {r.get('gi', '?'):8} "
              f"{r.get('width', 0)}x{r.get('height', 0):<5} {r.get('frame_ms', 0):9.2f} {r.get('frame_p99_ms', 0):7.2f} {gpu} {gp99} "
              f"{r.get('draw_calls', 0):6.0f} {r.get('texture_mib', 0):8.1f} {r.get('video_mib', 0):8.1f} {gi}  {verdict}")
    adapters = sorted({f"{r.get('adapter')} ({r.get('driver')}, {r.get('renderer')})" for r in recs})
    print(f"adapters: {'; '.join(adapters)}")
    print(f"deck budgets on another GPU: the time limits divided by {DECK_PROXY} (an estimate; real Deck numbers need the device)")
    return 1 if over else 0


if __name__ == "__main__":
    sys.exit(main())
