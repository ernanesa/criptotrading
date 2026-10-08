#!/usr/bin/env python3
"""Fail a NuGet JSON report on High/Critical findings without printing payloads."""
import json
import sys


def review(value):
    if isinstance(value, dict):
        if str(value.get("severity", "")).lower() in {"high", "critical"}:
            return 1
        if value.get("errors") or value.get("problems"):
            raise ValueError("Dependency report contains errors")
        return sum(review(item) for item in value.values())
    if isinstance(value, list):
        return sum(review(item) for item in value)
    return 0


try:
    with open(sys.argv[1], encoding="utf-8") as report_file:
        report = json.load(report_file)
    if not isinstance(report, dict) or not isinstance(report.get("projects"), list) or not report["projects"]:
        raise ValueError("Dependency report schema is incomplete")
    count = review(report)
except (OSError, ValueError, IndexError):
    print("Dependency vulnerability gate failed: unreadable or incomplete report.", file=sys.stderr)
    sys.exit(2)
print(f"Dependency vulnerability gate: {count} High/Critical finding(s).")
sys.exit(1 if count else 0)
