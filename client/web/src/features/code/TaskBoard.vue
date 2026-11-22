<script setup lang="ts">
/**
 * 编码模式的任务进度看板（参照 Cursor 对话里的 To-dos 卡片）：编码模式把右侧面板换成了 IDE，原来的「计划」面板看不到，
 * 这里固定在输入框上方，不用切回普通模式。
 * - 计划：AI 用 update_plan 列的步骤，逐条显示进行中 / 完成；收起时只显示「2 / 4 · 正在做的那一步」。
 * - 子代理 / 专家：这一轮开出去的（spawn_agent、ask_expert），显示名字、在做什么、状态；点一下滚到对话里那张卡片。
 * 数据全是已有的（plan.updated、子任务事件、工具调用），宿主不用改。
 */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, Check, ChevronDown, ChevronRight, Circle, CircleDot, ListTodo, Loader2, TriangleAlert } from '@lucide/vue'
import { current, currentState, subtaskFor } from '../../store'
import { askExpertArgs, isSubtaskTool, toolLabel } from '../../subtasks'
import type { ToolCallRef } from '../../types'

const { t } = useI18n()

const OpenKey = 'flyknit.ide.boardOpen'
function readOpen(): boolean {
  try {
    return localStorage.getItem(OpenKey) !== '0'
  } catch {
    return true
  }
}
const open = ref(readOpen())
watch(open, (v) => {
  try {
    localStorage.setItem(OpenKey, v ? '1' : '0')
  } catch {
    // 存不了就只在这次记住
  }
})

const plan = computed(() => currentState.value?.plan ?? [])
const done = computed(() => plan.value.filter((p) => p.status === 'completed').length)
const doing = computed(() => plan.value.find((p) => p.status === 'in_progress') ?? null)

type AgentState = 'running' | 'waiting' | 'completed' | 'failed'
interface AgentRow {
  callId: string
  name: string
  avatar: string
  state: AgentState
  detail: string
}

/** 最后一句员工消息之后开出去的子代理 / 专家（之前轮次的在对话里看） */
const agents = computed<AgentRow[]>(() => {
  const s = currentState.value
  const id = current.value?.id
  if (!s || !id) return []
  const lastUser = s.messages.map((m) => m.role).lastIndexOf('user')
  const calls = new Map<string, ToolCallRef>()
  for (const m of s.messages.slice(lastUser + 1)) for (const c of m.toolCalls ?? []) if (isSubtaskTool(c.name)) calls.set(c.id, c)
  for (const tool of Object.values(s.tools)) if (isSubtaskTool(tool.name) && !calls.has(tool.callId)) calls.set(tool.callId, { id: tool.callId, name: tool.name, arguments: tool.args ?? '' })
  const answered = new Set(s.messages.filter((m) => m.role === 'tool' && m.toolCallId).map((m) => m.toolCallId))
  return [...calls.values()].map((c) => {
    const sub = subtaskFor(id, c.id)
    const args = askExpertArgs(c.arguments, c.name)
    const expert = sub?.expert ?? args.expert
    const tool = s.tools[c.id]
    let state: AgentState
    if (sub?.result) state = ['completed', 'partial'].includes(sub.result.status) ? 'completed' : 'failed'
    else if (sub && !['completed', 'partial', 'failed', 'needs_input'].includes(sub.status)) state = sub.status === 'waiting_confirmation' ? 'waiting' : 'running'
    else if (tool) state = tool.state === 'running' ? 'running' : tool.state === 'waiting' ? 'waiting' : tool.state === 'done' ? 'completed' : 'failed'
    else state = answered.has(c.id) ? 'completed' : s.busy ? 'running' : 'failed'
    const step = sub?.step
    const detail =
      state === 'running' && step?.name ? toolLabel(step.name) : state === 'running' && step?.kind === 'thinking' ? t('ui.delegate.thinking') : ''
    return {
      callId: c.id,
      name: expert?.name || t('ui.subagent.name'),
      avatar: expert?.avatar ?? '',
      state,
      detail,
    }
  })
})
const agentsRunning = computed(() => agents.value.filter((a) => a.state === 'running' || a.state === 'waiting').length)

const visible = computed(() => !!current.value && (plan.value.length > 0 || agents.value.length > 0))

/** 收起时的一行：计划进度 + 正在做的那一步；没计划时显示子代理的数量 */
const summary = computed(() => {
  if (plan.value.length) {
    const head = t('ui.code.board.progress', { done: done.value, total: plan.value.length })
    if (done.value === plan.value.length) return head + ' · ' + t('ui.code.board.allDone')
    return doing.value ? head + ' · ' + doing.value.step : head
  }
  return t('ui.code.board.agents', { n: agents.value.length })
})

/** 点子代理：滚到对话里它那张卡片（在折叠的「已完成 N 步」里也会先展开，见 MessageList 的 flyknit:reveal-call） */
function reveal(callId: string) {
  window.dispatchEvent(new CustomEvent('flyknit:reveal-call', { detail: callId }))
}
</script>

<template>
  <div v-if="visible" class="task-board" :class="{ open }">
    <button type="button" class="head" :aria-expanded="open" @click="open = !open">
      <component :is="open ? ChevronDown : ChevronRight" :size="13" class="chev" />
      <ListTodo :size="14" class="icon" />
      <span class="title">{{ plan.length ? t('ui.code.board.todos', { n: plan.length }) : t('ui.code.board.agentsTitle') }}</span>
      <span v-if="!open || !plan.length" class="sum">{{ summary }}</span>
      <span v-if="agentsRunning" class="pill"><Loader2 :size="11" class="spin" />{{ t('ui.code.board.agentsRunning', { n: agentsRunning }) }}</span>
      <span v-if="plan.length" class="meter" :title="t('ui.code.board.progress', { done, total: plan.length })"><span :style="{ width: (done / plan.length) * 100 + '%' }" /></span>
    </button>
    <div v-if="open" class="body">
      <ol v-if="plan.length" class="steps">
        <li v-for="(p, i) in plan" :key="i" :class="p.status">
          <Check v-if="p.status === 'completed'" :size="14" />
          <CircleDot v-else-if="p.status === 'in_progress'" :size="14" />
          <Circle v-else :size="14" />
          <span>{{ p.step }}</span>
        </li>
      </ol>
      <template v-if="agents.length">
        <p class="group">{{ t('ui.code.board.agentsTitle') }}</p>
        <button v-for="a in agents" :key="a.callId" type="button" class="agent" :class="a.state" :title="t('ui.code.board.jump')" @click="reveal(a.callId)">
          <span class="avatar">{{ a.avatar && a.avatar.length <= 2 ? a.avatar : '' }}<Bot v-if="!a.avatar || a.avatar.length > 2" :size="13" /></span>
          <span class="name">{{ a.name }}</span>
          <span v-if="a.detail" class="detail">{{ a.detail }}</span>
          <span class="state">
            <Loader2 v-if="a.state === 'running'" :size="12" class="spin" />
            <TriangleAlert v-else-if="a.state === 'waiting' || a.state === 'failed'" :size="12" />
            <Check v-else :size="12" />
            {{ t(`ui.code.board.state.${a.state}`) }}
          </span>
        </button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.task-board {
  max-width: var(--column);
  margin: 0 auto 6px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--loom);
  font-size: calc(12.5px * var(--font-scale));
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 7px 10px;
  border: 0;
  background: transparent;
  color: var(--ink);
  text-align: left;
  cursor: pointer;
}
.head:hover {
  background: color-mix(in srgb, var(--ink) 4%, transparent);
}
.chev {
  flex: none;
  color: var(--ink-faint);
}
.icon {
  flex: none;
  color: var(--indigo);
}
.title {
  flex: none;
  font-weight: 600;
}
.sum {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.open .head .sum:empty {
  display: none;
}
.pill {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  margin-left: auto;
  padding: 1px 7px;
  border-radius: 9px;
  background: color-mix(in srgb, var(--indigo) 16%, transparent);
  color: var(--indigo);
  font-size: calc(11px * var(--font-scale));
}
.meter {
  flex: none;
  width: 44px;
  height: 4px;
  margin-left: auto;
  overflow: hidden;
  border-radius: 2px;
  background: var(--line);
}
.pill + .meter {
  margin-left: 6px;
}
.meter span {
  display: block;
  height: 100%;
  background: var(--thread, var(--indigo));
  transition: width 300ms ease;
}
.body {
  max-height: 240px;
  padding: 2px 12px 10px 30px;
  overflow-y: auto;
  border-top: 1px solid var(--line);
}
.steps {
  margin: 6px 0 0;
  padding: 0;
  list-style: none;
}
.steps li {
  display: flex;
  gap: 8px;
  padding: 4px 0;
  color: var(--ink-soft);
  line-height: 1.45;
}
.steps li svg {
  flex: none;
  margin-top: 2px;
  color: var(--ink-faint);
}
.steps li.completed {
  color: var(--ink-faint);
  text-decoration: line-through;
}
.steps li.completed svg {
  color: var(--green, #3fb27f);
}
.steps li.in_progress {
  color: var(--ink);
  font-weight: 500;
}
.steps li.in_progress svg {
  color: var(--indigo);
}
.group {
  margin: 10px 0 4px;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
}
.agent {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 5px 6px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink);
  text-align: left;
  cursor: pointer;
}
.agent:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.avatar {
  display: grid;
  flex: none;
  place-items: center;
  width: 20px;
  height: 20px;
  border-radius: 5px;
  background: color-mix(in srgb, var(--indigo) 14%, transparent);
  color: var(--indigo);
  font-size: 12px;
}
.name {
  flex: none;
  max-width: 45%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.detail {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
  text-overflow: ellipsis;
  white-space: nowrap;
}
.state {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  margin-left: auto;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
}
.agent.running .state {
  color: var(--indigo);
}
.agent.waiting .state {
  color: var(--amber);
}
.agent.completed .state {
  color: var(--green, #3fb27f);
}
.agent.failed .state {
  color: var(--red, #e5534b);
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
