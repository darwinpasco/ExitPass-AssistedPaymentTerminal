# Mode 1 Terminal Shell Validation Summary

Timestamp: 2026-07-14 19:07 Asia/Manila

Commands executed successfully:

- `cmd /c npm install`
- `cmd /c npm run app:typecheck`
- `cmd /c npm run app:test` - 4 files, 18 tests passed.
- `cmd /c npm run app:build`
- `dotnet restore`
- `dotnet build --no-restore` - 0 warnings, 0 errors.
- `dotnet test --no-build` - 8 tests passed.
- `cmd /c npm run e2e` - Playwright E2E passed 3 scenarios.
- `dotnet run --no-build --project src\AssistedPaymentTerminal.Desktop -- --profile=CASHIER_ASSISTED_TERMINAL --packaged-assets --smoke-check`
- `dotnet run --no-build --project src\AssistedPaymentTerminal.Desktop -- --profile=CONTINUITY_TERMINAL --smoke-check` - refused with exit code 2.

Manual smoke evidence:

- `01-terminal-shell.png` - branded Mode 1 shell and bound context.
- `02-valid-ticket.png` - valid ticket lookup with payable amount and tariff expiry.
- `03-expired-tariff.png` - expired tariff blocks payment and shows Recalculate Fee.
- `04-recalculated-tariff.png` - mock recalculation returns refreshed payable basis.
- `05-unsupported-profile.png` - unsupported profile refusal.
- `06-service-unavailable.png` - service unavailable failure with support reference.

Notes:

- Browser smoke uses the production build served by a local static server.
- Desktop smoke validates profile handling and packaged asset resolution without opening a GUI window.
- Full interactive WebView2 visual inspection was not automated; the shell is limited to host startup and asset-resolution smoke in this slice.
