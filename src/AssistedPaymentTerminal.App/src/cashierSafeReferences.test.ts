import { describe, expect, it } from "vitest";
import { cashierSafeSupportReference, containsInternalGuid } from "./cashierSafeReferences";

describe("cashier-safe references", () => {
  it("rejects full canonical GUIDs and values containing them", () => {
    const guid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa1001";

    expect(containsInternalGuid(guid)).toBe(true);
    expect(cashierSafeSupportReference(guid)).toBeNull();
    expect(cashierSafeSupportReference(`support-${guid}`)).toBeNull();
  });

  it("accepts an existing bounded support reference without making it authoritative", () => {
    expect(cashierSafeSupportReference("APT-SUPPORT-7K2M9Q")).toBe("APT-SUPPORT-7K2M9Q");
  });

  it("rejects URLs, paths, multiline values, and oversized values", () => {
    expect(cashierSafeSupportReference("https://internal.example/item")).toBeNull();
    expect(cashierSafeSupportReference("C:\\private\\item")).toBeNull();
    expect(cashierSafeSupportReference("line-one\nline-two")).toBeNull();
    expect(cashierSafeSupportReference("x".repeat(81))).toBeNull();
  });
});
