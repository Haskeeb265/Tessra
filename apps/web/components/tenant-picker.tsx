"use client";

import { useEffect, useState } from "react";
import { TENANTS, getTenants, type Tenant } from "@/lib/api";

export function TenantPicker({
  value,
  onChange,
}: {
  value: string;
  onChange: (tenantId: string) => void;
}) {
  const [tenants, setTenants] = useState<Tenant[]>(TENANTS);

  // Load the live tenant list (includes tenants created by a superadmin).
  // Falls back to the built-in defaults if the platform is unreachable.
  useEffect(() => {
    let cancelled = false;
    getTenants()
      .then((list) => {
        if (!cancelled && list.length > 0) setTenants(list);
      })
      .catch(() => {
        /* keep defaults */
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <label className="flex flex-col gap-1.5 text-sm font-medium text-roast">
      <span>Workspace (tenant)</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="w-full cursor-pointer rounded-xl border border-latte bg-white/70 px-4 py-2.5 text-sm text-espresso outline-none transition focus:border-caramel focus:ring-2 focus:ring-caramel/30"
      >
        {tenants.map((tenant) => (
          <option key={tenant.id} value={tenant.id}>
            {tenant.name} · {tenant.id}
          </option>
        ))}
      </select>
    </label>
  );
}
