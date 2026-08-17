// API client for the Tessera platform's superadmin area (apps/platform).
// Unlike the tenant portal, requests here do NOT send an X-Tenant-Id header
// — superadmin endpoints live under /admin and are validated by the
// SuperAdminOnly policy instead.

export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5085";

const AUTH_STORAGE_KEY = "tessera.admin";

export interface AdminAuth {
  accessToken: string;
  email: string;
}

export function getStoredAuth(): AdminAuth | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.localStorage.getItem(AUTH_STORAGE_KEY);
    return raw ? (JSON.parse(raw) as AdminAuth) : null;
  } catch {
    return null;
  }
}

function storeAuth(auth: AdminAuth | null): void {
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
}

async function readError(res: Response): Promise<string> {
  try {
    const data = (await res.json()) as {
      error?: string;
      message?: string;
      title?: string;
    };
    return (
      data.error ?? data.message ?? data.title ?? `Request failed (${res.status})`
    );
  } catch {
    return `Request failed (${res.status})`;
  }
}

export async function apiFetch<T>(
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  const auth = getStoredAuth();
  const headers: Record<string, string> = {};
  if (options.body !== undefined) headers["Content-Type"] = "application/json";
  if (auth?.accessToken) headers["Authorization"] = `Bearer ${auth.accessToken}`;

  const res = await fetch(`${API_BASE_URL}${path}`, {
    method: options.method ?? "GET",
    headers,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
  });

  if (!res.ok) {
    throw new ApiError(res.status, await readError(res));
  }

  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

// ─── Auth ───────────────────────────────────────────────────────────

export async function login(email: string, password: string): Promise<void> {
  const tokens = await apiFetch<{ accessToken: string }>("/admin/auth/login", {
    method: "POST",
    body: { email, password },
  });
  storeAuth({ accessToken: tokens.accessToken, email });
}

export function logout(): void {
  clearAuth();
}

// ─── Tenants ────────────────────────────────────────────────────────

export interface AdminTenant {
  id: string;
  identifier: string;
  name: string;
  envelopeId: string | null;
  envelopeName: string | null;
  createdAt: string;
}

export interface TenantInput {
  identifier: string;
  name: string;
  envelopeId?: string | null;
}

export async function getTenants(): Promise<AdminTenant[]> {
  return apiFetch<AdminTenant[]>("/admin/tenants");
}

export async function createTenant(input: TenantInput): Promise<AdminTenant> {
  return apiFetch<AdminTenant>("/admin/tenants", { method: "POST", body: input });
}

export async function updateTenant(
  id: string,
  input: TenantInput,
): Promise<AdminTenant> {
  return apiFetch<AdminTenant>(`/admin/tenants/${id}`, {
    method: "PUT",
    body: input,
  });
}

export async function deleteTenant(id: string): Promise<void> {
  return apiFetch<void>(`/admin/tenants/${id}`, { method: "DELETE" });
}

/**
 * Invites someone into a tenant. The first invite redeemed in a workspace
 * becomes its platform-managed Superadmin; later invites default to Admin.
 */
export async function inviteTenantUser(
  id: string,
  email: string,
): Promise<{ message: string }> {
  return apiFetch<{ message: string }>(`/admin/tenants/${id}/invites`, {
    method: "POST",
    body: { email },
  });
}

// ─── Envelopes ──────────────────────────────────────────────────────

export interface EnvelopeRole {
  name: string;
  actions: string[];
}

export interface Envelope {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
  roles: EnvelopeRole[];
}

export interface EnvelopeInput {
  name: string;
  description?: string | null;
  roles: { name: string; actions: string[] }[];
}

export async function getEnvelopes(): Promise<Envelope[]> {
  return apiFetch<Envelope[]>("/admin/envelopes");
}

export async function createEnvelope(input: EnvelopeInput): Promise<Envelope> {
  return apiFetch<Envelope>("/admin/envelopes", { method: "POST", body: input });
}

export async function updateEnvelope(
  id: string,
  input: EnvelopeInput,
): Promise<Envelope> {
  return apiFetch<Envelope>(`/admin/envelopes/${id}`, {
    method: "PUT",
    body: input,
  });
}

export async function deleteEnvelope(id: string): Promise<void> {
  return apiFetch<void>(`/admin/envelopes/${id}`, { method: "DELETE" });
}
