// Mirrors the API contracts in src/PrikBoard.Api/Contracts (see /openapi/v1.json).

export const noteColors = ['Yellow', 'Pink', 'Blue', 'Green', 'Orange', 'Purple'] as const
export type NoteColor = (typeof noteColors)[number]

export interface BoardSummary {
  id: string
  name: string
  description: string | null
  createdAt: string
  updatedAt: string
}

export interface BoardDetail extends BoardSummary {
  columns: ColumnDetail[]
}

export interface ColumnDetail {
  id: string
  boardId: string
  name: string
  position: number
  notes: NoteDetail[]
}

export interface NoteDetail {
  id: string
  columnId: string
  title: string
  body: string | null
  color: NoteColor
  position: number
  createdAt: string
  updatedAt: string
}

export interface NoteChanges {
  title?: string
  body?: string
  color?: NoteColor
}
