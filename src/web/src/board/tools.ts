export type Tool = 'select' | 'pan' | 'note' | 'bucket'

/** Single-key shortcuts, as shown in the tool tips. */
export const toolKeys: Record<string, Tool> = { v: 'select', h: 'pan', n: 'note', b: 'bucket' }
