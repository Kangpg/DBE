"""Summarize DBE_BUILD_TIMING=1 spans from one initiator build log."""
import argparse
import json
import re
from collections import defaultdict
from pathlib import Path


def merged(spans):
    result = []
    for a, b in sorted(spans):
        if result and a <= result[-1][1]:
            result[-1][1] = max(b, result[-1][1])
        else:
            result.append([a, b])
    return result


def summarize(log):
    raw = log.read_bytes()
    text = raw.decode("utf-16" if raw.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8", errors="replace")
    groups = defaultdict(list)
    counts = defaultdict(list)
    frequency = None
    for match in re.finditer(r"DBE-TIME (\d+) (\d+) (\d+) ([^\r\n]+)", text):
        start, end, hz = map(int, match.groups()[:3])
        if hz == 0 or end < start or frequency not in (None, hz):
            raise ValueError("invalid or inconsistent initiator clock")
        frequency = hz
        stage = match[4].strip()
        counts[stage].append((start, end))
        if stage.startswith("wait."):
            cause = stage.split(".", 2)[-1]
            # In placement, neither local admission nor any Worker ticket was
            # available. Attribute this as a joint observation, not two waits.
            local = cause.split(" + ", 1)[0]
            if stage.startswith("wait.request."):
                category = "wait.worker-execution"
            else:
                kind = {"compiler PDB in use": "pdb", "queued local PDB consumer": "pdb",
                        "slot capacity": "slot", "admission gate": "gate",
                        "CPU owner headroom": "cpu", "memory": "memory"}[local]
                category = "wait." + kind + ("+worker" if stage.startswith("wait.placement.") else "")
        else:
            category = "compile.local" if stage.startswith("compile.local.") else stage
        groups[category].append((start, end))
    if not frequency:
        raise ValueError("no DBE-TIME spans in build log")

    def stats(groups):
        return {stage: {"spans": len(spans),
                        "taskSeconds": round(sum(b-a for a, b in spans)/frequency, 3),
                        "unionSeconds": round(sum(b-a for a, b in merged(spans))/frequency, 3)}
                for stage, spans in sorted(groups.items())}
    return {"categories": stats(groups), "stages": stats(counts)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    print(json.dumps(summarize(parser.parse_args().log), indent=2))
