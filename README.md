# ExitPass Assisted Payment Terminal

ExitPass Assisted Payment Terminal is a separate cashier-facing terminal product for staffed parking sites. The initial profile is Mode 1, `CASHIER_ASSISTED_TERMINAL`, delivered as a React/TypeScript/Vite application hosted by a thin .NET WPF WebView2 Windows shell.

## Mode 1 Scope

The terminal requires an approved device-bound Central PMS APT human session before cashier work. After online username/password authentication, it resolves the cashier's own shift and cash-custody state and preserves the governed payment, fiscal, receipt, printing, and restart-recovery paths.

## Authority Boundaries

Business authority remains with:

- Vendor PMS for raw parking session and tariff authority.
- Central PMS for canonical ExitPass payment-linked state.
- Payment Orchestrator for provider execution and verified provider outcomes.
- POS Server for fiscal issuance, numbering, fiscal records, and controlled void behavior.
- Central PMS and Gate Integration for exit authorization and gate authority.

The terminal must never own tariff calculation, payment finality, fiscal numbering, fiscal records, direct database access, direct Vendor PMS access, direct gate control, or administrative Operator Console workflows.

Mode 2 `CONTINUITY_TERMINAL` is not implemented.

## Repository Structure

```text
src/
  AssistedPaymentTerminal.App/       React, TypeScript, Vite UI
  AssistedPaymentTerminal.Desktop/   Thin WPF WebView2 host
tests/
  AssistedPaymentTerminal.Desktop.Tests/
  AssistedPaymentTerminal.EndToEndTests/
contracts/
  central-pms/                       Inspected contract snapshot
docs/evidence/mode1-terminal-shell/  Validation evidence
scripts/                            Repository checks
packaging/windows/                   Reserved for later Windows packaging
```

No device service is included in this slice.

## Prerequisites

- .NET SDK selected in this repo: `8.0.421`, targeting `net8.0-windows`.
- Local machine also has .NET SDK `10.0.301`; the repo pins .NET 8 because ExitPass reference standards target .NET 8.
- Node.js observed locally: `v24.16.0`.
- npm observed locally: `11.13.0`.
- WebView2 Runtime for the desktop shell.

PowerShell execution policy may block `npm.ps1`; use `cmd /c npm ...` if needed.

## Configuration

The browser app loads non-secret runtime settings from:

```text
src/AssistedPaymentTerminal.App/public/apt-config.json
```

Required setting names:

- `APT_PROFILE`
- `APT_TERMINAL_ID`
- `APT_TERMINAL_DISPLAY_NAME`
- `APT_SITE_ID`
- `APT_SITE_NAME`
- `APT_SITE_GROUP_ID`
- `APT_POS_SERVER_ID`
- `CENTRAL_PMS_BASE_URL`
- `USE_MOCK_CENTRAL_PMS`
- `APT_WEB_UI_URL`

The Windows host additionally requires `APT_CENTRAL_PMS_SERVICE_IDENTITY_ID` and approved client-certificate/device trust for live Central PMS calls. `APT_HUMAN_SESSION_CREDENTIAL_PATH` may override the DPAPI CurrentUser-protected continuation file for bounded validation. Cashier identity, shift, and custody are not production configuration values.

Development values are explicitly non-production. Do not commit real credentials, private keys, certificates, production terminal identities, or payment secrets.

## Mock Ticket Scenarios

With `USE_MOCK_CENTRAL_PMS=true`:

- `APT-ACTIVE-1001`: valid active tariff.
- `APT-EXPIRED-2001`: expired tariff; Recalculate Fee returns a fresh payable basis.
- `APT-NOTFOUND-404`: ticket not found.
- `APT-INACTIVE-3001`: inactive or invalid session.
- `APT-AMBIG-409`: ambiguous result.
- `APT-UNAVAILABLE-503`: service unavailable.
- `APT-MALFORMED-502`: malformed response or contract error.
- `APT-RECALC-FAIL`: expired tariff with mock recalculation failure.

## Frontend Run

For the canonical live PITX local runtime, start the Vite UI and Windows host together:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-AptPitxLocal.ps1
```

The launcher verifies Central PMS at `https://localhost:56064`, serves the APT UI at `http://localhost:5173`, supplies the approved PITX terminal context to the native host, and restores the tracked `apt-config.json` when it exits.

For mock-only frontend development:

```powershell
npm.cmd ci
cmd /c npm run app:dev
```

Open `http://localhost:5173`.

The browser-only development human-session fixture is disabled by default. It requires mock mode, a loopback origin, and the explicit `?humanSessionFixture=1` test flag.

`USE_MOCK_CENTRAL_PMS` controls the React Central PMS client. It is not consumed by the Windows-host I-020 client. The host always uses `CENTRAL_PMS_BASE_URL`, `APT_CENTRAL_PMS_SERVICE_IDENTITY_ID`, and Windows client-certificate trust for human sessions. Live I-020 validation should still set the generated frontend runtime config to `USE_MOCK_CENTRAL_PMS=false` and must omit `humanSessionFixture=1`; see the J-008 implementation note.

## Desktop Shell Run

Development URL mode:

```powershell
$env:APT_PROFILE='CASHIER_ASSISTED_TERMINAL'
$env:APT_WEB_UI_URL='http://localhost:5173'
$env:CENTRAL_PMS_BASE_URL='https://localhost:56064'
$env:APT_CENTRAL_PMS_SERVICE_IDENTITY_ID='00000000-0000-4000-8000-000000000001'
dotnet run --project src\AssistedPaymentTerminal.Desktop
```

Production-style packaged asset mode:

```powershell
cmd /c npm run app:build
dotnet build src\AssistedPaymentTerminal.Desktop
dotnet run --project src\AssistedPaymentTerminal.Desktop -- --profile=CASHIER_ASSISTED_TERMINAL --packaged-assets
```

## Test Commands

```powershell
cmd /c npm run app:typecheck
cmd /c npm run app:test
cmd /c npm run app:build
dotnet restore
dotnet build
dotnet test
cmd /c npm run e2e
.\scripts\check-no-secrets.ps1
git diff --check
```

## Contract Integration Strategy

Central PMS `POST /v1/vendor-parking/resolve` was inspected from the reference repository at source commit `951a7c0f5fb03efbab8de03d555820f1e0d420d5`. This repo stores a narrow JSON snapshot under `contracts/central-pms/vendor-parking-resolve.contract.json` and implements a TypeScript adapter matching the inspected request, success response, error envelope, and `X-Correlation-Id` propagation.

Generated-client publication remains pending. This slice does not copy Central PMS DTO source, reference ExitPass projects, use submodules, or source-link internal code.

## Current Limitations

- Full governed supervisor custody handover remains fail closed pending the owner-policy resolution for DR-08/DR-09.
- Physical printer and cash-drawer certification remain Controlled-UAT/hardware work; cash drawer capability remains optional and disabled by default.
- No offline human login or cached authorization is available.
- Mode 2 continuity behavior is refused at startup.

## Next Recommended Slice

The smallest follow-on slice is an approved payable-basis refresh/recalculation integration if Central PMS publishes it. If payment contracts are approved first, implement payment-attempt creation with backend idempotency and status readback, still stopping before fiscal issuance until POS fiscal contracts are approved for terminal use.
