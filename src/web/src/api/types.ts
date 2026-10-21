// Mirrors the API contracts in src/PrikBoard.Api/Contracts (see /openapi/v1.json).

export const cardColors = ['Butter', 'Mint', 'Sky', 'Coral', 'Lilac'] as const
export type CardColor = (typeof cardColors)[number]

export const bucketColors = ['Blue', 'Amber', 'Violet', 'Teal', 'Rose'] as const
export type BucketColor = (typeof bucketColors)[number]

/** Default is a shade of the card's own fill; the others are solid chips of their own, in palette order. */
export const tagColors = ['Default', 'Brick', 'Rust', 'Ochre', 'Moss', 'Pine', 'Teal', 'Ocean', 'Indigo', 'Violet', 'Berry', 'Ink'] as const
export type TagColor = (typeof tagColors)[number]

/** Ordered: each role includes the ones before it. */
export const boardRoles = ['Viewer', 'Editor', 'Owner'] as const
export type BoardRole = (typeof boardRoles)[number]

/** The longest agent rules a board can have (Board.RulesMaxLength). */
export const rulesMaxLength = 20000

/** Whether `role` is at least `minimum`. */
export const hasRole = (role: BoardRole, minimum: BoardRole) => boardRoles.indexOf(role) >= boardRoles.indexOf(minimum)

export interface BoardSummary {
  id: string
  /** The board in web addresses (#/boards/{shortId}/cards/{number}): random and never changed. */
  shortId: string
  /** Shown before card numbers, e.g. "MPCT" in MPCT-415. Owners may change it; it is not unique. */
  key: string
  name: string
  description: string | null
  createdAt: string
  updatedAt: string
  yourRole: BoardRole
  /** Anyone with the link can view it, without being a member or signed in. */
  isPublic: boolean
  /** Whether those visitors see who is on the board (names and pictures), or anonymous circles. */
  publicShowsPeople: boolean
}

export interface BoardDetail extends BoardSummary {
  /** You see it because it is public, not as a member: read only, and without people when the board hides them. */
  visiting: boolean
  /** Agent rules in Markdown, written by the owners; null when none are set. */
  rules: string | null
  rulesUpdatedAt: string | null
  /** Back to front: later buckets are drawn on top. */
  buckets: BucketDetail[]
  /** Bucket by bucket, loose cards last; not stacking order (see updatedAt). */
  cards: CardDetail[]
  /** On the canvas (cardId null) and attached to cards, oldest first. */
  images: ImageDetail[]
  /** Pen strokes on the canvas (cardId null) and on cards' canvases, oldest first. */
  drawings: DrawingDetail[]
  /** The board's tags by name, with how many cards use each. Cards pick their tags from these. */
  tags: BoardTag[]
  /** Who can be assigned: you first, then people, then agents. */
  people: BoardPerson[]
}

/** A tag defined on a board; it stays when no card uses it. */
export interface BoardTag {
  id: string
  name: string
  color: TagColor
  cardCount: number
}

/** A PATCH of a board tag: omitted fields stay unchanged. A new name changes it on every card too. */
export interface TagChanges {
  name?: string
  color?: TagColor
}

/** Someone who can be assigned to cards on a board: a member, or the agent of a member's access token. */
export interface BoardPerson {
  principalId: string
  name: string
  kind: AssigneeKind
  isYou: boolean
  /** For an access token's agent: the person whose token it is. */
  ownerId: string | null
  /** People only: their chosen picture (getAvatar), shown instead of initials; null without one. */
  avatarUrl: string | null
}

/** File types the API accepts for images. */
export const imageTypes = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

export interface ImageDetail {
  id: string
  boardId: string
  cardId: string | null
  /** The bucket it sits in on the board (it moves with it); null when loose or attached to a card. */
  bucketId: string | null
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
  cardId?: string
  x?: number
  y?: number
  width?: number
}

/** Where an image sent to a one-time upload URL goes (createImageUpload). */
export interface ImageUploadRequest extends ImagePlacement {
  caption?: string
  fileName?: string
}

/** A one-time upload URL: PUT the file there, without authentication. */
export interface ImageUploadUrl {
  uploadUrl: string
  expiresAt: string
  /** A curl command that uploads FILE there. */
  command: string
}

/** x and y are on the board, or on the card's canvas when attached (or being attached). */
export interface ImageChanges {
  x?: number
  y?: number
  width?: number
  caption?: string
  /** Attach the image to this card. */
  cardId?: string
  /** Take an attached image off its card and put it on the board. */
  detach?: boolean
}

/**
 * Pen inks in two rows of matching pairs: light ones for the dark board, then dark ones for drawing over a card's
 * light fill. Mirrored as `--ink-*` tokens in index.css.
 */
export const inkColors = ['Chalk', 'Butter', 'Mint', 'Sky', 'Coral', 'Lilac', 'Ink', 'Ochre', 'Pine', 'Ocean', 'Brick', 'Violet'] as const
export type InkColor = (typeof inkColors)[number]

/** Base line widths: fine, medium and bold. */
export const strokeWidths = [2, 4, 8] as const
export type StrokeWidth = (typeof strokeWidths)[number]

/** One point of a stroke: x and y from the drawing's x/y, then pressure from 0 to 1 (0.5 without pressure). */
export type StrokePoint = [number, number, number]

/** A freehand pen stroke; its points come from contentUrl (getDrawingContent). */
export interface DrawingDetail {
  id: string
  boardId: string
  cardId: string | null
  /** The bucket it sits in on the board (it moves with it); null when loose or on a card. */
  bucketId: string | null
  x: number
  y: number
  width: number
  height: number
  color: InkColor
  strokeWidth: number
  pointCount: number
  addedBy: string
  addedById: string | null
  contentUrl: string
  createdAt: string
  updatedAt: string
}

export interface DrawingContent {
  drawingId: string
  points: StrokePoint[]
}

/** A new stroke: x and y on the grid, points measured from there. */
export interface NewDrawing {
  x: number
  y: number
  points: StrokePoint[]
  color?: InkColor
  strokeWidth?: number
  cardId?: string
}

/** A PATCH of a drawing; x and y are on wherever it ends up (give both when attaching or detaching). */
export interface DrawingChanges {
  x?: number
  y?: number
  color?: InkColor
  cardId?: string
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
  /** Folded up into a square in its top-right corner: its cards are hidden (keeping their places) until it opens again. */
  collapsed: boolean
}

/** The side a bucket grows towards when cards put into it don't fit; Left and Up move its edge out. */
export type GrowSide = 'Down' | 'Right' | 'Left' | 'Up'

/** placeCardsInBucket: cards to put into free slots, in order, and where the bucket grows when full (default Down). */
export interface PlaceCardsRequest {
  cardIds: string[]
  grow?: GrowSide
}

/** The bucket (possibly grown) and the cards put into it, in the order given. */
export interface PlacedCards {
  bucket: BucketDetail
  cards: CardDetail[]
}

export type AssigneeKind = 'Person' | 'Agent'

export interface Assignee {
  principalId: string
  /** Their current display name, or "Unknown". */
  name: string
  kind: AssigneeKind
  /** People only: their chosen picture (getAvatar), shown instead of initials; null without one. */
  avatarUrl: string | null
}

export interface ChecklistItem {
  text: string
  done: boolean
}

export interface CardLink {
  title: string
  url: string
}

export type CardComponentKind = 'Text' | 'Checklist' | 'Links'

/** A component of a card's content on its own canvas, in card pixels. Only the content field of its kind is set. */
export interface CardComponent {
  id: string
  kind: CardComponentKind
  x: number
  y: number
  width: number
  height: number
  title: string | null
  text: string | null
  items: ChecklistItem[] | null
  links: CardLink[] | null
}

/** A new component; without x and y the server finds a free spot, without a size it gets its kind's default. */
export interface NewCardComponent {
  kind: CardComponentKind
  title?: string | null
  text?: string | null
  items?: ChecklistItem[] | null
  links?: CardLink[] | null
  x?: number
  y?: number
  width?: number
  height?: number
  /** Only to put back a removed component: the id it had. */
  id?: string
}

/** A PATCH of a component: omitted fields stay unchanged, a given list replaces the component's whole list. */
export interface CardComponentChanges {
  title?: string
  text?: string
  items?: ChecklistItem[]
  links?: CardLink[]
  x?: number
  y?: number
  width?: number
  height?: number
}

export interface CardDetail {
  id: string
  /** Its number on the board: 1 for the first card, never reused. */
  number: number
  /** The board's key and the card's number, e.g. "MPCT-415". */
  readableId: string
  boardId: string
  bucketId: string | null
  title: string
  color: CardColor
  tags: string[]
  assignees: Assignee[]
  /** Its content: text, checklists and links on the card's own canvas. */
  components: CardComponent[]
  agentWorking: boolean
  x: number
  y: number
  /** Current display name of whoever created it; null for older cards. */
  createdBy: string | null
  createdById: string | null
  createdAt: string
  updatedAt: string
  /** Where the web app shows it: "/#/boards/{shortId}/cards/{number}". Links to it store this path. */
  path: string
  /** When it was deleted, for cards in the trash; null (or missing) otherwise. */
  deletedAt?: string | null
}

/** The trash: cards deleted in the last 7 days, most recently deleted first, and the images deleted with them. */
export interface TrashContents {
  cards: CardDetail[]
  images: ImageDetail[]
}

/** What emptyTrash did: how many cards were deleted for good. */
export interface EmptyTrashResult {
  purged: number
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
  /** With a move only: exactly these cards move along instead of the bucket's cards (undoing a move). */
  carryCardIds?: string[]
  /** Likewise for images on the board, instead of the images in the bucket. */
  carryImageIds?: string[]
  /** Like carryImageIds, for drawings on the board. */
  carryDrawingIds?: string[]
  collapsed?: boolean
}

/** A PATCH: omitted fields stay unchanged, a given list replaces the whole list. */
export interface CardChanges {
  title?: string
  color?: CardColor
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
  /** Sessions only: an admin of this PrikBoard, who may use the admin* operations. */
  isAdmin: boolean
  /** The picture others see (getAvatar), or null when initials are shown. */
  avatarUrl: string | null
  /** Sessions only: the sign-in provider's picture (GitHub's), which useSignInAvatar takes as avatar; never stored. */
  suggestedAvatarUrl: string | null
  /** People only: they removed their avatar, so sign-ins no longer take the provider's picture. */
  avatarOptedOut: boolean
}

/** A board as an admin sees it: who is on it, never what is on it. */
export interface AdminBoard {
  id: string
  name: string
  createdAt: string
  updatedAt: string
  /** Owners first. */
  members: AdminBoardMember[]
}

export interface AdminBoardMember {
  principalId: string
  kind: PrincipalKind
  displayName: string
  role: BoardRole
  /** People only: their chosen picture (getAvatar), shown instead of initials; null without one. */
  avatarUrl: string | null
}

export interface AdminBoardRef {
  id: string
  name: string
}

/** Someone with an account, as admins see them. */
export interface AdminUser {
  id: string
  displayName: string
  isAdmin: boolean
  isYou: boolean
  createdAt: string
  lastSeenAt: string
  boardCount: number
  /** Boards they are the only owner of; they can't be deleted until each has another owner. */
  soleOwnerOf: AdminBoardRef[]
  /** People only: their chosen picture (getAvatar), shown instead of initials; null without one. */
  avatarUrl: string | null
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
  /** People only: their chosen picture (getAvatar), shown instead of initials; null without one. */
  avatarUrl: string | null
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

/** Group: several selected items moving together (`items`). */
export type DragKind = 'Card' | 'Bucket' | 'Resize' | 'Image' | 'ImageResize' | 'Group'

/** The corner or edge a resize drags; the opposite one stays where it is, and an edge resizes in its direction only. */
export type ResizeHandle = 'BottomRight' | 'BottomLeft' | 'TopRight' | 'TopLeft' | 'Top' | 'Right' | 'Bottom' | 'Left'

/** Someone looking at a board right now (listed once, however many tabs they have open). */
export interface LiveViewer {
  principalId: string
  kind: PrincipalKind
  displayName: string
  /** People only: their chosen picture (getAvatar), shown instead of initials; null without one. */
  avatarUrl: string | null
}

export interface BoardPresence {
  boardId: string
  /** Members looking at the board; none for visitors of a board that hides people. */
  viewers: LiveViewer[]
  /** Visitors (not members) of a public board looking at it; for visitors of a board that hides people, everyone looking. */
  guests: number
}

export interface JoinedBoard {
  boardId: string
  role: BoardRole
  /** This connection's key in drag messages. */
  key: string
  viewers: LiveViewer[]
  guests: number
  /** Joined as a visitor of a public board: changes and presence, no drags. */
  visiting: boolean
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
  /**
   * For Group: every item moving by this distance (cards, buckets, images and drawings), `id` (the one held) among
   * them. What is in a bucket that moves isn't listed: the bucket carries it.
   */
  items?: string[] | null
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
