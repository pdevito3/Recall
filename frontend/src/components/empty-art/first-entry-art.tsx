function Sparkle({ x, y, size = 4 }: { x: number; y: number; size?: number }) {
  return <path d={`M${x} ${y - size}v${size * 2}M${x - size} ${y}h${size * 2}`} />
}

export function FirstEntryArt() {
  return (
    <svg
      viewBox="0 0 176 128"
      fill="none"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
      className="h-auto w-44 max-w-full stroke-muted-foreground/60"
    >
      <circle cx="88" cy="68" r="54" className="fill-muted-foreground/10 stroke-none" />
      <path d="M95 98v14l3.5-3 3.5 3V96" className="fill-muted-foreground/20" />
      <path d="M38 50v48c20-3 36-2 50 4 14-6 30-7 50-4V50" className="fill-muted-foreground/20" />
      <path d="M88 48c-12-6-28-8-44-5v50c16-3 32-1 44 5z" className="fill-background" />
      <path d="M88 48c12-6 28-8 44-5v50c-16-3-32-1-44 5z" className="fill-background" />
      <g transform="translate(128 52) rotate(35)">
        <rect x="-4" y="-26" width="8" height="34" rx="2" className="fill-background" />
        <rect x="-4" y="-26" width="8" height="7" rx="2" className="fill-muted-foreground/20" />
        <path d="M-4 8l4 9 4-9z" className="fill-muted-foreground/20" />
      </g>
      <Sparkle x={34} y={30} />
      <Sparkle x={150} y={100} size={3} />
      <circle cx="24" cy="78" r="2" />
      <circle cx="152" cy="22" r="2.5" />
      <circle cx="44" cy="112" r="1.5" className="fill-muted-foreground/30 stroke-none" />
    </svg>
  )
}
