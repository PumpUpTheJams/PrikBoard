// Inline stroke icons from the board design; they inherit the text colour.

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

export const PostItIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M4 4h16v10l-6 6H4z" />
    <path d="M14 20v-6h6" />
  </Icon>
)

export const SelectIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M5 3l14 7-6 2-2 6z" />
  </Icon>
)

export const PanIcon = ({ size = 18 }: IconProps) => (
  <Icon size={size}>
    <path d="M8 12V5.5a1.5 1.5 0 0 1 3 0V11M11 11V4.5a1.5 1.5 0 0 1 3 0V11M14 11V6a1.5 1.5 0 0 1 3 0v8a6 6 0 0 1-6 6h-.5a6 6 0 0 1-5-2.7L3.3 14.5a1.5 1.5 0 0 1 2.4-1.8L8 15" />
  </Icon>
)

export const FrameIcon = ({ size = 18 }: IconProps) => (
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

export const SparkleIcon = ({ size = 12 }: IconProps) => (
  <Icon size={size} width={2.4}>
    <path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z" />
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

export const NoteDocIcon = ({ size = 16 }: IconProps) => (
  <Icon size={size}>
    <path d="M5 4h14v16H5zM9 9h6M9 13h6M9 17h3" />
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

export const ResizeIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size} width={2.2}>
    <path d="M20 10L10 20M20 16l-4 4" />
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

export const ExternalIcon = ({ size = 14 }: IconProps) => (
  <Icon size={size}>
    <path d="M14 4h6v6M20 4l-9 9M18 14v6H4V6h6" />
  </Icon>
)
