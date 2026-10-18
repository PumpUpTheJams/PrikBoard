// Mirrors the API contracts in src/PrikBoard.Api/Contracts (see /openapi/v1.json).

export const noteColors = ['Butter', 'Mint', 'Sky', 'Coral', 'Lilac'] as const
export type NoteColor = (typeof noteColors)[number]

export const bucketColors = ['Blue', 'Amber', 'Violet', 'Teal', 'Rose'] as const
export type BucketColor = (typeof bucketColors)[number]

export interface BoardSummary {
  id: string
  name: string
  description: string | null
  createdAt: string
  updatedAt: string
}

export interface BoardDetail extends BoardSummary {
  /** Back to front: later buckets are drawn on top. */
  buckets: BucketDetail[]
  /** Bucket by bucket, loose notes last; not stacking order (see updatedAt). */
  notes: NoteDetail[]
  /** On the canvas (noteId null) and attached to notes, oldest first. */
  images: ImageDetail[]
}

/** File types the API accepts for images. */
export const imageTypes = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

export interface ImageDetail {
  id: string
  boardId: string
  noteId: string | null
  fileName: string
  contentType: string
  size: number
  pixelWidth: number
  pixelHeight: number
  x: number
  y: number
  width: number
  height: number
  caption: string | null
  addedBy: string
  contentUrl: string
  createdAt: string
  updatedAt: string
}

export interface ImagePlacement {
  noteId?: string
  x?: number
  y?: number
  width?: number
}

export interface ImageChanges {
  x?: number
  y?: number
  width?: number
  caption?: string
}

export interface BucketDetail {
  id: string
  boardId: string
  name: string
  color: BucketColor
  x: number
  y: number
  width: number
  height: number
}

export type AssigneeKind = 'Person' | 'Agent'

export interface Assignee {
  name: string
  kind: AssigneeKind
}

export interface ChecklistItem {
  text: string
  done: boolean
}

export interface NoteLink {
  title: string
  url: string
}

export interface NoteDetail {
  id: string
  boardId: string
  bucketId: string | null
  title: string
  body: string | null
  color: NoteColor
  tags: string[]
  assignees: Assignee[]
  checklist: ChecklistItem[]
  links: NoteLink[]
  agentWorking: boolean
  x: number
  y: number
  createdAt: string
  updatedAt: string
}

export interface Point {
  x: number
  y: number
}

export interface Rect extends Point {
  width: number
  height: number
}

export interface BucketChanges extends Partial<Rect> {
  name?: string
  color?: BucketColor
}

/** A PATCH: omitted fields stay unchanged, a given list replaces the whole list. */
export interface NoteChanges {
  title?: string
  body?: string
  color?: NoteColor
  tags?: string[]
  assignees?: Assignee[]
  checklist?: ChecklistItem[]
  links?: NoteLink[]
  agentWorking?: boolean
}

export interface AuthProvider {
  id: string
  displayName: string
}

/** Who the request is authenticated as (whoAmI). The browser is always a Session. */
export interface Caller {
  kind: 'Session' | 'Token'
  id: string
  name: string
  email: string | null
  provider: string
  scopes: string[]
}
