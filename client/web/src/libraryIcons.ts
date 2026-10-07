import {
  AudioLines,
  File,
  FileArchive,
  FileCode,
  FileSpreadsheet,
  FileText,
  Film,
  Image,
  Presentation,
  StickyNote,
} from '@lucide/vue'
import type { LibraryKind } from './types'

/** 资料库里每类文件的图标 */
export const kindIcon: Record<LibraryKind, unknown> = {
  image: Image,
  document: FileText,
  sheet: FileSpreadsheet,
  slides: Presentation,
  pdf: FileText,
  audio: AudioLines,
  video: Film,
  archive: FileArchive,
  code: FileCode,
  note: StickyNote,
  other: File,
}

export const kinds: LibraryKind[] = ['image', 'document', 'sheet', 'slides', 'pdf', 'audio', 'video', 'code', 'note', 'archive', 'other']
