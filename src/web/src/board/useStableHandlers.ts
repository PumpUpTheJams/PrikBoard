import { useLayoutEffect, useRef, useState } from 'react'

/**
 * Event handlers that keep their identity from render to render, each calling the newest version given. Board items
 * that get them (boardItemViews) then only render again when what they show changes, not because a closure was made
 * anew. Call them from events, not while rendering; the names must stay the same.
 */
export function useStableHandlers<T extends { [K in keyof T]: (...args: never[]) => unknown }>(handlers: T): T {
  const latest = useRef(handlers)
  useLayoutEffect(() => {
    latest.current = handlers
  })
  const [stable] = useState(
    () =>
      Object.fromEntries(
        Object.keys(handlers).map((name) => [name, (...args: never[]) => latest.current[name as keyof T](...args)]),
      ) as T,
  )
  return stable
}
