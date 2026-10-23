/** A count with its noun: "1 card", "3 cards". */
export const plural = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`
