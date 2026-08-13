"use client";

import { useEffect, useState } from "react";
import { getHealth } from "@/lib/api";

export function HealthBadge() {
  const [status, setStatus] = useState<"checking" | "up" | "down">("checking");

  useEffect(() => {
    let cancelled = false;
    getHealth()
      .then(() => !cancelled && setStatus("up"))
      .catch(() => !cancelled && setStatus("down"));
    return () => {
      cancelled = true;
    };
  }, []);

  const styles =
    status === "up"
      ? "bg-green-100 text-green-800 border-green-200"
      : status === "down"
        ? "bg-red-100 text-red-800 border-red-200"
        : "bg-beige text-mocha border-latte";

  const label =
    status === "up" ? "API online" : status === "down" ? "API offline" : "Checking API…";

  return (
    <span
      className={`inline-flex items-center gap-2 rounded-full border px-3 py-1 text-xs font-semibold ${styles}`}
    >
      <span
        className={`h-2 w-2 rounded-full ${
          status === "up"
            ? "bg-green-500"
            : status === "down"
              ? "bg-red-500"
              : "bg-mocha animate-pulse"
        }`}
      />
      {label}
    </span>
  );
}
