import { useEffect, useState } from "react";

export const DEFAULT_POS_VERSION_LABEL = "Version No. ExitPass V1.0";

const manilaDateFormatter = new Intl.DateTimeFormat("en-US", {
  timeZone: "Asia/Manila",
  month: "long",
  day: "numeric",
  year: "numeric",
});

const manilaTimeFormatter = new Intl.DateTimeFormat("en-US", {
  timeZone: "Asia/Manila",
  hour: "numeric",
  minute: "2-digit",
  second: "2-digit",
  hour12: true,
});

export function formatManilaDateTime(value: Date): string {
  return `${manilaDateFormatter.format(value)} | ${manilaTimeFormatter.format(value)}`;
}

export function ProductRuntimeIdentity({ versionLabel }: { versionLabel: string }) {
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 1_000);
    return () => window.clearInterval(timer);
  }, []);

  return (
    <div className="product-runtime-identity" aria-label="ExitPass POS version and Philippine time">
      <strong>{versionLabel}</strong>
      <time dateTime={now.toISOString()}>{formatManilaDateTime(now)}</time>
    </div>
  );
}
