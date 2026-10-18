import type {
  AuthProvider,
  BoardDetail,
  BoardSummary,
  BucketChanges,
  BucketColor,
  BucketDetail,
  Caller,
  NoteChanges,
  NoteDetail,
  Point,
  Rect,
} from './types'

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

let onUnauthorized = () => {}

/** Called whenever the API answers 401, for example when the session cookie expired. */
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

/** Browser navigation that starts sign-in and comes back to returnUrl (a local path) afterwards. */
export const loginUrl = (providerId: string, returnUrl: string) =>
  `/api/auth/login/${encodeURIComponent(providerId)}?returnUrl=${encodeURIComponent(returnUrl)}`

interface ProblemDetails {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === 401) onUnauthorized()
  if (!response.ok) {
    const problem: ProblemDetails | null = await response.json().catch(() => null)
    const firstError = problem?.errors && Object.values(problem.errors)[0]?.[0]
    throw new ApiError(response.status, firstError ?? problem?.detail ?? problem?.title ?? response.statusText)
  }

  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

// Method names match the operationIds in the OpenAPI document.
export const api = {
  listAuthProviders: () => request<AuthProvider[]>('GET', '/auth/providers'),
  logout: () => request<void>('POST', '/auth/logout'),
  whoAmI: () => request<Caller>('GET', '/me'),

  listBoards: () => request<BoardSummary[]>('GET', '/boards'),
  createBoard: (name: string, description?: string) =>
    request<BoardDetail>('POST', '/boards', { name, description }),
  getBoard: (boardId: string) => request<BoardDetail>('GET', `/boards/${boardId}`),
  updateBoard: (boardId: string, changes: { name?: string; description?: string }) =>
    request<BoardSummary>('PATCH', `/boards/${boardId}`, changes),
  deleteBoard: (boardId: string) => request<void>('DELETE', `/boards/${boardId}`),

  createBucket: (boardId: string, bucket: { name: string; color?: BucketColor } & Partial<Rect>) =>
    request<BucketDetail>('POST', `/boards/${boardId}/buckets`, bucket),
  updateBucket: (bucketId: string, changes: BucketChanges) =>
    request<BucketDetail>('PATCH', `/buckets/${bucketId}`, changes),
  deleteBucket: (bucketId: string) => request<void>('DELETE', `/buckets/${bucketId}`),

  createNote: (boardId: string, note: Omit<NoteChanges, 'title'> & { title: string } & ({ bucketId?: string } | Point)) =>
    request<NoteDetail>('POST', `/boards/${boardId}/notes`, note),
  updateNote: (noteId: string, changes: NoteChanges) => request<NoteDetail>('PATCH', `/notes/${noteId}`, changes),
  /** Into a free spot inside a bucket, or exactly to a point (the server then works out the bucket). */
  moveNote: (noteId: string, to: { bucketId: string } | Point) =>
    request<NoteDetail>('POST', `/notes/${noteId}/move`, to),
  deleteNote: (noteId: string) => request<void>('DELETE', `/notes/${noteId}`),
}
