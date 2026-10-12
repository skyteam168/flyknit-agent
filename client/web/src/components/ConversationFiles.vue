<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Download, Files } from '@lucide/vue'
import Popover from './Popover.vue'
import ConversationFilesDialog from './ConversationFilesDialog.vue'
import { closeFilesViewer, convFileCount, convFiles, extLabel, loadConversationFiles, outputFiles, uploadedFiles } from '../conversationFiles'
import { download } from '../library'
import { kindIcon } from '../libraryIcons'
import { current, currentState, state } from '../store'
import type { LibraryItem } from '../types'

// 顶栏右侧的「本对话的文件」：悬停或点击展开，按「输出」「上传的文件」两组列出，点某个文件弹窗预览
const { t } = useI18n()

const pop = ref<InstanceType<typeof Popover>>()
const open = ref(false)
/** 点击打开的不跟着鼠标移开自动收起，悬停打开的才收 */
const pinned = ref(false)
let hoverTimer: number | undefined

const groups = computed(() =>
  [
    { key: 'outputs', title: t('convFiles.outputs'), items: outputFiles.value },
    { key: 'uploads', title: t('convFiles.uploads'), items: uploadedFiles.value },
  ].filter((g) => g.items.length > 0),
)

function reload() {
  if (current.value && state.view === 'chat') void loadConversationFiles(current.value.id)
}

// 切换对话、发了新消息、一轮跑完时刷新；文件登记进资料库是异步的，宿主登记完会发 library.changed
let debounce: number | undefined
function reloadSoon() {
  clearTimeout(debounce)
  debounce = window.setTimeout(reload, 300)
}
watch(
  () => [state.currentId, state.view, current.value?.messageCount, currentState.value?.busy, currentState.value?.messages.length],
  (now, before) => {
    if (now[0] !== before?.[0]) {
      // 换了对话：先清掉上一个对话的，免得数字闪一下
      if (convFiles.conversationId !== now[0]) convFiles.items = []
      convFiles.viewing = null
      reload()
    } else reloadSoon()
  },
  { immediate: true },
)

function onLibraryChanged() {
  reloadSoon()
}
onMounted(() => window.addEventListener('flyknit:library-changed', onLibraryChanged))
onBeforeUnmount(() => {
  window.removeEventListener('flyknit:library-changed', onLibraryChanged)
  clearTimeout(hoverTimer)
  clearTimeout(debounce)
})

function onEnter() {
  clearTimeout(hoverTimer)
  if (!open.value) hoverTimer = window.setTimeout(() => pop.value?.show(), 350)
}
function onLeave() {
  clearTimeout(hoverTimer)
  if (open.value && !pinned.value) hoverTimer = window.setTimeout(() => pop.value?.close(), 300)
}
function onClick() {
  clearTimeout(hoverTimer)
  // 悬停已经展开了，再点一下是「钉住」，不是关掉
  if (open.value && !pinned.value) {
    pinned.value = true
    return
  }
  if (open.value) pop.value?.close()
  else {
    pinned.value = true
    pop.value?.show()
  }
}
function onChange(v: boolean) {
  open.value = v
  if (v) reload()
  else pinned.value = false
}

function view(group: { title: string; items: LibraryItem[] }, item: LibraryItem) {
  pop.value?.close()
  convFiles.viewing = { title: group.title, items: [...group.items], id: item.id }
}
</script>

<template>
  <div v-if="current && state.view === 'chat' && convFileCount > 0" class="conv-files" @mouseenter="onEnter" @mouseleave="onLeave">
    <Popover ref="pop" placement="down" align="end" :width="320" @change="onChange">
      <template #trigger>
        <button
          type="button"
          class="icon-btn files-btn"
          :class="{ active: open }"
          :title="t('convFiles.title')"
          :aria-label="t('convFiles.count', { n: convFileCount })"
          :aria-expanded="open"
          @click="onClick"
        >
          <Files :size="16" />
          <span class="count">{{ convFileCount }}</span>
        </button>
      </template>
      <div class="files-pop">
        <section v-for="g in groups" :key="g.key" class="group">
          <header>
            <h3>{{ g.title }}</h3>
            <button v-if="g.key === 'outputs' && g.items.length > 1" type="button" class="all" @click="download(g.items.map((i) => i.id))">
              <Download :size="13" /> {{ t('convFiles.downloadAll') }}
            </button>
          </header>
          <button v-for="item in g.items" :key="item.id" type="button" class="row" :class="{ gone: !item.exists }" :title="item.name" @click="view(g, item)">
            <component :is="kindIcon[item.kind]" :size="16" class="kind" />
            <span class="name">{{ item.name }}</span>
            <span class="ext">{{ extLabel(item) }}</span>
          </button>
        </section>
      </div>
    </Popover>

    <ConversationFilesDialog
      v-if="convFiles.viewing"
      :title="convFiles.viewing.title"
      :items="convFiles.viewing.items"
      :initial-id="convFiles.viewing.id"
      @close="closeFilesViewer"
    />
  </div>
</template>

<style scoped>
.conv-files {
  display: inline-flex;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.conv-files :deep(.panel) {
  max-height: min(440px, calc(100vh - 80px));
  padding: 4px 6px 6px;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
/* 很窄的窗口：按钮离左边太近，弹层贴着窗口两边放，不要跑出屏幕 */
@media (max-width: 520px) {
  .conv-files :deep(.panel) {
    position: fixed;
    top: 52px;
    right: 12px;
    left: 12px;
    width: auto !important;
  }
}
.files-btn {
  display: inline-flex;
  width: auto;
  gap: 5px;
  padding: 0 9px;
}
.count {
  font-size: var(--t-xs);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.group + .group {
  margin-top: 4px;
  padding-top: 4px;
  border-top: 1px solid var(--line);
}
.group header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 32px;
  padding: 0 4px 0 8px;
}
h3 {
  margin: 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-weight: 600;
}
.all {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 8px;
  border-radius: 12px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.all:hover {
  background: var(--chip);
  color: var(--ink);
}
.row {
  display: flex;
  align-items: center;
  gap: 9px;
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
.row.gone .name {
  color: var(--ink-faint);
  text-decoration: line-through;
}
.kind {
  flex: none;
  color: var(--ink-soft);
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
</style>
