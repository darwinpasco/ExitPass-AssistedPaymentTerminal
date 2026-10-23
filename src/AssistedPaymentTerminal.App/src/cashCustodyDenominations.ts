import catalogue from "../../AssistedPaymentTerminal.LocalOperations/CashCustodyDenominations.json";

export type CashCustodyDenominationDefinition = {
  code: string;
  displayLabel: string;
  valueMinorUnits: number;
};

export type CashCustodyDenominationLine = {
  denominationCode: string;
  denominationValueMinorUnits: number;
  quantity: number;
};

export const CASH_CUSTODY_DENOMINATIONS: CashCustodyDenominationDefinition[] = catalogue;

export function denominationTotalMinorUnits(lines: CashCustodyDenominationLine[]): number {
  return lines.reduce((total, line) => total + (line.denominationValueMinorUnits * line.quantity), 0);
}
