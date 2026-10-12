<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowLeft, Download, X } from '@lucide/vue'
import LibraryViewer from './LibraryViewer.vue'
import { download } from '../library'
import { kindIcon } from '../libraryIcons'
import { extLabel } from '../conversationFiles'
import type { LibraryItem } from '../types'

// 本对话的文件：左边是同一组（输出 / 上传的文件）的列表，右边预览选中的那个，像文件浏览器一样一个个点着看。
// 窄窗口放不下两栏：先显示列表，点了某个文件再整屏预览，左上角返回列表
const props = defineProps<{ title: string; items: LibraryItem[]; initialId: string }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()

const dialog = ref<HTMLElement>()
const selectedId = ref(props.initialId)
const selected = computed(() => props.items.find((i) => i.id === selectedId.value) ?? props.items[0] ?? null)

const media = window.matchMedia('(max-width: 719px)')
const narrow = ref(media.matches)
const onMedia = () => (narrow.value = media.matches)
/** 窄窗口下是否在看列表（宽窗口两栏都显示，不用这个） */
const listing = ref(false)

function pick(item: LibraryItem) {
  selectedId.value = item.id
  listing.value = false
}

/** 列表里上下键切换文件，和文件浏览器一样 */
function onListKey(e: KeyboardEvent) {
  if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
  e.preventDefault()
  const i = props.items.findIndex((x) => x.id === selected.value?.id)
  const next = props.items[Math.min(props.items.length - 1, Math.max(0, i + (e.key === 'ArrowDown' ? 1 : -1)))]
  if (next) {
    selectedId.value = next.id
    document.getElementById(`cf-${next.id}`)?.focus()
  }
}

onMounted(async () => {
  media.addEventListener('change', onMedia)
  // 焦点从输入框挪到弹窗里：Esc 关弹窗（而不是被输入框当成「停止」），上下键直接切文件
  await nextTick()
  const row = selected.value ? document.getElementById(`cf-${selected.value.id}`) : null
  if (row && row.offsetParent) row.focus()
  else dialog.value?.focus()
})
onBeforeUnmount(() => media.removeEventListener('change', onMedia))
</script>

<template>
  <Teleport to="body">
    <div class="scrim" @mousedown.self="emit('close')">
      <div ref="dialog" class="dialog" tabindex="-1" :class="{ narrow, listing: narrow && listing }" role="dialog" aria-modal="true" :aria-label="title">
        <aside class="list">
          <header>
            <h2>{{ title }}</h2>
            <span class="n">{{ items.length }}</span>
            <button
              v-if="items.length > 1"
              type="button"
              class="icon-btn"
              :title="t('convFiles.downloadAll')"
              @click="download(items.map((i) => i.id))"
            >
              <Download :size="16" />
            </button>
            <button v-if="narrow" type="button" class="icon-btn" :aria-label="t('library.close')" @click="emit('close')"><X :size="18" /></button>
          </header>
          <div class="rows" role="listbox" @keydown="onListKey">
            <button
              v-for="item in items"
              :id="`cf-${item.id}`"
              :key="item.id"
              type="button"
              class="row"
              role="option"
              :aria-selected="item.id === selected?.id"
              :class="{ on: item.id === selected?.id, gone: !item.exists }"
              :title="item.name"
              @click="pick(item)"
            >
              <component :is="kindIcon[item.kind]" :size="16" class="kind" />
              <span class="name">{{ item.name }}</span>
              <span class="ext">{{ extLabel(item) }}</span>
            </button>
          </div>
        </aside>

        <LibraryViewer v-if="selected" :key="selected.id" class="preview" :item="selected" embedded :crumb="title" @close="emit('close')">
          <template v-if="narrow" #lead>
            <button type="button" class="icon-btn back" :aria-label="t('convFiles.back')" :title="t('convFiles.back')" @click="listing = true">
              <ArrowLeft :size="18" />
            </button>
          </template>
        </LibraryViewer>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.dialog {
  display: flex;
  width: min(1120px, 100%);
  height: min(760px, calc(100vh - 32px));
  overflow: hidden;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  outline: 0;
  animation: rise 160ms ease-out;
}
.list {
  flex: none;
  display: flex;
  flex-direction: column;
  width: 260px;
  border-right: 1px solid var(--line);
  background: var(--cloth-sunk);
}
.list header {
  display: flex;
  align-items: center;
  gap: 6px;
  height: 55px;
  padding: 0 8px 0 16px;
  border-bottom: 1px solid var(--line);
}
.list h2 {
  margin: 0;
  font-size: var(--t-md);
  font-weight: 600;
}
.n {
  flex: 1;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.rows {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 6px;
}
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  height: 34px;
  padding: 0 8px;
  border-radius: var(--r-sm);
  color: var(--ink);
  font-size: var(--t-sm);
  text-align: left;
}
.row:hover {
  background: var(--chip);
}
.row.on {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.row.gone .name {
  color: var(--ink-faint);
  text-decoration: line-through;
}
.kind {
  flex: none;
  color: var(--ink-soft);
}
.row.on .kind {
  color: var(--indigo);
}
.name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ext {
  flex: none;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  font-weight: 500;
  letter-spacing: 0.02em;
}
.preview {
  min-width: 0;
}
/* 窄窗口：列表和预览二选一 */
.dialog.narrow .list {
  display: none;
}
.dialog.narrow.listing .list {
  display: flex;
  flex: 1;
  width: auto;
  min-width: 0;
  border-right: 0;
}
.dialog.narrow.listing .preview {
  display: none;
}
@media (max-width: 719px) {
  .scrim {
    padding: 8px;
  }
  .dialog {
    height: calc(100vh - 16px);
  }
}
@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(6px) scale(0.99);
  }
}
</style>
