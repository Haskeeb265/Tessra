"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import {
  ApiError,
  type Envelope,
  createEnvelope,
  deleteEnvelope,
  getEnvelopes,
  getStoredAuth,
  logout,
  updateEnvelope,
} from "@/lib/api";
import { Alert, Button, Card, Field, TextInput } from "@/components/ui";
import { AdminHeader } from "@/components/admin-header";

interface RoleDraft {
  name: string;
  actions: string; // comma-separated
}

interface EnvelopeDraft {
  name: string;
  description: string;
  roles: RoleDraft[];
}

const emptyDraft: EnvelopeDraft = { name: "", description: "", roles: [] };

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

export default function EnvelopesPage() {
  const router = useRouter();
  const [envelopes, setEnvelopes] = useState<Envelope[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [formMode, setFormMode] = useState<"closed" | "create" | "edit">("closed");
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState<EnvelopeDraft>(emptyDraft);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!getStoredAuth()) {
      router.replace("/login");
      return;
    }

    let cancelled = false;
    (async () => {
      try {
        const list = await getEnvelopes();
        if (cancelled) return;
        setEnvelopes(list);
        setError(null);
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          logout();
          router.replace("/login");
          return;
        }
        setError(err instanceof Error ? err.message : "Failed to load envelopes.");
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [router]);

  function startCreate() {
    setEditingId(null);
    setDraft(emptyDraft);
    setFormMode("create");
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  function startEdit(envelope: Envelope) {
    setEditingId(envelope.id);
    setDraft({
      name: envelope.name,
      description: envelope.description ?? "",
      roles: envelope.roles.map((r) => ({ name: r.name, actions: r.actions.join(", ") })),
    });
    setFormMode("edit");
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  function closeForm() {
    setFormMode("closed");
    setEditingId(null);
  }

  function updateRole(index: number, patch: Partial<RoleDraft>) {
    setDraft((prev) => ({
      ...prev,
      roles: prev.roles.map((r, i) => (i === index ? { ...r, ...patch } : r)),
    }));
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!draft.name.trim() || draft.roles.length === 0) return;
    setSaving(true);
    setError(null);
    try {
      const input = {
        name: draft.name.trim(),
        description: draft.description.trim() || null,
        roles: draft.roles
          .filter((r) => r.name.trim())
          .map((r) => ({
            name: r.name.trim(),
            actions: r.actions
              .split(",")
              .map((a) => a.trim())
              .filter(Boolean),
          })),
      };
      if (formMode === "edit" && editingId) {
        const updated = await updateEnvelope(editingId, input);
        setEnvelopes((prev) =>
          prev ? prev.map((e) => (e.id === editingId ? updated : e)) : prev,
        );
      } else {
        const created = await createEnvelope(input);
        setEnvelopes((prev) => (prev ? [...prev, created] : [created]));
      }
      closeForm();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save envelope.");
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(envelope: Envelope) {
    if (!window.confirm(`Delete envelope "${envelope.name}"?`)) return;
    setError(null);
    try {
      await deleteEnvelope(envelope.id);
      setEnvelopes((prev) =>
        prev ? prev.filter((e) => e.id !== envelope.id) : prev,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete envelope.");
    }
  }

  return (
    <main className="flex flex-1 flex-col">
      <AdminHeader active="envelopes" />

      <div className="mx-auto w-full max-w-5xl flex-1 px-6 py-10">
        <div className="mb-8 flex flex-wrap items-end justify-between gap-4">
          <div>
            <h1 className="font-serif text-4xl font-bold text-espresso">Envelopes</h1>
            <p className="mt-2 text-sm text-mocha">
              An envelope bundles the roles (and their actions) that a tenant
              can assign to its users. Assign an envelope to a tenant from the{" "}
              <span className="font-semibold text-roast">Tenants</span> page.
            </p>
          </div>
          {formMode === "closed" && (
            <Button onClick={startCreate}>+ New envelope</Button>
          )}
        </div>

        {error && (
          <div className="mb-6">
            <Alert kind="error">{error}</Alert>
          </div>
        )}

        {/* Create / edit form */}
        {formMode !== "closed" && (
          <Card className="mb-10 p-6">
            <h2 className="mb-1 font-serif text-xl font-bold text-espresso">
              {formMode === "edit" ? "Edit envelope" : "New envelope"}
            </h2>
            <p className="mb-4 text-sm text-mocha">
              Actions are the Tessera capabilities a role is allowed (e.g.{" "}
              <code className="rounded bg-beige px-1 font-mono text-xs">create_widget</code>).
              They&apos;re informational for now — enforcement is on the backlog.
            </p>
            <form onSubmit={handleSubmit} className="flex flex-col gap-4">
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Name">
                  <TextInput
                    required
                    placeholder="Standard"
                    value={draft.name}
                    onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                  />
                </Field>
                <Field label="Description">
                  <TextInput
                    placeholder="Default roles for new workspaces"
                    value={draft.description}
                    onChange={(e) =>
                      setDraft({ ...draft, description: e.target.value })
                    }
                  />
                </Field>
              </div>

              <div className="flex flex-col gap-3">
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-roast">Roles</span>
                  <Button
                    type="button"
                    variant="secondary"
                    onClick={() =>
                      setDraft((prev) => ({
                        ...prev,
                        roles: [...prev.roles, { name: "", actions: "" }],
                      }))
                    }
                  >
                    + Add role
                  </Button>
                </div>

                {draft.roles.length === 0 && (
                  <p className="rounded-xl border border-dashed border-latte bg-beige/40 px-4 py-3 text-sm text-mocha">
                    No roles yet — add at least one role (e.g. Admin, Manager,
                    User).
                  </p>
                )}

                {draft.roles.map((role, index) => (
                  <div
                    key={index}
                    className="grid gap-3 rounded-xl border border-latte bg-beige/30 p-4 sm:grid-cols-[1fr_2fr_auto]"
                  >
                    <Field label={`Role ${index + 1} name`}>
                      <TextInput
                        required
                        placeholder="Manager"
                        value={role.name}
                        onChange={(e) =>
                          updateRole(index, { name: e.target.value })
                        }
                      />
                    </Field>
                    <Field label="Actions (comma-separated)">
                      <TextInput
                        placeholder="create_widget, edit_widget"
                        value={role.actions}
                        onChange={(e) =>
                          updateRole(index, { actions: e.target.value })
                        }
                      />
                    </Field>
                    <div className="flex items-end pb-1">
                      <Button
                        type="button"
                        variant="danger"
                        onClick={() =>
                          setDraft((prev) => ({
                            ...prev,
                            roles: prev.roles.filter((_, i) => i !== index),
                          }))
                        }
                      >
                        Remove
                      </Button>
                    </div>
                  </div>
                ))}
              </div>

              <div className="flex gap-2">
                <Button
                  type="submit"
                  disabled={saving || !draft.name.trim() || draft.roles.length === 0}
                >
                  {saving ? "Saving…" : formMode === "edit" ? "Save changes" : "Create envelope"}
                </Button>
                <Button type="button" variant="ghost" onClick={closeForm}>
                  Cancel
                </Button>
              </div>
            </form>
          </Card>
        )}

        {/* Envelopes list */}
        {envelopes === null ? (
          <p className="py-16 text-center text-sm text-mocha">Loading envelopes…</p>
        ) : envelopes.length === 0 ? (
          <Card className="p-10 text-center">
            <div className="text-3xl" aria-hidden>
              📦
            </div>
            <h2 className="mt-3 font-serif text-xl font-bold text-espresso">
              No envelopes yet
            </h2>
            <p className="mx-auto mt-2 max-w-sm text-sm text-mocha">
              Create your first envelope to start assigning roles to tenants.
            </p>
          </Card>
        ) : (
          <div className="flex flex-col gap-5">
            {envelopes.map((envelope) => (
              <Card key={envelope.id} className="p-6">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <div className="flex flex-wrap items-center gap-2">
                      <h2 className="font-serif text-xl font-bold text-espresso">
                        {envelope.name}
                      </h2>
                      <span className="rounded-full bg-beige px-2 py-0.5 text-[11px] font-medium text-mocha">
                        {formatDate(envelope.createdAt)}
                      </span>
                    </div>
                    {envelope.description && (
                      <p className="mt-1 text-sm text-mocha">
                        {envelope.description}
                      </p>
                    )}
                  </div>
                  <div className="flex shrink-0 gap-2">
                    <Button variant="secondary" onClick={() => startEdit(envelope)}>
                      Edit
                    </Button>
                    <Button variant="danger" onClick={() => handleDelete(envelope)}>
                      Delete
                    </Button>
                  </div>
                </div>

                <div className="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                  {envelope.roles.map((role) => (
                    <div
                      key={role.name}
                      className="rounded-xl border border-latte bg-beige/30 p-4"
                    >
                      <p className="text-sm font-bold text-espresso">{role.name}</p>
                      {role.actions.length > 0 ? (
                        <div className="mt-2 flex flex-wrap gap-1">
                          {role.actions.map((action) => (
                            <span
                              key={action}
                              className="rounded-full bg-white/70 px-2 py-0.5 text-[10px] font-semibold text-mocha"
                            >
                              {action}
                            </span>
                          ))}
                        </div>
                      ) : (
                        <p className="mt-2 text-xs text-mocha/70">
                          No actions assigned
                        </p>
                      )}
                    </div>
                  ))}
                </div>
              </Card>
            ))}
          </div>
        )}
      </div>
    </main>
  );
}
