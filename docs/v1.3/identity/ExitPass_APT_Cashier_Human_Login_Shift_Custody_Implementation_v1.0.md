# ExitPass APT Cashier Human Login, Shift, and Custody Implementation v1.0

## Purpose

J-008 replaces production development-cashier authority with the merged I-020 Central PMS APT human-session contract. The sequence is device trust, online username/password authentication, APT audience/permission/Site scope validation, own shift open or resume, own cash-custody open or resume, then payment work.

Human identity, Windows account, device/service identity, human session, cashier shift, and custody remain distinct. Cashier and supervisor APT authentication is username/password only for v1.3. No TOTP, passkey, phone MFA, or offline login is implemented.

## I-020 and I-021 Contracts

The host consumes `POST /v1/apt/human-sessions`, `GET /v1/apt/human-sessions/{sessionReference}`, and the corresponding `continue`, `reauthenticate`, and `logout` operations. Every accepted session requires the `APT` audience, exact device binding, non-GLOBAL Site or Site Group scope, and the I-021A application permission `apt.access`.

I-021A defines separate operation permissions, and I-021B supplies their canonical ACTIVE catalog records and `SITE_OPERATOR` bindings:

- `apt.access` authorizes entry to the authenticated APT workspace.
- `cashier-shifts.operate` authorizes open or resume of only the authenticated cashier's shift.
- `cash-custody.operate` authorizes open or resume of only the authenticated cashier's custody.
- `terminal-cash.receive` supplies the human-permission dimension immediately before tender start and `CASH_RECEIVED`.

Each sensitive operation first refreshes the I-020 current session, then checks its operation-specific permission. Permission loss therefore blocks the next affected operation. `terminal-cash.payable-basis.read` remains strictly the read-only Central PMS payable-basis resolve/revalidate permission. The desktop does not treat it as APT access, shift, custody, or physical-cash authority, and does not redundantly inspect it where Central PMS already enforces the payable-basis endpoint policy.

The current human-session bridge exposes governed open/resume operations for shift and custody; it does not expose cashier close or supervisor handover commands. Any later close operation must enforce the corresponding shift or custody permission and current ownership. Supervisor handover remains deferred pending DR-08/DR-09 and cannot be inferred from the four I-021A permissions.

## Secret Boundary

WPF owns the I-020 token, device binding, and password-entry boundary. It uses the established device identity header, automatic Windows client-certificate selection, correlation IDs, and `ExitPass-HumanSession` authorization. React receives safe presentation and username commands, not passwords, tokens, service credentials, permissions, or password verifiers. Browser storage is not used.

The React/WebView login surface contains no password control, and its bridge DTO contains no password field. An explicit Sign in action from the initial or authentication-required screen asks the Windows host to present a blank native `PasswordBox`. A host-only, short-lived attempt reference binds that dialog to one operation and the current authority version. The host admits at most one credential flow and one native prompt, rejects duplicate bridge submissions, cancels the prompt when authority state changes, and requires the runtime to consume an operation-bound credential proof before `LoginAsync` or `ReauthenticateAsync` can run. Cancellation, close, failure, expiry, a stale callback, an authority-version change, or reuse destroys the attempt. A second normal desktop host for the same terminal is rejected so a stale hidden process cannot own an independent prompt. The host retains the bounded I-020 reauthentication operation for future system-triggered contexts and deterministic tests, but no generic Reauthenticate action is exposed in the normal cashier workspace. The password exists only in the native control and transient call stack for that one request and is cleared from the control when the dialog closes.

The Windows host also disables WebView2 password autosave and general autofill and clears pre-existing password-autosave/autofill profile data before navigation. Those controls are defense in depth, not password-use authority. Browser-populated values, React remounts, form submission, refresh, continuation, session loss, and WebView profile restoration cannot supply a password because the browser contract has no credential field.

Restart material is stored outside SQLite in a DPAPI `CurrentUser`-protected file. Its schema contains only the opaque Central PMS session reference and session token. It contains no username or password. Encrypted SQLite retains operational linkage, not passwords, tokens, or offline-login authority.

## Ownership and Recovery

The runtime serializes login, continuation, refresh, reauthentication, shift/custody, and cash authorization. Invalid credentials, wrong audience/device, missing scope/permission, expiry, revocation, malformed responses, and outage fail closed. Permission denials are classified by boundary as `APT_ACCESS_PERMISSION_DENIED`, `SHIFT_PERMISSION_DENIED`, `CUSTODY_PERMISSION_DENIED`, or `CASH_RECEIVE_PERMISSION_DENIED`; cashier copy remains operational and does not expose permission literals.

Only the authenticated cashier's own shift and custody can resume. Another cashier cannot inherit either. New custody requires an own open shift. Normal logout is blocked with open custody and returns a visible cashier-safe explanation. Expiry, revocation, invalid or missing sessions delete unusable continuation material, clear in-memory session and effective-permission authority, and block new cash while preserving cashier-owned durable accountability. Temporary Central PMS failure also clears current authority for new cash, although the encrypted continuation material may remain for a later online validation attempt. A locked login screen may show that the prior cashier's shift and custody remain open, but those durable facts are explicitly non-authoritative until fresh online authentication succeeds. No failure path falls back to password login or reauthentication. Same-user online authentication can recover the same user's state only after the cashier explicitly enters fresh credentials; SQLite state alone never authorizes cash.

Authority validation is automatic and internal. After sign-in and restart continuation, the runtime validates online before restoring authority. While the authenticated workspace is mounted, it revalidates the current I-020 session every 60 seconds. Shift open/resume, custody open/resume, and pre-cash authorization each perform their own immediate current-session validation regardless of that cadence. Revocation, expiry, invalid state, or unavailable Central PMS therefore transitions the UI to the initialized authentication-required screen and locks new cash without requiring a cashier-facing Refresh authority action. The bridge refresh operation remains available only as an internal runtime/test hook; neither Refresh authority nor a generic Reauthenticate button appears in the normal cashier workflow.

## CASH_RECEIVED

Production rejects development-session creation and checks online human authority before tender start and `CASH_RECEIVED`. React also checks host authority before existing payable-basis/statutory immediate revalidation. Cash requires a current APT session with `apt.access`, current `cashier-shifts.operate`, `cash-custody.operate`, and `terminal-cash.receive`, valid Site/Site Group and device binding, the authenticated cashier's own open shift/custody, Central PMS payable-basis readiness, and POS/fiscal readiness. `terminal-cash.receive` is necessary but never sufficient by itself.

Authentication loss before `CASH_RECEIVED` blocks it. Authentication loss afterward does not rewrite durable physical-cash history.

## Development Isolation

Configured development cashier, shift, and fabricated authentication references are not production authority. The UI fixture requires mock mode, loopback, and explicit `?humanSessionFixture=1`.

`USE_MOCK_CENTRAL_PMS` belongs to the React Central PMS client configuration. The Windows host does not read that setting: it always constructs `CentralPmsHumanSessionClient` from `CENTRAL_PMS_BASE_URL`, `APT_CENTRAL_PMS_SERVICE_IDENTITY_ID`, and automatic Windows client-certificate selection. Consequently, mock mode alone cannot replace I-020 login, readback, continuation, reauthentication, or logout. A synthetic human session is possible only when all three development gates are present: React mock mode, a loopback origin, and the explicit `humanSessionFixture=1` query parameter.

The original walkthrough's mock setting did not bypass host I-020 when the fixture query was absent, but it was ambiguous because non-authentication React calls remained mocked. The acceptance walkthrough therefore uses a generated, ignored `dist/apt-config.json` with `USE_MOCK_CENTRAL_PMS=false`, the disposable Central PMS URL, and no `humanSessionFixture` query. `APT_ENABLE_NON_LIVE_CASH_CAPTURE=true` remains a separate host-owned control for simulated physical cash; it does not mock authentication.

## Real I-020 Windows Walkthrough Configuration

The disposable Central PMS environment must authorize the configured terminal, Site, Site Group, service identity, and Windows client certificate. The certificate must be installed with its private key in the current Windows user's certificate store and trusted by the disposable Central PMS endpoint; the APT uses automatic Windows certificate selection and has no source-controlled certificate path or thumbprint setting.

Build a disposable live-mode frontend in Window 1. Enter the real disposable URL when prompted; do not append `humanSessionFixture=1`:

```powershell
Set-Location D:\wt\J008
$centralPmsBaseUrl = (Read-Host 'Disposable Central PMS base URL').TrimEnd('/')
npm.cmd run app:build
$runtimeConfigPath = 'D:\wt\J008\src\AssistedPaymentTerminal.App\dist\apt-config.json'
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$runtimeConfig.CENTRAL_PMS_BASE_URL = $centralPmsBaseUrl
$runtimeConfig.USE_MOCK_CENTRAL_PMS = 'false'
$runtimeConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $runtimeConfigPath -Encoding utf8
& 'D:\wt\J008\node_modules\.bin\vite.cmd' preview --host 127.0.0.1 --port 4173
```

Start the host in Window 2. Supply only disposable identifiers. Enter cashier, second-cashier, and supervisor passwords only in the native Windows credential dialog opened contextually by Sign in, never in the React page, environment variables, or command history:

```powershell
Set-Location D:\wt\J008
$env:APT_PROFILE = 'CASHIER_ASSISTED_TERMINAL'
$env:APT_WEB_UI_URL = 'http://127.0.0.1:4173'
$env:APT_TERMINAL_ID = 'APT-DEV-001'
$env:APT_SITE_ID = '11111111-1111-1111-1111-111111111111'
$env:APT_SITE_GROUP_ID = '22222222-2222-2222-2222-222222222222'
$env:APT_POS_SERVER_ID = 'POS-DEV-001'
$env:CENTRAL_PMS_BASE_URL = (Read-Host 'Disposable Central PMS base URL').TrimEnd('/')
$env:APT_CENTRAL_PMS_SERVICE_IDENTITY_ID = (Read-Host 'Disposable APT service identity ID').Trim()
$env:USE_MOCK_CENTRAL_PMS = 'false'
$env:APT_ENABLE_NON_LIVE_CASH_CAPTURE = 'true'
$env:APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION = 'false'
$env:APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE = 'false'
$env:APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL = 'false'
$env:APT_LOCAL_DB_PATH = 'D:\Temp\ExitPass\J008\LocalOperations\cash-journal.db'
$env:APT_HUMAN_SESSION_CREDENTIAL_PATH = 'D:\Temp\ExitPass\J008\human-session.credential'
dotnet run --project src\AssistedPaymentTerminal.Desktop --configuration Release
```

The two URL prompts must receive the same value. Before starting Window 2, verify the approved disposable certificate is available with `Get-ChildItem Cert:\CurrentUser\My | Where-Object HasPrivateKey`. The manual evidence must show requests to `/v1/apt/human-sessions` and its readback, continuation, reauthentication, and logout routes at that disposable URL. A mock ticket/payment fixture may not be cited as authentication evidence.

## Validation and Policy Gate

The Windows host recognizes two explicit initialized frontend states. `[data-testid='apt-human-login-shell'][data-app-ready='true']` means the React application is ready for device-trusted human authentication; it grants no shift, custody, or cash authority. `[data-testid='apt-terminal-shell'][data-app-ready='true']` means the authenticated operational shell mounted. Invalid credentials, expiry, and revocation remain on or return to the initialized login shell without triggering a generic startup failure, while new cash remains blocked. Blank, partially mounted, configuration-refused, navigation-failed, and JavaScript-failed pages expose neither marker and still fail startup readiness.

Automated coverage proves I-020 routes, device binding, no MFA UI, operation-specific I-021 permission checks and post-login revocation, non-GLOBAL scope, ownership, logout, expiry/revocation, restart continuation, DPAPI ciphertext, no local `CASH_RECEIVED` mutation after authorization denial, production bridge isolation, native one-shot credential consumption, browser credential exclusion, and browser-storage exclusion. Current-session readback treats terminal I-020 outcomes as locked, invalidates the opaque continuation credential, clears every effective operational permission, and returns the initialized login shell without discarding owned open shift/custody evidence. Malformed or unavailable readback also removes current authority and cannot leave the prior operational shell authoritative. The Windows WebView2 smoke proof uses the actual host profile settings, injects an autofill-like browser password value, submits the login form, and verifies the WebView emits only a username-only prompt request. Host tests prove that duplicate submissions, concurrent prompts, cancellation, stale and delayed results, authority-version changes, and attempt reuse produce zero additional Central PMS password calls. Three consecutive automatic validation cycles after authority loss cannot invoke a prompt, login, or reauthentication. Existing encryption, migration, statutory, payable-basis, receipt, printing, and reconciliation regressions remain mandatory.

Shift and custody ownership are stored against the stable I-020 `UserReference`. Human-session bridge snapshots serialize durable `Open`/`Closed` status values as strings, matching the React contract used for own-shift presentation and custody enablement. A successful shift open or resume returns the reconciled durable state immediately; refresh and online restart reconstruction query the same ownership key and never treat another cashier's open state as local authority.

Manual Windows validation must use disposable identities and cover valid/invalid login, own and cross-cashier state, logout, expiry/revocation, same-user recovery, restart, outages, and cash/receipt/printing regression.

The bounded walkthrough is not complete until an operator directly observes all of the following against disposable I-020 identities and an approved device certificate:

1. Device trust failure leaves Sign in disabled; trusted-device startup enables Sign in without an MFA prompt. Sign in opens a blank native Windows password dialog; no password field exists in WebView2.
2. Valid cashier credentials establish an `APT` session and invalid credentials return only the safe server classification.
3. The cashier opens or resumes only their own shift, then opens or resumes only their own custody using the existing opening denomination posture.
4. A second cashier cannot inherit the first cashier's shift or custody.
5. Normal logout with open custody is denied without closing or erasing custody.
6. Expiry and revocation are detected by the normal automatic validation interval or the next sensitive operation. The UI stops reporting `Session status: Current` and `Online cashier authority current`, returns to the initialized authentication-required shell, and locks new cash while visibly preserving durable open shift and custody as non-authoritative accountability facts. Three full automatic validation intervals and an ordinary sensitive-operation attempt must create no native prompt, new `PASSWORD` authentication attempt, or successor session. The same cashier restores authority only by clicking the contextual Sign in action, entering the password into the newly presented native Windows dialog, and explicitly submitting it.
7. Restart revalidates online for no-shift, own-shift, and own-custody states; revoked, expired, other-cashier, and Central-PMS-unavailable restart states remain locked.
8. Central PMS outage, POS/fiscal outage, and failed payable-basis/statutory immediate revalidation all block `CASH_RECEIVED`.
9. A successful governed cash flow records `CASH_RECEIVED` once, survives restart, and retains receipt retrieval, ORIGINAL/REPRINT, print-history, and reconciliation behavior.
10. SQLite contains no password, token, continuation secret, or offline-login material; React/browser storage contains no human-session secret; ordinary cashier presentation contains no internal identity GUID.
11. The Central PMS authentication-attempt audit shows zero successor `PASSWORD` attempts after revocation, three automatic validation intervals, and an ordinary sensitive-operation attempt; exactly one new attempt may appear only after the cashier uses the contextual Sign in action, enters fresh credentials in a new native dialog, and explicitly submits it.

The direct Windows walkthrough completed against disposable Central PMS and PostgreSQL fixtures on 2026-08-09. Representative headed observations covered native-password cashier sign-in, online continuation, own shift/custody recovery, automatic revocation lockout, preserved physical accountability, contextual recovery, cross-cashier denial, and blocked sign-out with the visible open-custody explanation. After revoking the exact active session at `2026-08-09T13:38:01.762156Z`, three unattended validation observations at `13:39:25.3845857Z`, `13:40:33.3263393Z`, and `13:41:41.0531249Z` each found zero new `PASSWORD_VERIFIED` attempts, zero successor sessions, and zero native credential prompts. Explicit Sign in then produced exactly one native prompt, one `PASSWORD_VERIFIED` attempt, and one successor session, after which the same durable shift and custody were recovered without duplication. Existing deterministic suites cover the remaining outage, fiscal, payable-basis, statutory, receipt, printing, restart, security, and privacy combinations.

The historical unexpected Central PMS correlation could not be retroactively mapped to a local caller because the earlier host did not emit operation traces. The corrected host now records bounded, non-secret authentication invocation evidence, and every observed post-correction Central PMS password correlation maps to an explicit native prompt submission. Controlled UAT remains not authorized.

Full supervisor custody handover remains fail closed pending DR-08/DR-09 and an approved operation contract. Same-session password reauthentication is implemented; supervisor authority is not invented. Controlled UAT and production rollout are not authorized.
