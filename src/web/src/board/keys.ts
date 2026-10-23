/** Ctrl/Cmd + `key` (a letter, either case) without Alt. `shift`: Shift held (true), not held (false), or either (undefined). */
export function shortcut(e: KeyboardEvent, key: string, { shift }: { shift?: boolean } = {}): boolean {
  return (e.ctrlKey || e.metaKey) && !e.altKey && (shift === undefined || e.shiftKey === shift) && e.key.toLowerCase() === key
}

interface KeyTargetOptions {
  /** Text typed in contenteditable elements counts as a field's too. */
  editable?: boolean
  /** Buttons and links keep their keys too (Space presses them). */
  controls?: boolean
  /** Keys in the open card's window (`.expanded`, a dialog of its own) reach the board. */
  inCard?: boolean
}

/** Whether an event's target is a field (or, with the options, an editable element, button or link) that keeps its keys. */
export function inField(target: EventTarget | null, { editable = false, controls = false }: KeyTargetOptions = {}): boolean {
  const fields = ['input', 'textarea', 'select', ...(controls ? ['button', 'a'] : []), ...(editable ? ['[contenteditable="true"]'] : [])]
  return target instanceof Element && target.closest(fields.join(', ')) !== null
}

/** Whether a key pressed on `target` is left to it rather than handled by the canvas: in a field, a menu or another window. */
export function keyTargetIgnored(target: EventTarget | null, options: KeyTargetOptions = {}): boolean {
  if (!(target instanceof Element)) return false
  if (inField(target, options) || target.closest('[role="menu"], [role="alertdialog"]')) return true
  return target.closest('[role="dialog"]') !== null && !(options.inCard && target.closest('.expanded'))
}
