<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowUp, Download, ExternalLink, FolderOpen, MoreHorizontal, Pencil, Save, Share2, Star, StarOff, Trash2, X } from '@lucide/vue'
import Popover from './Popover.vue'
import { bridge } from '../bridge'
import { renderMarkdown } from '../markdown'
import { useRichBlocks } from '../render/useRichBlocks'
import { chatAbout, download, formatSize, loadLibrary, removeItems, setFavorite, share } from '../library'
import { toast } from '../store'
import { kindIcon } from '../libraryIcons'
import type { LibraryItem, PreviewDoc } from '../types'

// 资料库里打开一个文件：在资料库页面里整页显示（不是弹窗），底部可以直接就这个文件提问
const props = defineProps<{ item: LibraryItem }>()
const emit = defineEmits<{ close: []; rename: [item: LibraryItem] }>()
const { t } = useI18n()

const doc = ref<PreviewDoc | null>(null)
const loading = ref(true)
const error = ref('')
const question = ref('')
const section = ref(0)
const body = ref<HTMLElement>()

/** 备注、Markdown、纯文本可以直接改（只限资料库自己保管的文件） */
const editable = computed(() => props.item.managed && ['note', 'document', 'code'].includes(props.item.kind) && /\.(md|markdown|txt)$/i.test(props.item.path))
const editing = ref(false)
const draft = ref('')
const saving = ref(false)

const lines = computed(() => (doc.value?.text ?? '').split('\n'))
const sections = computed(() => doc.value?.sections ?? [])
const current = computed(() => sections.value[section.value] ?? null)
const isMedia = computed(() => ['image', 'audio', 'video', 'pdf'].includes(props.item.kind))

async function load() {
  if (props.item.kind === 'image' || props.item.kind === 'audio' || props.item.kind === 'video' || props.item.kind === 'pdf') {
    loading.value = false
    return
  }
  loading.value = true
  error.value = ''
  try {
    doc.value = await bridge.libraryPreview(props.item.id)
    if (props.item.kind === 'note' && editable.value && !(doc.value.text ?? '').trim()) startEdit()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function startEdit() {
  draft.value = doc.value?.text ?? ''
  editing.value = true
}

async function save() {
  saving.value = true
  try {
    const ok = await bridge.libraryUpdateNote(props.item.id, draft.value)
    if (!ok) throw new Error(t('library.saveFailed'))
    if (doc.value) doc.value.text = draft.value
    else doc.value = { path: props.item.path, name: props.item.name, kind: 'markdown', text: draft.value, sections: [] }
    editing.value = false
    toast(t('library.saved'))
    void loadLibrary()
  } catch (e) {
    toast(e instanceof Error ? e.message : String(e))
  } finally {
    saving.value = false
  }
}

async function ask() {
  const q = question.value.trim()
  if (!q) return
  question.value = ''
  await chatAbout([props.item.id], q)
}

async function launch() {
  const r = await bridge.launchFile(props.item.path).catch(() => ({ ok: false, message: t('library.missing') }))
  if (!r.ok) toast(r.message)
}
async function reveal() {
  const r = await bridge.libraryReveal(props.item.id).catch(() => ({ ok: false, message: t('library.missing') }))
  if (!r.ok) toast(r.message)
}
async function remove() {
  await removeItems([props.item.id])
  emit('close')
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && !editing.value && !(e.target as HTMLElement).closest('input, textarea')) emit('close')
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's' && editing.value) {
    e.preventDefault()
    void save()
  }
}

useRichBlocks(body, () => [props.item.id, doc.value?.kind, doc.value?.text?.length, editing.value])
watch(() => props.item.id, load)
onMounted(() => {
  window.addEventListener('keydown', onKey)
  void load()
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <div class="viewer">
    <header>
      <button type="button" class="icon-btn" :aria-label="t('library.close')" @click="emit('close')"><X :size="20" /></button>
      <nav class="crumbs">
        <button type="button" class="crumb" @click="emit('close')">{{ t('library.title') }}</button>
        <span class="sep">/</span>
        <strong :title="item.name">{{ item.name }}</strong>
      </nav>
      <span class="meta">{{ formatSize(item.size) }}</span>
      <button v-if="editable && !editing" type="button" class="pill ghost" @click="startEdit"><Pencil :size="15" /> {{ t('library.edit') }}</button>
      <button v-if="editing" type="button" class="pill" :disabled="saving" @click="save"><Save :size="15" /> {{ t('library.save') }}</button>
      <button v-else type="button" class="pill" @click="share([item.id])"><Share2 :size="15" /> {{ t('library.share') }}</button>
      <Popover placement="down" align="end" :width="220">
        <template #trigger="{ toggle }">
          <button type="button" class="icon-btn" :aria-label="t('library.more')" @click="toggle"><MoreHorizontal :size="20" /></button>
        </template>
        <template #default="{ close }">
          <button type="button" class="opt" @click="(close(), download([item.id]))"><Download :size="16" /> {{ t('library.download') }}</button>
          <button type="button" class="opt" @click="(close(), setFavorite([item.id], !item.favorite))">
            <component :is="item.favorite ? StarOff : Star" :size="16" /> {{ item.favorite ? t('library.unfavorite') : t('library.favorite') }}
          </button>
          <button type="button" class="opt" @click="(close(), emit('rename', item))"><Pencil :size="16" /> {{ t('library.rename') }}</button>
          <button type="button" class="opt" @click="(close(), launch())"><ExternalLink :size="16" /> {{ t('library.openWith') }}</button>
          <button type="button" class="opt" @click="(close(), reveal())"><FolderOpen :size="16" /> {{ t('library.reveal') }}</button>
          <hr />
          <button type="button" class="opt danger" @click="(close(), remove())"><Trash2 :size="16" /> {{ t('library.delete') }}</button>
        </template>
      </Popover>
    </header>

    <nav v-if="sections.length > 1 && !editing" class="tabs">
      <button v-for="(s, i) in sections" :key="i" type="button" :class="{ on: i === section }" @click="section = i">{{ s.title }}</button>
    </nav>

    <div ref="body" class="body" :class="{ media: isMedia }">
      <p v-if="!item.exists" class="hint">{{ t('library.missing') }}</p>
      <p v-else-if="loading" class="hint">{{ t('ui.preview.loading') }}</p>

      <img v-else-if="item.kind === 'image'" :src="item.url" :alt="item.name" class="image" />
      <audio v-else-if="item.kind === 'audio'" :src="item.url" controls class="audio" />
      <video v-else-if="item.kind === 'video'" :src="item.url" controls class="video" />
      <iframe v-else-if="item.kind === 'pdf'" :src="item.url" class="pdf" :title="item.name" />

      <textarea v-else-if="editing" v-model="draft" class="editor" :placeholder="t('library.notePlaceholder')" spellcheck="false" />

      <template v-else-if="doc">
        <p v-if="doc.notice" class="notice">{{ doc.notice }}</p>
        <div v-if="doc.kind === 'markdown'" class="prose" v-html="renderMarkdown(doc.text ?? '')" />
        <div v-else-if="doc.kind === 'text' || doc.kind === 'diagram'" class="code">
          <div v-for="(line, i) in lines" :key="i" class="line"><span class="no">{{ i + 1 }}</span><span class="src">{{ line }}</span></div>
        </div>
        <article v-else-if="doc.kind === 'sections' && current" class="prose">
          <h3>{{ current.title }}</h3>
          <p class="pre">{{ current.text }}</p>
        </article>
        <div v-else-if="(doc.kind === 'table' || doc.kind === 'listing') && current?.rows?.length" class="table-wrap">
          <table>
            <tbody>
              <tr v-for="(row, ri) in current.rows" :key="ri">
                <td v-for="(cell, ci) in row" :key="ci" :class="{ head: ri === 0 }">{{ cell }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <div v-else class="fallback">
          <component :is="kindIcon[item.kind]" :size="44" />
          <p>{{ doc.error ?? t('ui.preview.unsupported') }}</p>
          <button type="button" class="pill" @click="launch"><ExternalLink :size="15" /> {{ t('library.openWith') }}</button>
        </div>
      </template>
      <p v-else class="hint">{{ error || t('ui.preview.unsupported') }}</p>
    </div>

    <form class="ask" @submit.prevent="ask">
      <input v-model="question" :placeholder="t('library.askPlaceholder')" maxlength="2000" />
      <button type="submit" class="send" :disabled="!question.trim()" :aria-label="t('library.startChat')"><ArrowUp :size="18" /></button>
    </form>
  </div>
</template>

<style scoped>
.viewer {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
header {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 20px;
  border-bottom: 1px solid var(--line);
}
.icon-btn {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 50%;
  color: var(--ink-soft);
}
.icon-btn:hover {
  background: var(--chip);
  color: var(--ink);
}
.crumbs {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: var(--t-sm);
}
.crumb {
  color: var(--ink-faint);
}
.crumb:hover {
  color: var(--ink);
}
.sep {
  color: var(--ink-faint);
}
.crumbs strong {
  overflow: hidden;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.meta {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 34px;
  padding: 0 16px;
  border-radius: 17px;
  background: var(--action);
  color: var(--action-ink);
  font-size: var(--t-sm);
  font-weight: 500;
}
.pill:hover:not(:disabled):not(.ghost),
.send:hover:not(:disabled) {
  background: var(--action-hover);
}
.pill.ghost {
  background: var(--chip);
  color: var(--ink);
}
.pill:disabled {
  opacity: 0.6;
}
.opt {
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
.opt:hover {
  background: var(--chip);
}
.opt.danger {
  color: var(--red);
}
hr {
  margin: 6px 4px;
  border: 0;
  border-top: 1px solid var(--line);
}
.tabs {
  display: flex;
  gap: 4px;
  padding: 8px 20px;
  overflow-x: auto;
  border-bottom: 1px solid var(--line);
}
.tabs button {
  padding: 4px 12px;
  border-radius: 14px;
  font-size: var(--t-sm);
  color: var(--ink-soft);
  white-space: nowrap;
}
.tabs button.on {
  background: var(--chip);
  color: var(--ink);
}
.body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 0 0 110px;
}
.body.media {
  display: grid;
  place-items: center;
  padding: 24px 24px 110px;
}
.hint {
  margin: 60px 0;
  color: var(--ink-faint);
  text-align: center;
}
.image {
  max-width: 100%;
  max-height: calc(100vh - 230px);
  border-radius: var(--r-md);
  object-fit: contain;
}
.video {
  max-width: 100%;
  max-height: calc(100vh - 230px);
  border-radius: var(--r-md);
}
.audio {
  width: min(560px, 100%);
}
.pdf {
  width: 100%;
  height: calc(100vh - 200px);
  border: 0;
  border-radius: var(--r-md);
  background: #fff;
}
.notice {
  margin: 12px 24px 0;
  padding: 8px 12px;
  border-radius: var(--r-sm);
  background: var(--amber-wash);
  color: var(--amber);
  font-size: var(--t-sm);
}
.prose {
  max-width: 900px;
  margin: 0 auto;
  padding: 24px 32px;
}
.pre {
  white-space: pre-wrap;
}
/* 代码和纯文本：带行号，和编辑器里看到的一样 */
.code {
  padding: 12px 0;
  font-family: var(--font-code);
  font-size: calc(13px * var(--font-scale));
  line-height: 1.7;
}
.line {
  display: flex;
}
.no {
  flex: none;
  width: 56px;
  padding-right: 18px;
  color: var(--ink-faint);
  text-align: right;
  user-select: none;
}
.src {
  flex: 1;
  min-width: 0;
  white-space: pre-wrap;
  word-break: break-all;
}
.table-wrap {
  padding: 16px 24px;
  overflow: auto;
}
table {
  border-collapse: collapse;
  font-size: var(--t-sm);
}
td {
  padding: 6px 10px;
  border: 1px solid var(--line);
  white-space: nowrap;
}
td.head {
  background: var(--cloth-sunk);
  font-weight: 600;
}
.fallback {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  margin: 80px 0;
  color: var(--ink-faint);
}
.editor {
  display: block;
  width: min(900px, 100%);
  height: calc(100% - 24px);
  min-height: 360px;
  margin: 12px auto 0;
  padding: 20px 24px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  color: var(--ink);
  font-family: var(--font-code);
  font-size: var(--t-md);
  line-height: 1.7;
  resize: none;
  outline: 0;
}
.editor:focus {
  border-color: var(--indigo);
}
.ask {
  position: absolute;
  left: 50%;
  bottom: 22px;
  display: flex;
  align-items: center;
  gap: 8px;
  width: min(680px, calc(100% - 40px));
  padding: 8px 8px 8px 22px;
  border: 1px solid var(--line-strong);
  border-radius: 28px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  transform: translateX(-50%);
}
.ask input {
  flex: 1;
  min-width: 0;
  height: 38px;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--ink);
  font-size: var(--t-md);
}
.send {
  display: grid;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 50%;
  background: var(--action);
  color: var(--action-ink);
}
.send:disabled {
  opacity: 0.35;
}
</style>
