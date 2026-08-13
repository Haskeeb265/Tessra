"use client";

import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import {
  ApiError,
  TENANTS,
  type Envelope,
  type TenantUser,
  createUser,
  deleteUser,
  getMe,
  getStoredAuth,
  getTenantEnvelope,
  getUsers,
  logout,
  roleFromToken,
  updateUserRole,
} from "@/lib/api";
import { Alert, Button, Card, Field, TextInput } from "@/components/ui";
import { PortalHeader } from "@/components/portal-header";

const FALLBACK_ROLES = ["Admin", "User"];

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

export default function TeamPage() {
  const router = useRouter();
  const auth = useMemo(() => getStoredAuth(), []);
  const role = roleFromToken(auth?.accessToken);
  const isAdmin = role === "Admin";
  const tenant = TENANTS.find((t) => t.id === auth?.tenantId);

  const [currentUserId, setCurrentUserId] = useState<string | null>(null);
  const [users, setUsers] = useState<TenantUser[] | null>(null);
  const [envelope, setEnvelope] = useState<Envelope | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [newRole, setNewRole] = useState("");
  const [creating, setCreating] = useState(false);
  const [changingRole, setChangingRole] = useState<string | null>(null);

  const availableRoles = envelope
    ? envelope.roles.map((r) => r.name)
    : FALLBACK_ROLES;

  useEffect(() => {
    if (!auth) {
      router.replace("/login");
      return;
    }

    let cancelled = false;
    (async () => {
      try {
        const [me, userList, env] = await Promise.all([
          getMe(),
          getUsers(),
          getTenantEnvelope().catch(() => null),
        ]);
        if (cancelled) return;
        setCurrentUserId(me.id);
        setUsers(userList);
        setEnvelope(env);
        setError(null);
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          logout();
          router.replace("/login");
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setError("Only workspace admins can manage the team.");
          return;
        }
        setError(err instanceof Error ? err.message : "Failed to load the team.");
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [auth, router]);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!email.trim() || !password || !newRole) return;
    setCreating(true);
    setError(null);
    try {
      const created = await createUser({
        email: email.trim(),
        password,
        role: newRole,
      });
      setUsers((prev) => (prev ? [...prev, created] : [created]));
      setEmail("");
      setPassword("");
      setNewRole("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to add user.");
    } finally {
      setCreating(false);
    }
  }

  async function handleRoleChange(user: TenantUser, nextRole: string) {
    if (nextRole === user.role) return;
    setChangingRole(user.id);
    setError(null);
    try {
      const updated = await updateUserRole(user.id, nextRole);
      setUsers((prev) =>
        prev ? prev.map((u) => (u.id === updated.id ? updated : u)) : prev,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update role.");
    } finally {
      setChangingRole(null);
    }
  }

  async function handleDelete(user: TenantUser) {
    if (!window.confirm(`Remove ${user.email} from this workspace?`)) return;
    setError(null);
    try {
      await deleteUser(user.id);
      setUsers((prev) => (prev ? prev.filter((u) => u.id !== user.id) : prev));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to remove user.");
    }
  }

  function handleLogout() {
    logout();
    router.push("/");
  }

  if (!auth) {
    return null;
  }

  return (
    <main className="flex flex-1 flex-col">
      <PortalHeader active="team" onLogout={handleLogout} />

      <div className="mx-auto w-full max-w-5xl flex-1 px-6 py-10">
        <div className="mb-8">
          <h1 className="font-serif text-4xl font-bold text-espresso">Team</h1>
          <p className="mt-2 text-sm text-mocha">
            Manage who can access <span className="font-semibold text-roast">{tenant?.name}</span>{" "}
            and which roles they hold.
          </p>
          {envelope && (
            <div className="mt-3 flex flex-wrap items-center gap-1.5">
              <span className="rounded-full bg-beige px-2.5 py-0.5 text-[11px] font-medium text-mocha">
                Envelope: {envelope.name}
              </span>
              {envelope.roles.map((r) => (
                <span
                  key={r.name}
                  title={r.actions.join(", ")}
                  className="cursor-help rounded-full bg-caramel/15 px-2.5 py-0.5 text-[11px] font-semibold text-caramel"
                >
                  {r.name}
                </span>
              ))}
            </div>
          )}
        </div>

        {error && (
          <div className="mb-6">
            <Alert kind={error.includes("admins") ? "info" : "error"}>{error}</Alert>
          </div>
        )}

        {isAdmin ? (
          <>
            {/* Add user */}
            <Card className="mb-10 p-6">
              <h2 className="mb-1 font-serif text-xl font-bold text-espresso">
                Add a teammate
              </h2>
              <p className="mb-4 text-sm text-mocha">
                Roles come from the envelope your platform admin assigned to this
                workspace.
              </p>
              <form onSubmit={handleCreate} className="flex flex-col gap-4">
                <div className="grid gap-4 sm:grid-cols-[1.4fr_1fr_1fr]">
                  <Field label="Email">
                    <TextInput
                      type="email"
                      required
                      placeholder="teammate@company.com"
                      value={email}
                      onChange={(e) => setEmail(e.target.value)}
                    />
                  </Field>
                  <Field label="Password">
                    <TextInput
                      type="password"
                      required
                      minLength={8}
                      placeholder="At least 8 characters"
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                    />
                  </Field>
                  <Field label="Role">
                    <select
                      required
                      value={newRole}
                      onChange={(e) => setNewRole(e.target.value)}
                      className="w-full cursor-pointer rounded-xl border border-latte bg-white/70 px-4 py-2.5 text-sm text-espresso outline-none transition focus:border-caramel focus:ring-2 focus:ring-caramel/30"
                    >
                      <option value="" disabled>
                        Select a role…
                      </option>
                      {availableRoles.map((r) => (
                        <option key={r} value={r}>
                          {r}
                        </option>
                      ))}
                    </select>
                  </Field>
                </div>
                <div>
                  <Button
                    type="submit"
                    disabled={creating || !email.trim() || !password || !newRole}
                  >
                    {creating ? "Adding…" : "+ Add user"}
                  </Button>
                </div>
              </form>
            </Card>

            {/* Users list */}
            {users === null ? (
              <p className="py-16 text-center text-sm text-mocha">Loading team…</p>
            ) : users.length === 0 ? (
              <Card className="p-10 text-center">
                <div className="text-3xl" aria-hidden>
                  🤝
                </div>
                <h2 className="mt-3 font-serif text-xl font-bold text-espresso">
                  No teammates yet
                </h2>
                <p className="mx-auto mt-2 max-w-sm text-sm text-mocha">
                  Add your first teammate above.
                </p>
              </Card>
            ) : (
              <Card className="overflow-hidden">
                <table className="w-full text-left text-sm">
                  <thead className="border-b border-latte bg-beige/60 text-xs uppercase tracking-wide text-mocha">
                    <tr>
                      <th className="px-6 py-3 font-semibold">Member</th>
                      <th className="px-6 py-3 font-semibold">Role</th>
                      <th className="px-6 py-3 font-semibold">Joined</th>
                      <th className="px-6 py-3" />
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-latte/60">
                    {users.map((user) => {
                      const isSelf = user.id === currentUserId;
                      return (
                        <tr key={user.id}>
                          <td className="px-6 py-4">
                            <div className="flex items-center gap-2">
                              <span className="font-semibold text-espresso">
                                {user.email}
                              </span>
                              {isSelf && (
                                <span className="rounded-full bg-beige px-2 py-0.5 text-[10px] font-bold text-mocha">
                                  you
                                </span>
                              )}
                            </div>
                          </td>
                          <td className="px-6 py-4">
                            <select
                              value={user.role}
                              disabled={isSelf || changingRole === user.id}
                              onChange={(e) =>
                                handleRoleChange(user, e.target.value)
                              }
                              className="cursor-pointer rounded-lg border border-latte bg-white/70 px-3 py-1.5 text-sm text-espresso outline-none transition focus:border-caramel disabled:cursor-not-allowed disabled:opacity-60"
                            >
                              {availableRoles.includes(user.role)
                                ? availableRoles.map((r) => (
                                    <option key={r} value={r}>
                                      {r}
                                    </option>
                                  ))
                                : [user.role, ...availableRoles].map((r) => (
                                    <option key={r} value={r}>
                                      {r}
                                    </option>
                                  ))}
                            </select>
                          </td>
                          <td className="px-6 py-4 text-mocha">
                            {formatDate(user.createdAt)}
                          </td>
                          <td className="px-6 py-4 text-right">
                            {!isSelf && (
                              <button
                                onClick={() => handleDelete(user)}
                                className="text-xs font-semibold text-red-700 hover:underline"
                              >
                                Remove
                              </button>
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </Card>
            )}
          </>
        ) : (
          <Card className="p-10 text-center">
            <div className="text-3xl" aria-hidden>
              🔒
            </div>
            <h2 className="mt-3 font-serif text-xl font-bold text-espresso">
              Admins only
            </h2>
            <p className="mx-auto mt-2 max-w-sm text-sm text-mocha">
              You&apos;re signed in as <span className="font-semibold">{role}</span>.
              Only users with the <span className="font-semibold">Admin</span> role can
              manage the team. Ask your workspace admin to change your role.
            </p>
          </Card>
        )}
      </div>
    </main>
  );
}
