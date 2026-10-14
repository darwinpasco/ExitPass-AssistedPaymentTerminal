import { afterEach, describe, expect, it, vi } from "vitest";
import { sendPayableBasisRequest } from "./centralPmsPayableBasisBridge";

describe("Central PMS payable-basis desktop bridge", () => {
  afterEach(() => {
    delete window.chrome;
  });

  it("falls back to the existing fetch transport outside the desktop host", async () => {
    await expect(sendPayableBasisRequest(
      "payableBasis.resolve",
      "11111111-1111-4111-8111-111111111111",
      "22222222-2222-4222-8222-222222222222",
      {},
    )).resolves.toBeNull();
  });

  it("sends only payable-basis data and never browser-owned authority", async () => {
    let listener: ((event: { data: unknown }) => void) | undefined;
    const postMessage = vi.fn((message: string) => {
      const request = JSON.parse(message) as Record<string, unknown>;
      expect(request).not.toHaveProperty("authorization");
      expect(request).not.toHaveProperty("sessionToken");
      expect(request).not.toHaveProperty("serviceIdentityId");
      listener?.({
        data: JSON.stringify({
          ok: true,
          command: request.command,
          correlationId: request.correlationId,
          payload: { statusCode: 404, body: { errorCode: "SESSION_NOT_FOUND" } },
        }),
      });
    });
    window.chrome = {
      webview: {
        postMessage,
        addEventListener: (_type, callback) => { listener = callback; },
        removeEventListener: vi.fn(),
      },
    };

    const result = await sendPayableBasisRequest(
      "payableBasis.resolve",
      "11111111-1111-4111-8111-111111111111",
      "22222222-2222-4222-8222-222222222222",
      { referenceType: "plate", plateNumber: "NO-SESSION" },
    );

    expect(result?.ok).toBe(true);
    expect(postMessage).toHaveBeenCalledOnce();
  });

  it("sends statutory decision readback without exposing host credentials", async () => {
    let listener: ((event: { data: unknown }) => void) | undefined;
    const postMessage = vi.fn((message: string) => {
      const request = JSON.parse(message) as Record<string, unknown>;
      expect(request.command).toBe("statutoryDecision.get");
      expect(request).not.toHaveProperty("authorization");
      expect(request).not.toHaveProperty("sessionToken");
      expect(request).not.toHaveProperty("serviceIdentityId");
      listener?.({
        data: JSON.stringify({
          ok: true,
          command: request.command,
          correlationId: request.correlationId,
          payload: { statusCode: 201, body: {} },
        }),
      });
    });
    window.chrome = {
      webview: {
        postMessage,
        addEventListener: (_type, callback) => { listener = callback; },
        removeEventListener: vi.fn(),
      },
    };

    await sendPayableBasisRequest(
      "statutoryDecision.get",
      "11111111-1111-4111-8111-111111111111",
      "22222222-2222-4222-8222-222222222222",
      { decisionCommandId: "33333333-3333-4333-8333-333333333333" },
    );

    expect(postMessage).toHaveBeenCalledOnce();
    expect(String(vi.mocked(postMessage).mock.calls[0][0])).not.toContain("actorUserId");
  });
});
