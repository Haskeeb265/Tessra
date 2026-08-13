"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { DEFAULT_TENANT_ID, register } from "@/lib/api";
import { Alert, Button, Card, Field, TextInput } from "@/components/ui";
import { TenantPicker } from "@/components/tenant-picker";

export default function RegisterPage() {
  const router = useRouter();
  const [tenantId, setTenantId] = useState(DEFAULT_TENANT_ID);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    if (password.length < 8) {
      setError("Password must be at least 8 characters long.");
      return;
    }
    if (password !== confirm) {
      setError("Passwords do not match.");
      return;
    }

    setSubmitting(true);
    try {
      await register(email, password, tenantId);
      router.push("/dashboard");
      router.refresh();
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Could not register. Is the platform running?",
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
            Start a fresh brew
          </h1>
          <p className="mt-1 text-sm text-mocha">Create your account</p>
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
              autoComplete="new-password"
              placeholder="At least 8 characters"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </Field>
          <Field label="Confirm password">
            <TextInput
              type="password"
              required
              autoComplete="new-password"
              placeholder="Repeat your password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
            />
          </Field>
          <Button type="submit" disabled={submitting} className="mt-2 w-full">
            {submitting ? "Creating account…" : "Create account"}
          </Button>
        </form>

        <p className="mt-6 text-center text-sm text-mocha">
          Already have an account?{" "}
          <Link href="/login" className="font-semibold text-caramel hover:underline">
            Log in
          </Link>
        </p>
      </Card>
    </main>
  );
}
