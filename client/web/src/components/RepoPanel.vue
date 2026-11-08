<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  BookMarked,
  Box,
  Check,
  ChevronDown,
  CircleCheck,
  CircleDashed,
  CircleX,
  Copy,
  ExternalLink,
  GitBranch,
  GitMerge,
  GitPullRequest,
  LoaderCircle,
  Plus,
  RefreshCw,
  Wrench,
  X,
} from '@lucide/vue'
import { bridge } from '../bridge'
import { fail, loadPrs, loadSandbox, state, toast } from '../store'
import type { PrWatchView, RepoInfo } from '../types'
import Popover from './Popover.vue'
import PromptDialog from './PromptDialog.vue'
import ConfirmDialog from './ConfirmDialog.vue'

/**
 * 「文件」标签上方的仓库信息（同 Claude Code 的环境面板）：仓库、当前分支，在 GitHub 打开、复制分支名、切换和新建分支；
 * 下面是这个对话盯着的 PR：CI、冲突、评审，下次什么时候查，自动修复开关。
 * 只在 IT 打开「开发者工具」、工作区是 git 仓库时显示。分支操作由宿主执行（有未提交改动时不让切，任务进行中不让切）。
 */
const props = defineProps<{ conversationId: string }>()
const { t, locale } = useI18n()

const repo = ref<RepoInfo | null>(null)
const branches = ref<string[]>([])
const busy = ref(false)
const naming = ref(false)
const checking = ref('')
const prs = computed(() => state.prs[props.conversationId] ?? [])
const activePrs = computed(() => prs.value.filter((w) => w.active))
const endedPrs = computed(() => prs.value.filter((w) => !w.active).slice(0, 3))
// 沙箱任务：开着时显示状态和任务分支；结束沙箱会删容器，先确认
const sandbox = computed(() => state.sandbox[props.conversationId])
const endingSandbox = ref(false)
async function endSandbox() {
  endingSandbox.value = false
  const id = props.conversationId
  try {
    const r = await bridge.setSandbox(id, false)
    if (!r.ok) toast(r.message)
    else {
      const c = state.conversations.find((x) => x.id === id)
      if (c) c.sandbox = false
      void loadSandbox(id)
    }
  } catch (e) {
    fail(e)
  }
}

async function load() {
  const id = props.conversationId
  if (!id) return
  try {
    const info = await bridge.repoInfo(id)
    if (id === props.conversationId) repo.value = info
  } catch {
    repo.value = null
  }
  void loadPrs(id)
  if (state.app?.sandboxTasks) void loadSandbox(id)
}

watch(() => props.conversationId, load, { immediate: true })
// 任务跑完（AI 可能刚切了分支、提交了）刷新一次；定时刷新领先/落后
watch(
  () => state.byId[props.conversationId]?.busy,
  (running, was) => {
    if (was && !running) void load()
  },
)
const timer = window.setInterval(load, 60_000)
onBeforeUnmount(() => clearInterval(timer))

async function loadBranches() {
  try {
    branches.value = await bridge.repoBranches(props.conversationId)
  } catch (e) {
    fail(e)
  }
}

async function changeBranch(name: string, create: boolean, close?: () => void) {
  close?.()
  if (busy.value) return
  busy.value = true
  try {
    const r = await bridge.repoBranch(props.conversationId, name, create)
    if (!r.ok) toast(r.message)
    else toast(t(create ? 'ui.repo.created' : 'ui.repo.switched', { name }))
    await load()
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

async function copyBranch(close: () => void) {
  close()
  if (!repo.value?.branch) return
  try {
    await navigator.clipboard.writeText(repo.value.branch)
    toast(t('ui.repo.copied'))
  } catch (e) {
    fail(e)
  }
}

async function open(what: 'repo' | 'branch' | 'pr', id = '', close?: () => void) {
  close?.()
  try {
    if (!(await bridge.repoOpen(props.conversationId, what, id))) toast(t('ui.repo.noRemote'))
  } catch (e) {
    fail(e)
  }
}

async function checkNow(w: PrWatchView) {
  checking.value = w.id
  try {
    await bridge.prCheck(w.id)
  } catch (e) {
    fail(e)
  } finally {
    checking.value = ''
  }
}

async function toggleAutoFix(w: PrWatchView) {
  try {
    await bridge.prAutoFix(w.id, !w.autoFix)
  } catch (e) {
    fail(e)
  }
}

async function stop(w: PrWatchView) {
  try {
    await bridge.prStop(w.id)
  } catch (e) {
    fail(e)
  }
}

const ciIcon = (w: PrWatchView) =>
  w.state === 'MERGED' ? GitMerge : w.ci === 'failure' ? CircleX : w.ci === 'success' ? CircleCheck : w.ci === 'pending' ? LoaderCircle : CircleDashed

function when(iso: string | null) {
  if (!iso) return ''
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' })
}

const status = (w: PrWatchView) =>
  w.state !== 'OPEN'
    ? t(`ui.repo.state.${w.state}`)
    : [
        t(`ui.repo.ci.${w.ci}`),
        w.mergeable === 'CONFLICTING' ? t('ui.repo.conflict') : '',
        w.review ? t(`ui.repo.review.${w.review}`) : '',
      ]
        .filter(Boolean)
        .join(' · ')
</script>

<template>
  <section v-if="repo" class="repo" :aria-label="t('ui.repo.title')">
    <!-- 仓库 -->
    <Popover placement="down" :width="220">
      <template #trigger="{ toggle, open: isOpen }">
        <button type="button" class="row" :class="{ open: isOpen }" :title="repo.remoteUrl || repo.root" @click="toggle">
          <BookMarked :size="15" class="ico" />
          <span class="name">{{ repo.name }}</span>
          <span v-if="repo.changed" class="badge" :title="t('ui.repo.changedHint')">{{ t('ui.repo.changed', { n: repo.changed }) }}</span>
        </button>
      </template>
      <template #default="{ close }">
        <button v-if="repo.webUrl" type="button" class="opt" @click="open('repo', '', close)"><ExternalLink :size="14" /> {{ t('ui.repo.openRepo', { host: repo.host }) }}</button>
        <p v-else class="note">{{ t('ui.repo.noRemote') }}</p>
      </template>
    </Popover>

    <!-- 分支 -->
    <Popover placement="down" :width="260" @change="(o: boolean) => o && loadBranches()">
      <template #trigger="{ toggle, open: isOpen }">
        <button type="button" class="row" :class="{ open: isOpen }" :disabled="busy" @click="toggle">
          <GitBranch :size="15" class="ico" />
          <span class="name mono">{{ repo.detached ? t('ui.repo.detached') : repo.branch }}</span>
          <span v-if="repo.ahead || repo.behind" class="badge" :title="t('ui.repo.syncHint')">↑{{ repo.ahead }} ↓{{ repo.behind }}</span>
          <LoaderCircle v-if="busy" :size="13" class="spin" />
          <ChevronDown v-else :size="13" class="chev" />
        </button>
      </template>
      <template #default="{ close }">
        <button type="button" class="opt" :disabled="!repo.branch" @click="copyBranch(close)"><Copy :size="14" /> {{ t('ui.repo.copyBranch') }}</button>
        <button v-if="repo.branchUrl" type="button" class="opt" @click="open('branch', '', close)"><ExternalLink :size="14" /> {{ t('ui.repo.openBranch', { host: repo.host }) }}</button>
        <button type="button" class="opt" @click="close(); naming = true"><Plus :size="14" /> {{ t('ui.repo.newBranch') }}</button>
        <p class="group">{{ t('ui.repo.switchTo') }}</p>
        <button
          v-for="b in branches"
          :key="b"
          type="button"
          class="opt mono"
          :disabled="b === repo.branch"
          :title="repo.changed && b !== repo.branch ? t('ui.repo.dirtyHint') : b"
          @click="changeBranch(b, false, close)"
        >
          <Check v-if="b === repo.branch" :size="14" />
          <span v-else class="pad" />
          {{ b }}
        </button>
      </template>
    </Popover>

    <!-- 沙箱任务 -->
    <div v-if="sandbox?.on" class="sandbox" :class="sandbox.state">
      <Box :size="15" class="ico" />
      <div class="body">
        <span class="name">{{ t(`ui.sandboxTask.state.${sandbox.state}`) }}</span>
        <small v-if="sandbox.state === 'preparing' && sandbox.message">{{ sandbox.message }}</small>
        <small v-else-if="sandbox.branch" class="mono" :title="t('ui.sandboxTask.branchHint', { base: sandbox.base })">{{ sandbox.branch }}</small>
      </div>
      <LoaderCircle v-if="sandbox.state === 'preparing'" :size="13" class="spin" />
      <button v-else type="button" class="icon" :title="t('ui.sandboxTask.end')" :disabled="!!state.byId[conversationId]?.busy" @click="endingSandbox = true">
        <X :size="13" />
      </button>
    </div>

    <!-- 盯着的 PR -->
    <div v-if="activePrs.length || endedPrs.length" class="prs">
      <p class="group"><GitPullRequest :size="13" /> {{ t('ui.repo.prs') }}</p>
      <div v-for="w in [...activePrs, ...endedPrs]" :key="w.id" class="pr" :class="[w.ci, { ended: !w.active }]">
        <component :is="ciIcon(w)" :size="15" class="ci" :class="{ spin: w.ci === 'pending' && w.state === 'OPEN' }" />
        <div class="body">
          <button type="button" class="title" :title="w.url" @click="open('pr', w.id)">#{{ w.number }} {{ w.title }}</button>
          <small>{{ status(w) }}</small>
          <small v-if="w.checks.some((c) => c.state === 'fail')" class="fails">
            {{ w.checks.filter((c) => c.state === 'fail').map((c) => c.name).join('、') }}
          </small>
          <small v-if="w.active" class="meta">
            {{ t('ui.repo.nextCheck', { at: when(w.nextCheckAt), m: w.intervalMinutes }) }}
            <template v-if="w.lastError"> · {{ t('ui.repo.checkFailed') }}</template>
          </small>
          <small v-else class="meta">{{ t(`ui.repo.ended.${w.endedReason}`) }}</small>
        </div>
        <div v-if="w.active" class="actions">
          <button type="button" class="icon" :class="{ on: w.autoFix }" :title="t(w.autoFix ? 'ui.repo.autoFixOn' : 'ui.repo.autoFixOff', { n: w.autoFixes, max: w.maxAutoFixes })" @click="toggleAutoFix(w)">
            <Wrench :size="13" />
          </button>
          <button type="button" class="icon" :title="t('ui.repo.checkNow')" :disabled="checking === w.id" @click="checkNow(w)">
            <RefreshCw :size="13" :class="{ spin: checking === w.id }" />
          </button>
          <button type="button" class="icon" :title="t('ui.repo.stop')" @click="stop(w)"><X :size="13" /></button>
        </div>
      </div>
    </div>

    <ConfirmDialog
      v-if="endingSandbox"
      :title="t('ui.sandboxTask.offTitle')"
      :body="t('ui.sandboxTask.offBody')"
      :ok="t('ui.sandboxTask.offOk')"
      danger
      @ok="endSandbox"
      @cancel="endingSandbox = false"
    />
    <PromptDialog
      v-if="naming"
      :title="t('ui.repo.newBranch')"
      :placeholder="t('ui.repo.branchPlaceholder')"
      :ok="t('ui.repo.create')"
      @ok="(name: string) => { naming = false; void changeBranch(name, true) }"
      @cancel="naming = false"
    />
  </section>
</template>

<style scoped>
.repo {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 8px 8px 10px;
  border-bottom: 1px solid var(--line);
}
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-width: 0;
  padding: 6px 8px;
  border-radius: 8px;
  color: var(--ink);
  text-align: left;
}
.row:hover,
.row.open {
  background: var(--cloth-sunk);
}
.ico {
  flex: none;
  color: var(--ink-soft);
}
.name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--t-sm);
}
.mono {
  font-family: var(--font-mono, ui-monospace, monospace);
}
.badge {
  flex: none;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.sandbox {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 8px;
  background: var(--indigo-wash);
  color: var(--ink);
}
.sandbox .ico {
  color: var(--indigo);
}
.sandbox .body {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}
.sandbox small {
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.chev {
  flex: none;
  color: var(--ink-faint);
}
.opt {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 7px 8px;
  border-radius: 6px;
  color: var(--ink);
  font-size: var(--t-sm);
  text-align: left;
}
.opt:hover:not(:disabled) {
  background: var(--cloth-sunk);
}
.opt:disabled {
  color: var(--ink-faint);
}
.pad {
  width: 14px;
}
.group {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 8px 8px 4px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.note {
  margin: 6px 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.prs {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.pr {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 8px;
}
.pr:hover {
  background: var(--cloth-sunk);
}
.pr.ended {
  opacity: 0.6;
}
.ci {
  flex: none;
  margin-top: 2px;
  color: var(--ink-faint);
}
.pr.success .ci {
  color: var(--green, #16a34a);
}
.pr.failure .ci {
  color: var(--red);
}
.pr.pending .ci {
  color: var(--amber);
}
.body {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--ink);
  font-size: var(--t-sm);
  text-align: left;
}
.title:hover {
  color: var(--thread);
  text-decoration: underline;
}
.body small {
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.body .fails {
  color: var(--red);
}
.body .meta {
  color: var(--ink-faint);
}
.actions {
  display: flex;
  gap: 2px;
}
.icon {
  display: inline-grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.icon:hover:not(:disabled) {
  background: var(--cloth);
  color: var(--ink);
}
.icon.on {
  color: var(--thread);
}
.spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
