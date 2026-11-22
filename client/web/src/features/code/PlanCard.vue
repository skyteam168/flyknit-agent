<script setup lang="ts">
/**
 * 编码模式的方案卡片（参照 Cursor 的 Plan）：计划模式下 AI 调研完、给出方案后，输入框上方显示：
 * 标题、几句说明、待办（前几项）、「查看方案」「继续修改」「执行」。
 * - 查看方案：在编辑器里打开「方案.md」，员工可以直接改步骤；
 * - 继续修改：在输入框里说要怎么调整，AI 再出一版（仍在计划模式，不动手）；
 * - 执行：退出计划模式让 AI 按方案动手；改过方案的，把改后的方案一起发过去。
 * 安全不变：计划模式下 Core 照样只给只读工具；执行时每一步照样走确认。
 */
import { computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Circle, CircleDot, ClipboardList, FileText, Pencil, Play } from '@lucide/vue'
import { current, currentState } from '../../store'
import { executePlanInIde, openPlanDoc, planAnswer, planEdited, planTitle, rememberPlanLabels } from './store'

const { t } = useI18n()
const labels = computed(() => ({ fallback: t('ui.code.plan.fallback'), todos: t('ui.code.plan.todos'), name: t('ui.code.plan.fileName') }))
watch(labels, (l) => rememberPlanLabels(l), { immediate: true })

const id = computed(() => current.value?.id ?? '')
const title = computed(() => (id.value ? planTitle(id.value, labels.value.fallback) : ''))
/** 说明：回答里标题之后的第一段（去掉 Markdown 符号），卡片上最多三行 */
const summary = computed(() => {
  const text = (id.value ? planAnswer(id.value)?.content : '') ?? ''
  const para = text
    .replace(/^\s*#{1,6}\s+.*$/gm, '')
    .split(/\n\s*\n/)
    .map((p) => p.trim())
    .find((p) => p && !/^[-*|>]/.test(p))
  return (para ?? '').replace(/[*_`#>]/g, '').replace(/\s+/g, ' ')
})
const todos = computed(() => currentState.value?.plan ?? [])
const shown = computed(() => todos.value.slice(0, 4))
const edited = computed(() => !!id.value && planEdited(id.value))

function view() {
  if (id.value) openPlanDoc(id.value, labels.value)
}
function revise() {
  window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
}
function execute() {
  if (id.value) void executePlanInIde(id.value, { go: t('ui.planMode.go'), goEdited: t('ui.code.plan.goEdited') })
}
</script>

<template>
  <div class="plan-card">
    <div class="file">
      <ClipboardList :size="14" />
      <span>{{ labels.name }}</span>
      <span v-if="edited" class="edited"><Pencil :size="11" />{{ t('ui.code.plan.edited') }}</span>
    </div>
    <h3>{{ title }}</h3>
    <p v-if="summary" class="summary">{{ summary }}</p>
    <div v-if="todos.length" class="todos">
      <p class="count">{{ t('ui.code.plan.todoCount', { n: todos.length }) }}</p>
      <ul>
        <li v-for="(p, i) in shown" :key="i" :class="p.status">
          <Check v-if="p.status === 'completed'" :size="13" />
          <CircleDot v-else-if="p.status === 'in_progress'" :size="13" />
          <Circle v-else :size="13" />
          <span>{{ p.step }}</span>
        </li>
      </ul>
      <button v-if="todos.length > shown.length" type="button" class="more" @click="view">{{ t('ui.code.plan.more', { n: todos.length - shown.length }) }}</button>
    </div>
    <div class="foot">
      <button type="button" class="link" @click="view"><FileText :size="13" />{{ t('ui.code.plan.view') }}</button>
      <span class="spacer" />
      <button type="button" class="act" :title="t('ui.code.plan.reviseHint')" @click="revise">{{ t('ui.planMode.revise') }}</button>
      <button type="button" class="act primary" :title="edited ? t('ui.code.plan.executeEditedHint') : t('ui.code.plan.executeHint')" @click="execute">
        <Play :size="13" />{{ t('ui.code.plan.execute') }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.plan-card {
  max-width: var(--column);
  margin: 0 auto 6px;
  padding: 10px 14px 10px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--loom);
  font-size: calc(12.5px * var(--font-scale));
}
.file {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
}
.file svg {
  color: var(--indigo);
}
.edited {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin-left: 4px;
  padding: 0 6px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--amber) 18%, transparent);
  color: var(--amber);
  font-size: calc(11px * var(--font-scale));
}
.edited svg {
  color: inherit;
}
h3 {
  margin: 8px 0 4px;
  color: var(--ink);
  font-size: calc(15px * var(--font-scale));
  font-weight: 600;
}
.summary {
  display: -webkit-box;
  margin: 0 0 8px;
  overflow: hidden;
  color: var(--ink-soft);
  line-height: 1.55;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 3;
}
.todos {
  padding: 8px 10px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
}
.count {
  margin: 0 0 4px;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
}
ul {
  margin: 0;
  padding: 0;
  list-style: none;
}
li {
  display: flex;
  gap: 7px;
  padding: 3px 0;
  color: var(--ink-soft);
  line-height: 1.45;
}
li svg {
  flex: none;
  margin-top: 2px;
  color: var(--ink-faint);
}
li.completed {
  color: var(--ink-faint);
  text-decoration: line-through;
}
.more {
  padding: 2px 0 0 20px;
  border: 0;
  background: none;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.more:hover {
  color: var(--ink);
}
.foot {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 10px;
}
.spacer {
  flex: 1;
}
.link {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  cursor: pointer;
}
.link:hover {
  color: var(--ink);
}
.act {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 12px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  cursor: pointer;
}
.act:hover {
  border-color: var(--indigo);
}
.act.primary {
  border-color: var(--amber);
  background: var(--amber);
  color: #1b1b1b;
  font-weight: 600;
}
</style>
