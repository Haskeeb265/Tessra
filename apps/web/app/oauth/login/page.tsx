"use client";

import { Suspense, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Alert, Button, Card, Field, TextInput } from "@/components/ui";
import { oauthCompleteMfa, oauthLogin } from "@/lib/oauth";

/**
 * OAuth login page (docs/mcp-auth-platform.md §6). The authorization server
 * redirects the browser here with the tenant slug and the full
 * /connect/authorize URL as `returnUrl`. On success this page navigates the
 * browser back to that URL with the OAuth cookie set.
 */
function OAuthLoginForm() {
  const router = useRouter();
  const params = useSearchParams();

  const tenant = params.get("tenant") ?? "";
  const returnUrl = params.get("returnUrl") ?? "";

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [mfaToken, setMfaToken] = useState<string | null>(null);
  const [mfaCode, setMfaCode] = useState("");

  function continueToAuthorize() {
    if (returnUrl) {
      window.location.href = returnUrl;
    } else {
      router.push("/");
    }
  }

  async function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setError(null);

    // Read the credentials straight off the form instead of trusting component
    // state. Browser password managers autofill — and anything typed before
    // hydration completes sticks in the DOM — without ever firing onChange, so
    // state can still be empty and we would POST {"email":"","password":""}.
    const data = new FormData(e.currentTarget);
    const submittedEmail = String(data.get("email") || email).trim();
    const submittedPassword = String(data.get("password") || password);

    setEmail(submittedEmail);
    setPassword(submittedPassword);
    setSubmitting(true);
    try {
      const result = await oauthLogin(submittedEmail, submittedPassword, tenant);
      if (result.mfaRequired && result.mfaToken) {
        setMfaToken(result.mfaToken);
        return;
      }
      continueToAuthorize();
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Could not log in. Try again.",
      );
    } finally {
      setSubmitting(false);
    }
  }

  async function handleMfaSubmit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!mfaToken) return;
    setError(null);

    // Same reasoning as the credential form: one-time-code autofill does not
    // always reach React state.
    const data = new FormData(e.currentTarget);
    const submittedCode = String(data.get("code") || mfaCode).trim();

    setMfaCode(submittedCode);
    setSubmitting(true);
    try {
      await oauthCompleteMfa(mfaToken, submittedCode, tenant);
      continueToAuthorize();
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Invalid authentication code.",
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
            🔐
          </div>
          <h1 className="mt-2 font-serif text-3xl font-bold text-espresso">
            {mfaToken ? "Two-factor authentication" : "Authorize sign-in"}
          </h1>
          <p className="mt-1 text-sm text-mocha">
            {tenant ? (
              <>
                Signing into workspace{" "}
                <code className="rounded bg-beige px-1 font-mono">
                  {tenant}
                </code>
              </>
            ) : (
              "No workspace was specified."
            )}
          </p>
        </div>

        {error && (
          <div className="mb-5">
            <Alert kind="error">{error}</Alert>
          </div>
        )}

        {mfaToken ? (
          <form onSubmit={handleMfaSubmit} className="flex flex-col gap-4">
            <Field label="Authentication code">
              <TextInput
                name="code"
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={6}
                placeholder="123456"
                value={mfaCode}
                onChange={(e) => setMfaCode(e.target.value)}
              />
            </Field>
            <Button
              type="submit"
              disabled={submitting || mfaCode.length !== 6}
              className="mt-2 w-full"
            >
              {submitting ? "Verifying…" : "Verify & continue"}
            </Button>
          </form>
        ) : (
          <form onSubmit={handleSubmit} className="flex flex-col gap-4">
            <Field label="Email">
              <TextInput
                name="email"
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
                name="password"
                type="password"
                required
                autoComplete="current-password"
                placeholder="••••••••"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </Field>
            <Button type="submit" disabled={submitting} className="mt-2 w-full">
              {submitting ? "Signing in…" : "Continue"}
            </Button>
          </form>
        )}
      </Card>
    </main>
  );
}

export default function OAuthLoginPage() {
  return (
    <Suspense fallback={<div className="flex-1" />}>
      <OAuthLoginForm />
    </Suspense>
  );
}
