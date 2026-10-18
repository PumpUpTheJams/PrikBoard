// Mirrors the API contracts in src/PrikBoard.Api/Contracts (see /openapi/v1.json).

export const noteColors = ['Yellow', 'Pink', 'Blue', 'Green', 'Orange', 'Purple'] as const
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

export interface NoteDetail {
  id: string
  boardId: string
  bucketId: string | null
  title: string
  body: string | null
  color: NoteColor
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

export interface NoteChanges {
  title?: string
  body?: string
  color?: NoteColor
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
