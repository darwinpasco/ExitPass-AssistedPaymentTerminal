export const activeShiftId = "SHIFT-DEV-20260714-A";
export const activeCustodyId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa0001";

const context = {
  cashierId: "CASHIER-DEV-001",
  terminalId: "APT-DEV-001",
  siteId: "11111111-1111-1111-1111-111111111111",
  siteGroupId: "22222222-2222-2222-2222-222222222222",
  posServerId: "POS-DEV-001",
};

export async function installLocalJournalBridgeFixture(page, options = {}) {
  const includeShift = options.includeShift !== false;
  const includeCustody = options.includeCustody === true;
  const shiftStatus = options.shiftStatus ?? "Open";

  await page.addInitScript(
    ({ activeShiftId, activeCustodyId, context, includeShift, includeCustody, shiftStatus }) => {
      const listeners = new Set();
      const activeShift = includeShift
        ? {
          id: activeShiftId,
          cashierId: context.cashierId,
          authenticatedCashierSessionReference: `dev-auth:${context.cashierId}:${activeShiftId}`,
          terminalId: context.terminalId,
          siteId: context.siteId,
          siteGroupId: context.siteGroupId,
          posServerId: context.posServerId,
          openedAt: "2026-07-14T08:00:00Z",
          closedAt: shiftStatus === "Closed" ? "2026-07-14T16:00:00Z" : null,
          status: shiftStatus,
        }
        : null;
      const activeCashCustodySession = includeCustody
        ? {
          id: activeCustodyId,
          cashierId: context.cashierId,
          authenticatedCashierSessionReference: `dev-auth:${context.cashierId}:${activeShiftId}`,
          cashierShiftId: activeShiftId,
          terminalId: context.terminalId,
          siteId: context.siteId,
          siteGroupId: context.siteGroupId,
          posServerId: context.posServerId,
          openingCashAmount: 0,
          openedAt: "2026-07-14T08:05:00Z",
          status: "Open",
        }
        : null;

      window.chrome = {
        webview: {
          postMessage(message) {
            const request = JSON.parse(message);
            if (request.source !== "apt-local-journal") {
              return;
            }

            const payload = payloadFor(request.command);
            const response = {
              source: "apt-local-journal",
              ok: true,
              command: request.command,
              correlationId: request.correlationId,
              payload,
            };

            queueMicrotask(() => {
              for (const listener of listeners) {
                listener({ data: JSON.stringify(response) });
              }
            });
          },
          addEventListener(type, listener) {
            if (type === "message") {
              listeners.add(listener);
            }
          },
          removeEventListener(type, listener) {
            if (type === "message") {
              listeners.delete(listener);
            }
          },
        },
      };

      function payloadFor(command) {
        if (command === "payableBasisState.getLatest") {
          return null;
        }

        if (command !== "localJournal.health") {
          return null;
        }

        return {
          healthy: true,
          enabled: true,
          databasePath: "e2e-fixture-cash-journal.db",
          cashDrawerEnabled: false,
          authorityWarning: "E2E fixture bridge.",
          localPersistence: {
            encryptionConfigured: true,
            dpapiScope: "CurrentUser",
            keyEnvelopeExists: true,
            keyAvailable: true,
            databaseExists: true,
            databaseEncrypted: true,
            legacyPlaintextDetected: false,
            migrationRequired: false,
            integrityValidated: true,
            schemaReady: true,
            persistenceReady: true,
            recoveryAllowed: true,
            cashOperationsAllowed: true,
            safeStatus: "READY",
            safeAction: "NONE",
            databasePath: "e2e-fixture-cash-journal.db",
            keyEnvelopePath: "e2e-fixture-cash-journal.key",
          },
          operationalState: {
            activeShiftRecordCount: activeShift?.status === "Open" ? 1 : 0,
            activeCashCustodySessionRecordCount: activeCashCustodySession ? 1 : 0,
            activeShift: activeShift?.status === "Open" ? activeShift : null,
            activeCashCustodySession,
          },
        };
      }
    },
    { activeShiftId, activeCustodyId, context, includeShift, includeCustody, shiftStatus },
  );
}
