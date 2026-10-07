import { reactive } from 'vue'
import { bridge } from './bridge'
import { i18n } from './i18n'
import { newConversation, send, state, toast } from './store'
import type { LibraryFolder, LibraryItem, LibraryTab } from './types'

// 资料库页面的状态。和对话互不干扰：从资料库「围绕此内容展开对话」时才把文件交给输入框。

function readPref<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
  try {
    const v = localStorage.getItem(key) as T | null
    return v && allowed.includes(v) ? v : fallback
  } catch {
    return fallback
  }
}
function writePref(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    // 存不了就算了，只是下次不记得
  }
}

export const lib = reactive({
  tab: 'recent' as LibraryTab,
  /** 打开的文件夹（null 表示不在文件夹里） */
  folderId: null as string | null,
  search: '',
  kind: '',
  sort: readPref('flyknit.library.sort', ['updated', 'name', 'size'] as const, 'updated'),
  layout: readPref('flyknit.library.layout', ['grid', 'list'] as const, 'grid'),
  showHidden: readPref('flyknit.library.hidden', ['0', '1'] as const, '0') === '1',
  items: [] as LibraryItem[],
  folders: [] as LibraryFolder[],
  selected: [] as string[],
  loading: false,
  /** 正在查看的文件 */
  viewing: null as LibraryItem | null,
})

const t = (key: string, params?: Record<string, unknown>) => i18n.global.t(key, params ?? {})

function fail(e: unknown) {
  toast(t('error.generic', { msg: e instanceof Error ? e.message : String(e) }))
}

let seq = 0
export async function loadLibrary() {
  const mine = ++seq
  lib.loading = true
  try {
    const r = await bridge.libraryList({
      tab: lib.tab === 'folders' && lib.folderId ? 'all' : lib.tab,
      folderId: lib.folderId,
      search: lib.search,
      kind: lib.kind,
      sort: lib.sort,
      hidden: lib.showHidden,
    })
    if (mine !== seq) return // 已经切到别的标签了，丢掉旧结果
    lib.items = r.items
    lib.folders = r.folders
    lib.selected = lib.selected.filter((id) => r.items.some((i) => i.id === id))
  } catch (e) {
    fail(e)
  } finally {
    if (mine === seq) lib.loading = false
  }
}

export function openLibrary() {
  state.view = 'library'
  lib.viewing = null
  void loadLibrary()
}

export function setTab(tab: LibraryTab) {
  lib.tab = tab
  lib.folderId = null
  lib.selected = []
  void loadLibrary()
}

export function openFolder(id: string | null) {
  lib.folderId = id
  lib.selected = []
  if (id) lib.tab = 'folders'
  void loadLibrary()
}

export function setLayout(layout: 'grid' | 'list') {
  lib.layout = layout
  writePref('flyknit.library.layout', layout)
}

export function setSort(sort: 'updated' | 'name' | 'size') {
  lib.sort = sort
  writePref('flyknit.library.sort', sort)
  void loadLibrary()
}

export function setShowHidden(show: boolean) {
  lib.showHidden = show
  writePref('flyknit.library.hidden', show ? '1' : '0')
  void loadLibrary()
}

export function toggleSelect(id: string) {
  const i = lib.selected.indexOf(id)
  if (i >= 0) lib.selected.splice(i, 1)
  else lib.selected.push(id)
}

/** 围绕这些文件开始一段新对话；带了问题就直接发出去 */
export async function chatAbout(ids: string[], question = '') {
  if (ids.length === 0) return
  try {
    const refs = await bridge.libraryAttachments(ids)
    if (refs.length === 0) {
      toast(t('library.missing'))
      return
    }
    for (const r of refs) {
      const item = lib.items.find((i) => i.name === r.fileName && i.thumbUrl)
      if (item?.thumbUrl) r.preview = item.thumbUrl
    }
    newConversation()
    state.pending = refs
    lib.selected = []
    lib.viewing = null
    if (question.trim()) await send(question)
    else window.setTimeout(() => window.dispatchEvent(new CustomEvent('flyknit:focus-input')), 50)
  } catch (e) {
    fail(e)
  }
}

export async function upload(accept?: 'image') {
  try {
    const added = await bridge.libraryUpload(lib.folderId, accept)
    if (added.length > 0) {
      toast(t('library.uploaded', { n: added.length }))
      await loadLibrary()
    }
  } catch (e) {
    fail(e)
  }
}

export async function uploadDropped(files: File[]) {
  if (files.length === 0) return
  try {
    const added = await bridge.libraryAddDropped(files, lib.folderId)
    toast(t('library.uploaded', { n: added.length }))
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function addNote(title: string, text: string) {
  try {
    const note = await bridge.libraryAddNote(title, text, lib.folderId)
    await loadLibrary()
    return note
  } catch (e) {
    fail(e)
    return null
  }
}

export async function createFolder(name: string) {
  try {
    await bridge.libraryCreateFolder(name)
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function renameItem(id: string, name: string) {
  if (!name.trim()) return
  try {
    await bridge.libraryRename(id, name)
    const item = lib.items.find((i) => i.id === id)
    if (item) item.name = name.trim()
    if (lib.viewing?.id === id) lib.viewing.name = name.trim()
  } catch (e) {
    fail(e)
  }
}

export async function renameFolder(id: string, name: string) {
  if (!name.trim()) return
  try {
    await bridge.libraryRenameFolder(id, name)
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function deleteFolder(id: string) {
  try {
    await bridge.libraryDeleteFolder(id)
    if (lib.folderId === id) lib.folderId = null
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function setFavorite(ids: string[], favorite: boolean) {
  try {
    await bridge.libraryFavorite(ids, favorite)
    for (const i of lib.items) if (ids.includes(i.id)) i.favorite = favorite
    if (lib.viewing && ids.includes(lib.viewing.id)) lib.viewing.favorite = favorite
    toast(favorite ? t('library.favorited') : t('library.unfavorited'))
    if (lib.tab === 'favorites') await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function moveTo(ids: string[], folderId: string | null) {
  try {
    const n = await bridge.libraryMove(ids, folderId)
    const folder = lib.folders.find((f) => f.id === folderId)
    toast(folder ? t('library.moved', { n, folder: folder.name }) : t('library.movedOut', { n }))
    lib.selected = []
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function removeItems(ids: string[]) {
  try {
    const n = await bridge.libraryDelete(ids)
    toast(t('library.deleted', { n }))
    lib.selected = []
    if (lib.viewing && ids.includes(lib.viewing.id)) lib.viewing = null
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function restoreItems(ids: string[]) {
  try {
    await bridge.libraryRestore(ids)
    lib.selected = []
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function purgeItems(ids: string[]) {
  try {
    await bridge.libraryPurge(ids)
    lib.selected = []
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function emptyTrash() {
  try {
    await bridge.libraryEmptyTrash()
    await loadLibrary()
  } catch (e) {
    fail(e)
  }
}

export async function download(ids: string[]) {
  try {
    const n = await bridge.libraryDownload(ids)
    if (n > 0) toast(t('library.downloaded', { n }))
  } catch (e) {
    fail(e)
  }
}

export async function share(ids: string[]) {
  try {
    const n = await bridge.libraryShare(ids)
    toast(n > 0 ? t('library.shared', { n }) : t('library.missing'))
  } catch (e) {
    fail(e)
  }
}

export function formatSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}
