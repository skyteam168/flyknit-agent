<script setup lang="ts">
/**
 * 编码模式左边的「源代码管理」（参照 VS Code）：当前分支和领先 / 落后、提交说明（可以让 AI 写）、提交、拉取 / 推送，
 * 改动分「暂存的更改」「更改」两组：点文件打开和上次提交的对比（右边可以直接改），悬停有暂存 / 取消暂存 / 丢弃。
 * 丢弃和推送先确认（会丢东西、会发到外面）。提交时没有暂存的改动，问一下要不要把全部改动一起提交。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown, ArrowUp, GitBranch, Minus, Plus, RefreshCw, Sparkles, Undo2 } from '@lucide/vue'
import { bridge } from '../../bridge'
import { current, currentState, toast } from '../../store'
import { code, currentCode, openGitDiff } from './store'
import type { ScmChange, ScmStatus } from './types'

const { t } = useI18n()

const status = ref<ScmStatus | null>(null)
const loading = ref(false)
const busy = ref('')
const message = ref('')

const id = computed(() => current.value?.id ?? '')
const staged = computed(() => status.value?.changes.filter((c) => c.staged) ?? [])
const unstaged = computed(() => status.value?.changes.filter((c) => !c.staged) ?? [])

async function refresh() {
  if (!id.value) return
  loading.value = true
  try {
    status.value = await bridge.scmStatus(id.value)
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  } finally {
    loading.value = false
  }
}
onMounted(refresh)
watch(id, refresh)
// 文件树刷新（新建 / 删除了文件）、AI 这一轮做完、员工在编辑器里保存了：跟着刷新
watch(() => (id.value ? code.treeVersion[id.value] : 0), refresh)
watch(() => currentState.value?.busy, (b) => !b && void refresh())
watch(() => currentCode.value?.tabs.map((x) => x.version).join('|'), refresh)

async function run(what: string, call: () => Promise<{ ok: boolean; message: string }>, done?: string) {
  if (busy.value) return
  busy.value = what
  try {
    const r = await call()
    if (!r.ok) toast(r.message)
    else if (done) toast(done)
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  } finally {
    busy.value = ''
    await refresh()
  }
}

const name = (p: string) => p.split(/[\\/]/).pop() ?? p
const dir = (p: string) => p.slice(0, Math.max(0, p.length - name(p).length)).replace(/[\\/]+$/, '')

async function open(c: ScmChange) {
  if (!id.value) return
  if (!c.workspacePath) {
    toast(t('ui.code.git.outside'))
    return
  }
  if (c.status === 'D') {
    toast(t('ui.code.git.deleted', { name: name(c.path) }))
    return
  }
  const head = await bridge.scmHead(id.value, c.path)
  if (!head.ok) {
    toast(head.message)
    return
  }
  await openGitDiff(id.value, c.workspacePath, head.exists ? head.text : '')
}

const stage = (paths: string[]) => run('stage', () => bridge.scmStage(id.value, paths))
const unstage = (paths: string[]) => run('unstage', () => bridge.scmUnstage(id.value, paths))
function discard(paths: string[]) {
  const news = unstaged.value.filter((c) => paths.includes(c.path) && c.status === 'U').length
  const text = paths.length === 1 ? t('ui.code.git.discardOne', { name: name(paths[0]) }) : t('ui.code.git.discardMany', { n: paths.length })
  if (!window.confirm(text + (news ? '\n' + t('ui.code.git.discardNew', { n: news }) : ''))) return
  void run('discard', () => bridge.scmDiscard(id.value, paths))
}

async function commit() {
  const msg = message.value.trim()
  if (!msg) {
    toast(t('ui.code.git.needMessage'))
    return
  }
  let stageAll = false
  if (!staged.value.length) {
    if (!unstaged.value.length) return
    if (!window.confirm(t('ui.code.git.commitAll', { n: unstaged.value.length }))) return
    stageAll = true
  }
  await run('commit', () => bridge.scmCommit(id.value, msg, stageAll), t('ui.code.git.committed'))
  if (!(status.value?.changes.some((c) => c.staged) ?? false)) message.value = ''
}

function push() {
  const s = status.value
  if (!s) return
  if (!window.confirm(t(s.hasUpstream ? 'ui.code.git.pushConfirm' : 'ui.code.git.publishConfirm', { branch: s.branch, n: s.ahead }))) return
  void run('push', () => bridge.scmPush(id.value), t('ui.code.git.pushed'))
}
const pull = () => run('pull', () => bridge.scmPull(id.value), t('ui.code.git.pulled'))

async function aiMessage() {
  if (busy.value || !id.value) return
  busy.value = 'message'
  try {
    const r = await bridge.scmMessage(id.value)
    if (r.ok && r.text) message.value = r.text
    else toast(r.message)
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  } finally {
    busy.value = ''
  }
}

function onKey(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
    e.preventDefault()
    void commit()
  }
}
</script>

<template>
  <div class="git-pane">
    <p v-if="status && !status.isRepo" class="hint">{{ t('ui.code.git.notRepo') }}</p>
    <template v-else-if="status">
      <div class="branch-row">
        <GitBranch :size="13" />
        <span class="branch" :title="status.root">{{ status.branch || '—' }}</span>
        <span v-if="status.ahead" class="count" :title="t('ui.code.git.ahead', { n: status.ahead })"><ArrowUp :size="11" />{{ status.ahead }}</span>
        <span v-if="status.behind" class="count" :title="t('ui.code.git.behind', { n: status.behind })"><ArrowDown :size="11" />{{ status.behind }}</span>
        <span class="spacer" />
        <button type="button" class="icon" :disabled="!!busy" :title="t('ui.code.git.pull')" @click="pull"><ArrowDown :size="14" /></button>
        <button type="button" class="icon" :disabled="!!busy || status.detached" :title="t('ui.code.git.push')" @click="push"><ArrowUp :size="14" /></button>
        <button type="button" class="icon" :class="{ spin: loading }" :title="t('ui.code.refresh')" @click="refresh"><RefreshCw :size="13" /></button>
      </div>
      <p v-if="status.error" class="error">{{ status.error }}</p>

      <div class="commit">
        <textarea v-model="message" rows="2" :placeholder="t('ui.code.git.messagePlaceholder')" @keydown="onKey" />
        <div class="commit-actions">
          <button type="button" class="ghost" :disabled="!!busy" :title="t('ui.code.git.aiMessageHint')" @click="aiMessage">
            <Sparkles :size="13" :class="{ pulse: busy === 'message' }" />{{ t('ui.code.git.aiMessage') }}
          </button>
          <button type="button" class="primary" :disabled="!!busy || !status.changes.length" @click="commit">
            {{ staged.length ? t('ui.code.git.commit') : t('ui.code.git.commitAllShort') }}
          </button>
        </div>
      </div>

      <section v-if="staged.length">
        <header>
          <span>{{ t('ui.code.git.staged') }}</span>
          <span class="badge">{{ staged.length }}</span>
          <button type="button" class="icon" :title="t('ui.code.git.unstageAll')" @click="unstage(staged.map((c) => c.path))"><Minus :size="13" /></button>
        </header>
        <div v-for="c in staged" :key="'s' + c.path" class="change" :title="c.path" @click="open(c)">
          <span class="fname">{{ name(c.path) }}</span>
          <span class="fdir">{{ dir(c.path) }}</span>
          <button type="button" class="icon act" :title="t('ui.code.git.unstage')" @click.stop="unstage([c.path])"><Minus :size="13" /></button>
          <span class="st" :class="'s-' + c.status">{{ c.status }}</span>
        </div>
      </section>

      <section>
        <header>
          <span>{{ t('ui.code.git.changes') }}</span>
          <span class="badge">{{ unstaged.length }}</span>
          <template v-if="unstaged.length">
            <button type="button" class="icon" :title="t('ui.code.git.discardAll')" @click="discard(unstaged.map((c) => c.path))"><Undo2 :size="13" /></button>
            <button type="button" class="icon" :title="t('ui.code.git.stageAll')" @click="stage(unstaged.map((c) => c.path))"><Plus :size="13" /></button>
          </template>
        </header>
        <div v-for="c in unstaged" :key="'u' + c.path" class="change" :title="c.path" @click="open(c)">
          <span class="fname" :class="{ gone: c.status === 'D' }">{{ name(c.path) }}</span>
          <span class="fdir">{{ dir(c.path) }}</span>
          <button v-if="c.status !== 'C'" type="button" class="icon act" :title="t('ui.code.git.discard')" @click.stop="discard([c.path])"><Undo2 :size="13" /></button>
          <button type="button" class="icon act" :title="t('ui.code.git.stage')" @click.stop="stage([c.path])"><Plus :size="13" /></button>
          <span class="st" :class="'s-' + c.status">{{ c.status }}</span>
        </div>
        <p v-if="!unstaged.length && !staged.length" class="hint">{{ t('ui.code.git.clean') }}</p>
      </section>
    </template>
    <p v-else class="hint">{{ t('ui.code.loading') }}</p>
  </div>
</template>

<style scoped>
.git-pane {
  font-size: calc(12.5px * var(--font-scale));
}
.branch-row {
  display: flex;
  align-items: center;
  gap: 4px;
  margin: 0 8px 6px;
  color: var(--ink-soft);
}
.branch {
  overflow: hidden;
  color: var(--ink);
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.count {
  display: inline-flex;
  align-items: center;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
}
.spacer {
  flex: 1;
}
.icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--ink-faint);
  cursor: pointer;
}
.icon:hover:not(:disabled) {
  background: color-mix(in srgb, var(--ink) 9%, transparent);
  color: var(--ink);
}
.icon:disabled {
  opacity: 0.4;
  cursor: default;
}
.spin svg {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
.commit {
  margin: 0 8px 10px;
}
.commit textarea {
  width: 100%;
  min-height: 52px;
  padding: 6px 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  resize: vertical;
}
.commit textarea:focus {
  border-color: var(--indigo);
  outline: none;
}
.commit-actions {
  display: flex;
  gap: 6px;
  margin-top: 6px;
}
.commit-actions button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
  height: 26px;
  padding: 0 10px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.commit-actions .primary {
  flex: 1;
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.commit-actions button:disabled {
  opacity: 0.5;
  cursor: default;
}
.pulse {
  animation: pulse 1s ease-in-out infinite;
}
@keyframes pulse {
  50% {
    opacity: 0.3;
  }
}
section header {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 4px 8px;
  color: var(--ink-soft);
  font-size: calc(11.5px * var(--font-scale));
  font-weight: 600;
}
section header > span:first-child {
  flex: 1;
}
.badge {
  min-width: 18px;
  padding: 0 5px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
  font-weight: 400;
  text-align: center;
}
.change {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 2px 8px 2px 14px;
  cursor: pointer;
  white-space: nowrap;
}
.change:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.fname {
  flex: none;
  max-width: 60%;
  overflow: hidden;
  color: var(--ink);
  text-overflow: ellipsis;
}
.fname.gone {
  text-decoration: line-through;
  color: var(--ink-faint);
}
.fdir {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  text-overflow: ellipsis;
}
.act {
  display: none;
}
.change:hover .act {
  display: grid;
}
.st {
  flex: none;
  width: 14px;
  font-family: var(--mono, monospace);
  font-size: 11px;
  font-weight: 600;
  text-align: center;
}
.s-M {
  color: var(--amber);
}
.s-A,
.s-U {
  color: var(--green, #3fb27f);
}
.s-D,
.s-C {
  color: var(--red, #e5534b);
}
.s-R {
  color: var(--indigo);
}
.hint {
  margin: 8px 12px;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
  line-height: 1.5;
}
.error {
  margin: 0 8px 8px;
  color: var(--red, #e5534b);
  font-size: calc(11.5px * var(--font-scale));
}
</style>
