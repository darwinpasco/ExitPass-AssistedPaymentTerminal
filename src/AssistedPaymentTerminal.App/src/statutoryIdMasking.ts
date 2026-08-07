export function maskStatutoryId(value: string | null | undefined): string {
  const trimmed = value?.trim() ?? "";
  if (!trimmed) return "";
  if (trimmed.length <= 6) return "*".repeat(trimmed.length);
  return `${trimmed.slice(0, 2)}${"*".repeat(trimmed.length - 6)}${trimmed.slice(-4)}`;
}

export function containsManualStatutoryIdMask(value: string | null | undefined): boolean {
  return (value ?? "").includes("*");
}
