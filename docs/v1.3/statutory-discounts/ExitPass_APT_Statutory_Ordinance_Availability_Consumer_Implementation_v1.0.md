# ExitPass APT Statutory Ordinance Availability Consumer Implementation v1.0

## Purpose

The Cashier-Assisted Terminal consumes Central PMS Site-level statutory ordinance availability before exposing a Senior Citizen or PWD request path. Central PMS remains the authority for jurisdiction, policy status, Site and Site Group scope, and immediate pre-cash revalidation.

This implementation does not determine customer entitlement, apply a statutory benefit, calculate a payable amount, or interpret ordinance content.

## Central PMS Contract

The existing APT facade client calls these J-005 operations:

- `POST /v1/apt/statutory-discounts/ordinance-availability/resolve`
- `POST /v1/apt/statutory-discounts/ordinance-availability/revalidate`

Requests use the already-resolved `parkingSessionId`, authoritative Site and Site Group references, terminal context, and one entitlement type: `SENIOR_CITIZEN` or `PWD`. UI components do not construct service identity, permission, or authorization headers.

The consumer accepts only the merged J-005 classifications: `AVAILABLE`, `NOT_AVAILABLE`, `NO_CONFIGURED_POLICY`, `NOT_YET_EFFECTIVE`, `EXPIRED`, `INACTIVE`, `AMBIGUOUS_SCOPE`, `SESSION_NOT_FOUND`, `AMBIGUOUS_SESSION`, `SOURCE_UNAVAILABLE`, `MALFORMED_AUTHORITATIVE_STATE`, `ACCESS_DENIED`, and `UNEXPECTED_FAILURE`.

The wire operation values are the canonical uppercase `RESOLVE` and `REVALIDATE`. Immediate revalidation passes only with `PASSED_UNCHANGED`; contradictory flags or operation values are classified locally as malformed and fail closed.

## Availability Flow

After ticket or plate resolution returns an authoritative payable basis, the terminal resolves Senior Citizen and PWD availability separately. It does not expose entitlement or evidence controls while either response is unknown. Only `AVAILABLE` with `statutoryRequestAllowed=true` enables the corresponding existing statutory request workflow.

No coverage, future, expired, inactive, ambiguous, denied, malformed, and unavailable states do not create a statutory request. Retry is shown only when Central PMS marks the result retryable. Ordinary payment remains a separate path and remains available when its existing payable-basis, fiscal, local persistence, shift, and custody prerequisites pass.

Responses are matched against the current parking session, Site, Site Group, and entitlement. A late response for an earlier lookup or a mismatched authoritative scope is discarded and cannot enable controls.

## Restart And Recovery

The existing encrypted SQLite workflow payload may retain only advisory facts needed to explain a recovered UI flow: parking-session reference, Site reference, entitlement, safe classification, support reference, evaluation timestamp, and correlation reference. The snapshot is explicitly non-authoritative.

On restart, the operational WebView waits for encrypted persistence as before, restores the pending workflow, and then resolves both entitlement types again through Central PMS. A stored `AVAILABLE` value cannot enable request initiation or cash acceptance without fresh readback.

No ordinance text, policy rules, ordinance documents, evidence images, service credentials, privileged headers, raw exceptions, or internal URLs are persisted.

## Pre-Cash Boundary

For an active statutory workflow, each pre-cash pass performs both:

1. the existing canonical payable-basis revalidation; and
2. J-005 ordinance revalidation for the selected entitlement.

This runs when the cashier selects Continue to Cash and again immediately before the local `CASH_RECEIVED` custody event. The statutory path proceeds only for `PASSED_UNCHANGED`, `AVAILABLE`, matching session and scope, `preCashRevalidationPassed=true`, and `readyForStatutoryCashFlow=true`.

Every other outcome closes the statutory cash path. It does not write `CASH_RECEIVED`, denomination completion, tender custody, payment commands, or fiscal commands. A changed amount continues to use the existing acknowledgement and idempotency contract. Ordinary payment requires a new independently authoritative ordinary-payable flow.

## Security And Privacy

- The desktop calls only the Central PMS APT facade. It has no HikCentral client or ordinance database access.
- Coverage is advisory in local storage and cannot authorize workflow or cash actions.
- Safe server messages, support references, retryability, and evaluation timestamps may be shown.
- Raw errors, SQL, policy internals, ordinance evidence, customer identity documents, credentials, and privileged service headers are excluded.
- The existing decision, application, payment, fiscal, receipt, and terminal-cash idempotency behavior is unchanged.

## Validation

Focused frontend tests cover both entitlements, one-entitlement coverage, no coverage, all canonical blocked classifications, retryable failure, malformed and cross-Site responses, restart re-resolution, ordinary-payment independence, and both immediate J-005 checks before local custody.

The existing development-only statutory visual-smoke surface remains isolated from production configuration and exercises the pre-cash and post-restart workflows without live payment, fiscal, receipt, printer, HikCentral, or gate operations. Full .NET, frontend, E2E, security, and artifact validation is required before the bounded Windows walkthrough.

## Exclusions

This slice does not add policy interpretation, jurisdiction resolution, customer entitlement adjudication, Operator Console approval behavior, payable calculation, production authentication UI, HikCentral access, policy persistence, ordinance-document storage, controlled UAT, or production rollout.
