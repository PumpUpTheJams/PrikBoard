import type {
  AccessToken,
  AdminBoard,
  AdminBoardMember,
  AdminUser,
  AppSignInCode,
  CreatedAccessToken,
  AuthProvider,
  ServerVersion,
  BoardDetail,
  BoardSummary,
  BoardTag,
  BucketChanges,
  BucketColor,
  BucketDetail,
  AcceptedInvite,
  BoardInvite,
  BoardMember,
  BoardRole,
  Caller,
  CreatedInvite,
  InvitePreview,
  TokenIssuer,
  UserMatch,
  ImageChanges,
  ImageDetail,
  ImagePlacement,
  NewNoteBlock,
  NoteBlock,
  NoteBlockChanges,
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

/** How requests are sent. The web app is served next to the API and signs in with a cookie, so it needs none of this. */
export interface ClientConfig {
  /** Origin of the API, for example http://localhost:5080. Default: relative to the page. */
  baseUrl?: string
  /** Headers for every request, for example a bearer token. Called per request, so a token can be refreshed. */
  headers?: () => Record<string, string> | Promise<Record<string, string>>
}

let config: ClientConfig = {}

/** For clients that are not the web app (the VS Code extension): point the client at an API and add its credentials. */
export function configureClient(next: ClientConfig) {
  config = next
}

let onUnauthorized = () => {}

/** Called whenever the API answers 401, for example when the session cookie expired. */
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

/** Form action that links another sign-in to the signed-in user (a POST navigation, see AuthEndpoints.Link). */
export const linkUrl = (providerId: string) => `/api/auth/link/${encodeURIComponent(providerId)}`

/** Browser navigation that starts sign-in and comes back to returnUrl (a local path) afterwards. */
export const loginUrl = (providerId: string, returnUrl: string) =>
  `/api/auth/login/${encodeURIComponent(providerId)}?returnUrl=${encodeURIComponent(returnUrl)}`

interface ProblemDetails {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

let liveConnectionId: string | null = null

/**
 * The open live-updates connection. Changes are sent with it, so the server does not echo them back to this tab,
 * which reloads after its own changes anyway.
 */
export function setLiveConnectionId(id: string | null) {
  liveConnectionId = id
}

/** A JSON request, or with a Blob body the file itself (sent with the file's own content type). */
async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await send(method, path, body)
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

/** Sends the request and throws an ApiError unless the answer is a success. */
async function send(method: string, path: string, body?: unknown): Promise<Response> {
  const headers: Record<string, string> = { ...(await config.headers?.()) }
  if (body !== undefined) headers['Content-Type'] = body instanceof Blob ? body.type : 'application/json'
  if (method !== 'GET' && liveConnectionId) headers['X-PrikBoard-Live'] = liveConnectionId
  const response = await fetch(`${config.baseUrl ?? ''}/api${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : body instanceof Blob ? body : JSON.stringify(body),
  })

  if (response.status === 401) onUnauthorized()
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as ProblemDetails | null
    const firstError = problem?.errors && Object.values(problem.errors)[0]?.[0]
    throw new ApiError(response.status, firstError ?? problem?.detail ?? problem?.title ?? response.statusText)
  }

  return response
}

// Method names match the operationIds in the OpenAPI document.
export const api = {
  listAuthProviders: () => request<AuthProvider[]>('GET', '/auth/providers'),
  getVersion: () => request<ServerVersion>('GET', '/auth/version'),
  logout: () => request<void>('POST', '/auth/logout'),
  whoAmI: () => request<Caller>('GET', '/me'),
  updateMe: (changes: { displayName?: string }) => request<Caller>('PATCH', '/me', changes),
  unlinkIdentity: (identityId: string) => request<void>('DELETE', `/me/identities/${identityId}`),
  listTokenIssuers: () => request<TokenIssuer[]>('GET', '/auth/token-issuers'),
  listAccessTokens: () => request<AccessToken[]>('GET', '/me/tokens'),
  createAccessToken: (token: { name: string; access: BoardRole; boardIds?: string[]; expiresInDays?: number }) =>
    request<CreatedAccessToken>('POST', '/me/tokens', token),
  revokeAccessToken: (tokenId: string) => request<void>('DELETE', `/me/tokens/${tokenId}`),
  approveAppSignIn: (signIn: { name: string; codeChallenge: string; redirectUri?: string }) =>
    request<AppSignInCode>('POST', '/me/app-sign-ins', signIn),
  exchangeAppSignIn: (code: string, codeVerifier: string) =>
    request<CreatedAccessToken>('POST', '/auth/app-sign-ins/token', { code, codeVerifier }),

  listBoards: () => request<BoardSummary[]>('GET', '/boards'),
  createBoard: (name: string, description?: string) =>
    request<BoardDetail>('POST', '/boards', { name, description }),
  getBoard: (boardId: string) => request<BoardDetail>('GET', `/boards/${boardId}`),
  updateBoard: (boardId: string, changes: { name?: string; description?: string; rules?: string }) =>
    request<BoardSummary>('PATCH', `/boards/${boardId}`, changes),
  deleteBoard: (boardId: string) => request<void>('DELETE', `/boards/${boardId}`),
  /** The board as one file to save: its content with images, but no people. */
  exportBoard: async (boardId: string) => {
    const response = await send('GET', `/boards/${boardId}/export`)
    const name = response.headers.get('Content-Disposition')?.match(/filename\*=(?:UTF-8'')?([^;]+)/i)?.[1]
    return { blob: await response.blob(), fileName: name ? decodeURIComponent(name) : 'board.prikboard.json' }
  },
  /** Makes a new board you own from a file made by exportBoard. The file is sent as it is, as JSON. */
  importBoard: async (file: Blob) =>
    request<BoardDetail>('POST', '/boards/import', new Blob([await file.text()], { type: 'application/json' })),

  createBucket: (boardId: string, bucket: { name: string; color?: BucketColor } & Partial<Rect>) =>
    request<BucketDetail>('POST', `/boards/${boardId}/buckets`, bucket),
  updateBucket: (bucketId: string, changes: BucketChanges) =>
    request<BucketDetail>('PATCH', `/buckets/${bucketId}`, changes),
  deleteBucket: (bucketId: string) => request<void>('DELETE', `/buckets/${bucketId}`),

  createNote: (
    boardId: string,
    note: Omit<NoteChanges, 'title'> & { title: string; blocks?: NewNoteBlock[] } & ({ bucketId?: string } | Point),
  ) => request<NoteDetail>('POST', `/boards/${boardId}/notes`, note),
  updateNote: (noteId: string, changes: NoteChanges) => request<NoteDetail>('PATCH', `/notes/${noteId}`, changes),
  /** Into a free spot inside a bucket, or exactly to a point (the server then works out the bucket). */
  moveNote: (noteId: string, to: { bucketId: string } | Point) =>
    request<NoteDetail>('POST', `/notes/${noteId}/move`, to),
  deleteNote: (noteId: string) => request<void>('DELETE', `/notes/${noteId}`),
  addNoteBlock: (noteId: string, block: NewNoteBlock) => request<NoteBlock>('POST', `/notes/${noteId}/blocks`, block),
  updateNoteBlock: (noteId: string, blockId: string, changes: NoteBlockChanges) =>
    request<NoteBlock>('PATCH', `/notes/${noteId}/blocks/${blockId}`, changes),
  deleteNoteBlock: (noteId: string, blockId: string) => request<void>('DELETE', `/notes/${noteId}/blocks/${blockId}`),
  /** Assigns you and shows the working halo (true), or takes the halo away (false). */
  setNoteWorking: (noteId: string, working: boolean) =>
    request<NoteDetail>('POST', `/notes/${noteId}/working`, { working }),

  listBoardTags: (boardId: string) => request<BoardTag[]>('GET', `/boards/${boardId}/tags`),
  createBoardTag: (boardId: string, name: string) => request<BoardTag>('POST', `/boards/${boardId}/tags`, { name }),
  /** Renames the tag on every note of the board too. */
  renameBoardTag: (boardId: string, tagId: string, name: string) =>
    request<BoardTag>('PATCH', `/boards/${boardId}/tags/${tagId}`, { name }),
  /** Removes the tag from every note of the board too. */
  deleteBoardTag: (boardId: string, tagId: string) => request<void>('DELETE', `/boards/${boardId}/tags/${tagId}`),

  /** Uploads a file onto the board, or attached to a note with noteId; at x/y (on the note's canvas with noteId) or a free spot. */
  uploadImage: (boardId: string, file: File, placement: ImagePlacement = {}) => {
    const query = new URLSearchParams({ fileName: file.name })
    for (const [key, value] of Object.entries(placement)) if (value !== undefined) query.set(key, String(value))
    return request<ImageDetail>('POST', `/boards/${boardId}/images?${query}`, file)
  },
  updateImage: (imageId: string, changes: ImageChanges) => request<ImageDetail>('PATCH', `/images/${imageId}`, changes),
  deleteImage: (imageId: string) => request<void>('DELETE', `/images/${imageId}`),

  listBoardMembers: (boardId: string) => request<BoardMember[]>('GET', `/boards/${boardId}/members`),
  addBoardMember: (boardId: string, principalId: string, role: BoardRole) =>
    request<BoardMember>('POST', `/boards/${boardId}/members`, { principalId, role }),
  addBoardAgent: (boardId: string, agent: { issuer: string; clientId: string; role: BoardRole; displayName?: string }) =>
    request<BoardMember>('POST', `/boards/${boardId}/agents`, agent),
  updateBoardMember: (boardId: string, principalId: string, role: BoardRole) =>
    request<BoardMember>('PATCH', `/boards/${boardId}/members/${principalId}`, { role }),
  removeBoardMember: (boardId: string, principalId: string) =>
    request<void>('DELETE', `/boards/${boardId}/members/${principalId}`),
  /** Exact email only; the address goes in the body so it stays out of logs. */
  findUserByEmail: (boardId: string, email: string) =>
    request<UserMatch[]>('POST', `/boards/${boardId}/members/lookup`, { email }),

  listBoardInvites: (boardId: string) => request<BoardInvite[]>('GET', `/boards/${boardId}/invites`),
  createBoardInvite: (boardId: string, role: BoardRole, expiresInDays?: number) =>
    request<CreatedInvite>('POST', `/boards/${boardId}/invites`, { role, expiresInDays }),
  revokeBoardInvite: (boardId: string, inviteId: string) => request<void>('DELETE', `/boards/${boardId}/invites/${inviteId}`),
  previewInvite: (token: string) => request<InvitePreview>('POST', '/invites/preview', { token }),
  acceptInvite: (token: string) => request<AcceptedInvite>('POST', '/invites/accept', { token }),

  adminListBoards: () => request<AdminBoard[]>('GET', '/admin/boards'),
  adminDeleteBoard: (boardId: string) => request<void>('DELETE', `/admin/boards/${boardId}`),
  adminListUsers: () => request<AdminUser[]>('GET', '/admin/users'),
  /** Adds the user to the board with the role, or changes the role they have. */
  adminSetBoardMember: (boardId: string, userId: string, role: BoardRole) =>
    request<AdminBoardMember>('PUT', `/admin/boards/${boardId}/members/${userId}`, { role }),
  adminRemoveBoardMember: (boardId: string, principalId: string) =>
    request<void>('DELETE', `/admin/boards/${boardId}/members/${principalId}`),
  adminUpdateUser: (userId: string, changes: { isAdmin?: boolean }) => request<void>('PATCH', `/admin/users/${userId}`, changes),
  adminDeleteUser: (userId: string) => request<void>('DELETE', `/admin/users/${userId}`),
}
