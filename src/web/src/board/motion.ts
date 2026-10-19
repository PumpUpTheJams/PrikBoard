export const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches

/** A quick pop of an element, to show something happened to it (a link was added). Skipped for reduced motion. */
export function pop(el: Element | null | undefined, scale = 1.08) {
  if (!el || reducedMotion()) return
  el.animate([{ transform: 'scale(1)' }, { transform: `scale(${scale})` }, { transform: 'scale(1)' }], { duration: 320, easing: 'ease-out' })
}
