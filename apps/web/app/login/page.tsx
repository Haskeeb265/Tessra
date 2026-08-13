"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { DEFAULT_TENANT_ID, login } from "@/lib/api";
import { Alert, Button, Card, Field, TextInput } from "@/components/ui";
import { TenantPicker } from "@/components/tenant-picker";

export default function LoginPage() {
  const router = useRouter();
  const [tenantId, setTenantId] = useState(DEFAULT_TENANT_ID);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await login(email, password, tenantId);
      router.push("/dashboard");
      router.refresh();
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Could not log in. Is the platform running?",
      );
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="flex flex-1 items-center justify-center px-6 py-16">
      <Card className="w-full max-w-md p-8">
        <div className="mb-6 text-center">
          <div className="text-3xl" aria-hidden>
            ☕
          </div>
          <h1 className="mt-2 font-serif text-3xl font-bold text-espresso">
            Welcome back
          </h1>
          <p className="mt-1 text-sm text-mocha">Log in to your workspace</p>
        </div>

        {error && (
          <div className="mb-5">
            <Alert kind="error">{error}</Alert>
          </div>
        )}

        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          <TenantPicker value={tenantId} onChange={setTenantId} />
          <Field label="Email">
            <TextInput
              type="email"
              required
              autoComplete="email"
              placeholder="you@company.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </Field>
          <Field label="Password">
            <TextInput
              type="password"
              required
              autoComplete="current-password"
              placeholder="••••••••"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </Field>
          <Button type="submit" disabled={submitting} className="mt-2 w-full">
            {submitting ? "Logging in…" : "Log in"}
          </Button>
        </form>

        <p className="mt-6 text-center text-sm text-mocha">
          New here?{" "}
          <Link href="/register" className="font-semibold text-caramel hover:underline">
            Create an account
          </Link>
        </p>
        <p className="mt-3 text-center text-xs text-mocha/80">
          Local dev (in-memory DB) has no seeded admin —{" "}
          <Link href="/register" className="font-semibold text-caramel hover:underline">
            register an account
          </Link>{" "}
          instead (the first user in a workspace becomes its admin).
        </p>
        <p className="mt-2 text-center text-xs text-mocha/60">
          With PostgreSQL:{" "}
          <code className="rounded bg-beige px-1 font-mono">admin@tessera.com</code> /{" "}
          <code className="rounded bg-beige px-1 font-mono">Admin123!</code>
        </p>
      </Card>
    </main>
  );
}
