# APT Cashier-Safe References and Non-Authentication Engineering Closeout

## Purpose

J-007 removes canonical implementation identifiers from ordinary cashier presentation without changing the identifiers used by Central PMS requests, idempotency, encrypted SQLite recovery, receipt retrieval, evidence continuation, print reconciliation, or internal diagnostics. It also closes the established non-authentication Assisted Payment Terminal engineering baseline before human identity work begins.

## Cashier-safe reference policy

- Cashier surfaces show operational status, safe classifications, authoritative timestamps, masked customer references, and fiscal document numbers intended for human use.
- A server-provided support reference is shown only when it is bounded, is not a GUID, and is not a URL, path, or multiline value.
- Correlation IDs, command IDs, entity IDs, evidence references, upload sessions, parking-session IDs, Site/Site Group IDs, tender IDs, payment/fiscal IDs, print-job IDs, and hashes remain internal.
- An omitted internal ID is not shortened into a second business identity. The application instead states that an internal diagnostic reference is retained where that context is useful.
- No presentation value is used for API addressing, idempotency, persistence authority, or reconciliation.

## Surfaces audited

The audit covered payable-basis lookup and revalidation; operational context; statutory ordinance availability; statutory request, decision, application, and evidence lifecycle; pre-cash readiness; CASH_RECEIVED and cash custody; payment and fiscal readback; receipt availability and preview metadata; ORIGINAL and REPRINT status; print history and reconciliation detail; restart recovery; errors; tooltips and accessibility attributes; and development visual-smoke shells used for Windows validation.

Receipt body content remains the authoritative POS Server presentation. J-007 does not alter required fiscal content. Cashier-adjacent technical metadata and local identifiers around that receipt are no longer exposed.

## Internal authority preservation

Canonical identifiers remain unchanged in API DTOs, request construction, idempotency keys, bridge messages, encrypted LocalOperations records, recovery snapshots, receipt/print lookups, and reconciliation logic. Focused tests prove that the UI omits identifiers while bridge and persistence payloads retain them.

## Security and privacy

J-006 evidence boundaries remain unchanged: no evidence bytes, Base64, source path, object key, bucket, signed URL, provider credential, scanner detail, or unmasked statutory identity is added to presentation or persistence. J-004/J-005 ordinance authority, Central PMS payable-basis authority, immediate pre-cash revalidation, POS/fiscal readiness, and the irreversible CASH_RECEIVED boundary are unchanged. The APT still has no direct HikCentral authority.

### Automatic statutory ID masking

The cashier enters the actual statutory ID normally. The application automatically trims the entry and derives the Central PMS `maskedIdReference`: the first two and last four characters remain visible, and every intervening character becomes `*`. Manual insertion of asterisks is neither required nor accepted as proof of masking. Values of six characters or fewer are rendered as a fully masked value of the same length so an unexpectedly short identifier is never exposed in full.

Raw input exists only in transient component memory while the cashier edits the field. On blur and before submission, the application derives the mask; only the masked contract value enters workflow state, Central PMS requests, encrypted SQLite recovery, or restart presentation. The raw value is not written to logs, telemetry, support references, browser storage, accessibility attributes after the masking boundary, or local persistence. Restart recovery can restore only the masked value and cannot reconstruct the raw identifier. Focused deterministic tests start with raw synthetic identifiers and prove automatic masking, short-ID fail-safe behavior, masked-only submission and recovery, and rejection of manual asterisk entry.

## Non-authentication closeout audit

### MUST CLOSE NOW

1. Full canonical identifiers were visible in cashier-facing payable-basis, statutory, evidence, cash, receipt, and print metadata. Resolved by J-007 presentation hardening and deterministic rendered-surface scanning.
2. The encrypted first-time database implementation note still stated that plaintext migration was not implemented. Resolved by linking the implemented explicit offline migration runtime while preserving the prohibition on automatic startup migration.
3. Older cash, fiscal, and receipt proof launchers randomized database filenames inside one shared temporary directory even though each encrypted database requires its own sibling `cash-journal.key`. This caused fail-closed envelope conflicts and two launchers could mask a failed `dotnet run`. Resolved by assigning each run a unique disposable directory, deleting the complete directory afterward, propagating nonzero exits, and reporting only safe bridge classifications.
4. The statutory request form treated `maskedIdReference` as cashier input, which required manual asterisk entry and did not perform application-owned masking. Resolved by separating transient raw input from masked presentation and the existing masked-only Central PMS/persistence contract.

No unresolved MUST CLOSE NOW item remains.

### Intentionally deferred

- Controlled UAT and production rollout.
- Physical 57/58/80 mm printer certification and physical cash-drawer certification.
- Deployment packaging beyond the existing repository build and proof infrastructure.
- Database key rotation, terminal-replacement recovery, and deployment-level backup/support-bundle controls where separately governed.

### Authentication workstream

User login, cashier authentication, password storage/reset, MFA, human sessions, user and role administration, Site/Site Group human grants, offline authentication, credential caching, Windows-domain authentication, OIDC/OAuth/Entra integration, Management Platform user administration, Operator Console login, and APT cashier login are not implemented by J-007.

### External dependencies

- Central PMS remains authoritative for payable basis, statutory policy, evidence lifecycle, decisions/applications, payment finality, fiscal readiness, receipt retrieval, and any future ExitAuthorization readback contract.
- POS Server remains authoritative for Sales Invoice issuance and presentation.
- Vendor PMS/HikCentral remains outside the APT boundary.
- Physical printer, drawer, Windows deployment, and site infrastructure validation require their controlled environments.

### Future enhancements

Evidence preview/download, OCR, biometric processing, retention/deletion workers, Annex E, and new payment methods remain outside the established baseline.

## Validation

Automated validation includes the complete .NET and frontend suites, type checking, production build, E2E, focused cashier-safe rendering tests, deterministic UI proofs, SQLCipher/provider proof, encrypted first-time creation proof, plaintext migration proof, statutory ordinance/evidence/cash proofs, receipt/printing/print-history proofs, security and secret scans, artifact scans, and `git diff --check`.

Final automated results on 2026-08-07:

- Release build: 0 warnings, 0 errors.
- Complete .NET tests: 290 passed, 0 failed, 0 skipped (provider 2, LocalOperations 133, Desktop 155).
- Complete frontend tests: 228 passed, 0 failed across 14 files.
- Cashier-safe reference and automatic masking proof: 20 passed, including DOM text, accessible-attribute GUID scanning, raw-input masking, masked-only submission, and restart privacy.
- Automatic statutory ID masking proof covers raw alphanumeric and numeric entry, exact first-two/last-four presentation, short-ID fail-safe behavior, masked-only API/state handling, manual-asterisk rejection, accessibility safety, and restart recovery.
- Desktop E2E: 4 scenarios passed.
- Type checking and production build passed.
- SQLCipher/provider, encrypted first-time creation, 50-scenario plaintext migration, restart/durability, statutory, payable-basis, cash, receipt, printing, and print-history proofs passed.
- Secret scanning passed. `npm audit` continues to report the baseline PostCSS 8.5.19 moderate build-tool advisory through Vite; J-007 does not change package metadata, and this is tracked as dependency maintenance rather than a cashier-runtime privacy or P0/P1 defect.

The first Windows walkthrough exposed that the old form required the cashier to type asterisks manually, so that walkthrough is failed and cannot be reused as acceptance. After this correction, the rerun must begin with raw `AB1234567890`, confirm automatic `AB******7890` presentation without manually typed asterisks, repeat with a numeric synthetic ID, and verify restart never restores the raw value. It must then repeat normal ticket and plate resolution, statutory and ordinary flows, amount/readiness changes, Central PMS and fiscal outages, restart, receipt and print flows, reconciliation, compact layout, keyboard operation, and absence of full GUIDs from rendered cashier surfaces. Manual results are recorded only after operator confirmation.

## Conclusion

APT NON-AUTH ENGINEERING BASELINE:
NOT READY

The implementation audit has no unresolved MUST CLOSE NOW software item. The baseline remains not ready until the required J-007 Windows manual walkthrough is confirmed.
