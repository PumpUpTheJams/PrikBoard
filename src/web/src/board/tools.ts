export type Tool = 'select' | 'note' | 'bucket' | 'pen'

/** Single-key shortcuts, as shown in the tool tips. */
export const toolKeys: Record<string, Tool> = { v: 'select', n: 'note', b: 'bucket', p: 'pen' }
