<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import MarkdownIt from 'markdown-it'
import { ExternalLink, FolderOpen, X } from '@lucide/vue'
import { closePreview, launchFile, revealFile, setPreviewWidth, state } from '../store'

// 右侧分屏：按宿主给的 kind 渲染，不在这里二次判断扩展名
const { t } = useI18n()
const md = new MarkdownIt({ html: false, linkify: true, breaks: true })

const preview = computed(() => state.preview)
const doc = computed(() => state.preview?.doc ?? null)
const activeSection = ref(0)

watch(
  () => state.preview?.file.path,
  () => {
    activeSection.value = 0
  },
)

const sections = computed(() => doc.value?.sections ?? [])
const current = computed(() => sections.value[activeSection.value] ?? null)

// ---------- 拖拽调整宽度 ----------
const dragging = ref(false)

function startDrag(e: PointerEvent) {
  dragging.value = true
  const target = e.currentTarget as HTMLElement
  target.setPointerCapture(e.pointerId)
  e.preventDefault()
}

function onDrag(e: PointerEvent) {
  if (!dragging.value) return
  // 分屏在右侧，所以宽度 = 窗口右边缘到鼠标的距离
  setPreviewWidth(window.innerWidth - e.clientX)
}

function endDrag(e: PointerEvent) {
  dragging.value = false
  ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
}

function onKeyResize(e: KeyboardEvent) {
  if (e.key === 'ArrowLeft') setPreviewWidth(state.previewWidth + 32)
  else if (e.key === 'ArrowRight') setPreviewWidth(state.previewWidth - 32)
}
</script>

<template>
  <aside v-if="preview" class="preview" :style="{ width: state.previewWidth + 'px' }">
    <div
      class="grip"
      :class="{ dragging }"
      role="separator"
      aria-orientation="vertical"
      tabindex="0"
      :aria-label="t('ui.preview.resize')"
      @pointerdown="startDrag"
      @pointermove="onDrag"
      @pointerup="endDrag"
      @pointercancel="endDrag"
      @keydown="onKeyResize"
    />

    <header>
      <span class="name" :title="preview.file.path">{{ preview.file.name }}</span>
      <button type="button" class="ico" :title="t('ui.outputs.openWithDefault')" @click="launchFile(preview.file)">
        <ExternalLink :size="15" />
      </button>
      <button type="button" class="ico" :title="t('ui.outputs.reveal')" @click="revealFile(preview.file)">
        <FolderOpen :size="15" />
      </button>
      <button type="button" class="ico" :title="t('ui.preview.close')" @click="closePreview()">
        <X :size="15" />
      </button>
    </header>

    <nav v-if="sections.length > 1" class="tabs">
      <button
        v-for="(s, i) in sections"
        :key="i"
        type="button"
        :class="{ on: i === activeSection }"
        @click="activeSection = i"
      >
        {{ s.title }}
      </button>
    </nav>

    <div class="body">
      <p v-if="preview.loading" class="hint">{{ t('ui.preview.loading') }}</p>

      <template v-else-if="doc">
        <p v-if="doc.notice" class="notice">{{ doc.notice }}</p>

        <img v-if="doc.kind === 'image'" :src="doc.dataUrl ?? ''" :alt="preview.file.name" class="image" />

        <iframe v-else-if="doc.kind === 'pdf'" :src="doc.dataUrl ?? ''" class="pdf" :title="preview.file.name" />

        <div v-else-if="doc.kind === 'markdown'" class="prose" v-html="md.render(doc.text ?? '')" />

        <pre v-else-if="doc.kind === 'text'" class="code"><code>{{ doc.text }}</code></pre>

        <pre v-else-if="doc.kind === 'diagram'" class="code"><code>{{ doc.text }}</code></pre>

        <div v-else-if="doc.kind === 'sections'" class="sections">
          <article v-if="current">
            <h3>{{ current.title }}</h3>
            <p v-if="current.text" class="text">{{ current.text }}</p>
            <p v-else class="hint">{{ t('ui.preview.emptySection') }}</p>
          </article>
        </div>

        <div v-else-if="doc.kind === 'table' || doc.kind === 'listing'" class="tableWrap">
          <table v-if="current?.rows?.length">
            <tbody>
              <tr v-for="(row, ri) in current.rows" :key="ri">
                <td v-for="(cell, ci) in row" :key="ci" :class="{ head: ri === 0 }">{{ cell }}</td>
              </tr>
            </tbody>
          </table>
          <p v-else class="hint">{{ t('ui.preview.emptySection') }}</p>
        </div>

        <div v-else class="fallback">
          <p>{{ doc.error ?? t('ui.preview.unsupported') }}</p>
          <button type="button" class="open" @click="launchFile(preview.file)">
            <ExternalLink :size="15" /> {{ t('ui.outputs.openWithDefault') }}
          </button>
        </div>
      </template>

      <p v-else class="hint">{{ preview.error || t('ui.preview.unsupported') }}</p>
    </div>
  </aside>
</template>

<style scoped>
.preview {
  position: relative;
  display: flex;
  flex: none;
  flex-direction: column;
  min-width: 0;
  border-left: 1px solid var(--line);
  background: var(--cloth);
}
.grip {
  position: absolute;
  top: 0;
  left: -3px;
  bottom: 0;
  z-index: 5;
  width: 7px;
  cursor: col-resize;
  touch-action: none;
}
.grip:hover::after,
.grip.dragging::after,
.grip:focus-visible::after {
  position: absolute;
  top: 0;
  bottom: 0;
  left: 3px;
  width: 1px;
  background: var(--accent);
  content: '';
}
header {
  display: flex;
  gap: 4px;
  align-items: center;
  padding: 10px 12px;
  border-bottom: 1px solid var(--line);
}
.name {
  flex: 1;
  overflow: hidden;
  font-size: 13.5px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ico {
  display: grid;
  flex: none;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.ico:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
  color: var(--ink);
}
.tabs {
  display: flex;
  gap: 4px;
  overflow-x: auto;
  padding: 8px 10px;
  border-bottom: 1px solid var(--line);
}
.tabs button {
  flex: none;
  max-width: 180px;
  overflow: hidden;
  padding: 4px 10px;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: var(--ink-soft);
  font-size: 12.5px;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}
.tabs button.on {
  border-color: var(--line);
  background: var(--cloth-soft, var(--paper, var(--cloth)));
  color: var(--ink);
}
.body {
  flex: 1;
  overflow: auto;
  padding: 14px;
}
.hint,
.notice {
  color: var(--ink-soft);
  font-size: 13px;
}
.notice {
  margin-bottom: 10px;
  padding: 6px 10px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--ink) 5%, transparent);
}
.image {
  max-width: 100%;
  border-radius: 8px;
}
.pdf {
  width: 100%;
  height: calc(100vh - 160px);
  border: 0;
  border-radius: 8px;
}
.code {
  margin: 0;
  overflow-x: auto;
  font-size: 12.5px;
  line-height: 1.65;
  white-space: pre;
}
.prose {
  font-size: 14px;
  line-height: 1.75;
}
.prose :deep(h1),
.prose :deep(h2),
.prose :deep(h3) {
  margin: 1.1em 0 0.5em;
}
.prose :deep(table) {
  border-collapse: collapse;
}
.prose :deep(td),
.prose :deep(th) {
  padding: 4px 8px;
  border: 1px solid var(--line);
}
.sections .text {
  font-size: 14px;
  line-height: 1.8;
  white-space: pre-wrap;
}
.sections h3 {
  margin: 0 0 10px;
  font-size: 15px;
}
.tableWrap {
  overflow: auto;
}
table {
  border-collapse: collapse;
  font-size: 12.5px;
}
td {
  max-width: 320px;
  overflow: hidden;
  padding: 5px 9px;
  border: 1px solid var(--line);
  text-overflow: ellipsis;
  white-space: nowrap;
}
td.head {
  position: sticky;
  top: 0;
  background: var(--cloth-soft, var(--cloth));
  font-weight: 600;
}
.fallback {
  display: flex;
  flex-direction: column;
  gap: 12px;
  align-items: flex-start;
  color: var(--ink-soft);
  font-size: 13px;
}
.open {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  padding: 7px 14px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: inherit;
  cursor: pointer;
}
.open:hover {
  border-color: var(--accent);
  color: var(--accent);
}
</style>
