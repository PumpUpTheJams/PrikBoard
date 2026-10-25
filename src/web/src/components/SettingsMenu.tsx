import type { Caller } from '../api/types'
import { CardIcon, ChevronDownIcon, ExternalIcon, ShieldIcon, SignOutIcon, SunIcon, UserIcon } from '../board/icons'
import { useColorScheme } from '../theme'
import { Face } from './Avatar'
import { Menu, MenuItem, MenuToggle } from './Menu'

/** Switches between the Productivity (dark) and Positivity (light) color schemes, in this browser. */
export function ColorSchemeToggle() {
  const { scheme, choose } = useColorScheme()
  return (
    <MenuToggle
      icon={<SunIcon />}
      title="Positivity: light daytime colors. Off: Productivity, the dark ones."
      checked={scheme === 'positivity'}
      onChange={(on) => choose(on ? 'positivity' : 'productivity')}
    >
      Positivity colors
    </MenuToggle>
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
