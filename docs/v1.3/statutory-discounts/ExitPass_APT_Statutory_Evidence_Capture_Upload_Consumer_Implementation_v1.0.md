# ExitPass APT Statutory Evidence Capture and Upload Consumer Implementation v1.0

## Status and authority

This note records J-006, the Windows Cashier-Assisted Terminal consumer of the merged Central PMS I-016 APT statutory-evidence contract. Central PMS remains authoritative for evidence governance, required item role, lifecycle, validation, malware scanning, reviewability, replacement permission, statutory approval, application, and evidence readiness.

The APT is a capture and recovery client. It does not interpret evidence, infer clean-scan or review outcomes, apply a statutory benefit, expose a preview/download path, or access HikCentral or object storage directly.

## Consumed I-016 routes

The WPF host calls only these APT-channel Central PMS routes:

| Operation | Method and route |
|---|---|
| Bootstrap requirements and rediscover lifecycle | `POST /v1/apt/statutory-discounts/evidence/bootstrap` |
| Read authoritative status | `GET /v1/apt/statutory-discounts/evidence/status` |
| Immediate evidence revalidation | `POST /v1/apt/statutory-discounts/evidence/revalidate` |
| Create opaque upload session | `POST /v1/apt/statutory-discounts/evidence/upload-sessions` |
| Stream evidence through Central PMS | `PUT /v1/apt/statutory-discounts/evidence/upload-sessions/{opaqueUploadSessionReference}` |
| Finalize upload | `POST /v1/apt/statutory-discounts/evidence/upload-sessions/{opaqueUploadSessionReference}/finalize` |

The request context is the existing statutory decision command reference. Central PMS derives the terminal, Site, Site Group, parking session, statutory request, governance profile, media rules, and evidence set/item authority. The desktop does not submit those values as trusted authority.

## Desktop boundary

`MainWindow` owns a separate `apt-statutory-evidence` WebView bridge. The React layer sends bounded commands to that bridge and never constructs Central PMS service-authentication headers. The WPF host reads `APT_CENTRAL_PMS_SERVICE_IDENTITY_ID`, attaches the existing APT service identity and correlation headers, and calls the I-016 APT routes.

There is no production command-line switch for evidence credentials or validation behavior. The React bundle contains no service identity, permission header, authorization token, storage credential, provider endpoint, scanner endpoint, or internal route credential.

## Capture and upload sequence

1. The React statutory panel requests bootstrap after a Central PMS decision reference exists.
2. Central PMS returns allowed media types, maximum content length, optional image limits, required role, lifecycle, replacement posture, and authoritative readiness.
3. The WPF host opens the native Windows single-file picker for JPEG or PNG.
4. The host checks non-empty length, server maximum length, and declared media type for operator feedback. These checks are not evidence authority.
5. The host generates a transient selection reference. The selected absolute path and SHA-256 value remain in process memory only.
6. The host re-reads authoritative Central PMS status before creating an upload session.
7. The host verifies the source file has not changed, requests an opaque I-016 upload session, and binds that session to the transient selection in memory.
8. The host streams the original file with a bounded `FileStream` buffer to the Central PMS `PUT` route. It creates no application-data or temporary copy.
9. Cancellation stops the local stream and requires authoritative reconciliation. It does not claim provider deletion.
10. The host finalizes through Central PMS and discards the transient file/session mapping.
11. The React panel refreshes or polls bounded pending states from Central PMS.

The APT never receives an object key, bucket/container, signed provider URL, provider header, permanent credential, checksum result, scanner endpoint, or raw scanner response in its public bridge contract.

## Lifecycle model

The UI preserves the I-016 lifecycle values rather than collapsing them:

- `NOT_REQUIRED`
- `REQUIRED_NOT_STARTED`
- `ITEM_CREATED`
- `UPLOAD_SESSION_AVAILABLE`
- `UPLOAD_IN_PROGRESS`
- `UPLOADED`
- `VALIDATION_PENDING`
- `VALIDATION_FAILED`
- `SCAN_PENDING`
- `SCAN_RETRYABLE`
- `SCAN_FAILED`
- `MALWARE_DETECTED`
- `NOT_REVIEWABLE`
- `REVIEWABLE`
- `REVIEW_PENDING`
- `APPROVED`
- `REJECTED`
- `APPLIED`
- `UNKNOWN_FAIL_CLOSED`

Only Central PMS can change these states. Validation success is not approval, a clean scan is not approval, reviewability is not approval, and approval without applied payable basis is not cash readiness. Unknown, malformed, unavailable, expired, interrupted, pending, rejected, and unapplied states fail closed for statutory cash.

## Replacement

The file-selection control follows only `REPLACEMENT_ALLOWED` or `REPLACEMENT_NOT_ALLOWED` from Central PMS. Allowed replacement warns that prior evidence is superseded according to server policy and uses a new opaque session. Denied replacement disables selection and offers no desktop override. Existing in-memory session state is discarded before a new selection.

## Encrypted local recovery

No database schema was added. The existing encrypted payable-basis recovery JSON may contain only advisory evidence metadata:

- statutory decision command reference
- opaque evidence set/item references
- opaque upload-session reference and expiry when reconciliation is required
- lifecycle and replacement classifications
- safe readiness flags and blocker code
- correlation reference and last synchronization time
- file-reselection requirement

Every stored record has `authoritative: false`. It does not contain evidence bytes, Base64, a source path, checksum, ETag, object key, bucket/container, provider endpoint, upload target, signed URL, service credential, scanner data, statutory ID, customer identity, or reviewer identity.

On restart, parsing forces any recovered evidence state to `STALE_LOCAL_STATE`, `readyForAptPreCash: false`, and `fileReselectionRequired: true`. The panel bootstraps Central PMS again. The original file is never reopened automatically. Central PMS readback replaces stale local claims.

## CASH_RECEIVED boundary

The existing `CASH_RECEIVED` transition remains the irreversible local physical-custody boundary. For an active statutory path, both Continue to Cash and Record Cash Received retain the existing payable-basis, ordinance, terminal-cash, local shift/custody, Sales Invoice, POS Server, and fiscal checks.

Immediately before local custody recording, the APT additionally calls the I-016 evidence revalidation route. Cash can proceed only when:

- the authoritative evidence lifecycle is `NOT_REQUIRED` or `APPLIED`;
- `readyForAptPreCash` is true; and
- the canonical payable-basis `statutoryEvidenceReadiness` dimension is ready.

Upload, finalization, validation, scan, reviewability, or approval alone cannot satisfy this boundary. `AMOUNT_CHANGED` returns to the existing amount acknowledgement flow and the stale amount is not accepted. Central PMS, ordinance, POS, or fiscal failure prevents statutory cash without writing custody or tender evidence.

Ordinary non-statutory payment remains on its existing governed path. Evidence failure is not converted to no evidence required and does not silently abandon a pending statutory request.

## Safe operator experience

The panel displays server-derived JPEG/PNG and size rules, generic selected-file metadata, bounded indeterminate upload progress, cancel/retry actions, authoritative lifecycle, replacement posture, readiness blockers, correlation/support-safe messages, and refresh. Native buttons and progress semantics remain keyboard reachable, carry visible focus through the existing design system, and wrap long safe messages at compact and high-DPI desktop sizes.

No evidence preview, download, OCR, biometric processing, camera/scanner hardware integration, or review UI is included.

## Automated proof

Focused frontend tests cover bootstrap, media rules, JPEG/PNG, PDF/empty/oversize rejection, opaque upload flow, cancellation, lifecycle distinctions, replacement denial, restart rediscovery, advisory persistence, and storage-internal exclusion. Desktop tests cover native selection validation, source mutation protection, streaming, cancellation, replay/finalization cleanup, exact I-016 routes, host-owned service authentication, safe HTTP failure classification, and no provider route.

The existing statutory visual-smoke suites continue to prove payable-basis and ordinance revalidation, amount changes, no `CASH_RECEIVED` on failure, single custody recording, restart recovery, ordinary-payment independence, and POS/fiscal fail-closed behavior. Full .NET, frontend, E2E, SQLCipher, plaintext-migration, secret, privacy, prohibited-storage, direct-integration, and artifact scans are required before review.

## Significant Windows walkthrough

Significant Windows validation passed by user acceptance using synthetic JPEG/PNG files, the disposable I-016 Central PMS environment, and the existing development cash harness. Representative scenarios were directly observed for ready-for-capture evidence, replacement allowed, lifecycle and readiness presentation, evidence-readiness cash blocking, Windows file selection, review pending, safe blocker and support-reference presentation, and desktop layout and usability.

The remaining scenarios were accepted based on the completed automated, deterministic, disposable-environment, encrypted SQLite, privacy, restart, outage, readiness, and security coverage. Keyboard accessibility, visible focus, and narrow, standard, and high-DPI responsive behavior are accepted as passed. Disposable PostgreSQL, MinIO, ClamAV, Central PMS, synthetic files, encrypted local database, logs, and validation processes were removed after acceptance.

Controlled UAT and production rollout remain unauthorized pending their separate governance gates.
