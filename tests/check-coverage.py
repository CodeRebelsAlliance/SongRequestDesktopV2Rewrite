#!/usr/bin/env python3
"""Coverage ratchet.

Fails if the overall line-rate in a coverlet cobertura report is below a floor.

Usage:
    python3 tests/check-coverage.py <results-dir> <floor>
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    directory, floor_arg = sys.argv[1], sys.argv[2]
    floor = float(floor_arg)

    files = glob.glob(os.path.join(directory, "**", "coverage.cobertura.xml"), recursive=True)
    if not files:
        print(f"No coverage reports found under '{directory}'.")
        return 1

    rates = []
    for path in files:
        root = ET.parse(path).getroot()
        rates.append(float(root.get("line-rate", "0")))

    rate = max(rates)
    print(f"coverage line-rate: {rate:.4f} (floor {floor:.2f}) [{directory}]")
    return 0 if rate + 1e-9 >= floor else 1


if __name__ == "__main__":
    sys.exit(main())
