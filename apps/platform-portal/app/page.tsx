"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { getStoredAuth } from "@/lib/api";
import { Button } from "@/components/ui";

export default function Home() {
  const router = useRouter();

  useEffect(() => {
    const auth = getStoredAuth();
    router.replace(auth?.accessToken ? "/tenants" : "/login");
  }, [router]);

  return (
    <main className="flex flex-1 flex-col items-center justify-center px-6 py-24 text-center">
      <div className="text-4xl" aria-hidden>
        🛠️
      </div>
      <h1 className="mt-4 font-serif text-4xl font-bold text-espresso">
        Tessera Platform Portal
      </h1>
      <p className="mt-3 max-w-md text-sm text-mocha">
        The superadmin console for the Tessera platform — manage tenants,
        envelopes, roles and the actions they allow.
      </p>
      <div className="mt-8 flex items-center gap-4">
        <Link href="/login">
          <Button>Log in</Button>
        </Link>
      </div>
    </main>
  );
}
