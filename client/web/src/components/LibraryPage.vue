<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Check,
  ChevronDown,
  ChevronRight,
  Download,
  Eye,
  Folder,
  FolderInput,
  FolderOpen,
  FolderPlus,
  ImagePlus,
  LayoutGrid,
  List,
  ListFilter,
  MessageSquarePlus,
  MoreHorizontal,
  Pencil,
  RotateCcw,
  Search,
  Settings,
  Share2,
  Star,
  StarOff,
  StickyNote,
  Trash2,
  Upload,
  X,
} from '@lucide/vue'
import Popover from './Popover.vue'
import PromptDialog from './PromptDialog.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import LibraryViewer from './LibraryViewer.vue'
import { kindIcon, kinds } from '../libraryIcons'
import {
  addNote,
  chatAbout,
  createFolder,
  deleteFolder,
  download,
  emptyTrash,
  formatSize,
  lib,
  loadLibrary,
  moveTo,
  openFolder,
  purgeItems,
  removeItems,
  renameFolder,
  renameItem,
  restoreItems,
  setFavorite,
  setLayout,
  setShowHidden,
  setSort,
  setTab,
  share,
  toggleSelect,
  upload,
} from '../library'
import { bridge } from '../bridge'
import { toast } from '../store'
import type { LibraryFolder, LibraryItem, LibraryTab } from '../types'

const { t, locale } = useI18n()

const tabs: LibraryTab[] = ['recent', 'favorites', 'folders', 'images', 'all']

const inTrash = computed(() => lib.tab === 'trash')
const currentFolder = computed(() => lib.folders.find((f) => f.id === lib.folderId) ?? null)
/** 文件夹标签页、还没进入某个文件夹时，显示文件夹列表 */
const showFolders = computed(() => lib.tab === 'folders' && !lib.folderId)
const selectedItems = computed(() => lib.items.filter((i) => lib.selected.includes(i.id)))
const selecting = computed(() => lib.selected.length > 0)

// ---------- 搜索 ----------
let searchTimer: number | undefined
watch(
  () => lib.search,
  () => {
    clearTimeout(searchTimer)
    searchTimer = window.setTimeout(loadLibrary, 220)
  },
)
watch(() => lib.kind, () => void loadLibrary())

// ---------- 日期 ----------
function modified(iso: string) {
  const d = new Date(iso)
  const fmt = new Intl.DateTimeFormat(locale.value, { month: 'short', day: 'numeric', ...(d.getFullYear() !== new Date().getFullYear() ? { year: 'numeric' } : {}) })
  return t('library.modifiedAt', { date: fmt.format(d) })
}

// ---------- 点击 ----------
function onCardClick(item: LibraryItem, e: MouseEvent) {
  if (selecting.value || e.ctrlKey || e.metaKey) {
    toggleSelect(item.id)
    return
  }
  if (inTrash.value) {
    toggleSelect(item.id)
    return
  }
  lib.viewing = item
}

// ---------- 单个文件的菜单 ----------
const menu = ref<{ item: LibraryItem | null; folder: LibraryFolder | null; x: number; y: number; moving: boolean } | null>(null)

function openMenu(e: MouseEvent, item: LibraryItem | null, folder: LibraryFolder | null = null) {
  e.stopPropagation()
  e.preventDefault()
  const rect = (e.currentTarget as HTMLElement).getBoundingClientRect?.()
  const x = e.type === 'contextmenu' || !rect ? e.clientX : rect.right - 220
  const y = e.type === 'contextmenu' || !rect ? e.clientY : rect.bottom + 4
  menu.value = { item, folder, x: Math.max(8, Math.min(x, window.innerWidth - 236)), y: Math.min(y, window.innerHeight - 360), moving: false }
}
const closeMenu = () => (menu.value = null)
function onDocDown(e: MouseEvent) {
  if (!(e.target as HTMLElement).closest('.lib-menu, .more-btn')) closeMenu()
}

// ---------- 对话框 ----------
const prompt = ref<{ title: string; value: string; ok: string; run: (v: string) => void } | null>(null)
const confirm = ref<{ title: string; body: string; ok: string; run: () => void } | null>(null)

function askRename(item: LibraryItem) {
  closeMenu()
  prompt.value = { title: t('library.rename'), value: item.name, ok: t('library.save'), run: (v) => void renameItem(item.id, v) }
}
function askNewFolder(then?: (f: LibraryFolder) => void) {
  closeMenu()
  prompt.value = {
    title: t('library.newFolder'),
    value: '',
    ok: t('library.create'),
    run: async (v) => {
      await createFolder(v)
      const f = lib.folders.find((x) => x.name === v)
      if (f && then) then(f)
    },
  }
}
function askRenameFolder(folder: LibraryFolder) {
  closeMenu()
  prompt.value = { title: t('library.renameFolder'), value: folder.name, ok: t('library.save'), run: (v) => void renameFolder(folder.id, v) }
}
function askDeleteFolder(folder: LibraryFolder) {
  closeMenu()
  confirm.value = { title: t('library.deleteFolderTitle'), body: t('library.deleteFolderBody', { name: folder.name }), ok: t('library.delete'), run: () => void deleteFolder(folder.id) }
}
function askPurge(ids: string[]) {
  closeMenu()
  confirm.value = { title: t('library.purgeTitle'), body: t('library.purgeBody', { n: ids.length }), ok: t('library.purge'), run: () => void purgeItems(ids) }
}
function askEmptyTrash() {
  confirm.value = { title: t('library.emptyTrashTitle'), body: t('library.emptyTrashBody'), ok: t('library.emptyTrash'), run: () => void emptyTrash() }
}
function runPrompt(v: string) {
  const p = prompt.value
  prompt.value = null
  p?.run(v)
}
function runConfirm() {
  const c = confirm.value
  confirm.value = null
  c?.run()
}

// ---------- 新建 ----------
async function newNote() {
  const note = await addNote('', '')
  if (note) lib.viewing = note
}

// ---------- 菜单动作 ----------
function act(fn: () => unknown) {
  closeMenu()
  void fn()
}
function moveMenuItems(): string[] {
  return menu.value?.item ? [menu.value.item.id] : lib.selected.slice()
}
const moveFromBar = ref(false)
function startMoveSelected(e: MouseEvent) {
  moveFromBar.value = true
  const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
  menu.value = { item: null, folder: null, x: Math.max(8, Math.min(rect.left, window.innerWidth - 236)), y: Math.max(8, rect.top - 300), moving: true }
}
function moveToFolder(folderId: string | null) {
  const ids = moveMenuItems()
  closeMenu()
  moveFromBar.value = false
  void moveTo(ids, folderId)
}

async function launch(item: LibraryItem) {
  closeMenu()
  const r = await bridge.launchFile(item.path).catch(() => ({ ok: false, message: t('library.missing') }))
  if (!r.ok) toast(r.message)
}
async function reveal(item: LibraryItem) {
  closeMenu()
  const r = await bridge.libraryReveal(item.id).catch(() => ({ ok: false, message: t('library.missing') }))
  if (!r.ok) toast(r.message)
}

function clearSelection() {
  lib.selected = []
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && !lib.viewing && !prompt.value && !confirm.value) {
    if (menu.value) closeMenu()
    else if (selecting.value) clearSelection()
  }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a' && !lib.viewing && !(e.target as HTMLElement).closest('input, textarea')) {
    e.preventDefault()
    lib.selected = lib.items.map((i) => i.id)
  }
}

const onChanged = () => {
  if (!lib.viewing) void loadLibrary()
}

onMounted(() => {
  document.addEventListener('mousedown', onDocDown)
  window.addEventListener('keydown', onKey)
  window.addEventListener('flyknit:library-changed', onChanged)
  void loadLibrary()
})
onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocDown)
  window.removeEventListener('keydown', onKey)
  window.removeEventListener('flyknit:library-changed', onChanged)
})

const emptyText = computed(() => {
  if (lib.search) return t('library.noResults')
  if (lib.folderId) return t('library.emptyFolder')
  return t(`library.empty.${lib.tab}`)
})
</script>

<template>
  <section class="library" :class="{ viewing: !!lib.viewing }">
    <LibraryViewer v-if="lib.viewing" :key="lib.viewing.id" :item="lib.viewing" @close="lib.viewing = null" @rename="askRename" />

    <template v-else>
      <header class="head">
        <div class="crumbs">
          <template v-if="lib.folderId || inTrash">
            <button type="button" class="crumb" @click="inTrash ? setTab('recent') : openFolder(null)">{{ t('library.title') }}</button>
            <ChevronRight :size="18" class="sep" />
            <h1>{{ inTrash ? t('library.trash') : currentFolder?.name }}</h1>
          </template>
          <h1 v-else>{{ t('library.title') }}</h1>
        </div>

        <div class="tools">
          <Popover placement="down" align="end" :width="220">
            <template #trigger="{ toggle, open }">
              <button type="button" class="icon-btn" :class="{ on: open || lib.kind }" :title="t('library.filter')" @click="toggle">
                <ListFilter :size="18" />
              </button>
            </template>
            <template #default>
              <p class="pop-title">{{ t('library.sortBy') }}</p>
              <button v-for="s in ['updated', 'name', 'size'] as const" :key="s" type="button" class="pop-opt" @click="setSort(s)">
                <span>{{ t(`library.sort.${s}`) }}</span>
                <Check v-if="lib.sort === s" :size="14" />
              </button>
              <p class="pop-title">{{ t('library.type') }}</p>
              <button type="button" class="pop-opt" @click="lib.kind = ''">
                <span>{{ t('library.kinds.all') }}</span>
                <Check v-if="!lib.kind" :size="14" />
              </button>
              <button v-for="k in kinds" :key="k" type="button" class="pop-opt" @click="lib.kind = k">
                <span><component :is="kindIcon[k]" :size="14" /> {{ t(`library.kinds.${k}`) }}</span>
                <Check v-if="lib.kind === k" :size="14" />
              </button>
            </template>
          </Popover>

          <span class="divider" />

          <div class="seg" role="radiogroup" :aria-label="t('library.layout')">
            <button type="button" role="radio" :aria-checked="lib.layout === 'grid'" :class="{ on: lib.layout === 'grid' }" :title="t('library.grid')" @click="setLayout('grid')">
              <LayoutGrid :size="17" />
            </button>
            <button type="button" role="radio" :aria-checked="lib.layout === 'list'" :class="{ on: lib.layout === 'list' }" :title="t('library.list')" @click="setLayout('list')">
              <List :size="17" />
            </button>
          </div>

          <label class="search">
            <Search :size="16" />
            <input v-model="lib.search" type="search" :placeholder="t('library.search')" />
            <button v-if="lib.search" type="button" class="clear" @click="lib.search = ''"><X :size="14" /></button>
          </label>

          <Popover v-if="!inTrash" placement="down" align="end" :width="230">
            <template #trigger="{ toggle }">
              <button type="button" class="new-btn" @click="toggle">{{ t('library.new') }} <ChevronDown :size="16" /></button>
            </template>
            <template #default="{ close }">
              <button type="button" class="pop-opt big" @click="(close(), upload('image'))"><ImagePlus :size="17" /> {{ t('library.newImage') }}</button>
              <button type="button" class="pop-opt big" @click="(close(), newNote())"><StickyNote :size="17" /> {{ t('library.newNote') }}</button>
              <button type="button" class="pop-opt big" @click="(close(), askNewFolder())"><FolderPlus :size="17" /> {{ t('library.newFolder') }}</button>
              <hr />
              <button type="button" class="pop-opt big" @click="(close(), upload())"><Upload :size="17" /> {{ t('library.upload') }}</button>
            </template>
          </Popover>

          <Popover placement="down" align="end" :width="250">
            <template #trigger="{ toggle, open }">
              <button type="button" class="icon-btn" :class="{ on: open }" :title="t('library.settings')" @click="toggle">
                <Settings :size="18" />
              </button>
            </template>
            <template #default="{ close }">
              <label class="pop-opt big switch-row">
                <span><Eye :size="17" /> {{ t('library.showHidden') }}</span>
                <input type="checkbox" class="switch" :checked="lib.showHidden" @change="setShowHidden(($event.target as HTMLInputElement).checked)" />
              </label>
              <p class="pop-hint">{{ t('library.showHiddenHint') }}</p>
              <hr />
              <button type="button" class="pop-opt big" @click="(close(), setTab('trash'))"><Trash2 :size="17" /> {{ t('library.trash') }}</button>
            </template>
          </Popover>
        </div>
      </header>

      <nav v-if="!inTrash" class="tabs" role="tablist">
        <button
          v-for="tab in tabs"
          :key="tab"
          type="button"
          role="tab"
          :aria-selected="lib.tab === tab && !lib.folderId"
          :class="{ on: lib.tab === tab && !(tab !== 'folders' && lib.folderId) }"
          @click="setTab(tab)"
        >
          {{ t(`library.tabs.${tab}`) }}
        </button>
      </nav>
      <div v-else class="trash-bar">
        <span>{{ t('library.trashHint') }}</span>
        <button v-if="lib.items.length" type="button" class="btn danger-outline" @click="askEmptyTrash"><Trash2 :size="15" /> {{ t('library.emptyTrash') }}</button>
      </div>

      <div class="content" @contextmenu.self.prevent>
        <!-- 文件夹 -->
        <div v-if="showFolders" class="folders">
          <button type="button" class="folder add" @click="askNewFolder()">
            <FolderPlus :size="26" />
            <span>{{ t('library.newFolder') }}</span>
          </button>
          <div
            v-for="f in lib.folders"
            :key="f.id"
            class="folder"
            role="button"
            tabindex="0"
            @click="openFolder(f.id)"
            @keydown.enter="openFolder(f.id)"
            @contextmenu="openMenu($event, null, f)"
          >
            <Folder :size="34" class="folder-ico" />
            <span class="folder-name">{{ f.name }}</span>
            <small>{{ t('library.folderCount', { n: f.count }) }}</small>
            <button type="button" class="more-btn" :aria-label="t('library.more')" @click="openMenu($event, null, f)"><MoreHorizontal :size="16" /></button>
          </div>
        </div>

        <template v-else>
          <p v-if="!lib.loading && lib.items.length === 0" class="empty">
            <FolderOpen :size="34" />
            <span>{{ emptyText }}</span>
            <small v-if="!inTrash && !lib.search">{{ t('library.dropHint') }}</small>
          </p>

          <!-- 网格 -->
          <div v-else-if="lib.layout === 'grid'" class="grid">
            <article
              v-for="item in lib.items"
              :key="item.id"
              class="card"
              :class="[item.kind, { selected: lib.selected.includes(item.id), image: !!item.thumbUrl && item.exists, missing: !item.exists }]"
              tabindex="0"
              @click="onCardClick(item, $event)"
              @keydown.enter="onCardClick(item, $event as unknown as MouseEvent)"
              @contextmenu="openMenu($event, item)"
            >
              <img v-if="item.thumbUrl && item.exists" :src="item.thumbUrl" :alt="item.name" loading="lazy" draggable="false" />
              <template v-else>
                <span class="card-name">{{ item.name }}</span>
                <component :is="kindIcon[item.kind]" :size="30" class="card-ico" />
                <span class="card-date">{{ item.exists ? modified(item.updatedAt) : t('library.missingShort') }}</span>
              </template>
              <Star v-if="item.favorite" :size="14" class="fav-mark" />
              <button
                type="button"
                class="check"
                role="checkbox"
                :aria-checked="lib.selected.includes(item.id)"
                :aria-label="item.name"
                @click.stop="toggleSelect(item.id)"
              >
                <Check :size="13" />
              </button>
              <button type="button" class="more-btn" :aria-label="t('library.more')" @click="openMenu($event, item)"><MoreHorizontal :size="16" /></button>
            </article>
          </div>

          <!-- 列表 -->
          <table v-else class="rows">
            <thead>
              <tr>
                <th class="c-check" />
                <th>{{ t('library.col.name') }}</th>
                <th class="c-src">{{ t('library.col.source') }}</th>
                <th class="c-size">{{ t('library.col.size') }}</th>
                <th class="c-date">{{ t('library.col.modified') }}</th>
                <th class="c-more" />
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="item in lib.items"
                :key="item.id"
                :class="{ selected: lib.selected.includes(item.id), missing: !item.exists }"
                @click="onCardClick(item, $event)"
                @contextmenu="openMenu($event, item)"
              >
                <td class="c-check">
                  <button
                    type="button"
                    class="check inline"
                    role="checkbox"
                    :aria-checked="lib.selected.includes(item.id)"
                    :aria-label="item.name"
                    @click.stop="toggleSelect(item.id)"
                  >
                    <Check :size="13" />
                  </button>
                </td>
                <td>
                  <div class="c-name">
                    <img v-if="item.thumbUrl && item.exists" :src="item.thumbUrl" alt="" class="mini-thumb" loading="lazy" />
                    <component :is="kindIcon[item.kind]" v-else :size="18" class="row-ico" :class="item.kind" />
                    <span>{{ item.name }}</span>
                    <Star v-if="item.favorite" :size="13" class="fav-inline" />
                  </div>
                </td>
                <td class="c-src">{{ t(`library.sources.${item.source}`) }}</td>
                <td class="c-size">{{ formatSize(item.size) }}</td>
                <td class="c-date">{{ new Date(item.updatedAt).toLocaleString(locale) }}</td>
                <td class="c-more">
                  <button type="button" class="more-btn" :aria-label="t('library.more')" @click="openMenu($event, item)"><MoreHorizontal :size="16" /></button>
                </td>
              </tr>
            </tbody>
          </table>
        </template>
      </div>

      <!-- 多选操作条 -->
      <div v-if="selecting" class="select-bar" role="toolbar">
        <span class="count">{{ t('library.selected', { n: lib.selected.length }) }}</span>
        <template v-if="!inTrash">
          <button type="button" class="bar-btn soft" @click="chatAbout(lib.selected.slice())"><MessageSquarePlus :size="16" /> {{ t('library.startChat') }}</button>
          <button type="button" class="bar-btn" @click="download(lib.selected.slice())"><Download :size="16" /> {{ t('library.download') }}</button>
          <button type="button" class="bar-btn danger" @click="removeItems(lib.selected.slice())"><Trash2 :size="16" /> {{ t('library.delete') }}</button>
          <Popover placement="up" align="end" :width="210">
            <template #trigger="{ toggle }">
              <button type="button" class="bar-icon" :aria-label="t('library.more')" @click="toggle"><MoreHorizontal :size="18" /></button>
            </template>
            <template #default="{ close }">
              <button type="button" class="pop-opt big" @click="(close(), setFavorite(lib.selected.slice(), !selectedItems.every((i) => i.favorite)))">
                <component :is="selectedItems.every((i) => i.favorite) ? StarOff : Star" :size="16" />
                {{ selectedItems.every((i) => i.favorite) ? t('library.unfavorite') : t('library.favorite') }}
              </button>
              <button type="button" class="pop-opt big" @click="(close(), share(lib.selected.slice()))"><Share2 :size="16" /> {{ t('library.share') }}</button>
              <button type="button" class="pop-opt big" @click="(close(), startMoveSelected($event))"><FolderInput :size="16" /> {{ t('library.moveTo') }}</button>
            </template>
          </Popover>
        </template>
        <template v-else>
          <button type="button" class="bar-btn" @click="restoreItems(lib.selected.slice())"><RotateCcw :size="16" /> {{ t('library.restore') }}</button>
          <button type="button" class="bar-btn danger" @click="askPurge(lib.selected.slice())"><Trash2 :size="16" /> {{ t('library.purge') }}</button>
        </template>
        <button type="button" class="bar-icon" :aria-label="t('library.clearSelection')" @click="clearSelection"><X :size="18" /></button>
      </div>
    </template>

    <Teleport to="body">
      <div v-if="menu" class="lib-menu" :style="{ left: `${menu.x}px`, top: `${menu.y}px` }" role="menu">
        <!-- 移到文件夹：选择目标 -->
        <template v-if="menu.moving">
          <p class="menu-title">{{ t('library.moveTo') }}</p>
          <button v-for="f in lib.folders" :key="f.id" role="menuitem" type="button" :disabled="f.id === lib.folderId" @click="moveToFolder(f.id)">
            <Folder :size="15" /> {{ f.name }}
          </button>
          <button v-if="lib.folderId" role="menuitem" type="button" @click="moveToFolder(null)"><FolderOpen :size="15" /> {{ t('library.moveOut') }}</button>
          <button role="menuitem" type="button" @click="askNewFolder((f) => moveToFolder(f.id))"><FolderPlus :size="15" /> {{ t('library.newFolder') }}</button>
        </template>

        <!-- 文件夹 -->
        <template v-else-if="menu.folder">
          <button role="menuitem" type="button" @click="askRenameFolder(menu.folder)"><Pencil :size="15" /> {{ t('library.rename') }}</button>
          <button role="menuitem" type="button" class="danger" @click="askDeleteFolder(menu.folder)"><Trash2 :size="15" /> {{ t('library.delete') }}</button>
        </template>

        <!-- 回收站里的文件 -->
        <template v-else-if="menu.item && inTrash">
          <button role="menuitem" type="button" @click="act(() => restoreItems([menu!.item!.id]))"><RotateCcw :size="15" /> {{ t('library.restore') }}</button>
          <button role="menuitem" type="button" class="danger" @click="askPurge([menu.item.id])"><Trash2 :size="15" /> {{ t('library.purge') }}</button>
        </template>

        <!-- 文件 -->
        <template v-else-if="menu.item">
          <button role="menuitem" type="button" @click="act(() => chatAbout([menu!.item!.id]))"><MessageSquarePlus :size="15" /> {{ t('library.chatAbout') }}</button>
          <button role="menuitem" type="button" @click="act(() => setFavorite([menu!.item!.id], !menu!.item!.favorite))">
            <component :is="menu.item.favorite ? StarOff : Star" :size="15" /> {{ menu.item.favorite ? t('library.unfavorite') : t('library.favorite') }}
          </button>
          <button role="menuitem" type="button" @click="act(() => download([menu!.item!.id]))"><Download :size="15" /> {{ t('library.download') }}</button>
          <button role="menuitem" type="button" @click="act(() => share([menu!.item!.id]))"><Share2 :size="15" /> {{ t('library.share') }}</button>
          <button role="menuitem" type="button" @click="askRename(menu.item)"><Pencil :size="15" /> {{ t('library.rename') }}</button>
          <button role="menuitem" type="button" @click="menu.moving = true"><FolderInput :size="15" /> {{ t('library.moveTo') }}</button>
          <button role="menuitem" type="button" @click="reveal(menu.item)"><FolderOpen :size="15" /> {{ t('library.reveal') }}</button>
          <button role="menuitem" type="button" @click="launch(menu.item)"><ChevronRight :size="15" /> {{ t('library.openWith') }}</button>
          <hr />
          <button role="menuitem" type="button" class="danger" @click="act(() => removeItems([menu!.item!.id]))"><Trash2 :size="15" /> {{ t('library.delete') }}</button>
          <p class="menu-meta">{{ t(`library.sources.${menu.item.source}`) }} · {{ formatSize(menu.item.size) }}</p>
        </template>
      </div>

      <PromptDialog v-if="prompt" :title="prompt.title" :value="prompt.value" :ok="prompt.ok" @ok="runPrompt" @cancel="prompt = null" />
      <ConfirmDialog v-if="confirm" :title="confirm.title" :body="confirm.body" :ok="confirm.ok" danger @ok="runConfirm" @cancel="confirm = null" />
    </Teleport>
  </section>
</template>

<style scoped>
.library {
  position: relative;
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  background: var(--loom);
}
.head {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 14px 28px 10px;
  flex-wrap: wrap;
}
.crumbs {
  flex: 1;
  min-width: 160px;
  display: flex;
  align-items: center;
  gap: 6px;
}
h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.crumb {
  font-size: 24px;
  font-weight: 600;
  color: var(--ink-faint);
}
.crumb:hover {
  color: var(--ink);
}
.sep {
  color: var(--ink-faint);
}
.tools {
  display: flex;
  align-items: center;
  gap: 10px;
}
.icon-btn {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 50%;
  color: var(--ink-soft);
}
.icon-btn:hover,
.icon-btn.on {
  background: var(--chip);
  color: var(--ink);
}
.divider {
  width: 1px;
  height: 22px;
  background: var(--line-strong);
}
.seg {
  display: flex;
  gap: 2px;
}
.seg button {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 50%;
  color: var(--ink-soft);
}
.seg button.on {
  background: var(--chip);
  color: var(--ink);
}
.search {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 250px;
  height: 36px;
  padding: 0 12px;
  border: 1px solid var(--line-strong);
  border-radius: 18px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
}
.search:focus-within {
  border-color: var(--indigo);
}
.search input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--ink);
  font-size: var(--t-sm);
}
.clear {
  display: grid;
  place-items: center;
  color: var(--ink-faint);
}
.new-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 36px;
  padding: 0 14px 0 16px;
  border-radius: 18px;
  background: var(--action);
  color: var(--action-ink);
  font-size: var(--t-sm);
  font-weight: 500;
  transition: background 120ms;
}
.new-btn:hover {
  background: var(--action-hover);
}
.pop-title {
  margin: 6px 8px 4px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.pop-opt {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 7px 10px;
  border-radius: var(--r-sm);
  font-size: var(--t-sm);
  color: var(--ink);
  text-align: left;
  cursor: pointer;
}
.pop-opt > span {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}
.pop-opt.big {
  justify-content: flex-start;
  gap: 12px;
  padding: 9px 12px;
}
.pop-opt.switch-row {
  justify-content: space-between;
}
.pop-opt:hover {
  background: var(--chip);
}
.pop-hint {
  margin: 0 12px 6px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.5;
}
hr {
  margin: 6px 4px;
  border: 0;
  border-top: 1px solid var(--line);
}
.tabs {
  display: flex;
  gap: 6px;
  padding: 8px 28px 14px;
  overflow-x: auto;
}
.tabs button {
  height: 34px;
  padding: 0 16px;
  border-radius: 17px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 500;
  white-space: nowrap;
}
.tabs button:hover {
  color: var(--ink);
}
.tabs button.on {
  background: var(--chip);
  color: var(--ink);
}
.trash-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin: 0 28px 14px;
  padding: 10px 14px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.danger-outline {
  border: 1px solid var(--red);
  color: var(--red);
}
.content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 0 28px 110px;
}
.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  margin: 80px 0;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  text-align: center;
}
.empty small {
  font-size: var(--t-xs);
}

/* 网格：瀑布流，图片按原比例，其他文件是固定高度的卡片 */
.grid {
  column-width: 210px;
  column-gap: 16px;
}
.card {
  position: relative;
  display: flex;
  flex-direction: column;
  break-inside: avoid;
  margin-bottom: 16px;
  min-height: 200px;
  padding: 18px 18px 16px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: 16px;
  background: var(--cloth-sunk);
  cursor: pointer;
  transition: box-shadow 120ms, border-color 120ms;
}
.card.image {
  min-height: 0;
  padding: 0;
  background: var(--chip);
}
.card img {
  display: block;
  width: 100%;
  max-height: 420px;
  object-fit: cover;
}
.card:hover {
  border-color: var(--line-strong);
  box-shadow: var(--shadow-card);
}
.card.selected {
  border-color: var(--indigo);
  box-shadow: 0 0 0 2px var(--indigo);
}
.card.missing {
  opacity: 0.55;
}
.card-name {
  font-size: var(--t-md);
  font-weight: 500;
  line-height: 1.45;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  word-break: break-all;
}
/* 勾选框和“更多”按钮出现时，标题让开，不压字 */
.card-name {
  transition: padding 120ms;
}
.card:not(.image):hover .card-name,
.card:not(.image):focus-visible .card-name,
.grid:has(.selected) .card:not(.image) .card-name {
  padding-left: 26px;
  padding-right: 22px;
}
.card-ico {
  flex: 1;
  align-self: center;
  margin: 26px 0;
  color: var(--indigo);
}
.card.audio .card-ico,
.card.video .card-ico {
  color: #8b5cf6;
}
.card.sheet .card-ico {
  color: #16a34a;
}
.card.slides .card-ico {
  color: #ea580c;
}
.card.pdf .card-ico {
  color: var(--red);
}
.card.note .card-ico {
  color: var(--amber);
}
.card.code .card-ico,
.card.other .card-ico,
.card.archive .card-ico {
  color: var(--ink-soft);
}
.card-date {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.fav-mark {
  position: absolute;
  right: 12px;
  bottom: 12px;
  color: #f5b301;
  fill: #f5b301;
}
.card.image .fav-mark {
  filter: drop-shadow(0 1px 2px rgba(0, 0, 0, 0.5));
}
.check {
  position: absolute;
  top: 10px;
  left: 10px;
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border: 2px solid rgba(255, 255, 255, 0.9);
  border-radius: 50%;
  background: rgba(0, 0, 0, 0.25);
  color: transparent;
  opacity: 0;
  transition: opacity 120ms;
}
.card:not(.image) .check {
  border-color: var(--line-strong);
  background: var(--cloth);
}
.card:hover .check,
.card:focus-visible .check,
.library .card.selected .check,
.grid:has(.selected) .check {
  opacity: 1;
}
.check[aria-checked='true'] {
  border-color: var(--indigo) !important;
  background: var(--indigo) !important;
  color: #fff;
}
.check.inline {
  position: static;
  opacity: 1;
  border-color: var(--line-strong);
  background: var(--cloth);
}
.more-btn {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  color: var(--ink-soft);
}
.card .more-btn,
.folder .more-btn {
  position: absolute;
  top: 8px;
  right: 8px;
  opacity: 0;
  background: var(--cloth);
  box-shadow: var(--shadow-card);
}
.card:hover .more-btn,
.card:focus-visible .more-btn,
.folder:hover .more-btn {
  opacity: 1;
}
.more-btn:hover {
  color: var(--ink);
}

/* 文件夹 */
.folders {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 16px;
}
.folder {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  padding: 18px;
  border: 1px solid var(--line);
  border-radius: 16px;
  background: var(--cloth-sunk);
  text-align: left;
  cursor: pointer;
}
.folder:hover {
  border-color: var(--line-strong);
}
.folder.add {
  align-items: center;
  justify-content: center;
  border-style: dashed;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.folder-ico {
  color: var(--indigo);
}
.folder-name {
  max-width: 100%;
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.folder small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}

/* 列表 */
.rows {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--t-sm);
}
.rows th {
  padding: 8px 10px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-faint);
  font-weight: 500;
  text-align: left;
}
.rows td {
  padding: 8px 10px;
  border-bottom: 1px solid var(--line);
}
.rows tbody tr {
  cursor: pointer;
}
.rows tbody tr:hover {
  background: var(--cloth-sunk);
}
.rows tr.selected {
  background: var(--indigo-wash);
}
.rows tr.missing {
  opacity: 0.55;
}
.c-check,
.c-more {
  width: 40px;
}
.rows td,
.rows th {
  vertical-align: middle;
}
.c-name {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}
.c-name span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.mini-thumb {
  width: 28px;
  height: 28px;
  flex: none;
  border-radius: 6px;
  object-fit: cover;
}
.row-ico {
  flex: none;
  color: var(--indigo);
}
.fav-inline {
  flex: none;
  color: #f5b301;
  fill: #f5b301;
}
.c-src,
.c-size,
.c-date {
  color: var(--ink-soft);
  white-space: nowrap;
}

/* 多选操作条 */
.select-bar {
  position: absolute;
  left: 50%;
  bottom: 24px;
  z-index: 20;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px 10px 22px;
  border-radius: 30px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  border: 1px solid var(--line);
  transform: translateX(-50%);
  white-space: nowrap;
}
.count {
  margin-right: 14px;
  font-size: var(--t-sm);
  font-weight: 500;
}
.bar-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 36px;
  padding: 0 16px;
  border: 1px solid var(--line-strong);
  border-radius: 18px;
  font-size: var(--t-sm);
  color: var(--ink);
}
.bar-btn:hover {
  background: var(--chip);
}
.bar-btn.soft {
  border-color: transparent;
  background: var(--chip);
}
.bar-btn.danger {
  border-color: var(--red);
  color: var(--red);
}
.bar-btn.danger:hover {
  background: var(--red-wash);
}
.bar-icon {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  color: var(--ink-soft);
}
.bar-icon:hover {
  background: var(--chip);
  color: var(--ink);
}

/* 菜单 */
.lib-menu {
  position: fixed;
  z-index: 65;
  width: 228px;
  max-height: calc(100vh - 16px);
  overflow-y: auto;
  padding: 6px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.lib-menu button {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 8px 10px;
  border-radius: var(--r-sm);
  font-size: var(--t-sm);
  color: var(--ink);
  text-align: left;
}
.lib-menu button:hover:not(:disabled) {
  background: var(--chip);
}
.lib-menu button:disabled {
  opacity: 0.45;
}
.lib-menu .danger {
  color: var(--red);
}
.menu-title {
  margin: 4px 10px 6px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.menu-meta {
  margin: 6px 10px 2px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}

@media (max-width: 900px) {
  .search {
    width: 160px;
  }
  .head,
  .tabs,
  .content {
    padding-left: 16px;
    padding-right: 16px;
  }
  .c-src,
  .c-size {
    display: none;
  }
}
</style>
