import type { ChatUpdate } from '../api/types'

/** The board activity a connected Discord or Slack channel can post, as Integrations offers it. */
export const updateChoices: { value: ChatUpdate; label: string }[] = [
  { value: 'NewCards', label: 'New cards' },
  { value: 'Moves', label: 'Cards moving between buckets' },
  { value: 'Assignments', label: 'People being assigned' },
  { value: 'AgentWork', label: 'Agents starting and stopping work' },
]
