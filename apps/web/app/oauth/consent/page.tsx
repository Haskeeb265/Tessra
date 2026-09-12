"use client";

import { Suspense, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Alert, Button, Card } from "@/components/ui";
import {
  oauthConsent,
  oauthConsentInfo,
  type ConsentInfo,
} from "@/lib/oauth";

/**
 * OAuth consent page (docs/mcp-auth-platform.md §6). The authorization server
 * redirects here when the user must approve a connector. Approving records
 * the grant and navigates back to the /connect/authorize URL; denying sends
 * the browser back with deny=1 so the AS returns access_denied to the client.
 */
function OAuthConsentForm() {
  const router = useRouter();
  const params = useSearchParams();

  const tenant = params.get("tenant") ?? "";
  const clientId = params.get("client_id") ?? "";
  const scope = params.get("scope") ?? "";
  const returnUrl = params.get("returnUrl") ?? "";

  const [info, setInfo] = useState<ConsentInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!clientId || !tenant) return;

    let cancelled = false;

    oauthConsentInfo(clientId, scope, tenant)
      .then((data) => {
        if (!cancelled) setInfo(data);
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(
            err instanceof Error
              ? err.message
              : "Could not load the authorization request.",
          );
        }
      });

    return () => {
      cancelled = true;
    };
  }, [clientId, scope, tenant]);

  async function approve() {
    setError(null);
    setBusy(true);
    try {
      const scopes = scope
        ? scope.split(" ").filter(Boolean)
        : ["tools"];
      await oauthConsent(clientId, scopes, tenant);

      if (returnUrl) {
        window.location.href = returnUrl;
      } else {
        router.push("/");
      }
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Could not approve. Try again.",
      );
      setBusy(false);
    }
  }

  function deny() {
    // Ask the AS to deny the request: it redirects to the client with
    // error=access_denied.
    const separator = returnUrl.includes("?") ? "&" : "?";
    window.location.href = `${returnUrl}${separator}deny=1`;
  }

  if (!clientId || !tenant) {
    return (
      <main className="flex flex-1 items-center justify-center px-6 py-16">
        <Alert kind="error">This authorization request is missing details.</Alert>
      </main>
    );
  }

  return (
    <main className="flex flex-1 items-center justify-center px-6 py-16">
      <Card className="w-full max-w-lg p-8">
        <div className="mb-6 text-center">
          <div className="text-3xl" aria-hidden>
            🤝
          </div>
          <h1 className="mt-2 font-serif text-3xl font-bold text-espresso">
            Authorize access
          </h1>
          <p className="mt-1 text-sm text-mocha">
            {info
              ? `${info.clientName} is asking to connect to ${info.tenantName}.`
              : "Loading the request…"}
          </p>
        </div>

        {error && (
          <div className="mb-5">
            <Alert kind="error">{error}</Alert>
          </div>
        )}

        {info && (
          <div className="mb-6 rounded-xl border border-latte bg-white/60 p-4">
            <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-mocha">
              This will allow it to:
            </p>
            <ul className="space-y-1.5">
              {info.scopes.map((scopeInfo) => (
                <li
                  key={scopeInfo.id}
                  className="flex items-start gap-2 text-sm text-espresso"
                >
                  <span className="mt-0.5 text-caramel">✓</span>
                  <span>
                    <code className="rounded bg-beige px-1 font-mono">
                      {scopeInfo.id}
                    </code>{" "}
                    — {scopeInfo.description}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}

        <div className="flex flex-col gap-3">
          <Button
            onClick={approve}
            disabled={busy || !info}
            className="w-full"
          >
            {busy ? "Approving…" : "Approve"}
          </Button>
          <Button
            onClick={deny}
            variant="ghost"
            className="w-full text-mocha hover:text-espresso"
          >
            Deny
          </Button>
        </div>
      </Card>
    </main>
  );
}

export default function OAuthConsentPage() {
  return (
    <Suspense fallback={<div className="flex-1" />}>
      <OAuthConsentForm />
    </Suspense>
  );
}
