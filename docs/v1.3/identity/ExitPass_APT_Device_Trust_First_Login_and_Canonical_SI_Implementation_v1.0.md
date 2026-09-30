# APT Device Trust, First Login, and Canonical Sales Invoice

## Scope

This task corrects the PITX local runtime connection, consumes the governed Central PMS first-login password-change state, recognizes the canonical `APT_CASHIER_OPERATOR` capability, and removes APT fiscal-fact reconstruction from Sales Invoice preview and printing.

## Device trust and connectivity

The PITX launcher previously targeted `https://localhost:56064`. That listener requires an approved client certificate, but the stale APT runtime had no matching certificate and Schannel failed before Central PMS could evaluate device trust. The disposable Central PMS runtime instead publishes an approved loopback development endpoint supplied through the PITX launcher.

`Start-AptPitxLocal.ps1` now uses that loopback endpoint, the registered APT service identity, PITX terminal/Site/Site Group values, and the canonical PITX vendor-system identifier. A request without the service identity is denied with `APT_DEVICE_TRUST_REQUIRED`; the same request with the registered identity reaches credential validation. Production TLS validation and client-certificate behavior are unchanged. No offline login exists.

## First-login contract

Central PMS is authoritative for credential verification, password policy, temporary-password status, authenticator verification, account status, APT audience, and Site scope.

An accepted temporary password returns a device-bound restricted APT session with `PASSWORD_CHANGE_REQUIRED`. The APT does not persist that restricted continuation credential and does not publish cashier, shift, custody, or cash authority. It opens a native Windows password-change dialog for:

- current temporary password;
- new password;
- new-password confirmation;
- authenticator code required by the Central PMS password-mutation contract.

The current/new/confirmation/authenticator values are operation-bound, one-shot values in the desktop host. React receives only username, safe state, and commands. The values are cleared when submitted, rejected, cancelled, or closed and are never written to SQLite, the DPAPI continuation file, browser storage, logs, or diagnostics.

After Central PMS returns `PASSWORD_CHANGED`, the APT retries authentication with the newly entered password. Only the resulting normal device-bound APT session can establish cashier authority. A rejected current password, invalid authenticator code, password-policy failure, malformed response, or Central PMS outage leaves authority unavailable.

Routine APT sign-in does not request TOTP. The authenticator challenge above is specific to the governed password-change operation. This task does not change Management Platform MFA policy or implementation.

## APT cashier role

The current canonical `APT_CASHIER_OPERATOR` role grants `apt.cashier.operate`. The APT accepts that capability at its existing access, own-shift, own-custody, and pre-`CASH_RECEIVED` human-authorization gates. It does not authorize by role name, does not grant GLOBAL scope, and does not bypass user, session, device, Site/Site Group, ownership, payable-basis, POS/fiscal, or Central PMS readiness checks. Older operation-specific permissions remain accepted for compatibility with existing I-021 sessions.

Central PMS also admits `apt.cashier.operate` at the APT payable-basis resolve/readiness policy used for ticket and plate lookup. This is an any-of policy with the dedicated read-only `terminal-cash.payable-basis.read` capability, not a role-name check or a broad grant. Existing human-session, trusted-device, terminal, Site/Site Group, and request-scope guards still apply, so an APT cashier can resolve only the authorized terminal workflow and gains no reporting, supervisor, Management Platform, or cross-Site authority.

## Canonical Sales Invoice

POS Server remains the sole fiscal Sales Invoice authority. Central PMS receipt retrieval supplies the POS-owned `DigitalSalesInvoicePresentationModel`. The APT acts only as a presentation adapter:

- React renders the ordered POS-owned section labels and row `displayValue` fields without calculating fiscal fields;
- print preparation uses those same canonical display values;
- REPRINT adds only the governed copy marker and does not alter fiscal facts;
- restart recovery rereads the persisted authoritative receipt retrieval command;
- missing required canonical presentation rows fail closed with no fallback receipt;
- internal IDs, audit hashes, raw values, placeholder rows, and deferred artifacts are excluded from cashier presentation.

The removed duplicate builder previously guessed alternate field names and assembled seller identity, document details, parking facts, lines, subtotal, discounts, VAT, tenders, customer/footer text, and accreditation metadata. APT no longer determines or reconstructs those facts; it formats only the canonical POS labels and display values for screen and 57/58/80 mm output.

## Safety invariants

- Device trust precedes human authentication.
- Cached local state never authorizes login.
- Password change is mandatory and cannot be deferred into cashier authority.
- Human login remains separate from shift and cash custody.
- Immediate online human authorization remains required at `CASH_RECEIVED`.
- Central PMS outage remains fail closed.
- ORIGINAL, REPRINT, and restart recovery use one canonical POS Sales Invoice representation.
