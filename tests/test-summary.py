#!/usr/bin/env python3
"""Summarises .NET test results (TRX) and coverage.

Usage:
    python3 tests/test-summary.py <results-dir> [<results-dir> ...]

Prints a Markdown report to stdout and, when the GITHUB_STEP_SUMMARY
environment variable is set, appends it to the GitHub Actions job summary.
On any failure the failing tests are listed with their error messages.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def parse_trx(path):
    root = ET.parse(path).getroot()
    counters = root.find(f"{NS}ResultSummary/{NS}Counters")
    total = int(counters.get("total", "0")) if counters is not None else 0
    passed = int(counters.get("passed", "0")) if counters is not None else 0
    failed = int(counters.get("failed", "0")) if counters is not None else 0
    skipped = 0
    if counters is not None:
        skipped = int(counters.get("notExecuted", "0")) + int(counters.get("inconclusive", "0"))

    failures = []
    for result in root.iter(f"{NS}UnitTestResult"):
        if result.get("outcome") != "Failed":
            continue
        name = result.get("testName", "?")
        message = result.findtext(f"{NS}Output/{NS}ErrorInfo/{NS}Message", default="") or ""
        stack = result.findtext(f"{NS}Output/{NS}ErrorInfo/{NS}StackTrace", default="") or ""
        failures.append((name, message.strip(), stack.strip()))

    return total, passed, failed, skipped, failures


def coverage_rate(directory):
    rates = []
    for path in glob.glob(os.path.join(directory, "**", "coverage.cobertura.xml"), recursive=True):
        try:
            rates.append(float(ET.parse(path).getroot().get("line-rate", "0")))
        except ET.ParseError:
            pass
    return max(rates) if rates else None


def main() -> int:
    directories = sys.argv[1:] or ["TestResults"]
    out = []

    totals = {"total": 0, "passed": 0, "failed": 0, "skipped": 0}
    all_failures = []

    for directory in directories:
        trx_files = glob.glob(os.path.join(directory, "**", "*.trx"), recursive=True)
        if not trx_files:
            continue
        for trx in trx_files:
            total, passed, failed, skipped, failures = parse_trx(trx)
            totals["total"] += total
            totals["passed"] += passed
            totals["failed"] += failed
            totals["skipped"] += skipped
            all_failures.extend(failures)

        rate = coverage_rate(directory)
        cov = f" · coverage **{rate * 100:.1f}%**" if rate is not None else ""
        out.append(f"- `{directory}`: **{passed}/{total}** passed, {failed} failed, {skipped} skipped{cov}")

    status = "❌ FAILED" if totals["failed"] else "✅ PASSED"
    out.insert(0, f"## Test summary — {status}")
    out.append(
        f"\n**Totals:** {totals['passed']}/{totals['total']} passed · "
        f"{totals['failed']} failed · {totals['skipped']} skipped"
    )

    if all_failures:
        out.append("\n### Failures\n")
        for name, message, stack in all_failures:
            out.append(f"#### `{name}`")
            if message:
                out.append("```")
                out.append(message[:4000])
                out.append("```")
            if stack:
                out.append("<details><summary>Stack trace</summary>\n")
                out.append("```")
                out.append(stack[:4000])
                out.append("```")
                out.append("</details>")
            out.append("")

    report = "\n".join(out)
    print(report)

    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_file:
        with open(summary_file, "a", encoding="utf-8") as handle:
            handle.write(report + "\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
