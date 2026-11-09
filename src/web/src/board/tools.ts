/** `hand`: only moves the view, a left press panning as the middle button does. */
export type Tool = 'select' | 'hand' | 'card' | 'bucket' | 'pen' | 'label'

/** Single-key shortcuts, as shown in the tool tips. */
export const toolKeys: Record<string, Tool> = { v: 'select', h: 'hand', c: 'card', b: 'bucket', p: 'pen', t: 'label' }
