# Measuring board performance

Tools for measuring how PrikBoard copes with a big board, so a change can be compared before and after with the same
numbers on the same machine. None of this runs in CI.

| File | What it does |
|---|---|
| `make-board.mjs` | Writes a big board as an export file, to import. |
| `measure.js` | Times the open board in the browser: scrolling, dragging, zooming, reloading, opening. |
| `serve-board.mjs` | A read-only stand-in API serving one board, so the web app can be measured without signing in. |
| `../../src/PrikBoard.Api.Tests/PerfBenchmarks.cs` | Server timings: import, getBoard, placeItemsInBucket, createCards, `Layout.PackIn`. |

Generated files go in `tools/perf/out/`, which git ignores.

## The board

```bash
node tools/perf/make-board.mjs --out=tools/perf/out/perf-board.json
```

The defaults are 1,250 cards, 100 images and 100 drawings (`--cards`, `--images`, `--drawings`; at most 2,000 cards and
200 images, the import's limits). The layout is the same every run:

- six buckets side by side, named `Bucket 1 (225)` to `Bucket 6 (75)`, holding 60% of the cards;
- an empty `Drop here` bucket;
- the other cards loose below them;
- images (2400 × 1600, so they decode at full size) and drawings to the left.

Every card has a note, every fourth a checklist, and up to three tags. `measure.js` looks for the bucket names.

Import it on your own PrikBoard through the board menu's **Import**.

## Server timings

```bash
PRIKBOARD_PERF=1 dotnet test PrikBoard.slnx -c Release --filter "FullyQualifiedName~PerfBenchmarks" --logger "console;verbosity=detailed"
```

This needs the compose database, like the tests, and Node: without `PRIKBOARD_PERF_BOARD` (the path of an export file)
it runs `make-board.mjs` itself. With `PRIKBOARD_PERF_SNAPSHOT` set to a path such as `tools/perf/out/board.json`, it
also saves the board as getBoard returns it there, for `serve-board.mjs`.

## Browser timings

Measure a production build, since the development build of React is several times slower:

```bash
cd src/web && npm run build && npm run preview
```

`npm run preview` serves `dist` on port 4173 and proxies `/api` like the dev server (`PRIKBOARD_API_URL` picks the
API). Open the imported board, make the window the size you want to compare at, paste `measure.js` into the
console, and wait for the table. It takes about a minute: the reload measurement waits for the board's next poll.

Without signing in, serve the board from a snapshot instead:

```bash
node tools/perf/serve-board.mjs --snapshot=tools/perf/out/board.json --export=tools/perf/out/perf-board.json
```

Then run the preview with `PRIKBOARD_API_URL=http://localhost:5099`. The `perf-api` and `perf-web` entries in
`.claude/launch.json` start both. To load `measure.js` without pasting it, copy it into `src/web/dist` and add it with
a script tag; its results are then in `prikPerf.last`.

What it reports, each as the median of 20–30 runs:

| Name | What is timed |
|---|---|
| `scroll` | One scroll event. |
| `drag` | One pointer move while a card is dragged. |
| `group` | One pointer move while about 100 cards selected in `Bucket 1` are dragged over `Drop here`. |
| `bucket` | One pointer move while `Bucket 1` and its 225 cards are dragged. |
| `zoom` | One wheel-zoom step, frame included. It needs a visible tab. |
| `reload` | The board's next poll with nothing changed, from the response to the end of the render. |
| `open` | Opening the board, fetch included. |
| `nodes` | DOM elements on the page and on the canvas. |

`ms` is the work an event causes (the React render and the task it runs in, plus painting when the tab is visible).
`layoutMs` is the layout forced after it.

The script never saves anything:
- drags are cancelled before they drop;
- while it runs, requests other than GET are answered in the page without reaching the server.

Synthetic pointer events can't capture the pointer, so it stubs `setPointerCapture` while it runs.

Numbers from a hidden tab (such as an agent's browser pane in the background) leave out painting and skip `zoom`. In
a hidden tab the board doesn't poll, so the `reload` measurement needs `document.visibilityState` overridden to
`'visible'` first. Compare like with like.

For painting and frame rates, record a trace in the browser's Performance panel while zooming out to the whole board
and while dragging a group of cards.
