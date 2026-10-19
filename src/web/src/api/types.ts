// Mirrors the API contracts in src/PrikBoard.Api/Contracts (see /openapi/v1.json).

export const noteColors = ['Butter', 'Mint', 'Sky', 'Coral', 'Lilac'] as const
export type NoteColor = (typeof noteColors)[number]

export const bucketColors = ['Blue', 'Amber', 'Violet', 'Teal', 'Rose'] as const
export type BucketColor = (typeof bucketColors)[number]

/** Ordered: each role includes the ones before it. */
export const boardRoles = ['Viewer', 'Editor', 'Owner'] as const
export type BoardRole = (typeof boardRoles)[number]

/** The longest agent rules a board can have (Board.RulesMaxLength). */
export const rulesMaxLength = 20000

/** Whether `role` is at least `minimum`. */
export const hasRole = (role: BoardRole, minimum: BoardRole) => boardRoles.indexOf(role) >= boardRoles.indexOf(minimum)

export interface BoardSummary {
  id: string
  name: string
  description: string | null
  createdAt: string
  updatedAt: string
  yourRole: BoardRole
}

export interface BoardDetail extends BoardSummary {
  /** Agent rules in Markdown, written by the owners; null when none are set. */
  rules: string | null
  rulesUpdatedAt: string | null
  /** Back to front: later buckets are drawn on top. */
  buckets: BucketDetail[]
  /** Bucket by bucket, loose notes last; not stacking order (see updatedAt). */
  notes: NoteDetail[]
  /** On the canvas (noteId null) and attached to notes, oldest first. */
  images: ImageDetail[]
  /** The board's tags by name, with how many notes use each. Notes pick their tags from these. */
  tags: BoardTag[]
  /** Who can be assigned: you first, then people, then agents. */
  people: BoardPerson[]
}

/** A tag defined on a board; it stays when no note uses it. */
export interface BoardTag {
  id: string
  name: string
  noteCount: number
}

/** Someone who can be assigned to notes on a board: a member, or the agent of a member's access token. */
export interface BoardPerson {
  principalId: string
  name: string
  kind: AssigneeKind
  isYou: boolean
  /** For an access token's agent: the person whose token it is. */
  ownerId: string | null
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
  /** Current display name of whoever added it, or "Unknown". */
  addedBy: string
  addedById: string | null
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

/** x and y are on the board, or on the note's canvas when attached (or being attached). */
export interface ImageChanges {
  x?: number
  y?: number
  width?: number
  caption?: string
  /** Attach the image to this note. */
  noteId?: string
  /** Take an attached image off its note and put it on the board. */
  detach?: boolean
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
  principalId: string
  /** Their current display name, or "Unknown". */
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

export type NoteBlockKind = 'Text' | 'Checklist' | 'Links'

/** A block of a note's content on its own canvas, in note pixels. Only the content field of its kind is set. */
export interface NoteBlock {
  id: string
  kind: NoteBlockKind
  x: number
  y: number
  width: number
  height: number
  title: string | null
  text: string | null
  items: ChecklistItem[] | null
  links: NoteLink[] | null
}

/** A new block; without x and y the server finds a free spot, without a size it gets its kind's default. */
export interface NewNoteBlock {
  kind: NoteBlockKind
  title?: string | null
  text?: string | null
  items?: ChecklistItem[] | null
  links?: NoteLink[] | null
  x?: number
  y?: number
  width?: number
  height?: number
}

/** A PATCH of a block: omitted fields stay unchanged, a given list replaces the block's whole list. */
export interface NoteBlockChanges {
  title?: string
  text?: string
  items?: ChecklistItem[]
  links?: NoteLink[]
  x?: number
  y?: number
  width?: number
  height?: number
}

export interface NoteDetail {
  id: string
  boardId: string
  bucketId: string | null
  title: string
  color: NoteColor
  tags: string[]
  assignees: Assignee[]
  /** Its content: text, checklists and links on the note's own canvas. */
  blocks: NoteBlock[]
  agentWorking: boolean
  x: number
  y: number
  /** Current display name of whoever created it; null for older notes. */
  createdBy: string | null
  createdById: string | null
  createdAt: string
  updatedAt: string
  /** Where the web app shows it: "/#/boards/{boardId}/notes/{id}". Links to it store this path. */
  path: string
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
  color?: NoteColor
  /** Tag names; new names are added to the board. */
  tags?: string[]
  /** Principal ids, from the board's people. */
  assigneeIds?: string[]
  agentWorking?: boolean
}

export interface ServerVersion {
  version: string
}

export interface AuthProvider {
  id: string
  displayName: string
}

export type PrincipalKind = 'User' | 'Agent'

export interface LinkedIdentity {
  id: string
  provider: string
  providerName: string
  linkedAt: string
}

/** Who the request is authenticated as (whoAmI). The browser is always a Session. No email is ever returned. */
export interface Caller {
  kind: 'Session' | 'Token'
  principalKind: PrincipalKind
  /** The principal id that board members refer to. */
  id: string
  /** What others see: the chosen name or a fallback like "User 1a2b". */
  displayName: string
  chosenDisplayName: string | null
  /** The sign-in provider's name, offered once while no name is chosen; never stored. */
  suggestedDisplayName: string | null
  provider: string | null
  clientId: string | null
  identities: LinkedIdentity[]
  scopes: string[]
}

export interface TokenIssuer {
  id: string
}

export interface BoardMember {
  principalId: string
  kind: PrincipalKind
  displayName: string
  role: BoardRole
  addedAt: string
  isYou: boolean
  issuer: string | null
  clientId: string | null
}

export interface UserMatch {
  principalId: string
  displayName: string
  role: BoardRole | null
}

export interface BoardInvite {
  id: string
  role: BoardRole
  createdBy: string | null
  createdAt: string
  expiresAt: string
}

export interface CreatedInvite {
  id: string
  role: BoardRole
  expiresAt: string
  token: string
  /** e.g. "/#/invite/…"; prefix the site's origin to share it. */
  path: string
}

export interface InvitePreview {
  boardId: string
  boardName: string
  role: BoardRole
  invitedBy: string | null
  expiresAt: string
  yourRole: BoardRole | null
}

export interface AcceptedInvite {
  boardId: string
  role: BoardRole
}

// Live updates over SignalR (/api/live); mirrors Contracts/Live.cs.

export type DragKind = 'Note' | 'Bucket' | 'Resize' | 'Image' | 'ImageResize'

/** The corner or edge a resize drags; the opposite one stays where it is, and an edge resizes in its direction only. */
export type ResizeHandle = 'BottomRight' | 'BottomLeft' | 'TopRight' | 'TopLeft' | 'Top' | 'Right' | 'Bottom' | 'Left'

/** Someone looking at a board right now (listed once, however many tabs they have open). */
export interface LiveViewer {
  principalId: string
  kind: PrincipalKind
  displayName: string
}

export interface BoardPresence {
  boardId: string
  viewers: LiveViewer[]
}

export interface JoinedBoard {
  boardId: string
  role: BoardRole
  /** This connection's key in drag messages. */
  key: string
  viewers: LiveViewer[]
}

export interface BoardChange {
  boardId: string
  by: string
}

/** A drag in progress: pointer travel since it started, in canvas pixels. */
export interface DragUpdate {
  kind: DragKind
  id: string
  dx: number
  dy: number
  /** For Resize and ImageResize: the corner or edge being dragged (default BottomRight). */
  handle?: ResizeHandle
}

export interface RemoteDrag extends DragUpdate {
  key: string
  principalId: string
  displayName: string
}

/** A personal access token, without the token itself (only shown when created). */
/** Agent: its own agent with capped access. App: a PrikBoard app (the VS Code extension) signed in as you. */
export type AccessTokenKind = 'Agent' | 'App'

export interface AccessToken {
  id: string
  kind: AccessTokenKind
  name: string
  /** The token's last four characters. */
  hint: string
  /** Viewer (read-only) or Editor; capped by your own role on each board. */
  access: BoardRole
  /** The only boards it can use, or null for all of yours. */
  boardIds: string[] | null
  /** The agent it acts as; null for app tokens, which act as you. */
  agentId: string | null
  createdAt: string
  expiresAt: string
  lastUsedAt: string | null
}

export interface CreatedAccessToken {
  /** Shown once: `pb_pat_...`. */
  token: string
  details: AccessToken
}

/** A one-time code for an app you allowed to sign in; it trades it with its PKCE verifier for a token. */
export interface AppSignInCode {
  code: string
  expiresAt: string
}
