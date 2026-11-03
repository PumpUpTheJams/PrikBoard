# FigJam and Miro → PrikBoard

Moves a FigJam or Miro board to PrikBoard once, one way. Sections and frames become buckets, sticky notes become cards, images and drawings come along, and connectors between cards become links. Nothing goes back.

Each source runs in three steps, so you can check the result before anything reaches PrikBoard:

```bash
# FigJam
FIGMA_TOKEN=figd_... node tools/board-import/figma/fetch.mjs --file=https://www.figma.com/board/<key>/...
node tools/board-import/figma/convert.mjs
PRIKBOARD_TOKEN=pb_pat_... node tools/board-import/paste.mjs --from=figma --board=<short id> --server=https://prikboard.example.com

# Miro
MIRO_TOKEN=... node tools/board-import/miro/fetch.mjs --board=https://miro.com/app/board/<id>=/
node tools/board-import/miro/convert.mjs
PRIKBOARD_TOKEN=pb_pat_... node tools/board-import/paste.mjs --from=miro --board=<short id> --server=https://prikboard.example.com
```

In PowerShell, set a token for the open window with `$env:FIGMA_TOKEN = "figd_..."` and run the command without the prefix.

1. **fetch** downloads the board and its images into `out/figma` or `out/miro` (gitignored), so step 2 can run again without the source.
2. **convert** writes the PrikBoard clips (`part-1.json`, …) and `report.md`: which source item became which bucket, card, image or drawing, and what was left out.
3. **paste** puts the parts on a board.

## Tokens

- **Figma**: Figma → Settings → Security → Personal access tokens, scope *File content: read*.
- **Miro**: Miro → Profile → Settings → Your apps → *Create new app*. Give it the `boards:read` scope, install it to your team, and copy the access token it shows.
- **PrikBoard**:
  1. Make an empty board in the web app.
  2. Under Settings → Access tokens, make an Editor token limited to that board.
  3. Pass the board's short id (from its address, `#/boards/<short id>`) as `--board`.
  4. Revoke the token afterwards, and the others too.

  `--name="…"` makes a new board instead, but that needs a token that isn't limited to boards.

  If the board holds only its empty starting buckets (To do, Doing, Done), `paste.mjs` deletes them first (`--keep-buckets` keeps them). Otherwise it pastes to the right of what is there.

## What becomes what

| FigJam | Miro | PrikBoard |
| --- | --- | --- |
| Section, frame | Frame | Bucket at the same place, nested ones on top |
| Sticky | Sticky note | Card in its bucket, centered where it was. The first line is the title, and the full text goes in a Text component |
| — | Card | Card: title and description, plus the due date and assignee's name as text (PrikBoard's people are its own) |
| Sticky color | Sticky note color, card color | Card fill (`colors` changes it) |
| `#hashtag` | Tag (with its color), `#hashtag` | Tag; `colorTags` adds a tag per color |
| Image | Image | Image at its place, or attached to the card it sits on (`attachImagesToCards`) |
| Pen stroke | Pen stroke (if Miro's API hands them out, see below) | Drawing; strokes grouped together stay one drawing |
| Connector between two stickies | Connector between two cards | Link on the card it starts from |
| Shapes, text, tables, … | Shapes, text, app cards, documents, embeds, … | Left out, counted in the report |

- **Scale**: positions shrink by `scale`.
  - FigJam: 0.85, so its 240 px stickies have room as 200×120 cards.
  - Miro: `"auto"`. Miro sticky notes come in every size, so the board shrinks until a typical sticky note's area becomes a card's. The buckets then look about as full as they did in Miro.
- **Tidy** (on for Miro): cards go to the nearest free spot of a card-sized grid near where they were, so none overlap and none covers a loose image. A bucket grows if its cards need more room.
- **Rectangles as buckets** (Miro, `shapesAsBuckets`): boards without frames often mark areas with see-through rectangles. Each one becomes a bucket, named after the topmost text inside it.
- **Images**: images PrikBoard can't take (other formats, over 10 MB) are rendered by Figma as JPEG, or taken from Miro's preview.
- **Ink colors**: drawing inks follow the stroke's color. Dark strokes get the light ink of the same color (black → Chalk), so they show on the dark board. `inks` picks others, e.g. `"black": "Ink"` for the light color scheme.
- **Pen strokes as pictures**: when Figma gives only a stroke's outline and not its line, the stroke comes over as a picture instead.

## Settings

Copy `config.example.json` to `config.json` (or pass `--config=`). The settings at the top apply to both sources; those under `figma` and `miro` apply to one.

| Setting | Meaning |
| --- | --- |
| `titleLength` | Longest card title (80); longer first lines are cut at a word, and the full text stays in the card |
| `attachImagesToCards` | Attach images that sit on a sticky or card |
| `inks` | Color name → drawing ink (Chalk, Butter, Mint, Sky, Coral, Lilac, Ink, Ochre, Pine, Ocean, Brick, Violet) |
| `scale` | How much positions shrink: a number, or `"auto"` (Miro) |
| `tidy` | Put cards on a grid so none overlap, growing buckets to fit (Miro: on, FigJam: off) |
| `shapesAsBuckets` (Miro) | See-through rectangles become buckets too |
| `includeAuthor` | Add who made the sticky to its text |
| `colorTags` | Color → tag. FigJam color names: yellow, orange, red, pink, violet, blue, teal, green, gray, white, black. Miro sticky colors: light_yellow, yellow, orange, light_green, green, dark_green, cyan, light_pink, pink, violet, red, light_blue, blue, dark_blue, gray, black. The report shows each card's color |
| `colors` | Color → fill (Butter, Mint, Sky, Coral, Lilac), to override the defaults |
| `page` (FigJam) | Which page to read: its name or index (default: the first) |
| `framesAsBuckets` (FigJam) | Frames become buckets too, not only sections |

## Big boards

One paste takes up to 2,000 cards, 200 images, 100 buckets, 5,000 drawings and 150 MB of images. A bigger board is split into `part-1.json`, `part-2.json`, …, and `paste.mjs` pastes them all onto the same board.

- A top-level bucket and everything in it always stay in one part.
- Each part lands where it was relative to the others.
- A connector between cards in different parts is left out and counted in the report.
- Each part is one paste, so it lands whole or not at all. If one fails, the parts before it are on the board already.

A board has at most 100 tags; convert stops if there are more.
