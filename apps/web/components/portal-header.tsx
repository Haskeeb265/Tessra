"use client";

import Link from "next/link";
import { TENANTS, getStoredAuth, logout, roleFromToken } from "@/lib/api";
import { Button } from "@/components/ui";

export type PortalSection = "widgets" | "team";

/**
 * Shared header for the tenant (business) portal: brand, section nav,
 * current user + role, and logout. The "Team" nav item only appears for
 * workspace admins.
 */
export function PortalHeader({
  active,
  onLogout,
}: {
  active: PortalSection;
  onLogout: () => void;
}) {
  const auth = getStoredAuth();
  const role = roleFromToken(auth?.accessToken);
  const isAdmin = role === "Admin";
  const tenant = TENANTS.find((t) => t.id === auth?.tenantId);

  const navItem = (href: string, label: string, isActive: boolean) => (
    <Link
      href={href}
      className={`rounded-lg px-3 py-1.5 text-sm font-semibold transition ${
        isActive
          ? "bg-beige text-espresso"
          : "text-mocha hover:bg-latte/60 hover:text-espresso"
      }`}
    >
      {label}
    </Link>
  );

  return (
    <header className="sticky top-0 z-10 border-b border-latte bg-cream/90 backdrop-blur">
      <div className="mx-auto flex w-full max-w-5xl items-center justify-between gap-4 px-6 py-4">
        <div className="flex min-w-0 items-center gap-4">
          <Link
            href="/dashboard"
            className="flex items-center gap-2 text-lg font-bold tracking-tight text-espresso"
          >
            <span aria-hidden>☕</span> Tessera
          </Link>
          <nav className="hidden items-center gap-1 sm:flex">
            {navItem("/dashboard", "Widgets", active === "widgets")}
            {isAdmin && navItem("/dashboard/users", "Team", active === "team")}
          </nav>
        </div>
        <div className="flex min-w-0 items-center gap-3">
          <div className="hidden items-center gap-2 text-right sm:flex">
            <div className="min-w-0">
              <p className="truncate text-sm font-semibold text-espresso">
                {auth?.email}
              </p>
              <p className="text-xs text-mocha">{tenant?.name ?? auth?.tenantId}</p>
            </div>
            <span
              className={`rounded-full px-2.5 py-0.5 text-xs font-bold ${
                isAdmin ? "bg-caramel/20 text-caramel" : "bg-beige text-mocha"
              }`}
            >
              {role ?? "User"}
            </span>
          </div>
          <Button variant="ghost" onClick={onLogout}>
            Log out
          </Button>
        </div>
      </div>
    </header>
  );
}

export { logout };
