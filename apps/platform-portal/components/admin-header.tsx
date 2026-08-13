"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { getStoredAuth, logout } from "@/lib/api";
import { Button } from "@/components/ui";

export type AdminSection = "tenants" | "envelopes";

export function AdminHeader({ active }: { active: AdminSection }) {
  const router = useRouter();
  const auth = getStoredAuth();

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

  function handleLogout() {
    logout();
    router.push("/login");
  }

  return (
    <header className="sticky top-0 z-10 border-b border-latte bg-cream/90 backdrop-blur">
      <div className="mx-auto flex w-full max-w-5xl items-center justify-between gap-4 px-6 py-4">
        <div className="flex min-w-0 items-center gap-4">
          <Link
            href="/tenants"
            className="flex items-center gap-2 text-lg font-bold tracking-tight text-espresso"
          >
            <span aria-hidden>🛠️</span> Tessera Platform
          </Link>
          <nav className="hidden items-center gap-1 sm:flex">
            {navItem("/tenants", "Tenants", active === "tenants")}
            {navItem("/envelopes", "Envelopes", active === "envelopes")}
          </nav>
        </div>
        <div className="flex min-w-0 items-center gap-3">
          <div className="hidden items-center gap-2 text-right sm:flex">
            <div className="min-w-0">
              <p className="truncate text-sm font-semibold text-espresso">
                {auth?.email}
              </p>
              <p className="text-xs text-mocha">Superadmin</p>
            </div>
            <span className="rounded-full bg-roast px-2.5 py-0.5 text-xs font-bold text-cream">
              Platform
            </span>
          </div>
          <Button variant="ghost" onClick={handleLogout}>
            Log out
          </Button>
        </div>
      </div>
    </header>
  );
}
