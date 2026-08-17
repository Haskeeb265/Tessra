import type {
  ButtonHTMLAttributes,
  InputHTMLAttributes,
  ReactNode,
  TextareaHTMLAttributes,
} from "react";

// Small shared UI primitives styled with the coffee palette.

const inputClasses =
  "w-full rounded-xl border border-latte bg-white/70 px-4 py-2.5 text-sm text-espresso " +
  "placeholder:text-mocha/50 outline-none transition focus:border-caramel focus:ring-2 focus:ring-caramel/30";

export function Field({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1.5 text-sm font-medium text-roast">
      <span>{label}</span>
      {children}
    </label>
  );
}

export function TextInput(props: InputHTMLAttributes<HTMLInputElement>) {
  return <input {...props} className={`${inputClasses} ${props.className ?? ""}`} />;
}

export function TextArea(props: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea {...props} className={`${inputClasses} ${props.className ?? ""}`} />;
}

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";

const buttonVariants: Record<ButtonVariant, string> = {
  primary:
    "bg-roast text-cream hover:bg-espresso shadow-sm disabled:opacity-50",
  secondary:
    "bg-caramel text-espresso hover:bg-mocha hover:text-cream disabled:opacity-50",
  ghost:
    "bg-transparent text-roast hover:bg-latte/60 disabled:opacity-50",
  danger:
    "bg-transparent text-red-800 border border-red-200 hover:bg-red-50 disabled:opacity-50",
};

export function Button({
  variant = "primary",
  className = "",
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant }) {
  return (
    <button
      {...props}
      className={`inline-flex items-center justify-center gap-2 rounded-xl px-5 py-2.5 text-sm font-semibold transition ${buttonVariants[variant]} ${className}`}
    />
  );
}

export function Card({ children, className = "" }: { children: ReactNode; className?: string }) {
  return (
    <div
      className={`rounded-2xl border border-latte bg-white/80 shadow-sm ${className}`}
    >
      {children}
    </div>
  );
}

export function Alert({
  kind,
  children,
}: {
  kind: "error" | "info" | "success";
  children: ReactNode;
}) {
  const styles =
    kind === "error"
      ? "border-red-200 bg-red-50 text-red-800"
      : kind === "success"
        ? "border-green-200 bg-green-50 text-green-800"
        : "border-latte bg-beige text-roast";
  return (
    <div className={`rounded-xl border px-4 py-3 text-sm ${styles}`}>
      {children}
    </div>
  );
}
