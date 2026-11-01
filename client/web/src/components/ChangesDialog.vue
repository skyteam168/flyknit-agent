<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { CircleAlert, ExternalLink, FileMinus, FilePen, FilePlus, Loader2, MessageSquarePlus, RotateCcw, Send, Trash2, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { fail, interject, loadChanges, openPath, send, state, toast } from '../store'
import { diffLines, diffTable, withContext } from '../diff'
import type { LineOp, TableRowDiff } from '../diff'
import type { FileChangeInfo, PreviewDoc } from '../types'
import ConfirmDialog from './ConfirmDialog.vue'

const { t } = useI18n()
const view = computed(() => state.changesView!)
const turn = computed(() => (state.changes[view.value.conversationId] ?? []).find((x) => x.turnId === view.value.turnId) ?? null)
const files = computed(() => turn.value?.files ?? [])
const selected = ref<string | null>(null)
const picked = ref(new Set<string>())
const confirming = ref<'all' | 'picked' | null>(null)
const reverting = ref(false)
const skipped = ref<{ path: string; reason: string }[]>([])

const current = computed(() => files.value.find((f) => f.path === selected.value) ?? null)
const restorable = computed(() => files.value.filter((f) => f.restorable && !f.reverted))

function close() {
  state.changesView = null
}
function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && !confirming.value) {
    e.stopPropagation()
    close()
  }
}
onMounted(() => {
  window.addEventListener('keydown', onKey, true)
  selected.value = files.value.find((f) => f.kind === 'modified')?.path ?? files.value[0]?.path ?? null
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKey, true))

const kindIcon = { created: FilePlus, modified: FilePen, deleted: FileMinus }

function togglePick(f: FileChangeInfo) {
  const next = new Set(picked.value)
  if (next.has(f.path)) next.delete(f.path)
  else next.add(f.path)
  picked.value = next
}

async function revert(which: 'all' | 'picked') {
  confirming.value = null
  reverting.value = true
  try {
    const paths = which === 'picked' ? [...picked.value] : undefined
    const r = await bridge.revertChanges(view.value.conversationId, view.value.turnId, paths)
    skipped.value = r.skipped
    picked.value = new Set()
    toast(t('ui.changes.revertedToast', { n: r.reverted.length }))
    await loadChanges(view.value.conversationId)
    loadCompare()
  } catch (e) {
    fail(e)
  } finally {
    reverting.value = false
  }
}

// ---------- 对比 ----------

type Compare =
  | { mode: 'lines'; ops: ReturnType<typeof withContext>; added: number; removed: number }
  | { mode: 'table'; sheets: { title: string; rows: TableRowDiff[]; changed: number }[] }
  | { mode: 'none'; reason: string }

const compare = ref<Compare | null>(null)
const comparing = ref(false)

const textKinds = new Set(['text', 'markdown', 'html', 'diagram'])

async function preview(path: string | null | undefined): Promise<PreviewDoc | null> {
  if (!path) return null
  try {
    return await bridge.previewFile(path)
  } catch {
    return null
  }
}

/** Word、PPT 的「分节」按节拼成文本再逐行比 */
function asText(doc: PreviewDoc | null): string[] | null {
  if (!doc || doc.error) return null
  if (textKinds.has(doc.kind)) return (doc.text ?? '').split(/\r?\n/)
  if (doc.kind === 'sections') return doc.sections.flatMap((s) => [`## ${s.title}`, ...(s.text ?? '').split(/\r?\n/)])
  return null
}

async function loadCompare() {
  const f = current.value
  compare.value = null
  if (!f) return
  comparing.value = true
  try {
    const before = f.kind === 'created' ? null : f.emptyBefore ? ({ kind: 'text', text: '', sections: [] } as unknown as PreviewDoc) : await preview(f.backup)
    const after = f.kind === 'deleted' || !f.exists ? null : await preview(f.path)
    if (f.kind !== 'created' && !before) {
      compare.value = { mode: 'none', reason: t('ui.changes.noBackup') }
      return
    }
    const kind = (after ?? before)!.kind
    if (kind === 'table') {
      const sheets = new Map<string, { b: string[][]; a: string[][] }>()
      for (const s of before?.sections ?? []) sheets.set(s.title, { b: s.rows ?? [], a: [] })
      for (const s of after?.sections ?? []) sheets.set(s.title, { b: sheets.get(s.title)?.b ?? [], a: s.rows ?? [] })
      compare.value = {
        mode: 'table',
        sheets: [...sheets].map(([title, x]) => {
          const rows = diffTable(x.b, x.a)
          return { title, rows, changed: rows.filter((r) => r.type !== 'same').length }
        }),
      }
      return
    }
    const a = asText(after) ?? (f.kind === 'deleted' ? [] : null)
    const b = asText(before) ?? (f.kind === 'created' ? [] : null)
    if (!a || !b) {
      compare.value = { mode: 'none', reason: t('ui.changes.cannotCompare') }
      return
    }
    const ops = diffLines(b, a)
    compare.value = {
      mode: 'lines',
      ops: withContext(ops, 3),
      added: ops.filter((o) => o.type === 'add').length,
      removed: ops.filter((o) => o.type === 'del').length,
    }
  } finally {
    comparing.value = false
  }
}
watch(selected, loadCompare)

// ---------- 意见 ----------

interface Note {
  file: string
  where: string
  quote: string
  text: string
}
const notes = ref<Note[]>([])
const editing = ref<{ key: string; where: string; quote: string } | null>(null)
const draft = ref('')

function startNote(key: string, where: string, quote: string) {
  editing.value = { key, where, quote }
  draft.value = ''
}
function saveNote() {
  const e = editing.value
  if (!e || !draft.value.trim() || !current.value) return
  notes.value.push({ file: current.value.name, where: e.where, quote: e.quote.slice(0, 80), text: draft.value.trim() })
  editing.value = null
}
function lineWhere(op: LineOp) {
  return op.type === 'del' ? t('ui.changes.oldLine', { n: op.a }) : t('ui.changes.line', { n: op.b })
}
function colName(c: number) {
  let s = ''
  for (let n = c + 1; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + ((n - 1) % 26)) + s
  return s
}

/** 把意见整理成一句话交给 AI：这一轮在跑就当插话，没在跑就照常发 */
async function sendNotes() {
  if (!notes.value.length) return
  const byFile = new Map<string, Note[]>()
  for (const n of notes.value) byFile.set(n.file, [...(byFile.get(n.file) ?? []), n])
  const body = [
    t('ui.changes.notesIntro'),
    ...[...byFile].map(([file, list]) => [`\n【${file}】`, ...list.map((n) => `- ${n.where}${n.quote ? `「${n.quote}」` : ''}：${n.text}`)].join('\n')),
  ].join('\n')
  const id = view.value.conversationId
  if (state.currentId !== id) return
  const ok = state.byId[id]?.busy ? await interject(body, 'queue') : await send(body)
  if (ok) {
    notes.value = []
    close()
  }
}
</script>

<template>
  <div class="scrim" @mousedown.self="close">
    <section class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.changes.title')">
      <header>
        <div class="titles">
          <h2>{{ t('ui.changes.title') }}</h2>
          <p>{{ t('ui.changes.subtitle', { n: files.length }) }}</p>
        </div>
        <button type="button" class="icon" :aria-label="t('ui.paste.close')" @click="close"><X :size="17" /></button>
      </header>

      <div class="body">
        <!-- 文件列表 -->
        <aside class="files">
          <div v-for="f in files" :key="f.path" class="file" :class="{ on: f.path === selected, reverted: f.reverted }" @click="selected = f.path">
            <input
              v-if="f.restorable && !f.reverted"
              type="checkbox"
              :checked="picked.has(f.path)"
              :aria-label="t('ui.changes.pick')"
              @click.stop="togglePick(f)"
            />
            <span v-else class="box-gap" />
            <component :is="kindIcon[f.kind]" :size="15" class="kind" :class="f.kind" />
            <div class="fname">
              <span class="name" :title="f.path">{{ f.name }}</span>
              <span class="tags">
                <span class="tag" :class="f.kind">{{ t(`ui.changes.kind.${f.kind}`) }}</span>
                <span v-if="f.times > 1" class="tag">{{ t('ui.changes.times', { n: f.times }) }}</span>
                <span v-if="f.reverted" class="tag ok">{{ t('ui.changes.reverted') }}</span>
                <span v-else-if="!f.restorable" class="tag warn" :title="t('ui.changes.notRestorable')">{{ t('ui.changes.manual') }}</span>
              </span>
            </div>
          </div>
          <div v-if="skipped.length" class="skipped">
            <p><CircleAlert :size="14" /> {{ t('ui.changes.skipped') }}</p>
            <p v-for="s in skipped" :key="s.path" class="why"><b>{{ s.path.split(/[\\/]/).pop() }}</b>：{{ s.reason }}</p>
          </div>
        </aside>

        <!-- 对比 -->
        <div class="compare">
          <div v-if="current" class="compare-head">
            <span class="path" :title="current.path">{{ current.path }}</span>
            <template v-if="compare?.mode === 'lines'">
              <span class="plus">+{{ compare.added }}</span>
              <span class="minus">−{{ compare.removed }}</span>
            </template>
            <button v-if="current.exists" type="button" class="link" @click="openPath(current.path)"><ExternalLink :size="13" /> {{ t('ui.changes.open') }}</button>
            <button v-if="current.backup" type="button" class="link" @click="openPath(current.backup!)"><ExternalLink :size="13" /> {{ t('ui.changes.openBefore') }}</button>
          </div>
          <div v-if="comparing" class="state"><Loader2 :size="16" class="spin" /> {{ t('ui.changes.loading') }}</div>
          <p v-else-if="!current" class="state">{{ t('ui.changes.pickFile') }}</p>
          <p v-else-if="compare?.mode === 'none'" class="state">{{ compare.reason }}</p>

          <!-- 文本：逐行，点一行写意见 -->
          <div v-else-if="compare?.mode === 'lines'" class="lines">
            <p class="hint">{{ t('ui.changes.noteHint') }}</p>
            <template v-for="(op, k) in compare.ops" :key="k">
              <div v-if="op.type === 'gap'" class="gap">{{ t('ui.changes.gap', { n: op.count }) }}</div>
              <template v-else>
                <div class="line" :class="op.type" @click="startNote('l' + k, lineWhere(op), op.text)">
                  <span class="no">{{ op.type === 'add' ? '' : op.a }}</span>
                  <span class="no">{{ op.type === 'del' ? '' : op.b }}</span>
                  <span class="sign">{{ op.type === 'add' ? '+' : op.type === 'del' ? '−' : ' ' }}</span>
                  <span class="txt">{{ op.text || ' ' }}</span>
                  <MessageSquarePlus :size="13" class="add-note" />
                </div>
                <div v-if="editing?.key === 'l' + k" class="note-edit" @click.stop>
                  <input v-model="draft" :placeholder="t('ui.changes.notePlaceholder')" @keydown.enter.prevent="saveNote" @keydown.esc.stop="editing = null" />
                  <button type="button" class="btn primary" :disabled="!draft.trim()" @click="saveNote">{{ t('ui.changes.addNote') }}</button>
                </div>
              </template>
            </template>
          </div>

          <!-- 表格：只列有变化的行，改了的格子标出来，悬停看原值 -->
          <div v-else-if="compare?.mode === 'table'" class="sheets">
            <p class="hint">{{ t('ui.changes.cellHint') }}</p>
            <section v-for="s in compare.sheets" :key="s.title" class="sheet">
              <h4>{{ s.title }} <span class="count">{{ t('ui.changes.rowsChanged', { n: s.changed }) }}</span></h4>
              <p v-if="!s.changed" class="state small">{{ t('ui.changes.sheetSame') }}</p>
              <table v-else>
                <tbody>
                  <template v-for="(r, k) in s.rows.filter((x) => x.type !== 'same')" :key="k">
                    <tr :class="r.type">
                      <th>{{ r.type === 'del' ? '−' : r.row }}</th>
                      <td
                        v-for="(c, ci) in r.cells"
                        :key="ci"
                        :class="{ changed: c.changed && r.type === 'changed' }"
                        :title="c.changed && r.type === 'changed' ? t('ui.changes.was', { v: c.before || t('ui.changes.empty') }) : undefined"
                        @click="startNote(`${s.title}-${k}-${ci}`, `${s.title}!${colName(ci)}${r.row}`, c.text)"
                      >
                        <span v-if="c.changed && r.type === 'changed'" class="old">{{ c.before }}</span>{{ c.text }}
                      </td>
                    </tr>
                    <tr v-if="editing?.key.startsWith(`${s.title}-${k}-`)" class="note-row">
                      <td :colspan="r.cells.length + 1" @click.stop>
                        <div class="note-edit">
                          <span class="where">{{ editing.where }}</span>
                          <input v-model="draft" :placeholder="t('ui.changes.notePlaceholder')" @keydown.enter.prevent="saveNote" @keydown.esc.stop="editing = null" />
                          <button type="button" class="btn primary" :disabled="!draft.trim()" @click="saveNote">{{ t('ui.changes.addNote') }}</button>
                        </div>
                      </td>
                    </tr>
                  </template>
                </tbody>
              </table>
            </section>
          </div>
        </div>
      </div>

      <!-- 写下的意见 -->
      <div v-if="notes.length" class="notes">
        <div v-for="(n, i) in notes" :key="i" class="note">
          <span class="where">{{ n.file }} · {{ n.where }}</span>
          <span class="ntext">{{ n.text }}</span>
          <button type="button" class="icon small" :aria-label="t('menu.delete')" @click="notes.splice(i, 1)"><Trash2 :size="13" /></button>
        </div>
      </div>

      <footer>
        <span class="foot-hint">{{ t('ui.changes.footHint') }}</span>
        <button v-if="notes.length" type="button" class="btn primary" @click="sendNotes"><Send :size="14" /> {{ t('ui.changes.sendNotes', { n: notes.length }) }}</button>
        <button type="button" class="btn" :disabled="!picked.size || reverting" @click="confirming = 'picked'">
          <RotateCcw :size="14" /> {{ t('ui.changes.revertPicked', { n: picked.size }) }}
        </button>
        <button type="button" class="btn undo-all" :disabled="!restorable.length || reverting" @click="confirming = 'all'">
          <Loader2 v-if="reverting" :size="14" class="spin" />
          <RotateCcw v-else :size="14" /> {{ t('ui.changes.revertAll') }}
        </button>
      </footer>
    </section>
    <ConfirmDialog
      v-if="confirming"
      :title="t('ui.changes.confirmTitle')"
      :body="t('ui.changes.confirmBody', { n: confirming === 'all' ? restorable.length : picked.size })"
      :ok="t('ui.changes.revertOk')"
      danger
      @ok="revert(confirming!)"
      @cancel="confirming = null"
    />
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(1080px, 100%);
  height: min(760px, 90vh);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  overflow: hidden;
}
header {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 16px 16px 12px 20px;
  border-bottom: 1px solid var(--line);
}
.titles {
  flex: 1;
  min-width: 0;
}
h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
}
.titles p {
  margin: 4px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.icon {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: var(--r-sm);
  color: var(--ink-soft);
}
.icon:hover {
  background: var(--cloth-sunk);
}
.icon.small {
  width: 24px;
  height: 24px;
}
.body {
  flex: 1;
  min-height: 0;
  display: flex;
}
.files {
  width: 280px;
  flex: none;
  overflow-y: auto;
  padding: 8px;
  border-right: 1px solid var(--line);
  background: var(--loom);
}
.file {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px;
  border-radius: var(--r-sm);
  cursor: pointer;
}
.file:hover {
  background: var(--cloth-sunk);
}
.file.on {
  background: var(--cloth);
  box-shadow: inset 0 0 0 1px var(--line);
}
.file.reverted .name {
  color: var(--ink-faint);
  text-decoration: line-through;
}
.file input {
  margin-top: 2px;
}
.box-gap {
  width: 13px;
  flex: none;
}
.kind {
  flex: none;
  margin-top: 1px;
}
.kind.created {
  color: var(--thread);
}
.kind.modified {
  color: var(--indigo);
}
.kind.deleted {
  color: var(--red);
}
.fname {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.name {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  font-size: var(--t-sm);
}
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.tag {
  padding: 0 6px;
  border-radius: 999px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.tag.created {
  background: var(--thread-wash);
  color: var(--thread);
}
.tag.deleted {
  background: var(--red-wash);
  color: var(--red);
}
.tag.ok {
  background: var(--thread-wash);
  color: var(--thread);
}
.tag.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
.skipped {
  margin-top: 10px;
  padding: 8px;
  border-radius: var(--r-sm);
  background: var(--amber-wash);
  font-size: var(--t-xs);
}
.skipped p {
  display: flex;
  align-items: center;
  gap: 4px;
  margin: 0 0 4px;
}
.skipped .why {
  display: block;
  color: var(--ink-soft);
}
.compare {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  overflow: auto;
}
.compare-head {
  position: sticky;
  top: 0;
  z-index: 1;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 14px;
  border-bottom: 1px solid var(--line);
  background: var(--cloth);
  font-size: var(--t-sm);
}
.path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--ink-soft);
  font-family: var(--font-code);
  font-size: var(--t-xs);
}
.plus {
  color: var(--thread);
}
.minus {
  color: var(--red);
}
.link {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  color: var(--indigo);
  font-size: var(--t-xs);
}
.state {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 24px;
  color: var(--ink-faint);
}
.state.small {
  margin: 6px 0;
  font-size: var(--t-sm);
}
.hint {
  margin: 8px 14px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.lines {
  font-family: var(--font-code);
  font-size: 12.5px;
}
.line {
  display: flex;
  align-items: baseline;
  gap: 8px;
  padding: 0 14px;
  cursor: pointer;
}
.line:hover {
  background: var(--cloth-sunk);
}
.line.add {
  background: var(--thread-wash);
}
.line.del {
  background: var(--red-wash);
}
.no {
  width: 34px;
  flex: none;
  color: var(--ink-faint);
  text-align: right;
}
.sign {
  width: 10px;
  flex: none;
  color: var(--ink-faint);
}
.txt {
  flex: 1;
  white-space: pre-wrap;
  word-break: break-all;
}
.add-note {
  flex: none;
  opacity: 0;
  color: var(--indigo);
}
.line:hover .add-note {
  opacity: 1;
}
.gap {
  padding: 2px 14px 2px 112px;
  background: var(--loom);
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.note-edit {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 14px;
  background: var(--indigo-wash);
  font-family: var(--font);
}
.note-edit input {
  flex: 1;
  min-width: 0;
  padding: 5px 8px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
}
.where {
  flex: none;
  color: var(--indigo);
  font-size: var(--t-xs);
}
.sheets {
  padding-bottom: 12px;
}
.sheet {
  padding: 0 14px;
}
.sheet h4 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 10px 0 6px;
  font-size: var(--t-sm);
}
.count {
  color: var(--ink-faint);
  font-weight: 400;
}
table {
  border-collapse: collapse;
  font-size: var(--t-sm);
}
th,
td {
  padding: 4px 8px;
  border: 1px solid var(--line);
  white-space: nowrap;
}
th {
  background: var(--loom);
  color: var(--ink-faint);
  font-weight: 400;
}
td {
  cursor: pointer;
}
td:hover {
  outline: 2px solid var(--indigo);
  outline-offset: -2px;
}
tr.add td {
  background: var(--thread-wash);
}
tr.del td {
  background: var(--red-wash);
  text-decoration: line-through;
}
td.changed {
  background: var(--amber-wash);
}
.old {
  margin-right: 6px;
  color: var(--ink-faint);
  text-decoration: line-through;
}
.note-row td {
  padding: 0;
  cursor: default;
}
.notes {
  max-height: 120px;
  overflow-y: auto;
  padding: 8px 16px;
  border-top: 1px solid var(--line);
  background: var(--indigo-wash);
}
.note {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: var(--t-sm);
}
.ntext {
  flex: 1;
  min-width: 0;
}
footer {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 16px;
  border-top: 1px solid var(--line);
}
.foot-hint {
  flex: 1;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 6px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
}
.btn:disabled {
  opacity: 0.45;
  cursor: default;
}
.btn.primary {
  border-color: var(--action);
  background: var(--action);
  color: var(--action-ink);
}
.btn.undo-all:not(:disabled) {
  border-color: var(--red);
  background: var(--cloth);
  color: var(--red);
}
.btn.undo-all:not(:disabled):hover {
  background: var(--red-wash);
}
.spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@media (max-width: 760px) {
  .body {
    flex-direction: column;
  }
  .files {
    width: auto;
    max-height: 34%;
    border-right: none;
    border-bottom: 1px solid var(--line);
  }
}
</style>
