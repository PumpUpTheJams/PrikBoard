import { createContext, useCallback, useContext, useEffect, useState } from 'react'

/**
 * Which layout the app shows: the board as a canvas (desktop), or boards, buckets and cards as lists one screen at a
 * time (mobile). Both read the same address, so a link opens the same board or card on either.
 */
export type LayoutMode = 'desktop' | 'mobile'

/** Phones: a narrow screen with a touch screen as the main pointer. A narrow desktop window keeps the canvas. */
const PHONE = '(max-width: 760px) and (pointer: coarse)'
/** Devices that are touch first (tablets too): they can switch layouts in the settings menu. */
const TOUCH = '(pointer: coarse)'
/** The layout picked in the settings menu on this device; without one, the layout follows the screen. */
const KEY = 'prikboard.layout'

function stored(): LayoutMode | null {
  try {
    const value = localStorage.getItem(KEY)
    return value === 'desktop' || value === 'mobile' ? value : null
  } catch {
    return null
  }
}

const matches = (query: string) => typeof window.matchMedia === 'function' && window.matchMedia(query).matches

/**
 * The layout to show, following the screen (rotating a tablet or resizing a window can change it) unless one was
 * picked on this device. `choose(null)` goes back to following the screen.
 */
export function useLayoutMode() {
  const [phone, setPhone] = useState(() => matches(PHONE))
  const [picked, setPicked] = useState(stored)

  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return
    const query = window.matchMedia(PHONE)
    const onChange = () => setPhone(query.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])

  const choose = useCallback((mode: LayoutMode | null) => {
    try {
      if (mode) localStorage.setItem(KEY, mode)
      else localStorage.removeItem(KEY)
    } catch {
      // Without storage the choice lasts until the page reloads.
    }
    setPicked(mode)
  }, [])

  const mode: LayoutMode = picked ?? (phone ? 'mobile' : 'desktop')
  return { mode, choose, canSwitch: picked !== null || matches(TOUCH) }
}

/** Set by the mobile layout: shared windows (the tag and people pickers) open as sheets from the bottom of the screen. */
export const MobileLayout = createContext(false)

export const useMobileLayout = () => useContext(MobileLayout)
