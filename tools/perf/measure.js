// Measures how fast the open board responds (see README.md). Paste it into the browser console on a board made by
// make-board.mjs, opened in a production build (npm run build && npm run preview), and wait for the table. It sends
// synthetic events and times the work each one causes: React rendering (and the task it runs in) and the layout that
// follows, as the median of several runs. It changes nothing: drags are cancelled before they drop, and while it runs,
// every request other than GET is answered locally without reaching the server.
//
// prikPerf.run() measures everything again; prikPerf.scroll(), .drag(), .group(), .bucket(), .zoom(), .reload(),
// .open() and .nodes() measure one thing.
;(() => {
  const flush = () =>
    new Promise((resolve) => {
      const channel = new MessageChannel()
      channel.port1.onmessage = () => resolve()
      channel.port2.postMessage(0)
    })
  const median = (list) => {
    const sorted = [...list].sort((a, b) => a - b)
    return Math.round(sorted[sorted.length >> 1] * 10) / 10
  }
  const scroller = () => document.querySelector('.board-scroll')
  const canvas = () => document.querySelector('.canvas')

  /** Runs `step` `runs` times, timing the task(s) it causes and then a forced layout. */
  async function time(step, runs = 20) {
    const work = []
    const layout = []
    for (let i = 0; i < runs; i++) {
      const start = performance.now()
      step(i)
      // React renders in a task of its own, queued after the event: two round trips get past it.
      await flush()
      await flush()
      const rendered = performance.now()
      void canvas().offsetHeight
      work.push(rendered - start)
      layout.push(performance.now() - rendered)
    }
    return { ms: median(work), layoutMs: median(layout) }
  }

  const pointer = (type, x, y, extra = {}) =>
    new PointerEvent(type, {
      bubbles: true,
      cancelable: true,
      composed: true,
      pointerId: 1,
      pointerType: 'mouse',
      isPrimary: true,
      button: 0,
      buttons: type === 'pointerup' ? 0 : 1,
      clientX: x,
      clientY: y,
      ...extra,
    })

  /** While measuring: synthetic pointers can't be captured, and nothing but GET reaches the server. */
  async function guarded(fn) {
    const capture = Element.prototype.setPointerCapture
    const release = Element.prototype.releasePointerCapture
    const fetch = window.fetch
    let blocked = 0
    Element.prototype.setPointerCapture = () => {}
    Element.prototype.releasePointerCapture = () => {}
    window.fetch = (input, init) => {
      const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase()
      if (method === 'GET' || method === 'HEAD') return fetch(input, init)
      blocked++
      return Promise.resolve(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }))
    }
    try {
      return await fn()
    } finally {
      Element.prototype.setPointerCapture = capture
      Element.prototype.releasePointerCapture = release
      window.fetch = fetch
      if (blocked) console.info(`prikPerf: answered ${blocked} write request(s) locally; nothing was saved.`)
    }
  }

  const onScreen = (el) => {
    const r = el.getBoundingClientRect()
    const view = scroller().getBoundingClientRect()
    return r.left > view.left + 80 && r.top > view.top + 60 && r.right < view.right - 260 && r.bottom < view.bottom - 160
  }
  const bucketNamed = (prefix) => [...document.querySelectorAll('section.bucket')].find((b) => b.getAttribute('aria-label')?.startsWith(prefix))
  const cardsIn = (bucket) => {
    const r = bucket.getBoundingClientRect()
    return [...document.querySelectorAll('.card')].filter((c) => {
      const cr = c.getBoundingClientRect()
      return cr.left >= r.left && cr.right <= r.right && cr.top >= r.top && cr.bottom <= r.bottom
    })
  }
  /** Scrolls so `el`'s top-left corner is near the top-left of the board's viewport. */
  async function bringIntoView(el) {
    const s = scroller()
    const r = el.getBoundingClientRect()
    const view = s.getBoundingClientRect()
    s.scrollLeft += r.left - view.left - 120
    s.scrollTop += r.top - view.top - 80
    s.dispatchEvent(new Event('scroll'))
    await flush()
    await flush()
  }
  const cancel = async (x, y) => {
    canvas().dispatchEvent(pointer('pointercancel', x, y))
    await flush()
    await flush()
  }

  /** One scroll event (a pixel down). */
  async function scroll() {
    const s = scroller()
    return time(() => {
      s.scrollTop += 1
      s.dispatchEvent(new Event('scroll'))
    })
  }

  /** One pointer move while dragging a card on its own. */
  const drag = () =>
    guarded(async () => {
      const home = bucketNamed('Bucket 1')
      if (home) await bringIntoView(home)
      const card = [...document.querySelectorAll('.card')].find(onScreen)
      if (!card) return { skipped: 'no card on screen' }
      const r = card.getBoundingClientRect()
      let x = r.left + 30
      const y = r.top + r.height / 2
      card.querySelector('.card-foot').dispatchEvent(pointer('pointerdown', x, y))
      await flush()
      const result = await time(() => {
        x += 3
        canvas().dispatchEvent(pointer('pointermove', x, y))
      }, 30)
      await cancel(x, y)
      return result
    })

  /**
   * One pointer move while about 100 cards selected in the biggest bucket are dragged over the empty "Drop here"
   * bucket (where the app works out their places in it on every move).
   */
  const group = () =>
    guarded(async () => {
      const from = bucketNamed('Bucket 1')
      const into = bucketNamed('Drop here')
      if (!from || !into) return { skipped: 'not a make-board.mjs board' }
      // Let go of what an earlier measurement selected, so the selection rectangle starts afresh.
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
      await new Promise((resolve) => setTimeout(resolve, 200))
      await bringIntoView(from)
      const cards = cardsIn(from).sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top || a.getBoundingClientRect().left - b.getBoundingClientRect().left)
      const first = cards[0].getBoundingClientRect()
      // A selection rectangle from the gap after the first column, across the other three and 34 rows down.
      const start = { x: first.right + 10, y: first.top + 10 }
      document.elementFromPoint(start.x, start.y)?.dispatchEvent(pointer('pointerdown', start.x, start.y))
      await flush()
      const end = { x: from.getBoundingClientRect().right - 30, y: first.top + 34 * (first.height + 20) }
      for (let i = 1; i <= 10; i++) {
        canvas().dispatchEvent(pointer('pointermove', start.x + ((end.x - start.x) * i) / 10, start.y + ((end.y - start.y) * i) / 10))
        await flush()
      }
      canvas().dispatchEvent(pointer('pointerup', end.x, end.y))
      await flush()
      await flush()
      const selected = document.querySelectorAll('.card.selected')
      const held = selected[0]
      if (selected.length < 2) return { skipped: `selected ${selected.length} card(s)` }
      const h = held.getBoundingClientRect()
      let x = h.left + 30
      let y = h.top + h.height / 2
      held.querySelector('.card-foot').dispatchEvent(pointer('pointerdown', x, y))
      await flush()
      // Over the middle of the drop bucket, then small moves there.
      const target = into.getBoundingClientRect()
      x = target.left + target.width / 2
      y = target.top + 200
      canvas().dispatchEvent(pointer('pointermove', x, y))
      await flush()
      await flush()
      const result = await time((i) => {
        x += i % 2 ? 3 : -3
        y += 1
        canvas().dispatchEvent(pointer('pointermove', x, y))
      })
      await cancel(x, y)
      return { ...result, cards: selected.length }
    })

  /** One pointer move while dragging the biggest bucket (it carries its cards). */
  const bucket = () =>
    guarded(async () => {
      const b = bucketNamed('Bucket 1')
      if (!b) return { skipped: 'not a make-board.mjs board' }
      await bringIntoView(b)
      const name = b.querySelector('.bucket-name')
      const r = name.getBoundingClientRect()
      let x = r.left + 5
      const y = r.top + r.height / 2
      name.dispatchEvent(pointer('pointerdown', x, y))
      await flush()
      const result = await time(() => {
        x += 3
        canvas().dispatchEvent(pointer('pointermove', x, y))
      })
      await cancel(x, y)
      return { ...result, cards: cardsIn(b).length }
    })

  /** One wheel-zoom step (applied on the next animation frame, so it needs a visible tab). */
  async function zoom() {
    const s = scroller()
    const view = s.getBoundingClientRect()
    const frame = () => new Promise((resolve) => requestAnimationFrame(() => resolve(true)))
    const ticks = await Promise.race([frame(), new Promise((resolve) => setTimeout(() => resolve(false), 500))])
    if (!ticks) return { skipped: 'the tab is hidden: no animation frames' }
    const work = []
    for (let i = 0; i < 20; i++) {
      const start = performance.now()
      s.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: i % 2 ? 40 : -40, clientX: view.left + view.width / 2, clientY: view.top + view.height / 2 }))
      await frame()
      await flush()
      await flush()
      work.push(performance.now() - start)
    }
    return { ms: median(work), note: 'includes waiting for the frame' }
  }

  /** The board reloaded without changes (the next poll; up to 35 s): from its response to the render being done, parsing included. */
  async function reload() {
    const text = Response.prototype.text
    const times = []
    let done
    const finished = new Promise((resolve) => (done = resolve))
    Response.prototype.text = function () {
      const isBoard = /\/api\/boards\/[^/]+$/.test(new URL(this.url).pathname)
      const parsed = text.call(this)
      if (isBoard) {
        parsed.then(async () => {
          const start = performance.now()
          await flush()
          await flush()
          times.push(performance.now() - start)
          done()
        })
      }
      return parsed
    }
    try {
      const timeout = new Promise((resolve) => setTimeout(resolve, 35_000))
      await Promise.race([finished, timeout])
    } finally {
      Response.prototype.text = text
    }
    return times.length ? { ms: median(times) } : { skipped: 'no reload within 35 s' }
  }

  /**
   * Opening the board: leaves it for an address that isn't a board, comes back, and waits for its buckets (which render
   * with its cards, whether the board puts all of them on the page or only those near the screen).
   */
  async function open() {
    const hash = location.hash
    const count = () => document.querySelectorAll('.canvas section.bucket, .canvas [data-fold]').length
    const expected = count()
    location.hash = '#/boards/-'
    for (let i = 0; i < 200 && count() > 0; i++) await new Promise((resolve) => setTimeout(resolve, 20))
    await new Promise((resolve) => setTimeout(resolve, 300))
    const start = performance.now()
    location.hash = hash
    while (count() < expected && performance.now() - start < 20_000) await flush()
    void canvas().offsetHeight
    return { ms: Math.round(performance.now() - start), cards: document.querySelectorAll('.card').length, note: 'includes fetching the board' }
  }

  const nodes = () => ({
    all: document.querySelectorAll('*').length,
    canvas: document.querySelectorAll('.canvas *').length,
    cards: document.querySelectorAll('.canvas .card').length,
  })

  async function run({ withOpen = true, withReload = true } = {}) {
    const results = {}
    const note = (name, value) => {
      results[name] = value
      console.info(`prikPerf: ${name}`, value)
    }
    note('nodes', nodes())
    note('scroll', await scroll())
    note('drag', await drag())
    note('group', await group())
    note('bucket', await bucket())
    note('zoom', await zoom())
    if (withReload) note('reload', await reload())
    if (withOpen) note('open', await open())
    console.table(Object.fromEntries(Object.entries(results).map(([k, v]) => [k, v])))
    return results
  }

  window.prikPerf = { run, scroll, drag, group, bucket, zoom, reload, open, nodes }
  console.info('prikPerf: measuring; prikPerf.run() measures again.')
  // Loaded through a script tag, the results are still there to await.
  window.prikPerf.last = run()
  return window.prikPerf.last
})()
