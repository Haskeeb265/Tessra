// OAuth interactive-flow client for the login + consent pages (/oauth/*).
//
// The OAuth authorization server lives in the platform API (/connect/*) and
// the portal is served from the same origin by the local TLS reverse proxy
// (Caddy), so these calls are RELATIVE — no NEXT_PUBLIC_API_URL. Cookies set
// by the API (the OAuth auth cookie) therefore flow naturally.
//
// Tenant is passed as the X-Tenant-Id header, mirroring the platform's
// existing API convention.

export class OAuthError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "OAuthError";
  }
}

async function oauthFetch(
  path: string,
  options: { method?: string; body?: unknown; tenant: string },
): Promise<Response> {
  const headers: Record<string, string> = { "X-Tenant-Id": options.tenant };
  if (options.body !== undefined) headers["Content-Type"] = "application/json";

  return fetch(path, {
    method: options.method ?? "POST",
    headers,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
  });
}

async function readError(res: Response): Promise<string> {
  try {
    const data = (await res.json()) as { error?: string };
    return data.error ?? `Request failed (${res.status})`;
  } catch {
    return `Request failed (${res.status})`;
  }
}

export interface MfaStep {
  mfaRequired: true;
  mfaToken: string;
}

export interface LoginResult {
  ok?: boolean;
  mfaRequired?: boolean;
  mfaToken?: string;
}

/** Step 1 of the OAuth login: verify credentials (or start the MFA step). */
export async function oauthLogin(
  email: string,
  password: string,
  tenant: string,
): Promise<LoginResult> {
  const res = await oauthFetch("/connect/login", {
    body: { email, password },
    tenant,
  });

  if (!res.ok) throw new OAuthError(await readError(res));
  return (await res.json()) as LoginResult;
}

/** Step 2: complete an MFA-protected OAuth login. */
export async function oauthCompleteMfa(
  mfaToken: string,
  code: string,
  tenant: string,
): Promise<void> {
  const res = await oauthFetch("/connect/login/mfa", {
    body: { mfaToken, code },
    tenant,
  });

  if (!res.ok) throw new OAuthError(await readError(res));
}

export interface ScopeInfo {
  id: string;
  description: string;
}

export interface ConsentInfo {
  clientId: string;
  clientName: string;
  tenantId: string;
  tenantName: string;
  scopes: ScopeInfo[];
}

/** Loads the client + scope details shown on the consent page. */
export async function oauthConsentInfo(
  clientId: string,
  scope: string,
  tenant: string,
): Promise<ConsentInfo> {
  const params = new URLSearchParams({ client_id: clientId, scope });
  const res = await oauthFetch(`/connect/consent-info?${params}`, {
    method: "GET",
    tenant,
  });

  if (!res.ok) throw new OAuthError(await readError(res));
  return (await res.json()) as ConsentInfo;
}

/** Records the user's grant; the page then navigates back to the AS. */
export async function oauthConsent(
  clientId: string,
  scopes: string[],
  tenant: string,
): Promise<void> {
  const res = await oauthFetch("/connect/consent", {
    body: { clientId, scopes },
    tenant,
  });

  if (!res.ok) throw new OAuthError(await readError(res));
}
