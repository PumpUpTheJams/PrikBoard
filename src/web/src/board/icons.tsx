// Inline stroke icons from the board design; they inherit the text color.

import type { ReactNode } from 'react'

function Icon({ size = 16, width = 2, children }: { size?: number; width?: number; children: ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={width}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {children}
    </svg>
  )
}

interface IconProps {
  size?: number
}

export const CardIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 4h16v10l-6 6H4z" />
    <path d="M14 20v-6h6" />
  </Icon>
)

export const PenIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 20h4L19 9l-4-4L4 16z" />
    <path d="M13 7l4 4" />
  </Icon>
)

/** A card with a frown, for pages with nothing to show. */
export const SadCardIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 4h16v10l-6 6H4z" />
    <path d="M14 20v-6h6" />
    <path d="M8.5 8.5h.01M13.5 8.5h.01" />
    <path d="M8 14c1.2-1.4 3.8-1.4 5 0" />
  </Icon>
)

export const ShieldIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size}>
    <path d="M12 3l7 3v5c0 4.5-3 8-7 10-4-2-7-5.5-7-10V6z" />
  </Icon>
)

export const SelectIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M5 3l14 7-6 2-2 6z" />
  </Icon>
)

export const BucketToolIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <rect x="3" y="4" width="18" height="16" rx="2" />
    <path d="M9 4v16M15 4v16" />
  </Icon>
)

export const ImageIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M3 4h18v16H3zM21 16l-5-5-9 9M9 8.5a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3" />
  </Icon>
)

export const BucketIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size} width={2.2}>
    <rect x="3" y="4" width="18" height="16" rx="2" />
  </Icon>
)

export const ExpandIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size} width={2.2}>
    <path d="M14 4h6v6M10 20H4v-6M20 4l-7 7M4 20l7-7" />
  </Icon>
)

export const CloseIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size} width={2.2}>
    <path d="M6 6l12 12M18 6L6 18" />
  </Icon>
)

export const ChevronDownIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M6 9l6 6 6-6" />
  </Icon>
)

export const ChevronUpIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M6 15l6-6 6 6" />
  </Icon>
)

export const ChevronLeftIcon = ({ size = 22 }: IconProps) => (
  <Icon size={size}>
    <path d="M15 6l-6 6 6 6" />
  </Icon>
)

export const ChevronRightIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M9 6l6 6-6 6" />
  </Icon>
)

export const MoreIcon = ({ size = 20 }: IconProps) => (
  <Icon size={size} width={3}>
    <path d="M12 5h.01M12 12h.01M12 19h.01" />
  </Icon>
)

export const SparkleIcon = ({ size = 12 }: IconProps) => (
  <Icon size={size} width={2.4}>
    <path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z" />
  </Icon>
)

export const UndoIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M9 14 4 9l5-5" />
    <path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11" />
  </Icon>
)

export const RedoIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="m15 14 5-5-5-5" />
    <path d="M20 9H9.5a5.5 5.5 0 0 0 0 11H13" />
  </Icon>
)

export const PlusIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M12 5v14M5 12h14" />
  </Icon>
)

export const MinusIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M5 12h14" />
  </Icon>
)

export const TextIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size}>
    <path d="M5 4h14v16H5zM9 9h6M9 13h6M9 17h3" />
  </Icon>
)

/** A page with a small sparkle: the board's agent rules. */
export const RulesIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M14 3H6v18h12V9" />
    <path d="M9 10h5M9 14h6M9 18h4" />
    <path d="M19 2v4M17 4h4" />
  </Icon>
)

export const ChecklistIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 6l2 2 3-3M4 13l2 2 3-3M12 7h8M12 14h8M12 20h8" />
  </Icon>
)

export const LinkIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size}>
    <path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1" />
  </Icon>
)

export const PullRequestIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size}>
    <circle cx="6" cy="5" r="2" />
    <circle cx="6" cy="19" r="2" />
    <circle cx="18" cy="19" r="2" />
    <path d="M6 7v10M18 17V9a3 3 0 0 0-3-3h-4M13 3l-3 3 3 3" />
  </Icon>
)

export const TrashIcon = ({ size = 15 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3" />
  </Icon>
)

/** An arrow coming back out: put a deleted card back on the board. */
export const PutBackIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M9 14L4 9l5-5" />
    <path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11" />
  </Icon>
)

export const CheckIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size} width={2.4}>
    <path d="M5 12l5 5 9-10" />
  </Icon>
)

export const UserIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size} width={2.2}>
    <circle cx="12" cy="8" r="4" />
    <path d="M4 21a8 8 0 0 1 16 0" />
  </Icon>
)

export const PeopleIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size}>
    <circle cx="9" cy="8" r="3.5" />
    <path d="M2.5 20a6.5 6.5 0 0 1 13 0M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6 6 0 0 1 3.5 6" />
  </Icon>
)

export const SignOutIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M15 4h4v16h-4M10 8l-4 4 4 4M6 12h10" />
  </Icon>
)

export const DownloadIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M12 4v11M7 11l5 5 5-5M5 20h14" />
  </Icon>
)

export const UploadIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M12 16V5M7 9l5-5 5 5M5 20h14" />
  </Icon>
)

export const ExternalIcon =({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M14 4h6v6M20 4l-9 9M18 14v6H4V6h6" />
  </Icon>
)

export const PencilIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 20h4L19 9l-4-4L4 16z" />
    <path d="M13 7l4 4" />
  </Icon>
)

export const SearchIcon = ({ size = 15 }: IconProps) => (
  <Icon size={size}>
    <circle cx="11" cy="11" r="6" />
    <path d="M20 20l-4.5-4.5" />
  </Icon>
)

export const FilterIcon = ({ size = 15 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 5h16l-6 8v5l-4 2v-7z" />
  </Icon>
)
