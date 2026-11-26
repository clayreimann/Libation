const paths: Record<string, string> = {
  sidebar: "M3 4h14a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1Zm4 0v12",
  inspector: "M3 4h14a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1Zm10 0v12",
  search: "M8.5 3a5.5 5.5 0 1 1 0 11 5.5 5.5 0 0 1 0-11Zm4 9.5L17 17",
  close: "M5 5l10 10M15 5 5 15",
  chevronDown: "M5 8l5 5 5-5",
  chevronLeft: "M12 4l-6 6 6 6",
  chevronRight: "M8 4l6 6-6 6",
  more: "M4.5 10h.01M10 10h.01M15.5 10h.01",
  gear: "M10 7a3 3 0 1 1 0 6 3 3 0 0 1 0-6Zm0-4.5v2m0 11v2m7.5-7.5h-2m-11 0h-2m12.8-5.3-1.4 1.4m-7.8 7.8-1.4 1.4m0-10.6 1.4 1.4m7.8 7.8 1.4 1.4",
  library: "M4 3v14M8 3v14M12 4l4 13",
  queue: "M10 3v9m-4-4 4 4 4-4M4 16h12",
  account: "M10 4a3 3 0 1 1 0 6 3 3 0 0 1 0-6Zm-6 13c0-3 2.7-5 6-5s6 2 6 5",
  trash: "M4 6h12M8 6V4h4v2m-6 0 1 11h6l1-11",
  play: "M6 4l10 6-10 6Z",
  pause: "M7 4v12M13 4v12",
  plus: "M10 4v12M4 10h12",
  filter: "M3 5h14M6 10h8M9 15h2",
  menu: "M3 6h14M3 10h14M3 14h14",
  check: "M4 10l4 4 8-8",
  refresh: "M16 10a6 6 0 1 1-2-4.5M16 3v3.5h-3.5",
};

export type IconName = keyof typeof paths;

export function Icon({ name, size = 18, className }: { name: IconName; size?: number; className?: string }) {
  const filled = name === "play";
  return (
    <svg
      className={`icon ${className ?? ""}`}
      width={size}
      height={size}
      viewBox="0 0 20 20"
      fill={filled ? "currentColor" : "none"}
      stroke="currentColor"
      strokeWidth={name === "more" ? 2.6 : 1.6}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name]} />
    </svg>
  );
}
