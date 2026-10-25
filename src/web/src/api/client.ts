import type {
  AccessToken,
  AdminBoard,
  AdminBoardMember,
  AdminUser,
  AppSignInCode,
  CreatedAccessToken,
  DiscordAccountLinkPreview,
  DiscordAccountStatus,
  DiscordBotDetail,
  DiscordConnection,
  DiscordServers,
  DiscordUpdate,
  SlackAppDetail,
  SlackConnection,
  SlackWorkspaces,
  AuthProvider,
  BoardClip,
  CopyItemsRequest,
  ServerVersion,
  BoardDetail,
  BoardSummary,
  BoardTag,
  BucketChanges,
  BucketDetail,
  AcceptedInvite,
  BoardInvite,
  BoardMember,
  BoardRole,
  Caller,
  CreatedInvite,
  InvitePreview,
  TokenIssuer,
  TrashContents,
  EmptyTrashResult,
  UserMatch,
  DrawingChanges,
  DrawingContent,
  DrawingDetail,
  DrawingStrokesChange,
  DrawingTransform,
  ImageChanges,
  ImageDetail,
  ImageUploadRequest,
  ImageUploadUrl,
  ImagePlacement,
  NewDrawing,
  NewDrawingStroke,
  NewCardComponent,
  CardComponent,
  CardComponentChanges,
  CardChange,
  CardChanges,
  CardDetail,
  PastedItems,
  PastePlace,
  PlaceItemsRequest,
  PlacedItems,
  Point,
  Rect,
  TagChanges,
  TagColor,
  CreateSignInProvider,
  GitHubAppManifest,
  OpenIdAuthority,
  SetupStatus,
  SignInProviderChanges,
  SignInProviderDetail,
  SystemCheck,
} from './types'
import { MAX_IMAGE_BYTES } from './types'

export class ApiError extends Error {
  readonly status: number
  /** A validation problem's messages by field (the message is the first of them). */
  readonly errors: Record<string, string[]>

  constructor(status: number, message: string, errors: Record<string, string[]> = {}) {
    super(message)
    this.status = status
    this.errors = errors
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

/** Browser navigation through a provider that signs nobody in and comes back to returnUrl with ?setupTest=ok or failed. */
export const testSignInUrl = (providerId: string, returnUrl: string) =>
  `/api/setup/providers/${encodeURIComponent(providerId)}/test?returnUrl=${encodeURIComponent(returnUrl)}`

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

/** What an answer without a message of its own means. */
const statusMessages: Record<number, string> = {
  413: 'That is too large for the server.',
  502: "The server can't be reached right now.",
  503: "The server can't be reached right now.",
  504: 'The server took too long to answer.',
}

/** Sends the request and throws an ApiError unless the answer is a success. */
async function send(method: string, path: string, body?: unknown): Promise<Response> {
  const headers: Record<string, string> = { ...(await config.headers?.()) }
  if (body !== undefined) headers['Content-Type'] = body instanceof Blob ? body.type : 'application/json'
  if (method !== 'GET' && liveConnectionId) headers['X-PrikBoard-Live'] = liveConnectionId
  let response: Response
  try {
    response = await fetch(`${config.baseUrl ?? ''}/api${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : body instanceof Blob ? body : JSON.stringify(body),
    })
  } catch (error) {
    // The server drops the connection on a body over its request limit, before it can answer.
    if (body instanceof Blob && error instanceof TypeError) throw new ApiError(0, "The file didn't reach the server. It may be too large.")
    throw error
  }

  if (response.status === 401) onUnauthorized()
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as ProblemDetails | null
    const firstError = problem?.errors && Object.values(problem.errors)[0]?.[0]
    // Without a problem (a proxy's answer, say) there may be no status text either: HTTP/2 has none.
    const message = firstError ?? problem?.detail ?? problem?.title ?? (response.statusText || statusMessages[response.status])
    throw new ApiError(response.status, message ?? `The server answered ${response.status}.`, problem?.errors)
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
  useSignInAvatar: () => request<Caller>('PUT', '/me/avatar'),
  removeAvatar: () => request<Caller>('DELETE', '/me/avatar'),
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

  listDiscordConnections: (boardId: string) => request<DiscordConnection[]>('GET', `/boards/${boardId}/discord`),
  listDiscordServers: (boardId: string) => request<DiscordServers>('GET', `/boards/${boardId}/discord/servers`),
  /** Previews the board in a channel of the server, or in all of it when channelId is null; returns the board's connections. */
  connectDiscordChannel: (boardId: string, guildId: string, channelId: string | null) =>
    request<DiscordConnection[]>('POST', `/boards/${boardId}/discord`, { guildId, channelId }),
  removeDiscordConnection: (boardId: string, guildId: string) => request<void>('DELETE', `/boards/${boardId}/discord/${guildId}`),
  removeDiscordChannel: (boardId: string, guildId: string, channelId: string) =>
    request<void>('DELETE', `/boards/${boardId}/discord/${guildId}/channels/${channelId}`),
  removeDiscordWholeServer: (boardId: string, guildId: string) => request<void>('DELETE', `/boards/${boardId}/discord/${guildId}/whole-server`),
  /** Replaces what a connected channel does for the board; returns the board's connections. */
  updateDiscordChannel: (boardId: string, guildId: string, channelId: string, settings: { updates: DiscordUpdate[]; saveBucketId: string | null }) =>
    request<DiscordConnection[]>('PUT', `/boards/${boardId}/discord/${guildId}/channels/${channelId}`, settings),
  getDiscordAccount: () => request<DiscordAccountStatus>('GET', '/me/discord-account'),
  previewDiscordAccountLink: (code: string) => request<DiscordAccountLinkPreview>('GET', `/me/discord-account/links/${encodeURIComponent(code)}`),
  linkDiscordAccount: (code: string) => request<DiscordAccountStatus>('POST', '/me/discord-account', { code }),
  unlinkDiscordAccount: () => request<void>('DELETE', '/me/discord-account'),
  listSlackConnections: (boardId: string) => request<SlackConnection[]>('GET', `/boards/${boardId}/slack`),
  listSlackWorkspaces: (boardId: string) => request<SlackWorkspaces>('GET', `/boards/${boardId}/slack/workspaces`),
  /** A channel, or every channel of the workspace when channelId is null. */
  connectSlackChannel: (boardId: string, teamId: string, channelId: string | null) =>
    request<SlackConnection[]>('POST', `/boards/${boardId}/slack`, { teamId, channelId }),
  removeSlackConnection: (boardId: string, teamId: string) => request<void>('DELETE', `/boards/${boardId}/slack/${teamId}`),
  removeSlackChannel: (boardId: string, teamId: string, channelId: string) =>
    request<void>('DELETE', `/boards/${boardId}/slack/${teamId}/channels/${channelId}`),
  removeSlackWholeWorkspace: (boardId: string, teamId: string) => request<void>('DELETE', `/boards/${boardId}/slack/${teamId}/whole-workspace`),

  listBoards: () => request<BoardSummary[]>('GET', '/boards'),
  createBoard: (name: string, description?: string) =>
    request<BoardDetail>('POST', '/boards', { name, description }),
  getBoard: (boardId: string) => request<BoardDetail>('GET', `/boards/${boardId}`),
  /** A board by the short id in its address; public boards answer anyone, signed in or not. */
  getBoardByShortId: (shortId: string) => request<BoardDetail>('GET', `/boards/${shortId}`),
  updateBoard: (
    boardId: string,
    changes: {
      name?: string
      key?: string
      description?: string
      rules?: string
      isPublic?: boolean
      publicShowsPeople?: boolean
      showCursors?: boolean
    },
  ) => request<BoardSummary>('PATCH', `/boards/${boardId}`, changes),
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

  createBucket: (boardId: string, bucket: { name: string } & Partial<Rect>) =>
    request<BucketDetail>('POST', `/boards/${boardId}/buckets`, bucket),
  updateBucket: (bucketId: string, changes: BucketChanges) =>
    request<BucketDetail>('PATCH', `/buckets/${bucketId}`, changes),
  /** Puts a bucket on top of the others, for everyone (no item changes bucket). */
  bringBucketToFront: (bucketId: string) => request<BucketDetail>('POST', `/buckets/${bucketId}/front`),
  /** Puts a loose drawing on top of the stack it shares with the buckets, for everyone. */
  bringDrawingToFront: (drawingId: string) => request<DrawingDetail>('POST', `/drawings/${drawingId}/front`),
  /** Puts cards, images and drawings into a bucket's free room, each at its own size, growing it towards `grow` when they don't fit. */
  placeItemsInBucket: (bucketId: string, placement: PlaceItemsRequest) =>
    request<PlacedItems>('POST', `/buckets/${bucketId}/items`, placement),

  /** The items as a clip, with everything on them (images as base64), for pasteItems here or on another server. Changes nothing. */
  copyItems: (boardId: string, what: CopyItemsRequest) => request<BoardClip>('POST', `/boards/${boardId}/copy`, what),
  /** Adds what is in a clip to the board as new items. */
  pasteItems: (boardId: string, clip: BoardClip, place: PastePlace = {}) =>
    request<PastedItems>('POST', `/boards/${boardId}/paste`, { clip, ...place }),
  deleteBucket: (bucketId: string) => request<void>('DELETE', `/buckets/${bucketId}`),
  /** Brings back a bucket deleted in the last 7 days, with the same id. */
  restoreBucket: (bucketId: string) => request<BucketDetail>('POST', `/buckets/${bucketId}/restore`),

  createCard: (
    boardId: string,
    card: Omit<CardChanges, 'title'> & { title: string; components?: NewCardComponent[] } & ({ bucketId?: string } | (Point & { bucketId?: string })),
  ) => request<CardDetail>('POST', `/boards/${boardId}/cards`, card),
  /** Several cards at once, in order; all are created or none. */
  createCards: (
    boardId: string,
    cards: (Omit<CardChanges, 'title'> & { title: string; components?: NewCardComponent[]; bucketId?: string; x?: number; y?: number })[],
  ) => request<CardDetail[]>('POST', `/boards/${boardId}/cards/batch`, { cards }),
  /** The card at a path's board short id and number (#/boards/{shortId}/cards/{number}). */
  getCardByNumber: (shortId: string, number: number) => request<CardDetail>('GET', `/boards/${shortId}/cards/${number}`),
  updateCard: (cardId: string, changes: CardChanges) => request<CardDetail>('PATCH', `/cards/${cardId}`, changes),
  /**
   * Moves a card into a free spot in a bucket, or exactly to a point. With a point alone the server works out the bucket;
   * with a `bucketId` too, the card joins that bucket, which must contain the card's center (the web app sends the bucket
   * shown in front where buckets overlap).
   */
  moveCard: (cardId: string, to: { bucketId: string } | (Point & { bucketId?: string })) =>
    request<CardDetail>('POST', `/cards/${cardId}/move`, to),
  deleteCard: (cardId: string) => request<void>('DELETE', `/cards/${cardId}`),
  /**
   * Brings back a card deleted in the last 7 days, with the same id and the images and drawings deleted along with it:
   * where it was, or at `at` (dragged out of the trash).
   */
  restoreCard: (cardId: string, at?: Point) => request<CardDetail>('POST', `/cards/${cardId}/restore`, at),
  /** Who changed what on a card and when, newest first; also for cards in the trash (members only). */
  listCardHistory: (cardId: string) => request<CardChange[]>('GET', `/cards/${cardId}/history`),
  /** The cards deleted in the last 7 days, most recently deleted first, with the images deleted with them. */
  listDeletedCards: (boardId: string) => request<TrashContents>('GET', `/boards/${boardId}/trash`),
  /** Deletes a card in the trash for good, with its images and drawings. */
  purgeCard: (boardId: string, cardId: string) => request<void>('DELETE', `/boards/${boardId}/trash/${cardId}`),
  /** Deletes everything in the board's trash for good. */
  emptyTrash: (boardId: string) => request<EmptyTrashResult>('DELETE', `/boards/${boardId}/trash`),
  addCardComponent: (cardId: string, component: NewCardComponent) => request<CardComponent>('POST', `/cards/${cardId}/components`, component),
  updateCardComponent: (cardId: string, componentId: string, changes: CardComponentChanges) =>
    request<CardComponent>('PATCH', `/cards/${cardId}/components/${componentId}`, changes),
  deleteCardComponent: (cardId: string, componentId: string) => request<void>('DELETE', `/cards/${cardId}/components/${componentId}`),
  /** Assigns you and shows the working halo (true), or takes the halo away (false). */
  setCardWorking: (cardId: string, working: boolean) =>
    request<CardDetail>('POST', `/cards/${cardId}/working`, { working }),

  listBoardTags: (boardId: string) => request<BoardTag[]>('GET', `/boards/${boardId}/tags`),
  createBoardTag: (boardId: string, name: string, color?: TagColor) =>
    request<BoardTag>('POST', `/boards/${boardId}/tags`, { name, color }),
  /** Renames (on every card of the board too) and/or recolors a tag. */
  updateBoardTag: (boardId: string, tagId: string, changes: TagChanges) =>
    request<BoardTag>('PATCH', `/boards/${boardId}/tags/${tagId}`, changes),
  /** Removes the tag from every card of the board too. */
  deleteBoardTag: (boardId: string, tagId: string) => request<void>('DELETE', `/boards/${boardId}/tags/${tagId}`),
  /** Brings back a deleted tag and puts it back on the cards it was removed from. */
  restoreBoardTag: (boardId: string, tagId: string) => request<BoardTag>('POST', `/boards/${boardId}/tags/${tagId}/restore`),

  /** Uploads a file onto the board, or attached to a card with cardId; at x/y (on the card's canvas with cardId) or a free spot. */
  uploadImage: async (boardId: string, file: File, placement: ImagePlacement = {}) => {
    // Refused before it is sent: the server would only say so after the whole upload, if a proxy lets it through at all.
    if (file.size > MAX_IMAGE_BYTES) {
      const size = (bytes: number) => `${Math.round((bytes / 1024 / 1024) * 10) / 10} MB`
      throw new ApiError(413, `${file.name} is ${size(file.size)}; images can be at most ${size(MAX_IMAGE_BYTES)}.`)
    }
    const query = new URLSearchParams({ fileName: file.name })
    for (const [key, value] of Object.entries(placement)) if (value !== undefined) query.set(key, String(value))
    return request<ImageDetail>('POST', `/boards/${boardId}/images?${query}`, file)
  },
  /** A one-time URL (for agents: curl -T) that adds one image where the request says, as you; expires after 15 minutes. */
  createImageUpload: (boardId: string, upload: ImageUploadRequest = {}) =>
    request<ImageUploadUrl>('POST', `/boards/${boardId}/image-uploads`, upload),
  /** Sends a file to an upload URL from createImageUpload; the URL itself is the key. */
  completeImageUpload: (uploadUrl: string, file: Blob) =>
    request<ImageDetail>('PUT', `/image-uploads/${uploadUrl.slice(uploadUrl.lastIndexOf('/') + 1)}`, file),
  updateImage: (imageId: string, changes: ImageChanges) => request<ImageDetail>('PATCH', `/images/${imageId}`, changes),
  deleteImage: (imageId: string) => request<void>('DELETE', `/images/${imageId}`),
  /** Brings back an image deleted in the last 7 days (not one whose card is deleted: restore the card). */
  restoreImage: (imageId: string) => request<ImageDetail>('POST', `/images/${imageId}/restore`),

  createDrawing: (boardId: string, drawing: NewDrawing) => request<DrawingDetail>('POST', `/boards/${boardId}/drawings`, drawing),
  /** The strokes at a drawing's contentUrl. The URL names their revision, so the browser may cache the answer. */
  getDrawingContent: (contentUrl: string) => request<DrawingContent>('GET', contentUrl.replace(/^\/api/, '')),
  addDrawingStroke: (drawingId: string, stroke: NewDrawingStroke) => request<DrawingDetail>('POST', `/drawings/${drawingId}/strokes`, stroke),
  /** Takes a stroke (by its index in drawing order) off a drawing; not its only one. */
  deleteDrawingStroke: (drawingId: string, index: number) => request<DrawingDetail>('DELETE', `/drawings/${drawingId}/strokes/${index}`),
  /** Scales and turns a drawing about a pivot (default: the middle of its box); its points change, line widths stay. */
  transformDrawing: (drawingId: string, transform: DrawingTransform) => request<DrawingDetail>('POST', `/drawings/${drawingId}/transform`, transform),
  /** Writes new points for every stroke of a drawing (inks kept), e.g. to put it back as it was before a transform. */
  setDrawingStrokes: (drawingId: string, change: DrawingStrokesChange) => request<DrawingDetail>('PUT', `/drawings/${drawingId}/strokes`, change),
  updateDrawing: (drawingId: string, changes: DrawingChanges) =>
    request<DrawingDetail>('PATCH', `/drawings/${drawingId}`, changes),
  deleteDrawing: (drawingId: string) => request<void>('DELETE', `/drawings/${drawingId}`),
  /** Brings back a drawing deleted in the last 7 days (not one whose card is deleted: restore the card). */
  restoreDrawing: (drawingId: string) => request<DrawingDetail>('POST', `/drawings/${drawingId}/restore`),

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
  getDiscordBot: () => request<DiscordBotDetail>('GET', '/admin/discord'),
  /** Fields left out stay as they are; the bot (re)connects at once. */
  setDiscordBot: (settings: { botToken?: string; publicUrl?: string }) => request<DiscordBotDetail>('PUT', '/admin/discord', settings),
  deleteDiscordBot: () => request<void>('DELETE', '/admin/discord'),
  leaveDiscordServer: (guildId: string) => request<void>('DELETE', `/admin/discord/servers/${guildId}`),
  getSlackApp: () => request<SlackAppDetail>('GET', '/admin/slack'),
  /** Fields left out stay as they are; the app (re)connects at once. Both tokens the first time. */
  setSlackApp: (settings: { botToken?: string; appToken?: string; publicUrl?: string }) => request<SlackAppDetail>('PUT', '/admin/slack', settings),
  deleteSlackApp: () => request<void>('DELETE', '/admin/slack'),
  forgetSlackWorkspace: (teamId: string) => request<void>('DELETE', `/admin/slack/workspaces/${teamId}`),

  getSetupStatus: () => request<SetupStatus>('GET', '/setup'),
  unlockSetup: (key: string) => request<void>('POST', '/setup/unlock', { key }),
  listSignInProviders: () => request<SignInProviderDetail[]>('GET', '/setup/providers'),
  createSignInProvider: (provider: CreateSignInProvider) => request<SignInProviderDetail>('POST', '/setup/providers', provider),
  updateSignInProvider: (id: string, changes: SignInProviderChanges) =>
    request<SignInProviderDetail>('PATCH', `/setup/providers/${encodeURIComponent(id)}`, changes),
  deleteSignInProvider: (id: string) => request<void>('DELETE', `/setup/providers/${encodeURIComponent(id)}`),
  checkOpenIdAuthority: (authority: string) => request<OpenIdAuthority>('POST', '/setup/openid-check', { authority }),
  /** The manifest for a new GitHub App; post it to GitHub as a form (see postGitHubManifest). */
  beginGitHubApp: (app: { id: string; name: string; organization?: string; returnUrl?: string }) =>
    request<GitHubAppManifest>('POST', '/setup/github-app', app),
  getSystemChecks: () => request<SystemCheck[]>('GET', '/setup/checks'),
  getRecoveryKit: async () => {
    const response = await send('GET', '/setup/recovery-kit')
    return { blob: await response.blob(), fileName: 'prikboard-recovery-kit.txt' }
  },
}
