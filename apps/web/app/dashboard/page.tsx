"use client";

import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import {
  ApiError,
  TENANTS,
  type MeInfo,
  type Widget,
  createWidget,
  deleteWidget,
  getMe,
  getStoredAuth,
  getWidgets,
  logout,
  updateWidget,
} from "@/lib/api";
import { Alert, Button, Card, Field, TextArea, TextInput } from "@/components/ui";
import { PortalHeader } from "@/components/portal-header";

interface WidgetDraft {
  name: string;
  description: string;
}

const emptyDraft: WidgetDraft = { name: "", description: "" };

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return iso;
  }
}

export default function DashboardPage() {
  const router = useRouter();
  const auth = useMemo(() => getStoredAuth(), []);

  const tenant = TENANTS.find((t) => t.id === auth?.tenantId);

  const [widgets, setWidgets] = useState<Widget[] | null>(null);
  const [me, setMe] = useState<MeInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<WidgetDraft>(emptyDraft);
  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editDraft, setEditDraft] = useState<WidgetDraft>(emptyDraft);
  const [savingId, setSavingId] = useState<string | null>(null);

  // UI is driven by the same actions the server enforces (A4).
  const actions = me?.actions ?? [];
  const canCreate = actions.includes("create_widget");
  const canEdit = actions.includes("edit_widget");
  const canDelete = actions.includes("delete_widget");

  useEffect(() => {
    if (!auth) {
      router.replace("/login");
      return;
    }

    let cancelled = false;
    (async () => {
      try {
        const [data, meData] = await Promise.all([getWidgets(), getMe()]);
        if (cancelled) return;
        setWidgets(data);
        setMe(meData);
        setError(null);
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          logout();
          router.replace("/login");
          return;
        }
        setError(
          err instanceof Error ? err.message : "Failed to load widgets.",
        );
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [auth, router]);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!draft.name.trim()) return;
    setCreating(true);
    setError(null);
    try {
      const created = await createWidget({
        name: draft.name.trim(),
        description: draft.description.trim() || undefined,
      });
      setWidgets((prev) => (prev ? [...prev, created] : [created]));
      setDraft(emptyDraft);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create widget.");
    } finally {
      setCreating(false);
    }
  }

  function startEdit(widget: Widget) {
    setEditingId(widget.id);
    setEditDraft({
      name: widget.name,
      description: widget.description ?? "",
    });
  }

  async function handleSaveEdit(id: string) {
    if (!editDraft.name.trim()) return;
    setSavingId(id);
    setError(null);
    try {
      const updated = await updateWidget(id, {
        name: editDraft.name.trim(),
        description: editDraft.description.trim() || undefined,
      });
      setWidgets((prev) =>
        prev ? prev.map((w) => (w.id === id ? updated : w)) : prev,
      );
      setEditingId(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update widget.");
    } finally {
      setSavingId(null);
    }
  }

  async function handleDelete(id: string) {
    if (!window.confirm("Delete this widget? This cannot be undone.")) return;
    setError(null);
    try {
      await deleteWidget(id);
      setWidgets((prev) => (prev ? prev.filter((w) => w.id !== id) : prev));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete widget.");
    }
  }

  function handleLogout() {
    logout();
    router.push("/");
  }

  return (
    <main className="flex flex-1 flex-col">
      <PortalHeader active="widgets" onLogout={handleLogout} />

      <div className="mx-auto w-full max-w-5xl flex-1 px-6 py-10">
        <div className="mb-8">
          <h1 className="font-serif text-4xl font-bold text-espresso">
            Your widgets
          </h1>
          <p className="mt-2 text-sm text-mocha">
            Signed in as <span className="font-semibold text-roast">{auth?.email}</span> ·{" "}
            workspace <span className="font-semibold text-roast">{tenant?.name}</span> · role{" "}
            <span className="font-semibold text-roast">{me?.role ?? "—"}</span>
          </p>
          {me && me.actions.length > 0 && (
            <div className="mt-3 flex flex-wrap items-center gap-1.5">
              <span className="text-xs font-medium text-mocha">Your role allows:</span>
              {me.actions.map((action) => (
                <span
                  key={action}
                  className="rounded-full bg-beige px-2 py-0.5 text-[11px] font-medium text-mocha"
                >
                  {action}
                </span>
              ))}
            </div>
          )}
        </div>

        {error && (
          <div className="mb-6">
            <Alert kind="error">{error}</Alert>
          </div>
        )}

        {/* Create widget */}
        {canCreate && (
        <Card className="mb-10 p-6">
          <h2 className="mb-4 font-serif text-xl font-bold text-espresso">
            Brew a new widget
          </h2>
          <form onSubmit={handleCreate} className="flex flex-col gap-4">
            <div className="grid gap-4 sm:grid-cols-[1fr_2fr]">
              <Field label="Name">
                <TextInput
                  required
                  maxLength={200}
                  placeholder="Espresso machine"
                  value={draft.name}
                  onChange={(e) => setDraft({ ...draft, name: e.target.value })}
                />
              </Field>
              <Field label="Description (optional)">
                <TextInput
                  maxLength={1000}
                  placeholder="What does it do?"
                  value={draft.description}
                  onChange={(e) =>
                    setDraft({ ...draft, description: e.target.value })
                  }
                />
              </Field>
            </div>
            <div>
              <Button type="submit" disabled={creating || !draft.name.trim()}>
                {creating ? "Brewing…" : "+ Create widget"}
              </Button>
            </div>
          </form>
        </Card>
        )}

        {/* Widgets list */}
        {widgets === null ? (
          <p className="py-16 text-center text-sm text-mocha">Loading widgets…</p>
        ) : widgets.length === 0 ? (
          <Card className="p-10 text-center">
            <div className="text-3xl" aria-hidden>
              🫖
            </div>
            <h2 className="mt-3 font-serif text-xl font-bold text-espresso">
              No widgets yet
            </h2>
            <p className="mx-auto mt-2 max-w-sm text-sm text-mocha">
              This workspace has no widgets yet.{" "}
              {canCreate
                ? "Create the first one above."
                : "Ask a workspace admin to create the first one."}
            </p>
          </Card>
        ) : (
          <div className="grid gap-5 sm:grid-cols-2">
            {widgets.map((widget) => (
              <Card key={widget.id} className="flex flex-col p-6">
                {editingId === widget.id ? (
                  <div className="flex flex-col gap-3">
                    <Field label="Name">
                      <TextInput
                        maxLength={200}
                        value={editDraft.name}
                        onChange={(e) =>
                          setEditDraft({ ...editDraft, name: e.target.value })
                        }
                      />
                    </Field>
                    <Field label="Description">
                      <TextArea
                        rows={2}
                        maxLength={1000}
                        value={editDraft.description}
                        onChange={(e) =>
                          setEditDraft({
                            ...editDraft,
                            description: e.target.value,
                          })
                        }
                      />
                    </Field>
                    <div className="flex gap-2">
                      <Button
                        onClick={() => handleSaveEdit(widget.id)}
                        disabled={savingId === widget.id || !editDraft.name.trim()}
                      >
                        {savingId === widget.id ? "Saving…" : "Save"}
                      </Button>
                      <Button variant="ghost" onClick={() => setEditingId(null)}>
                        Cancel
                      </Button>
                    </div>
                  </div>
                ) : (
                  <>
                    <div className="flex items-start justify-between gap-3">
                      <h3 className="font-serif text-lg font-bold text-espresso">
                        {widget.name}
                      </h3>
                      <span className="rounded-full bg-beige px-2 py-0.5 text-[11px] font-medium text-mocha">
                        {formatDate(widget.createdAt)}
                      </span>
                    </div>
                    <p className="mt-2 flex-1 text-sm leading-relaxed text-roast/75">
                      {widget.description || "No description."}
                    </p>
                    <div className="mt-4 flex gap-2 border-t border-latte/60 pt-4">
                      {canEdit && (
                        <Button variant="secondary" onClick={() => startEdit(widget)}>
                          Edit
                        </Button>
                      )}
                      {canDelete && (
                        <Button
                          variant="danger"
                          onClick={() => handleDelete(widget.id)}
                        >
                          Delete
                        </Button>
                      )}
                    </div>
                  </>
                )}
              </Card>
            ))}
          </div>
        )}

        {!canCreate && !canEdit && !canDelete && (
          <p className="mt-10 text-center text-xs text-mocha/80">
            Your role is view-only for widgets. Ask a workspace admin to grant
            you additional actions if you need them.
          </p>
        )}
      </div>
    </main>
  );
}
