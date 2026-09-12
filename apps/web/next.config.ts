import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  /**
   * Claude web reaches this portal through a cloudflared quick tunnel, whose
   * hostname is different on every run. Next.js dev blocks `/_next/*` requests
   * from any origin that is not localhost, so without this the client bundle
   * never loads over the tunnel: the page renders from SSR but never hydrates,
   * the login form falls back to a native submit, and the `?tenant=` /
   * `?returnUrl=` query params are lost ("No workspace was specified").
   *
   * The wildcard keeps working when `scripts/start-claude-web.sh` gets a new
   * tunnel hostname. Development only — production builds are unaffected.
   */
  allowedDevOrigins: [
    "*.trycloudflare.com",
    "localhost:3000",
    "127.0.0.1:3000",
  ],
};

export default nextConfig;
