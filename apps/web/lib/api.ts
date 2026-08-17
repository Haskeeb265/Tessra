// API client for the Tessera platform (apps/platform).
// All requests run from the browser and include the X-Tenant-Id header
// required by the platform's tenant middleware. Access tokens are stored
// in localStorage and refreshed automatically on 401.

export interface Tenant {
  id: string;
  name: string;
}

// Default tenants shown while the live list loads (and as a fallback if
// the platform is unreachable). The platform exposes the real list at
// GET /tenants, which includes tenants created by a superadmin.
export const TENANTS: Tenant[] = [
  { id: "alpha-corp", name: "Alpha Corp" },
  { id: "beta-industries", name: "Beta Industries" },
];

export const DEFAULT_TENANT_ID = TENANTS[0].id;

export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5085";

export interface Widget {
  id: string;
  tenantId?: string | null;
  name: string;
  description?: string | null;
  createdAt: string;
}

export interface TokenPair {
  accessToken: string;
  refreshToken: string;
}

interface StoredAuth extends TokenPair {
  tenantId: string;
  email: string;
}

const AUTH_STORAGE_KEY = "tessera.auth";

export function getStoredAuth(): StoredAuth | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.localStorage.getItem(AUTH_STORAGE_KEY);
    return raw ? (JSON.parse(raw) as StoredAuth) : null;
  } catch {
    return null;
  }
}

export function storeAuth(auth: StoredAuth | null): void {
  if (typeof window === "undefined") return;
  if (auth) {
    window.localStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify(auth));
  } else {
    window.localStorage.removeItem(AUTH_STORAGE_KEY);
  }
}

export function clearAuth(): void {
  storeAuth(null);
}

const ROLE_CLAIM_NAMES = [
  "role",
  "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
];

export function decodeJwtPayload(
  token: string,
): Record<string, unknown> | null {
  try {
    const payload = token.split(".")[1];
    if (!payload) return null;
    const base64 = payload.replace(/-/g, "+").replace(/_/g, "/");
    return JSON.parse(window.atob(base64)) as Record<string, unknown>;
  } catch {
    return null;
  }
}

export function roleFromToken(token: string | undefined | null): string | null {
  if (!token) return null;
  const payload = decodeJwtPayload(token);
  if (!payload) return null;
  for (const name of ROLE_CLAIM_NAMES) {
    const value = payload[name];
    if (typeof value === "string" && value) return value;
  }
  return null;
}

export function emailFromToken(token: string | undefined | null): string | null {
  if (!token) return null;
  const payload = decodeJwtPayload(token);
  if (!payload) return null;
  const value =
    payload["email"] ??
    payload["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"];
  return typeof value === "string" ? value : null;
}

export class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

interface RequestOptions {
  method?: string;
  body?: unknown;
  tenantId?: string;
  useAuth?: boolean;
}

async function readError(res: Response): Promise<string> {
  try {
    const data = (await res.json()) as {
      error?: string;
      message?: string;
      title?: string;
    };
    return data.error ?? data.message ?? data.title ?? `Request failed (${res.status})`;
  } catch {
    return `Request failed (${res.status})`;
  }
}

async function refreshAccessToken(
  refreshToken: string,
  tenantId: string,
): Promise<string | null> {
  const res = await fetch(`${API_BASE_URL}/auth/refresh`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Tenant-Id": tenantId,
    },
    body: JSON.stringify({ refreshToken }),
  });
  if (!res.ok) return null;
  const data = (await res.json()) as TokenPair;
  return data.accessToken ?? null;
}

export async function apiFetch<T>(
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  const auth = getStoredAuth();
  const tenantId = options.tenantId ?? auth?.tenantId ?? DEFAULT_TENANT_ID;
  const useAuth = options.useAuth !== false;

  const buildHeaders = (accessToken?: string): Record<string, string> => {
    const headers: Record<string, string> = { "X-Tenant-Id": tenantId };
    if (options.body !== undefined) headers["Content-Type"] = "application/json";
    if (useAuth && accessToken) {
      headers["Authorization"] = `Bearer ${accessToken}`;
    }
    return headers;
  };

  const doFetch = (accessToken?: string) =>
    fetch(`${API_BASE_URL}${path}`, {
      method: options.method ?? "GET",
      headers: buildHeaders(accessToken),
      body:
        options.body !== undefined ? JSON.stringify(options.body) : undefined,
    });

  let res = await doFetch(useAuth ? auth?.accessToken : undefined);

  // Retry once with a fresh access token if the token expired.
  if (res.status === 401 && useAuth && auth?.refreshToken) {
    const newAccessToken = await refreshAccessToken(
      auth.refreshToken,
      tenantId,
    );
    if (newAccessToken) {
      const current = getStoredAuth();
      if (current) storeAuth({ ...current, accessToken: newAccessToken });
      res = await doFetch(newAccessToken);
    }
  }

  if (!res.ok) {
    throw new ApiError(res.status, await readError(res));
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

// ─── Auth ───────────────────────────────────────────────────────────

export interface MfaChallenge {
  mfaRequired: true;
  mfaToken: string;
}

/**
 * Logs in. Returns an MFA token when the account requires a second factor
 * (the caller must then call completeMfaLogin), or null when the login
 * succeeded and tokens were stored.
 */
export async function login(
  email: string,
  password: string,
  tenantId: string,
): Promise<string | null> {
  const data = await apiFetch<
    Partial<TokenPair> & { mfaRequired?: boolean; mfaToken?: string }
  >("/auth/login", {
    method: "POST",
    body: { email, password },
    tenantId,
    useAuth: false,
  });

  if (data.mfaRequired && data.mfaToken) {
    return data.mfaToken;
  }

  if (data.accessToken && data.refreshToken) {
    storeAuth({ ...data, tenantId, email } as TokenPair & { tenantId: string; email: string });
    return null;
  }

  throw new ApiError(400, "Unexpected response from the login endpoint.");
}

/** Second step of an MFA-protected login. */
export async function completeMfaLogin(
  mfaToken: string,
  code: string,
  tenantId: string,
  email: string,
): Promise<void> {
  const tokens = await apiFetch<TokenPair>("/auth/mfa", {
    method: "POST",
    body: { mfaToken, code },
    tenantId,
    useAuth: false,
  });
  storeAuth({ ...tokens, tenantId, email });
}

/**
 * Registers (optionally redeeming an invite token). Returns a message when
 * the account needs email verification instead of tokens.
 */
export async function register(
  email: string,
  password: string,
  tenantId: string,
  inviteToken?: string,
): Promise<{ message?: string }> {
  const data = await apiFetch<
    Partial<TokenPair> & { message?: string }
  >("/auth/register", {
    method: "POST",
    body: {
      email,
      password,
      inviteToken: inviteToken ?? null,
    },
    tenantId,
    useAuth: false,
  });

  if (data.accessToken && data.refreshToken) {
    storeAuth({ ...data, tenantId, email } as TokenPair & { tenantId: string; email: string });
    return {};
  }

  return { message: data.message };
}

export function logout(): void {
  clearAuth();
}

// ─── Widgets ────────────────────────────────────────────────────────

export async function getWidgets(): Promise<Widget[]> {
  return apiFetch<Widget[]>("/widgets");
}

export async function createWidget(input: {
  name: string;
  description?: string;
}): Promise<Widget> {
  return apiFetch<Widget>("/widgets", { method: "POST", body: input });
}

export async function updateWidget(
  id: string,
  input: { name: string; description?: string },
): Promise<Widget> {
  return apiFetch<Widget>(`/widgets/${id}`, { method: "PUT", body: input });
}

export async function deleteWidget(id: string): Promise<void> {
  return apiFetch<void>(`/widgets/${id}`, { method: "DELETE" });
}

// ─── Tenants ────────────────────────────────────────────────────────

export async function getTenants(): Promise<Tenant[]> {
  return apiFetch<Tenant[]>("/tenants", { useAuth: false });
}

// ─── Envelope & users (platform-managed roles/actions) ─────────────

export interface EnvelopeRole {
  name: string;
  actions: string[];
  isSystem?: boolean;
}

export interface Envelope {
  id: string;
  name: string;
  description: string | null;
  roles: EnvelopeRole[];
}

export interface TenantUser {
  id: string;
  email: string;
  role: string;
  roleIsSystem?: boolean;
  createdAt: string;
}

export interface MeInfo {
  id: string;
  email: string;
  role: string;
  actions: string[];
  tenantId: string | null;
  tenantIdentifier: string | null;
}

/** Current user's info: email, role, and the actions their role allows. */
export async function getMe(): Promise<MeInfo> {
  return apiFetch<MeInfo>("/tenant/me");
}

/** The workspace's envelope: the roles (and actions) a superadmin assigned. */
export async function getTenantEnvelope(): Promise<Envelope> {
  return apiFetch<Envelope>("/tenant/envelope");
}

export async function getUsers(): Promise<TenantUser[]> {
  return apiFetch<TenantUser[]>("/tenant/users");
}

export async function createUser(input: {
  email: string;
  password: string;
  role: string;
}): Promise<TenantUser> {
  return apiFetch<TenantUser>("/tenant/users", { method: "POST", body: input });
}

export async function updateUserRole(id: string, role: string): Promise<TenantUser> {
  return apiFetch<TenantUser>(`/tenant/users/${id}`, { method: "PUT", body: { role } });
}

export async function deleteUser(id: string): Promise<void> {
  return apiFetch<void>(`/tenant/users/${id}`, { method: "DELETE" });
}

// ─── Health ─────────────────────────────────────────────────────────

export async function getHealth(): Promise<{ status: string }> {
  return apiFetch<{ status: string }>("/health", { useAuth: false });
}
