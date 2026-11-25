import { useEffect, useRef, useState, type ReactNode } from "react";

interface Props {
  label: ReactNode;
  children: (close: () => void) => ReactNode;
  disabled?: boolean;
  className?: string;
  title?: string;
}

export function Popover({ label, children, disabled, className = "", title }: Props) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div className={`popover ${className}`} ref={ref}>
      <button onClick={() => setOpen((o) => !o)} disabled={disabled} aria-expanded={open} aria-haspopup="true" title={title}>
        {label}
      </button>
      {open && <div className="popover-panel">{children(() => setOpen(false))}</div>}
    </div>
  );
}
