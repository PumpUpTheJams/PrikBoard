export type Tool = 'select' | 'card' | 'bucket' | 'pen'

/** Single-key shortcuts, as shown in the tool tips. */
export const toolKeys: Record<string, Tool> = { v: 'select', n: 'card', b: 'bucket', p: 'pen' }
