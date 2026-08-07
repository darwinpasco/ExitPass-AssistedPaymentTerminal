const internalGuidPattern = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i;

export function containsInternalGuid(value: string): boolean {
  return internalGuidPattern.test(value);
}

export function cashierSafeSupportReference(value?: string | null): string | null {
  const candidate = value?.trim();
  if (!candidate || candidate.length > 80 || containsInternalGuid(candidate)) return null;
  if (/https?:\/\/|[\\/]|[\r\n]/i.test(candidate)) return null;
  return candidate;
}
