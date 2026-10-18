// Inline stroke icons; they inherit the text colour.

const stroke = {
  width: 16,
  height: 16,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 2,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
} as const

export const MoveIcon = () => (
  <svg {...stroke}>
    <path d="M4 3l7 17 2.5-7.5L21 10z" />
  </svg>
)

export const BucketIcon = () => (
  <svg {...stroke}>
    <rect x="3" y="5" width="18" height="15" rx="3" strokeDasharray="4 3" />
    <path d="M3 9h7" />
  </svg>
)

export const NoteIcon = () => (
  <svg {...stroke}>
    <path d="M4 4h16v11l-5 5H4z" />
    <path d="M15 20v-5h5" />
  </svg>
)

export const GripIcon = () => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden>
    {[6, 12, 18].map((y) => (
      <g key={y}>
        <circle cx="9" cy={y} r="1.6" />
        <circle cx="15" cy={y} r="1.6" />
      </g>
    ))}
  </svg>
)

export const ResizeIcon = () => (
  <svg {...stroke} strokeWidth={2.2}>
    <path d="M20 10L10 20M20 16l-4 4" />
  </svg>
)

export const CloseIcon = () => (
  <svg {...stroke}>
    <path d="M6 6l12 12M18 6L6 18" />
  </svg>
)

export const SunIcon = () => (
  <svg {...stroke} width={18} height={18}>
    <circle cx="12" cy="12" r="4" />
    <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
  </svg>
)

export const MoonIcon = () => (
  <svg {...stroke} width={18} height={18}>
    <path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z" />
  </svg>
)
