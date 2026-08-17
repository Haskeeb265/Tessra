"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import {
  ApiError,
  type AdminTenant,
  type Envelope,
  createTenant,
  deleteTenant,
  getEnvelopes,
  getStoredAuth,
  getTenants,
  inviteTenantUser,
  logout,
  updateTenant,
} from "@/lib/api";
import { Alert, Button, Card, Field, TextInput } from "@/components/ui";
import { AdminHeader } from "@/components/admin-header";

interface TenantDraft {
  identifier: string;
  name: string;
  envelopeId: string;
}

const emptyDraft: TenantDraft = { identifier: "", name: "", envelopeId: "" };

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleDateString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  } catch {
    return iso;
  }
}

export default function TenantsPage() {
  const router = useRouter();
  const [tenants, setTenants] = useState<AdminTenant[] | null>(null);
  const [envelopes, setEnvelopes] = useState<Envelope[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<TenantDraft>(emptyDraft);
  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState<TenantDraft>(emptyDraft);
  const [savingId, setSavingId] = useState<string | null>(null);

  useEffect(() => {
    if (!getStoredAuth()) {
      router.replace("/login");
      return;
    }

    let cancelled = false;
    (async () => {
      try {
        const [tenantList, envelopeList] = await Promise.all([
          getTenants(),
          getEnvelopes(),
        ]);
        if (cancelled) return;
        setTenants(tenantList);
        setEnvelopes(envelopeList);
        setError(null);
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          logout();
          router.replace("/login");
          return;
        }
        setError(err instanceof Error ? err.message : "Failed to load tenants.");
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [router]);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!draft.identifier.trim() || !draft.name.trim()) return;
    setCreating(true);
    setError(null);
    try {
      const created = await createTenant({
        identifier: draft.identifier.trim().toLowerCase(),
        name: draft.name.trim(),
        envelopeId: draft.envelopeId || null,
      });
      setTenants((prev) => (prev ? [...prev, created] : [created]));
      setDraft(emptyDraft);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create tenant.");
    } finally {
      setCreating(false);
    }
  }

  function startEdit(tenant: AdminTenant) {
    setEditingId(tenant.id);
    setEditDraft({
      identifier: tenant.identifier,
      name: tenant.name,
      envelopeId: tenant.envelopeId ?? "",
    });
  }

  async function handleSave(id: string) {
    if (!editDraft.identifier.trim() || !editDraft.name.trim()) return;
    setSavingId(id);
    setError(null);
    try {
      const updated = await updateTenant(id, {
        identifier: editDraft.identifier.trim().toLowerCase(),
        name: editDraft.name.trim(),
        envelopeId: editDraft.envelopeId || null,
      });
      setTenants((prev) =>
        prev ? prev.map((t) => (t.id === id ? updated : t)) : prev,
      );
      setEditingId(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update tenant.");
    } finally {
      setSavingId(null);
    }
  }

  async function handleDelete(tenant: AdminTenant) {
    if (!window.confirm(`Delete ${tenant.name}? This cannot be undone.`)) return;
    setError(null);
    try {
      await deleteTenant(tenant.id);
      setTenants((prev) => (prev ? prev.filter((t) => t.id !== tenant.id) : prev));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete tenant.");
    }
  }

  // The first invite redeemed in a workspace becomes its platform-managed
  // Superadmin; later platform invites default to Admin.
  async function handleInvite(tenant: AdminTenant) {
    const email = window.prompt(`Invite someone into ${tenant.name} (email):`);
    if (!email || !email.trim()) return;
    setError(null);
    try {
      const result = await inviteTenantUser(tenant.id, email.trim());
      window.alert(result.message ?? "Invitation sent.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to send invitation.");
    }
  }

  const envelopeSelect = (
    value: string,
    onChange: (v: string) => void,
    disabled = false,
  ) => (
    <select
      value={value}
      disabled={disabled}
      onChange={(e) => onChange(e.target.value)}
      className="w-full cursor-pointer rounded-xl border border-latte bg-white/70 px-4 py-2.5 text-sm text-espresso outline-none transition focus:border-caramel disabled:cursor-not-allowed disabled:opacity-60"
    >
      <option value="">No envelope</option>
      {envelopes.map((env) => (
        <option key={env.id} value={env.id}>
          {env.name}
        </option>
      ))}
    </select>
  );

  return (
    <main className="flex flex-1 flex-col">
      <AdminHeader active="tenants" />

      <div className="mx-auto w-full max-w-5xl flex-1 px-6 py-10">
        <div className="mb-8">
          <h1 className="font-serif text-4xl font-bold text-espresso">Tenants</h1>
          <p className="mt-2 text-sm text-mocha">
            Workspaces that use the Tessera platform. Assign each tenant an
            envelope to define the roles (and actions) available to its users.
          </p>
        </div>

        {error && (
          <div className="mb-6">
            <Alert kind="error">{error}</Alert>
          </div>
        )}

        {/* Create tenant */}
        <Card className="mb-10 p-6">
          <h2 className="mb-4 font-serif text-xl font-bold text-espresso">
            Add a tenant
          </h2>
          <form onSubmit={handleCreate} className="flex flex-col gap-4">
            <div className="grid gap-4 sm:grid-cols-[1fr_1.5fr_1fr]">
              <Field label="Identifier (X-Tenant-Id)">
                <TextInput
                  required
                  pattern="[a-z0-9-]+"
                  placeholder="acme-corp"
                  value={draft.identifier}
                  onChange={(e) =>
                    setDraft({ ...draft, identifier: e.target.value })
                  }
                />
              </Field>
              <Field label="Name">
                <TextInput
                  required
                  placeholder="Acme Corp"
                  value={draft.name}
                  onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                />
              </Field>
              <Field label="Envelope">{envelopeSelect(draft.envelopeId, (v) => setDraft({ ...draft, envelopeId: v }))}</Field>
            </div>
            <div>
              <Button
                type="submit"
                disabled={creating || !draft.identifier.trim() || !draft.name.trim()}
              >
                {creating ? "Adding…" : "+ Add tenant"}
              </Button>
            </div>
          </form>
        </Card>

        {/* Tenants list */}
        {tenants === null ? (
          <p className="py-16 text-center text-sm text-mocha">Loading tenants…</p>
        ) : tenants.length === 0 ? (
          <Card className="p-10 text-center">
            <div className="text-3xl" aria-hidden>
              🏢
            </div>
            <h2 className="mt-3 font-serif text-xl font-bold text-espresso">
              No tenants yet
            </h2>
            <p className="mx-auto mt-2 max-w-sm text-sm text-mocha">
              Add your first tenant above.
            </p>
          </Card>
        ) : (
          <Card className="overflow-hidden">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-latte bg-beige/60 text-xs uppercase tracking-wide text-mocha">
                <tr>
                  <th className="px-6 py-3 font-semibold">Tenant</th>
                  <th className="px-6 py-3 font-semibold">Identifier</th>
                  <th className="px-6 py-3 font-semibold">Envelope</th>
                  <th className="px-6 py-3 font-semibold">Created</th>
                  <th className="px-6 py-3" />
                </tr>
              </thead>
              <tbody className="divide-y divide-latte/60">
                {tenants.map((tenant) =>
                  editingId === tenant.id ? (
                    <tr key={tenant.id} className="bg-beige/40">
                      <td className="px-6 py-4">
                        <TextInput
                          value={editDraft.name}
                          onChange={(e) =>
                            setEditDraft({ ...editDraft, name: e.target.value })
                          }
                        />
                      </td>
                      <td className="px-6 py-4">
                        <TextInput
                          value={editDraft.identifier}
                          pattern="[a-z0-9-]+"
                          onChange={(e) =>
                            setEditDraft({
                              ...editDraft,
                              identifier: e.target.value,
                            })
                          }
                        />
                      </td>
                      <td className="px-6 py-4">
                        {envelopeSelect(
                          editDraft.envelopeId,
                          (v) => setEditDraft({ ...editDraft, envelopeId: v }),
                        )}
                      </td>
                      <td className="px-6 py-4 text-mocha">
                        {formatDate(tenant.createdAt)}
                      </td>
                      <td className="px-6 py-4 text-right">
                        <div className="flex justify-end gap-2">
                          <Button
                            onClick={() => handleSave(tenant.id)}
                            disabled={savingId === tenant.id}
                          >
                            {savingId === tenant.id ? "Saving…" : "Save"}
                          </Button>
                          <Button variant="ghost" onClick={() => setEditingId(null)}>
                            Cancel
                          </Button>
                        </div>
                      </td>
                    </tr>
                  ) : (
                    <tr key={tenant.id}>
                      <td className="px-6 py-4 font-semibold text-espresso">
                        {tenant.name}
                      </td>
                      <td className="px-6 py-4">
                        <code className="rounded bg-beige px-1.5 py-0.5 font-mono text-xs text-roast">
                          {tenant.identifier}
                        </code>
                      </td>
                      <td className="px-6 py-4">
                        <span
                          className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                            tenant.envelopeName
                              ? "bg-caramel/15 text-caramel"
                              : "bg-beige text-mocha"
                          }`}
                        >
                          {tenant.envelopeName ?? "No envelope"}
                        </span>
                      </td>
                      <td className="px-6 py-4 text-mocha">
                        {formatDate(tenant.createdAt)}
                      </td>
                      <td className="px-6 py-4 text-right">
                        <div className="flex justify-end gap-2">
                          <Button variant="secondary" onClick={() => startEdit(tenant)}>
                            Edit
                          </Button>
                          <Button
                            variant="ghost"
                            onClick={() => handleInvite(tenant)}
                            title="Invite by email — the first redemption becomes the workspace Superadmin"
                          >
                            Invite
                          </Button>
                          <Button variant="danger" onClick={() => handleDelete(tenant)}>
                            Delete
                          </Button>
                        </div>
                      </td>
                    </tr>
                  ),
                )}
              </tbody>
            </table>
          </Card>
        )}
      </div>
    </main>
  );
}
