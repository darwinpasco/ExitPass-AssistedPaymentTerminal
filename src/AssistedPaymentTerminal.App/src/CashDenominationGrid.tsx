import { useMemo, useState } from "react";
import type { CashCustodyDenominationDefinition, CashCustodyDenominationLine } from "./cashCustodyDenominations";
import { denominationTotalMinorUnits } from "./cashCustodyDenominations";

type CashDenominationGridProps = {
  purpose: "opening" | "closing" | "movement";
  catalogue: CashCustodyDenominationDefinition[];
  disabled: boolean;
  onConfirm: (lines: CashCustodyDenominationLine[]) => void;
};

export function CashDenominationGrid({ purpose, catalogue, disabled, onConfirm }: CashDenominationGridProps) {
  const [quantities, setQuantities] = useState<Record<string, string>>(() =>
    Object.fromEntries(catalogue.map((denomination) => [denomination.code, "0"])),
  );
  const [reviewing, setReviewing] = useState(false);
  const lines = useMemo<CashCustodyDenominationLine[] | null>(() => {
    const result: CashCustodyDenominationLine[] = [];
    for (const denomination of catalogue) {
      const raw = quantities[denomination.code] ?? "";
      if (!/^\d+$/.test(raw)) return null;
      const quantity = Number(raw);
      if (!Number.isSafeInteger(quantity) || quantity < 0) return null;
      result.push({
        denominationCode: denomination.code,
        denominationValueMinorUnits: denomination.valueMinorUnits,
        quantity,
      });
    }
    return result;
  }, [catalogue, quantities]);
  const totalMinorUnits = lines ? denominationTotalMinorUnits(lines) : 0;
  const label = purpose === "opening" ? "opening cash" : purpose === "closing" ? "actual cash" : "movement cash";
  const title = purpose === "opening" ? "Opening" : purpose === "closing" ? "Closing" : "Movement";

  return (
    <div className="custody-denomination-capture" aria-label={`${title} denomination count`}>
      <div className="denomination-grid-header" aria-hidden="true">
        <span>Denomination</span><span>Quantity</span><span>Subtotal</span>
      </div>
      {catalogue.map((denomination) => {
        const quantity = lines?.find((line) => line.denominationCode === denomination.code)?.quantity ?? 0;
        return (
          <label className="denomination-grid-row" key={denomination.code}>
            <span>{denomination.displayLabel}</span>
            <input
              aria-label={`${denomination.displayLabel} quantity`}
              type="number"
              inputMode="numeric"
              min="0"
              step="1"
              value={quantities[denomination.code] ?? ""}
              disabled={disabled || reviewing}
              onChange={(event) => {
                setQuantities((current) => ({ ...current, [denomination.code]: event.target.value }));
                setReviewing(false);
              }}
            />
            <strong>{formatPhpMinorUnits(denomination.valueMinorUnits * quantity)}</strong>
          </label>
        );
      })}
      <div className="denomination-total" data-testid={`${purpose}-denomination-total`}>
        <span>Calculated {label}</span>
        <strong>{formatPhpMinorUnits(totalMinorUnits)}</strong>
      </div>
      {reviewing && (
        <div className="status-notice info" role="status">
          <strong>Review physical count: {formatPhpMinorUnits(totalMinorUnits)}</strong>
          <p>Confirm only after checking every denomination quantity.</p>
        </div>
      )}
      <div className="denomination-actions">
        {reviewing ? (
          <>
            <button type="button" className="secondary-action" disabled={disabled} onClick={() => setReviewing(false)}>Edit count</button>
            <button type="button" disabled={disabled || !lines} onClick={() => lines && onConfirm(lines)}>
              {purpose === "opening" ? "Confirm opening" : purpose === "closing" ? "Confirm and close" : "Confirm movement count"}
            </button>
          </>
        ) : (
          <button type="button" disabled={disabled || !lines || catalogue.length === 0} onClick={() => setReviewing(true)}>
            {purpose === "opening" ? "Review opening count" : purpose === "closing" ? "Review actual count" : "Review movement count"}
          </button>
        )}
      </div>
    </div>
  );
}

function formatPhpMinorUnits(amountMinorUnits: number): string {
  return new Intl.NumberFormat("en-PH", { style: "currency", currency: "PHP" }).format(amountMinorUnits / 100);
}
