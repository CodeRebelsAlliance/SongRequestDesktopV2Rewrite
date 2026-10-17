#!/usr/bin/env bash
#
# Runs the whole test suite locally: unit + integration (.NET) and keys.js (Node),
# checks the coverage floors and prints a summary.
#
# Integration tests need FFmpeg on the PATH; they skip themselves when it is
# missing. OpenAL runs headless via the null driver.
set -uo pipefail

cd "$(dirname "$0")/.."

RESULTS="TestResults"
rm -rf "$RESULTS"
mkdir -p "$RESULTS"
status=0

echo "==> Unit tests"
dotnet test tests/SongRequestDesktopV2Rewrite.UnitTests/UnitTests.csproj \
  --configuration Release \
  --settings tests/coverage.runsettings \
  --logger "trx;LogFileName=unit.trx" \
  --collect:"XPlat Code Coverage" \
  --results-directory "$RESULTS/unit" || status=1

echo "==> Integration tests"
dotnet test tests/SongRequestDesktopV2Rewrite.IntegrationTests/IntegrationTests.csproj \
  --configuration Release \
  --settings tests/coverage.runsettings \
  --logger "trx;LogFileName=integration.trx" \
  --collect:"XPlat Code Coverage" \
  --results-directory "$RESULTS/integration" || status=1

echo "==> Coverage floors"
python3 tests/check-coverage.py "$RESULTS/unit" 0.36 || status=1
python3 tests/check-coverage.py "$RESULTS/integration" 0.18 || status=1

echo "==> JavaScript tests"
node --test tests/js 2>&1 | tee "$RESULTS/js.txt" || status=1

echo "==> Summary"
python3 tests/test-summary.py "$RESULTS/unit" "$RESULTS/integration" || true

exit "$status"
