# FigJam → PrikBoard

Moves a FigJam board to PrikBoard once, one way. Sections (and frames) become buckets, stickies become cards, images come along, and connectors between stickies become links. Nothing goes back to Figma.

It runs in three steps, so you can check the result before anything reaches PrikBoard:

```bash
# 1. Download the file and its images (cached in out/, so step 2 can run again without Figma)
FIGMA_TOKEN=figd_... node tools/figma-import/fetch.mjs --file=https://www.figma.com/board/<key>/...

# 2. Make the PrikBoard clips and out/report.md (the mapping: Figma node → bucket / card)
node tools/figma-import/convert.mjs

# 3. Paste them onto a board
PRIKBOARD_TOKEN=pb_pat_... node tools/figma-import/paste.mjs --board=<short id> --server=https://prikboard.example.com
```

- **Figma token**: Figma → Settings → Security → Personal access tokens, scope *File content: read*.
- **PrikBoard token**:
  1. Make an empty board in the web app.
  2. Under Settings → Access tokens, make an Editor token limited to that board.
  3. Pass the board's short id (from its address, `#/boards/<short id>`) as `--board`.
  4. Revoke the token afterwards.

  `--name="…"` makes a new board instead, but that needs a token that isn't limited to boards.

  If the board holds only its empty starting buckets (To do, Doing, Done), `paste.mjs` deletes them first (`--keep-buckets` keeps them). Otherwise it pastes to the right of what is there.

## What becomes what

| FigJam | PrikBoard |
| --- | --- |
| Section, frame (`framesAsBuckets`) | Bucket at the same place, nested ones on top; a section with hidden contents is collapsed |
| Sticky | Card in its section's bucket, centered where the sticky was. The first line is the title. The full text, if there is more, goes in a Text component. The author goes last (`includeAuthor`) |
| Sticky color | Card fill: yellow/gray → Butter, green/teal → Mint, blue → Sky, red/orange/pink → Coral, violet → Lilac (`colors` changes it) |
| `#hashtag` in a sticky | Tag (and taken out of the text); `colorTags` adds a tag per sticky color |
| Image (any node with an image fill) | Image at its place. If its center is on a sticky, it is attached to that card instead (`attachImagesToCards`) |
| Connector between two stickies | Link on the card it starts from |
| Shapes with text, loose text, stamps, widgets, … | Left out, counted in the report |

Positions are scaled by `scale` (0.85), so 240 px stickies have room as 200×120 cards.

Images PrikBoard can't take (other formats, over 10 MB) are rendered by Figma as JPEG in step 1.

## Settings

Copy `config.example.json` to `config.json` (or pass `--config=`):

| Setting | Meaning |
| --- | --- |
| `page` | Which page to read: its name or index (default: the first) |
| `scale` | How much positions shrink |
| `framesAsBuckets` | Frames become buckets too |
| `attachImagesToCards` | Attach images that sit on stickies |
| `includeAuthor` | Add the sticky's author to the text |
| `titleLength` | Longest card title (80); longer first lines are cut at a word, and the full text stays in the card |
| `colorTags` | Sticky color → tag, e.g. `"red": "bug"`. Color names: yellow, orange, red, pink, violet, blue, teal, green, gray, white, black. The report shows each sticky's color |
| `colors` | Sticky color → fill, to override the defaults |

## Big boards

One paste takes up to 2,000 cards, 200 images, 100 buckets and 150 MB of images. A bigger board is split into `part-1.json`, `part-2.json`, …, and `paste.mjs` pastes them all onto the same board.

- A top-level section and everything in it always stay in one part.
- Each part lands where it was relative to the others.
- A connector between cards in different parts is left out and counted in the report.
- Each part is one paste, so it lands whole or not at all. If one fails, the parts before it are on the board already.

A board has at most 100 tags; convert stops if the hashtags and `colorTags` add up to more.
