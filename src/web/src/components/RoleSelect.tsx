import { boardRoles, type BoardRole } from '../api/types'
import { Select, type SelectOption } from './Select'

const roleOptions: SelectOption<BoardRole>[] = boardRoles.map((role) => ({
  value: role,
  label: role,
  description: {
    Viewer: 'Can look, not change',
    Editor: 'Can change post-its, buckets and images',
    Owner: 'Can also manage people and the board',
  }[role],
}))

export function RoleSelect({ value, onChange, label, disabled }: { value: BoardRole; onChange: (role: BoardRole) => void; label: string; disabled?: boolean }) {
  return <Select label={label} value={value} options={roleOptions} onChange={onChange} disabled={disabled} className="role-select" />
}
