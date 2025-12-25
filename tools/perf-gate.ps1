# Perf gate helper script.
# CI already runs VapeCache.PerfGates.Tests which contains the allocation assertions.
# This script exists to keep the workflow stable and to provide a single place to extend gating.

$ErrorActionPreference = 'Stop'

Write-Host "Perf gates: validated by VapeCache.PerfGates.Tests (zero-alloc assertions)."

# Future hook: parse test results, enforce runtime knobs, etc.
exit 0
