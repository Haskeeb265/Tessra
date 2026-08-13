# Tessera Web — Business (Tenant) Portal

Frontend for the [Tessera platform](../platform) — a minimal, modern Next.js
app with a coffee-themed UI that exercises the platform API from a real
user's point of view.

This is the **business portal**: it is what companies (tenants) use to
register/log in, manage widgets, and — for workspace admins — manage their
team by adding users and assigning roles from the envelope a superadmin
assigned to the workspace.

The companion **platform portal** (`apps/platform-portal`, port 3001) is for
superadmins: creating tenants and envelopes (roles + their actions).

Stack: Next.js 16 (App Router) · TypeScript (strict) · Tailwind CSS v4.

## Features

- **Landing page** (`/`) with a live API health badge
- **Register / Log in** (`/register`, `/login`) against `POST /auth/register` and `POST /auth/login`
- **Workspace (tenant) picker** — requests send the `X-Tenant-Id` header required by the platform
- **Dashboard** (`/dashboard`) — tenant-scoped widget CRUD (`GET/POST/PUT/DELETE /widgets`)
- JWT access tokens stored in localStorage, refreshed automatically on 401 via `POST /auth/refresh`
- Role-aware UI — widget **delete** only shows for `Admin` users

## Running it

1. Start the platform API (see `apps/platform/Guide.md`):

   ```bash
   dotnet run --project apps/platform/src/Tessera.Platform.Api
   ```

   The API listens on `http://localhost:5085` (http profile). If you run it on
   another port, copy `.env.example` to `.env.local` and set `NEXT_PUBLIC_API_URL`.

2. Start the web app:

   ```bash
   cd apps/web
   npm install
   npm run dev
   ```

3. Open http://localhost:3000 and create an account (pick a workspace).

> **Note:** the seeded admin (`admin@tessera.com` / `Admin123!`) is only created
> when the API uses PostgreSQL (e.g. `docker compose up` in `apps/platform`).
> With the in-memory database, register a new account instead.

## Environment variables

| Variable              | Default               | Description                          |
| --------------------- | --------------------- | ------------------------------------ |
| `NEXT_PUBLIC_API_URL` | `http://localhost:5085` | Base URL of the platform API.      |
