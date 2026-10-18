import { imageTypes } from '../api/types'

/** Opens the system file picker for images. Must be called from a click (or another user gesture). */
export function pickImageFiles(): Promise<File[]> {
  return new Promise((resolve) => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = imageTypes.join(',')
    input.multiple = true
    input.addEventListener('change', () => resolve([...(input.files ?? [])]))
    input.addEventListener('cancel', () => resolve([]))
    input.click()
  })
}

/** Image files from a paste or drop; anything else (text, other files) is left alone. */
export function imageFiles(data: DataTransfer | null): File[] {
  return [...(data?.files ?? [])].filter((f) => f.type.startsWith('image/'))
}

/** Whether a drag carries files, so the drop target can accept it before the files are readable. */
export const draggingFiles = (data: DataTransfer | null) => !!data && [...data.types].includes('Files')
