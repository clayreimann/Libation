import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";

interface Props {
  label: ReactNode;
  children: (close: () => void) => ReactNode;
  disabled?: boolean;
  className?: string;
  title?: string;
}

export function Popover({ label, children, disabled, className = "", title }: Props) {
  const [open, setOpen] = useState(false);
  const [startAligned, setStartAligned] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const panel = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    if (!open) return setStartAligned(false);
    const el = panel.current;
    if (el && !startAligned && el.getBoundingClientRect().left < 0) setStartAligned(true);
  }, [open, startAligned]);

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
    <div className={`popover ${className}${startAligned ? " start" : ""}`} ref={ref}>
      <button onClick={() => setOpen((o) => !o)} disabled={disabled} aria-expanded={open} aria-haspopup="true" title={title}>
        {label}
      </button>
      {open && <div className="popover-panel" ref={panel}>{children(() => setOpen(false))}</div>}
    </div>
  );
}
