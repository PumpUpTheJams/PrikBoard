import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { Caller } from '../api/types'
import { CardIcon, CheckIcon, ChevronDownIcon, ContrastIcon, ExternalIcon, ShieldIcon, SignOutIcon, UserIcon } from '../board/icons'
import { BUILT_IN_THEMES, useTheme } from '../theme'
import { tokenValue, type Theme } from '../themes/themeFile'
import { Face } from './Avatar'
import { Menu, MenuItem } from './Menu'

/** A little picture of a theme, drawn from its own tokens: the board under its bar, the accent and three card fills. */
function ThemeSwatch({ theme }: { theme: Theme }) {
  const token = (name: string) => tokenValue(theme, name)
  return (
    <span className="theme-swatch" aria-hidden="true" style={{ background: token('bg'), borderColor: token('line') }}>
      <span className="theme-swatch-bar" style={{ background: token('bar') }}>
        <span style={{ background: token('accent') }} />
      </span>
      <span className="theme-swatch-cards">
        <span style={{ background: token('fill-butter') }} />
        <span style={{ background: token('fill-mint') }} />
        <span style={{ background: token('fill-sky') }} />
      </span>
    </span>
  )
}

/**
 * Picks the theme, in this browser: a row naming the theme in use that opens the list of themes in place, each with a
 * preview of its colors. The menu stays open, so the page's fade to the new theme shows behind it.
 */
export function ThemePicker() {
  const { theme, choose } = useTheme()
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (open) listRef.current?.querySelector<HTMLElement>('[aria-checked="true"]')?.focus()
  }, [open])

  // Up and down move between the themes, Home and End to the first and last.
  function onListKeyDown(e: KeyboardEvent) {
    const options = [...(listRef.current?.querySelectorAll<HTMLElement>('[role="menuitemradio"]') ?? [])]
    const at = options.indexOf(document.activeElement as HTMLElement)
    const next = ({ ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: options.length - 1 } as Record<string, number>)[e.key]
    if (next === undefined) return
    e.preventDefault()
    options[(next + options.length) % options.length]?.focus()
  }

  return (
    <div className="theme-picker">
      <button
        ref={triggerRef}
        type="button"
        role="menuitem"
        aria-haspopup="true"
        aria-expanded={open}
        className="menu-item theme-picker-trigger"
        onClick={() => setOpen((o) => !o)}
      >
        <ContrastIcon />
        <span className="menu-item-text">Theme</span>
        <span className="theme-picker-current">{theme.name}</span>
        <ChevronDownIcon />
      </button>
      {open && (
        <div ref={listRef} className="theme-picker-list" role="group" aria-label="Theme" onKeyDown={onListKeyDown}>
          {BUILT_IN_THEMES.map((option) => (
            <button
              key={option.id}
              type="button"
              role="menuitemradio"
              aria-checked={option.id === theme.id}
              className="menu-item theme-option"
              title={option.description}
              onClick={() => {
                if (option.id !== theme.id) choose(option.id)
                setOpen(false)
                triggerRef.current?.focus()
              }}
            >
              <ThemeSwatch theme={option} />
              <span className="theme-option-text">
                <span className="theme-option-name">{option.name}</span>
                <span className="theme-option-description">{option.description}</span>
              </span>
              {option.id === theme.id && <CheckIcon />}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

interface SettingsMenuProps {
  user: Caller
  /** Whether this device may switch to the mobile layout. */
  canSwitch: boolean
  onAccount: () => void
  onMobile: () => void
  onSignOut: () => void
}

/** Your menu on the right of the top bar: who you are, your account, admin, the layout, the API docs and sign-out. */
export function SettingsMenu({ user, canSwitch, onAccount, onMobile, onSignOut }: SettingsMenuProps) {
  const provider = user.identities.find((i) => i.provider === user.provider)?.providerName ?? user.provider
  const face = (
    <span className="me">
      <Face name={user.displayName} avatarUrl={user.avatarUrl} />
    </span>
  )

  return (
    <Menu
      label="Your settings"
      align="end"
      triggerClassName="settings-trigger"
      trigger={
        <>
          {face}
          <ChevronDownIcon />
        </>
      }
    >
      {(close) => (
        <>
          <div className="menu-user">
            {face}
            <span className="menu-user-text">
              <span className="menu-user-name">{user.displayName}</span>
              <span className="menu-user-detail">{provider ? `Signed in with ${provider}` : 'Signed in'}</span>
            </span>
          </div>
          <span className="menu-divider" role="separator" />
          {user.kind === 'Session' && (
            <MenuItem icon={<UserIcon />} onClick={onAccount}>
              Account
            </MenuItem>
          )}
          {user.isAdmin && (
            <MenuItem
              icon={<ShieldIcon />}
              onClick={() => {
                window.location.hash = '/admin'
              }}
            >
              Admin
            </MenuItem>
          )}
          <ThemePicker />
          {canSwitch && (
            <MenuItem icon={<CardIcon />} onClick={onMobile}>
              Mobile view
            </MenuItem>
          )}
          <a role="menuitem" className="menu-item" href="/scalar" target="_blank" rel="noreferrer" onClick={close}>
            <ExternalIcon />
            API reference
          </a>
          <a role="menuitem" className="menu-item" href="/openapi/v1.json" target="_blank" rel="noreferrer" onClick={close}>
            <ExternalIcon />
            OpenAPI document
          </a>
          <span className="menu-divider" role="separator" />
          <MenuItem icon={<SignOutIcon />} onClick={onSignOut}>
            Sign out
          </MenuItem>
        </>
      )}
    </Menu>
  )
}
