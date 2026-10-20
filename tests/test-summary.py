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
    attrs = counters.attrib if counters is not None else {}
    total = int(attrs.get("total", "0"))
    passed = int(attrs.get("passed", "0"))
    failed = int(attrs.get("failed", "0"))
    skipped = int(attrs.get("notExecuted", "0")) + int(attrs.get("inconclusive", "0"))

    # Some xUnit/vstest combinations report a skipped test as `NotExecuted` in the
    # per-result Outcome but leave the counters at zero. Trust the results when
    # they show more skips, otherwise the summary calls the run "aborted".
    result_skips = 0
    for result in root.iter(f"{NS}UnitTestResult"):
        if result.get("outcome") in ("NotExecuted", "Inconclusive", "NotRunnable", "Skipped"):
            result_skips += 1
    if result_skips > skipped:
        skipped = result_skips

    # Anything left over is aborted/error/timeout/inProgress/passedButRunAborted:
    # vstest writes these when the test host dies part-way through a run (which
    # also means no coverage is produced), so surface them instead of hiding them.
    aborted = max(0, total - passed - failed - skipped)

    failures = []
    for result in root.iter(f"{NS}UnitTestResult"):
        if result.get("outcome") != "Failed":
            continue
        name = result.get("testName", "?")
        message = result.findtext(f"{NS}Output/{NS}ErrorInfo/{NS}Message", default="") or ""
        stack = result.findtext(f"{NS}Output/{NS}ErrorInfo/{NS}StackTrace", default="") or ""
        failures.append((name, message.strip(), stack.strip()))

    return total, passed, failed, skipped, aborted, failures


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

    totals = {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "aborted": 0}
    all_failures = []

    for directory in directories:
        trx_files = glob.glob(os.path.join(directory, "**", "*.trx"), recursive=True)
        if not trx_files:
            continue
        for trx in trx_files:
            total, passed, failed, skipped, aborted, failures = parse_trx(trx)
            totals["total"] += total
            totals["passed"] += passed
            totals["failed"] += failed
            totals["skipped"] += skipped
            totals["aborted"] += aborted
            all_failures.extend(failures)

        rate = coverage_rate(directory)
        cov = f" · coverage **{rate * 100:.1f}%**" if rate is not None else ""
        aborted_note = f" · ⚠ **{aborted} aborted**" if aborted else ""
        out.append(
            f"- `{directory}`: **{passed}/{total}** passed, {failed} failed, "
            f"{skipped} skipped{aborted_note}{cov}"
        )

    status = "❌ FAILED" if (totals["failed"] or totals["aborted"]) else "✅ PASSED"
    out.insert(0, f"## Test summary — {status}")

    if totals["aborted"]:
        out.append(
            "\n> ⚠ Some tests were aborted, which means the test host died before finishing "
            "(no coverage is produced in that case). Look for `Sequence_*.xml` in the "
            "results (produced by `dotnet test --blame`) to see which test was running."
        )

    suite_outcomes = []
    for label, variable in (
        ("unit", "SUITE_OUTCOME_UNIT"),
        ("integration", "SUITE_OUTCOME_INTEGRATION"),
        ("javascript", "SUITE_OUTCOME_JS"),
        ("coverage", "SUITE_OUTCOME_COVERAGE"),
    ):
        value = os.environ.get(variable)
        if value:
            suite_outcomes.append(f"{label}={value}")
    if suite_outcomes:
        out.append(
            "\n- Suite steps: " + ", ".join(suite_outcomes)
            + " *(these steps use `continue-on-error`, so read these outcomes rather than the step icons)*"
        )

    out.append(
        f"\n**Totals:** {totals['passed']}/{totals['total']} passed · "
        f"{totals['failed']} failed · {totals['skipped']} skipped"
        + (f" · {totals['aborted']} aborted" if totals["aborted"] else "")
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
