import type { AptConfig } from "../config";
import type { CentralPmsClient } from "./centralPmsTypes";
import { LiveCentralPmsClient } from "./centralPmsClient";
import { MockCentralPmsClient } from "./mockCentralPms";

export function createCentralPmsClient(config: AptConfig): CentralPmsClient {
  return config.centralPmsConnectionMode === "mock" ? new MockCentralPmsClient(config) : new LiveCentralPmsClient(config);
}
