# Threat Model TM-XXXX

**Scope/version/owner/reviewers:**
**Environment/data classification:**
**Review/expiry:**

## Assets and security objectives

Capital, exchange keys, admin identity/session, RiskPolicy/config, ledger/audit, supply chain, host/database/backups and strategy IP.

## Architecture, trust boundaries and data flows

Link a diagram and enumerate browser/control plane, Worker, database, secret store, CI/CD, registry, telemetry, operator and exchange boundaries.

## Threats and misuse cases

| ID | Threat/misuse case | Entry point | Likelihood/impact | Prevention/detection | Owner/evidence | Residual risk |
|---|---|---|---|---|---|---|

Cover credential theft, replay, unauthorized risk change, forged callback/event, SSRF/injection/XSS/CSRF, supply-chain compromise, secret leakage, log tampering, backup theft, denial of service and insider/operator error.

## Verification baseline

- selected OWASP ASVS IDs in versioned form:
- security tests/scans:
- accepted risks and expiry:
