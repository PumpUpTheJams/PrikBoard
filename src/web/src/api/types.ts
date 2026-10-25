// Mirrors the API contracts in src/PrikBoard.Api/Contracts (see /openapi/v1.json).

export const cardColors = ['Butter', 'Mint', 'Sky', 'Coral', 'Lilac'] as const
export type CardColor = (typeof cardColors)[number]

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
  /** Whether members looking at the board see each other's pointers and selections live. Visitors never do. */
  showCursors: boolean
}

export interface BoardDetail extends BoardSummary {
  /** You see it because it is public, not as a member: read only, and without people when the board hides them. */
  visiting: boolean
  /** Agent rules in Markdown, written by the owners; null when none are set. */
  rules: string | null
  rulesUpdatedAt: string | null
  /** In creation order (To do, Doing and Done first); stackedAt says how they stack where they overlap. */
  buckets: BucketDetail[]
  /** Bucket by bucket, loose cards last. Not the drawing order: cards edited later are drawn on top (updatedAt). */
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

/** The largest image file the API accepts (ImageFormats.MaxBytes). */
export const MAX_IMAGE_BYTES = 10 * 1024 * 1024

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
  bucketId?: string
  loose?: boolean
}

/**
 * Which bucket an item dropped at x and y goes into, by the pointer (the web app's drops): the bucket named, which it
 * must overlap, or none with `loose`. Without either it joins the topmost bucket containing its center.
 */
export interface DropBucket {
  bucketId?: string
  loose?: boolean
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

/**
 * A freehand drawing: one or more pen strokes that select, move and delete as one (everything drawn between picking up
 * the pen and putting it down). Its strokes come from contentUrl (getDrawingContent), which changes whenever they do.
 */
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
  /** The ink of each stroke, in drawing order. */
  colors: InkColor[]
  pointCount: number
  addedBy: string
  addedById: string | null
  contentUrl: string
  createdAt: string
  updatedAt: string
  /**
   * Loose (no bucket, no card): its place in the stack it shares with the buckets, the same for everyone (ISO time, the
   * later the higher). Drawing, moving or reshaping it puts it on top.
   */
  stackedAt: string
}

/** One stroke of a drawing: its base width and its points, measured from the drawing's x/y. */
export interface DrawingStroke {
  strokeWidth: number
  points: StrokePoint[]
}

export interface DrawingContent {
  drawingId: string
  strokes: DrawingStroke[]
}

/** A new drawing of one stroke: x and y on the grid, points measured from there. */
export interface NewDrawing {
  x: number
  y: number
  points: StrokePoint[]
  color?: InkColor
  strokeWidth?: number
  cardId?: string
}

/** A stroke to add to a drawing: its own x and y on the drawing's canvas (on the grid), points measured from there. */
export interface NewDrawingStroke {
  x: number
  y: number
  points: StrokePoint[]
  color?: InkColor
  strokeWidth?: number
}

/**
 * Scales a drawing (scaleX, scaleY), then turns it clockwise (rotation, degrees), both about a pivot on its canvas
 * (default: the middle of its box). Its points change; line widths stay.
 */
export interface DrawingTransform {
  scaleX?: number
  scaleY?: number
  rotation?: number
  /** Give both or neither. */
  pivotX?: number
  pivotY?: number
}

/** New points for every stroke of a drawing (as many as it has, inks kept), measured from its new x/y on the grid. */
export interface DrawingStrokesChange {
  x: number
  y: number
  strokes: DrawingStroke[]
}

/** A PATCH of a drawing. x and y are on the canvas it ends up on (give both when attaching or detaching). */
export interface DrawingChanges {
  x?: number
  y?: number
  /** One ink for every stroke. */
  color?: InkColor
  /** An ink for each stroke, as many as it has. */
  colors?: InkColor[]
  cardId?: string
  detach?: boolean
  bucketId?: string
  loose?: boolean
}

export interface BucketDetail {
  id: string
  boardId: string
  name: string
  x: number
  y: number
  width: number
  height: number
  /** Folded into a square in its top-right corner: its cards and images are hidden (keeping their places) until it opens again. */
  collapsed: boolean
  /**
   * Its place in the stack, the same for everyone (ISO time, the later the higher): the topmost is drawn above the
   * buckets it overlaps, and an item put there joins it. Moving, resizing, opening or filling a bucket puts it on top.
   */
  stackedAt: string
}

/** The side a bucket grows towards when items put into it don't fit; Left and Up move its edge out. */
export type GrowSide = 'Down' | 'Right' | 'Left' | 'Up'

/**
 * placeItemsInBucket: what to put into the bucket's free room (cards first, then images, then drawings, each in order)
 * and where it grows when full (default Down). Images and drawings must be on the board itself.
 */
export interface PlaceItemsRequest {
  cardIds?: string[]
  imageIds?: string[]
  drawingIds?: string[]
  grow?: GrowSide
  /** Only cards count as in the way: images and drawings already there don't (the web app's drops). */
  onlyCardsTakeRoom?: boolean
}

/** The bucket (possibly grown) and the items put into it, in the order given. */
export interface PlacedItems {
  bucket: BucketDetail
  cards: CardDetail[]
  images: ImageDetail[]
  drawings: DrawingDetail[]
}

/**
 * Items copied with copyItems, with everything on them, to paste with pasteItems on any board, on this server or
 * another. Clients pass it on unchanged; the web app only reads the fields typed here (for the clipboard's text and
 * where pasted items land).
 */
export interface BoardClip {
  format: 'prikboard-clip'
  version: number
  copiedAt: string
  source: ClipSource
  cards: ClippedCard[]
  images: ClippedImage[]
  drawings: ClippedDrawing[]
  tagColors?: Record<string, TagColor> | null
  appVersion?: string | null
  /** Buckets with what is in them (from version 2). */
  buckets?: ClippedBucket[] | null
  /** Cut rather than copied (from version 3): pasted on another board of the same server, the originals go to the trash. */
  cut?: boolean
}

export interface ClipSource {
  /** The server the items were copied on; card links in the clip are paths on it. */
  url: string
  boardShortId: string
  boardKey: string
}

/** A card in a clip (the rest of its fields are passed on untouched). */
export interface ClippedCard {
  id: string
  number: number
  title: string
  x: number
  y: number
  /** The copied bucket it is in. */
  bucketId?: string | null
  /** Who created it (from version 6): copies pasted on the same board keep them. */
  createdById?: string | null
}

/** A bucket in a clip (the rest of its fields are passed on untouched). */
export interface ClippedBucket {
  id: string
  name: string
  x: number
  y: number
  width: number
  height: number
  collapsed?: boolean
}

/** An image or drawing in a clip: on a card in it, or (null) on the board. */
export interface ClippedItem {
  /** Its id on its board (from version 3). */
  id?: string | null
  cardId: string | null
  x: number
  y: number
  /** On the board: the copied bucket it is in. */
  bucketId?: string | null
}

/** An image in a clip. */
export interface ClippedImage extends ClippedItem {
  fileName: string
  width: number
  /** Display height (from version 6). */
  height?: number | null
  /** The file as base64; empty when copyItems left it out (imageData false). */
  data: string
}

/** A drawing in a clip: its strokes' points are relative to its x and y. */
export interface ClippedDrawing extends ClippedItem {
  strokes?: { strokeWidth: number; points: number[][] }[] | null
}

/** What copyItems copies, all on one board. A bucket comes with what is in it. */
export interface CopyItemsRequest {
  cardIds?: string[]
  bucketIds?: string[]
  imageIds?: string[]
  drawingIds?: string[]
  /** Cut them (Editor): pasted on another board of the same server, the originals go to the trash. */
  cut?: boolean
  /** False leaves the images' data out of the clip (get each from getImageContent), which allows more of them. */
  imageData?: boolean
}

/** An image uploaded for a paste (uploadPasteImage): its token goes in pasteItems' imageFiles, at the image's index. */
export interface PasteImageFile {
  token: string
  contentType: string
  size: number
  expiresAt: string
}

/** Items of one board by id, for deleteItems and restoreItems. */
export interface ItemIds {
  cardIds?: string[]
  bucketIds?: string[]
  imageIds?: string[]
  drawingIds?: string[]
}

/** What restoreItems brought back. */
export interface RestoredItems {
  cards: CardDetail[]
  buckets: BucketDetail[]
  images: ImageDetail[]
  drawings: DrawingDetail[]
}

/**
 * Where pasted items go: their top-left at x and y (the rest keep their places around it); a clip of cards alone into a
 * bucket; or, with neither, cards into the board's first bucket and anything else to a free spot.
 */
export type PastePlace = Point | { bucketId: string } | Record<string, never>

export interface PastedItems {
  cards: CardDetail[]
  /** On the new cards, or on the board. */
  images: ImageDetail[]
  /** On the new cards, or on the board. */
  drawings: DrawingDetail[]
  /** The bucket the cards went into, when one was asked for. */
  bucket: BucketDetail | null
  /** The new buckets, back to front. */
  buckets: BucketDetail[]
  /** For a cut from another board of this server: the originals, now in that board's trash, as they were. */
  moved?: MovedOriginals | null
}

/** The originals of a cut, deleted by the paste (the restore endpoints bring them back). */
export interface MovedOriginals {
  boardId: string
  boardName: string
  cards: CardDetail[]
  buckets: BucketDetail[]
  /** The images that were on the board (those on the cards went with them). */
  images: ImageDetail[]
  drawings: DrawingDetail[]
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
  /** Added to the end of a Text component, as a new paragraph. */
  appendText?: string
  /** Added to the end of a Checklist component. */
  addItems?: ChecklistItem[]
  /** Texts of checklist items to tick off (whole text, ignoring case). */
  checkItems?: string[]
  /** Texts of checklist items to untick. */
  uncheckItems?: string[]
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
  /** While agentWorking: the assignee the agent works for (the person whose access token it uses), whose avatar shows it; null when not known. */
  agentWorkingFor?: string | null
}

export type CardChangeKind =
  | 'Created'
  | 'Title'
  | 'Color'
  | 'Tags'
  | 'Assignees'
  | 'AgentWorking'
  | 'Bucket'
  | 'ComponentAdded'
  | 'ComponentChanged'
  | 'ComponentRemoved'
  | 'ImageAdded'
  | 'ImageRemoved'
  | 'DrawingAdded'
  | 'DrawingRemoved'
  | 'Deleted'
  | 'Restored'
  /** Added as a copy: before is the original's readable id, after its link (a full URL when it was on another server). */
  | 'Copied'
  /** Added as the new place of a card cut from another board: before is the original's readable id, after its link. */
  | 'Moved'
  /** Cut and pasted on another board (the card went to the trash): before is its new readable id, after its link. */
  | 'MovedAway'
  /** A change this version can't describe; also how to treat kinds added later. */
  | 'Other'

/** One thing that changed; which fields are set depends on the kind. */
export interface CardChangeEntry {
  kind: CardChangeKind
  /** Old title or color, the bucket it left (null: loose), a component's kind, or a removed image's caption or file name. */
  before: string | null
  /** New title or color, the bucket it went into (null: loose), a component's title, an added image's name, or "true"/"false". */
  after: string | null
  /** Tags added, or the names of people assigned. */
  added: string[] | null
  /** Tags removed, or the names of people unassigned. */
  removed: string[] | null
}

/** One change to a card (quick edits by the same person merged), from listCardHistory. */
export interface CardChange {
  id: string
  at: string
  /** Who made it, with their current name; null once they no longer exist. */
  by: Assignee | null
  entries: CardChangeEntry[]
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

/** Whether this PrikBoard still needs setting up (getSetupStatus). */
export interface SetupStatus {
  /** Nobody has an account yet: the setup makes the first admin. */
  needed: boolean
  /** The server runs with Setup__Recovery: the setup key opens the sign-in providers again. */
  recovery: boolean
  /** This browser entered the current setup key. */
  unlocked: boolean
}

export type ProviderType = 'OpenIdConnect' | 'GitHub' | 'Discord' | 'Steam' | 'Slack'

/** A sign-in provider as the setup shows it; the client secret never comes back. */
export interface SignInProviderDetail {
  /** In the login and callback URLs (/api/auth/callback/{id}); never changes. */
  id: string
  type: ProviderType
  displayName: string
  authority: string | null
  clientId: string
  hasSecret: boolean
  scopes: string[]
  emailsAreVerified: boolean
  /** Offered first on the sign-in page. */
  isDefault: boolean
  /** Configuration providers are read-only here. */
  source: 'Configuration' | 'Setup'
  /** The GitHub App the setup created for it. */
  gitHubAppUrl: string | null
  /** How many people's sign-ins use it. */
  signIns: number
}

export interface CreateSignInProvider {
  id: string
  type: ProviderType
  displayName?: string
  authority?: string
  /** Not for Steam. */
  clientId?: string
  /** For Steam: an optional Steam Web API key. */
  clientSecret?: string
  emailsAreVerified?: boolean
  isDefault?: boolean
}

/** Fields left out stay as they are; an empty displayName or clientSecret clears it. */
export interface SignInProviderChanges {
  displayName?: string
  authority?: string
  clientId?: string
  clientSecret?: string
  emailsAreVerified?: boolean
  isDefault?: boolean
}

/** A form to post to postUrl with one field, manifest. */
export interface GitHubAppManifest {
  postUrl: string
  manifest: string
}

export interface OpenIdAuthority {
  issuer: string
}

export type CheckStatus = 'Ok' | 'Warning' | 'Failed'

export interface SystemCheck {
  id: string
  title: string
  status: CheckStatus
  message: string
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
export type DragKind = 'Card' | 'Bucket' | 'Resize' | 'Image' | 'ImageResize' | 'Group' | 'Drawing'

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
  /** Joined as a visitor of a public board: changes and presence, no drags or pointers. */
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

/** Your pointer on the open board, in canvas pixels (MoveCursor). */
export interface CursorUpdate {
  x: number
  y: number
}

/** Another member's pointer on the open board; their name and picture are in the presence list. */
export interface RemoteCursor extends CursorUpdate {
  /** Their connection; CursorHidden names the same key. */
  key: string
  principalId: string
}

/** What another member has selected on the open board (Selected); no items when nothing is, or they left. */
export interface RemoteSelection {
  /** Their connection, as in RemoteCursor. */
  key: string
  principalId: string
  /** Cards, buckets, images and drawings. */
  items: string[]
}

/** Agent: its own agent with capped access. App: a PrikBoard app (the VS Code extension) signed in as you. */
export type AccessTokenKind = 'Agent' | 'App'

/** A personal access token, without the token itself (only shown when created). */
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

/** A text channel the Discord bot may read and post in. */
export interface DiscordChannelChoice {
  id: string
  name: string
}

/** Board activity a chat channel (Discord or Slack) can follow. */
export type ChatUpdate = 'NewCards' | 'Moves' | 'Assignments' | 'AgentWork'

/** A Discord channel connected to a board, with what it does there. */
export interface DiscordChannel {
  id: string
  name: string
  /** What the bot posts there as it happens on the board. */
  updates: ChatUpdate[]
  /** Where Save as card in this channel puts cards; null for the board's first bucket. */
  saveBucketId: string | null
}

/** Off: no admin set up a Discord bot. Failed: Discord refused it (problem says why). */
export type DiscordBotState = 'Off' | 'Connecting' | 'Online' | 'Failed'

/** A Discord server the bot is in, with the text channels where it may read and post. */
export interface DiscordServerChoice {
  guildId: string
  name: string
  channels: DiscordChannelChoice[]
}

/** Where the instance's Discord bot can preview a board's links. */
export interface DiscordServers {
  state: DiscordBotState
  servers: DiscordServerChoice[]
}

/** A Discord server where the bot previews a board's links. */
export interface DiscordConnection {
  guildId: string
  guildName: string
  /** The server's member on the board (a Viewer agent). */
  agentId: string
  /** Whether links preview in every channel of the server. */
  wholeServer: boolean
  /** The channels picked for the board: links preview there, and each can post updates and take Save as card. */
  channels: DiscordChannel[]
  connectedAt: string
  connectedBy: string | null
}

/** A board a Discord server previews, with where. */
export interface DiscordBoardConnection {
  boardId: string
  shortId: string
  boardName: string
  wholeServer: boolean
  channels: DiscordChannel[]
}

/** Whether the Discord bot knows who you are, for Save as card: through a Discord sign-in, or a link you made. */
export interface DiscordAccountStatus {
  linked: boolean
  source: 'SignIn' | 'Link' | null
  linkedAt: string | null
}

/** The Discord user behind a link code from the bot, so you can check it is you. */
export interface DiscordAccountLinkPreview {
  discordName: string
  expiresAt: string
}

/** A server the Discord bot is in, for admins. */
export interface DiscordBotServer {
  guildId: string
  name: string
  /** Channels where the bot may read and post. */
  channelCount: number
  boards: DiscordBoardConnection[]
}

/** The instance's Discord bot, for admins; never its token. */
export interface DiscordBotDetail {
  state: DiscordBotState
  problem: string | null
  /** Where the token comes from: Configuration (Discord:BotToken, read-only here) or Setup; null without one. */
  source: 'Configuration' | 'Setup' | null
  botName: string | null
  applicationId: string | null
  inviteUrl: string | null
  /** Whether anyone may add the bot to their server; known once it is online. */
  isPublic: boolean | null
  /** The PrikBoard address pasted links are matched against. */
  publicUrl: string | null
  /** Whether that comes from Discord:PublicUrl in the server's configuration (read-only here). */
  publicUrlIsConfigured: boolean
  updatedAt: string | null
  servers: DiscordBotServer[]
}

/** Off: no admin set up the Slack app. Failed: Slack refused a token (problem says why). */
export type SlackAppState = 'Off' | 'Connecting' | 'Online' | 'Failed'

/** A Slack channel connected to a board. */
export interface SlackChannel {
  id: string
  name: string
  /** What the app posts there as it happens on the board. */
  updates: ChatUpdate[]
  /** Where Save as card in this channel puts cards; null for the board's first bucket. */
  saveBucketId: string | null
}

/** A Slack workspace where the app unfurls a board's links. */
export interface SlackConnection {
  teamId: string
  teamName: string
  /** The workspace's member on the board (a Viewer agent). */
  agentId: string
  wholeWorkspace: boolean
  channels: SlackChannel[]
  connectedAt: string
  connectedBy: string | null
}

/** The workspace the Slack app is in, with the channels it was added to (not those shared with other organizations). */
export interface SlackWorkspaceChoice {
  teamId: string
  name: string
  channels: { id: string; name: string }[]
}

/** Where the instance's Slack app can preview a board's links. */
export interface SlackWorkspaces {
  state: SlackAppState
  workspaces: SlackWorkspaceChoice[]
}

/** A board a Slack workspace previews, with where. */
export interface SlackBoardConnection {
  boardId: string
  shortId: string
  boardName: string
  wholeWorkspace: boolean
  channels: SlackChannel[]
}

/** The workspace the Slack app is in, for admins. */
export interface SlackAppWorkspace {
  teamId: string
  name: string
  channelCount: number
  boards: SlackBoardConnection[]
}

/** The instance's Slack app, for admins; never its tokens. */
export interface SlackAppDetail {
  state: SlackAppState
  problem: string | null
  /** Where the tokens come from: Configuration (Slack:BotToken and Slack:AppToken, read-only here) or Setup; null without them. */
  source: 'Configuration' | 'Setup' | null
  teamId: string | null
  teamName: string | null
  botName: string | null
  /** The PrikBoard address pasted links are matched against; its host is the app's unfurl domain. */
  publicUrl: string | null
  /** Whether that comes from Slack:PublicUrl in the server's configuration (read-only here). */
  publicUrlIsConfigured: boolean
  updatedAt: string | null
  /** The app manifest (JSON) to create the Slack app from. */
  manifest: string
  /** Slack's page for creating an app, with the manifest filled in. */
  createAppUrl: string
  workspaces: SlackAppWorkspace[]
}

/** Whether the Slack app knows who you are, for Save as card: through a Slack sign-in, or a link you made. */
export interface SlackAccountStatus {
  linked: boolean
  source: 'SignIn' | 'Link' | null
  linkedAt: string | null
}

/** The Slack user behind a link code from the app, so you can check it is you. */
export interface SlackAccountLinkPreview {
  slackName: string
  expiresAt: string
}
