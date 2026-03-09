# Production Readiness Criteria

This document defines objective gates for moving from "promising OSS" to "default production dependency."

## Current Status

Current status: `Early Adoption`

Interpretation:

- usable for evaluation and controlled production pilots
- not yet presented as ecosystem default
- benchmark claims are constrained by `docs/BENCHMARK_CLAIMS_POLICY.md`

## Readiness Levels

### Level 0: Early Adoption

- CI/release pipelines are green
- package install and restore work from NuGet
- docs are aligned with package names and install flow

### Level 1: Production Candidate

- sustained zero P0 regressions for 60 days
- release runbook followed for at least two successful releases
- NuGet consumer validation workflow is green for 30 consecutive days
- reconnect/failover drills pass in CI and one external environment

### Level 2: Production Default

- sustained zero P0 regressions for 90 days
- at least 3 external production references or case studies
- benchmark claims independently reproduced by at least one external contributor
- minimum two maintainers with release permissions and documented backup

## Mandatory Release Evidence

Each release must provide:

- commit SHA and release tag
- CI pass for build/tests/perf gates
- vulnerability scan pass
- NuGet publish confirmation for all OSS packages
- release notes plus migration notes (if any)

## Reliability Gates

- no known data loss regressions
- no known cache correctness regressions
- no known reconnect deadlock/starvation regressions
- no untriaged P0 issues open at release time

## Performance Claim Gates

Before any "faster than X" claim:

- strict/fair benchmark class only
- command lines and environment metadata published
- median and tail latency metrics shown
- allocation deltas included

See `docs/BENCHMARK_CLAIMS_POLICY.md`.

## Adoption Signal Gates

Adoption is tracked, not guessed:

- NuGet downloads trend
- GitHub issue and PR velocity
- external contributor count
- external production reports

Until Level 2 is achieved, messaging should remain "production-capable with active hardening."

