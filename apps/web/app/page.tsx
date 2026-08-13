import Link from "next/link";
import { HealthBadge } from "@/components/health-badge";

const features = [
  {
    title: "Authentication & JWT",
    description:
      "Register, login and refresh tokens handled by the platform — with per-tenant access control.",
  },
  {
    title: "Multi-tenant workspaces",
    description:
      "Every request carries an X-Tenant-Id header. Pick a workspace and data stays isolated.",
  },
  {
    title: "Widgets API",
    description:
      "A full CRUD API to exercise end-to-end: create, list, edit and delete widgets per tenant.",
  },
  {
    title: "Observability",
    description:
      "Request logging, correlation IDs and a consistent error contract from the platform layer.",
  },
];

export default function Home() {
  return (
    <main className="flex flex-1 flex-col">
      {/* Top bar */}
      <header className="mx-auto flex w-full max-w-5xl items-center justify-between px-6 py-5">
        <div className="flex items-center gap-2 text-lg font-bold tracking-tight text-espresso">
          <span aria-hidden>☕</span> Tessera
        </div>
        <nav className="flex items-center gap-3">
          <HealthBadge />
          <Link
            href="/login"
            className="rounded-xl px-4 py-2 text-sm font-semibold text-roast transition hover:bg-latte/60"
          >
            Log in
          </Link>
          <Link
            href="/register"
            className="rounded-xl bg-roast px-4 py-2 text-sm font-semibold text-cream transition hover:bg-espresso"
          >
            Get started
          </Link>
        </nav>
      </header>

      {/* Hero */}
      <section className="mx-auto w-full max-w-5xl px-6 pt-16 pb-20 text-center">
        <p className="mx-auto mb-4 inline-flex items-center gap-2 rounded-full border border-latte bg-beige px-4 py-1.5 text-xs font-semibold tracking-wide text-mocha uppercase">
          Multi-tenant SaaS platform
        </p>
        <h1 className="font-serif text-5xl leading-tight font-bold tracking-tight text-espresso md:text-6xl">
          The platform that
          <br />
          <span className="text-caramel">brews</span> your backend.
        </h1>
        <p className="mx-auto mt-6 max-w-2xl text-lg text-roast/80">
          Tessera is a C# platform handling authentication, tenants and widgets
          for your product. This web app is the front door — sign in, pick a
          workspace and try it from a real user&apos;s point of view.
        </p>
        <div className="mt-10 flex flex-wrap items-center justify-center gap-4">
          <Link
            href="/register"
            className="rounded-2xl bg-roast px-8 py-3.5 text-base font-semibold text-cream shadow-sm transition hover:bg-espresso"
          >
            Create an account
          </Link>
          <Link
            href="/login"
            className="rounded-2xl border border-latte bg-white/70 px-8 py-3.5 text-base font-semibold text-roast transition hover:bg-latte/50"
          >
            Log in
          </Link>
        </div>
        <p className="mt-6 text-sm text-mocha">
          Try the seeded admin on PostgreSQL: <code className="rounded bg-beige px-1.5 py-0.5 font-mono text-xs">admin@tessera.com</code> /{" "}
          <code className="rounded bg-beige px-1.5 py-0.5 font-mono text-xs">Admin123!</code>
        </p>
      </section>

      {/* Features */}
      <section className="mx-auto w-full max-w-5xl px-6 pb-20">
        <div className="grid gap-5 sm:grid-cols-2">
          {features.map((feature) => (
            <div
              key={feature.title}
              className="rounded-2xl border border-latte bg-white/70 p-6 shadow-sm transition hover:border-caramel/50"
            >
              <h2 className="font-serif text-xl font-bold text-espresso">
                {feature.title}
              </h2>
              <p className="mt-2 text-sm leading-relaxed text-roast/75">
                {feature.description}
              </p>
            </div>
          ))}
        </div>
      </section>

      {/* Footer */}
      <footer className="border-t border-latte bg-beige/60 py-6 text-center text-sm text-mocha">
        ☕ Tessera · brewed with care ·{" "}
        <span className="font-mono text-xs">apps/web → apps/platform</span>
      </footer>
    </main>
  );
}
