import type { Caller } from '../api/types'
import { CardIcon, ChevronDownIcon, ContrastIcon, ExternalIcon, MoonIcon, ShieldIcon, SignOutIcon, SunIcon, UserIcon } from '../board/icons'
import { useColorScheme } from '../theme'
import { Face } from './Avatar'
import { Menu, MenuItem } from './Menu'

/**
 * Switches between the Productivity (dark) and Positivity (light) color schemes, in this browser: the name of the one
 * in use slides in, and the switch's knob slides over with a moon or a sun in it. The menu stays open to show it.
 */
export function ColorSchemeToggle() {
  const { scheme, choose } = useColorScheme()
  const on = scheme === 'positivity'
  return (
    <button
      type="button"
      role="menuitemcheckbox"
      aria-checked={on}
      aria-label="Positivity colors"
      className="menu-item scheme-toggle"
      title={on ? 'Positivity: light daytime colors. Switch for Productivity, the dark ones.' : 'Productivity: dark colors. Switch for Positivity, the light daytime ones.'}
      onClick={() => choose(on ? 'productivity' : 'positivity')}
    >
      <ContrastIcon />
      <span className="scheme-names" aria-hidden="true">
        <span className="scheme-name productivity">Productivity</span>
        <span className="scheme-name positivity">Positivity</span>
      </span>
      <span className="scheme-switch" aria-hidden="true">
        <span className="scheme-knob">
          <MoonIcon size={12} />
          <SunIcon size={12} />
        </span>
      </span>
    </button>
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
          <ColorSchemeToggle />
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
