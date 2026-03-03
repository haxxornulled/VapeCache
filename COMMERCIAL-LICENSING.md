# Commercial Licensing and Enforcement

This file explains how VapeCache licensing is structured and enforced.

It is operational guidance, not legal advice.

## Current Model

- Community source: `LICENSE` (PolyForm Noncommercial 1.0.0 + required notices).
- Enterprise packages: `LICENSE-ENTERPRISE.txt` + signed commercial agreement.

## Practical Boundary

- Noncommercial/evaluation use is permitted under `LICENSE`.
- Commercial and production-for-profit use requires a paid enterprise agreement.
- Enterprise package rights exist only under written agreement or written trial terms.

## How To Avoid Revenue Leakage

1. Keep enterprise source private.
2. Ship enterprise packages from authenticated feeds only.
3. Require license key validation at startup for enterprise features.
4. Implement online revocation checks for high-risk environments.
5. Publish clear "commercial use requires enterprise license" language in README/docs.
6. Maintain logs for issued license keys, customer org, expiry, and revocations.
7. Enforce violations quickly (written notice, cure window if desired, then legal escalation).

## Recommended Legal Stack

1. Repo license: standard noncommercial license text (already in `LICENSE`).
2. Enterprise customer contract: MSA + Order Form + SLA + DPA (as applicable).
3. Trial terms: separate written trial agreement with end date and usage limits.
4. Trademark policy: explicitly restrict use of VapeCache marks outside factual attribution.

## Evidence Checklist for Enforcement

- Copy of applicable license text at the time of use.
- Customer/non-customer status and timeline.
- Access/download records for enterprise artifacts.
- License key issuance/validation/revocation records.
- Screenshots/logs showing commercial deployment or paid service usage.

## Next Step (Strongly Recommended)

Have counsel review:

- `LICENSE`
- `LICENSE-ENTERPRISE.txt`
- your enterprise order form / MSA template

so the license language, venue, remedies, and audit clauses match your target jurisdictions and sales process.
