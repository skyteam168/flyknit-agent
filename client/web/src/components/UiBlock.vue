<script setup lang="ts">
import { ref, type Component } from 'vue'
import {
  ArrowDown, Bell, Boxes, Bug, Calculator, Calendar, ChartColumn, ChartLine, ChartPie, Check, CircleAlert, CircleCheck,
  CircleDashed, CircleX, ClipboardList, Clock, Code, Cpu, Database, Factory, File, FileText, Flag, Folder, GitBranch,
  Globe, Hammer, Info, Layers, Lightbulb, Link, ListChecks, LoaderCircle, Lock, Mail, MapPin, MessageSquare, Minus,
  Package, Phone, Printer, Rocket, Scale, Search, Send, Server, Settings, Shield, ShieldCheck, Sparkles, Star, Table,
  Target, Timer, TrendingDown, TrendingUp, TriangleAlert, Truck, User, Users, Wallet, Workflow, Wrench, Zap,
} from '@lucide/vue'
import type { UiItem, UiSpec, UiTone } from '../render/ui'

/**
 * ```ui 卡片的画法。内容全部当纯文字显示（反引号里的当行内代码），不插任何 HTML。
 * labels 由外面按界面语言传进来（这个组件是单独挂载的，不走 i18n 插件）。
 */
const props = defineProps<{
  spec: UiSpec
  labels: { recommended: string; pros: string; cons: string }
  /** 点了快捷回复：把话发出去 */
  onAction?: (text: string) => void
}>()

/** 模型能用的图标名。不认识的名字按卡片类型和状态给默认图标 */
const icons: Record<string, Component> = {
  calendar: Calendar, date: Calendar, database: Database, db: Database, api: Server, server: Server,
  workflow: Workflow, flow: Workflow, process: Workflow, chart: ChartLine, trend: ChartLine, bar: ChartColumn, pie: ChartPie,
  table: Table, report: FileText, file: File, doc: FileText, document: FileText, folder: Folder,
  user: User, person: User, users: Users, team: Users, people: Users, clock: Clock, time: Clock, timer: Timer,
  mail: Mail, email: Mail, settings: Settings, config: Settings, shield: Shield, security: ShieldCheck, check: ListChecks,
  checklist: ListChecks, search: Search, factory: Factory, production: Factory, truck: Truck, delivery: Truck,
  package: Package, box: Boxes, inventory: Boxes, money: Wallet, cost: Wallet, wallet: Wallet, bug: Bug, code: Code,
  link: Link, idea: Lightbulb, lightbulb: Lightbulb, target: Target, goal: Target, rocket: Rocket, launch: Rocket,
  lock: Lock, globe: Globe, web: Globe, phone: Phone, layers: Layers, tool: Wrench, wrench: Wrench, fix: Hammer,
  zap: Zap, fast: Zap, info: Info, sparkles: Sparkles, ai: Sparkles, bell: Bell, notify: Bell, calculator: Calculator,
  calc: Calculator, clipboard: ClipboardList, task: ClipboardList, cpu: Cpu, machine: Cpu, branch: GitBranch,
  send: Send, print: Printer, printer: Printer, scale: Scale, compare: Scale, flag: Flag, location: MapPin, map: MapPin,
  star: Star, message: MessageSquare, chat: MessageSquare, warning: TriangleAlert, alert: TriangleAlert, error: CircleX,
  ok: CircleCheck, done: CircleCheck,
}

const toneIcon: Record<UiTone, Component> = {
  ok: CircleCheck, todo: CircleDashed, warn: TriangleAlert, fail: CircleAlert, info: Info, tip: Lightbulb, running: LoaderCircle, skip: Minus,
}

function iconOf(it: UiItem, fallback: Component = Info): Component {
  return icons[it.icon] ?? (it.tone ? toneIcon[it.tone] : fallback)
}

/** 反引号里的当行内代码，其余原样（纯文字） */
function segments(text: string) {
  return text.split(/(`[^`\n]+`)/g).filter(Boolean).map((s) => (s.startsWith('`') && s.endsWith('`') && s.length > 2 ? { code: true, text: s.slice(1, -1) } : { code: false, text: s }))
}

function deltaGood(it: UiItem) {
  if (!it.trend || it.trend === 'flat') return null
  return it.trend === it.good
}

const chosen = ref<number | null>(null)
function act(i: number, it: UiItem) {
  if (chosen.value !== null || !props.onAction) return
  chosen.value = i
  props.onAction(it.prompt || it.title)
}
</script>

<template>
  <div class="ui" :class="`k-${spec.kind}`">
    <div v-if="spec.title" class="ui-title">{{ spec.title }}</div>

    <!-- 检查清单：测试结果、验收项、待办 -->
    <ul v-if="spec.kind === 'checklist'" class="rows">
      <li v-for="(it, i) in spec.items" :key="i" class="row" :class="`t-${it.tone || 'ok'}`">
        <component :is="iconOf(it, CircleCheck)" :size="18" class="ico" :class="{ spin: it.tone === 'running' }" />
        <div class="body">
          <div class="name"><template v-for="(s, j) in segments(it.title)" :key="j"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></div>
          <div v-if="it.detail" class="detail"><template v-for="(s, j) in segments(it.detail)" :key="j"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></div>
        </div>
      </li>
    </ul>

    <!-- 提示 / 问题清单：按严重程度上色 -->
    <ul v-else-if="spec.kind === 'callouts'" class="rows">
      <li v-for="(it, i) in spec.items" :key="i" class="row" :class="`t-${it.tone || 'info'}`">
        <component :is="iconOf(it)" :size="18" class="ico" />
        <div class="body">
          <div class="name">{{ spec.items.length > 1 ? `${i + 1}. ` : '' }}<template v-for="(s, j) in segments(it.title)" :key="j"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></div>
          <div v-if="it.detail" class="detail"><template v-for="(s, j) in segments(it.detail)" :key="j"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></div>
        </div>
      </li>
    </ul>

    <!-- 步骤 / 流程：从上到下，带箭头 -->
    <div v-else-if="spec.kind === 'steps'" class="steps">
      <template v-for="(it, i) in spec.items" :key="i">
        <div class="step" :class="[`t-${it.tone || 'none'}`, { edge: i === 0 || i === spec.items.length - 1 }]">
          <component :is="iconOf(it, Workflow)" :size="18" class="ico" :class="{ spin: it.tone === 'running' }" />
          <div class="name">{{ i + 1 }}. <template v-for="(s, j) in segments(it.title)" :key="j"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></div>
          <div v-if="it.detail" class="detail"><template v-for="(s, j) in segments(it.detail)" :key="j"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></div>
        </div>
        <ArrowDown v-if="i < spec.items.length - 1" :size="14" class="arrow" />
      </template>
    </div>

    <!-- 关键数字 -->
    <div v-else-if="spec.kind === 'stats'" class="stats">
      <div v-for="(it, i) in spec.items" :key="i" class="stat">
        <div class="label"><component :is="iconOf(it, ChartColumn)" v-if="it.icon" :size="14" />{{ it.title }}</div>
        <div class="value">{{ it.value }}<small v-if="it.unit">{{ it.unit }}</small></div>
        <div v-if="it.delta || it.detail" class="delta" :class="{ good: deltaGood(it) === true, bad: deltaGood(it) === false }">
          <TrendingUp v-if="it.trend === 'up'" :size="13" /><TrendingDown v-else-if="it.trend === 'down'" :size="13" />
          {{ it.delta }}<span v-if="it.detail" class="muted">{{ it.delta ? ' · ' : '' }}{{ it.detail }}</span>
        </div>
      </div>
    </div>

    <!-- 时间线 -->
    <ol v-else-if="spec.kind === 'timeline'" class="timeline">
      <li v-for="(it, i) in spec.items" :key="i" :class="`t-${it.tone || 'none'}`">
        <span class="dot" />
        <div class="when">{{ it.value }}</div>
        <div class="name">{{ it.title }}</div>
        <div v-if="it.detail" class="detail">{{ it.detail }}</div>
      </li>
    </ol>

    <!-- 方案对比 -->
    <div v-else-if="spec.kind === 'compare'" class="compare">
      <div v-for="(it, i) in spec.items" :key="i" class="col" :class="{ rec: it.recommended }">
        <div class="col-head">
          <strong>{{ it.title }}</strong>
          <span v-if="it.recommended" class="badge"><Star :size="11" /> {{ labels.recommended }}</span>
        </div>
        <div v-if="it.detail" class="detail">{{ it.detail }}</div>
        <div v-if="it.tags.length" class="tags"><span v-for="tg in it.tags" :key="tg">{{ tg }}</span></div>
        <ul v-if="it.pros.length" class="pc pros" :aria-label="labels.pros">
          <li v-for="p in it.pros" :key="p"><Check :size="13" />{{ p }}</li>
        </ul>
        <ul v-if="it.cons.length" class="pc cons" :aria-label="labels.cons">
          <li v-for="c in it.cons" :key="c"><CircleX :size="13" />{{ c }}</li>
        </ul>
      </div>
    </div>

    <!-- 进度 / 完成率 -->
    <div v-else-if="spec.kind === 'progress'" class="progress">
      <div v-for="(it, i) in spec.items" :key="i" class="bar-row" :class="`t-${it.tone || 'none'}`">
        <div class="bar-head"><span>{{ it.title }}</span><strong>{{ Math.round(it.percent) }}%</strong></div>
        <div class="bar"><span :style="{ width: `${it.percent}%` }" /></div>
        <div v-if="it.detail" class="detail">{{ it.detail }}</div>
      </div>
    </div>

    <!-- 要点 / 参数 -->
    <dl v-else-if="spec.kind === 'facts'" class="facts">
      <template v-for="(it, i) in spec.items" :key="i">
        <dt>{{ it.title }}</dt>
        <dd><template v-for="(s, j) in segments(it.value || it.detail)" :key="j"><code v-if="s.code">{{ s.text }}</code><template v-else>{{ s.text }}</template></template></dd>
      </template>
    </dl>

    <!-- 快捷回复：点一下就把话发出去，不用打字 -->
    <div v-else-if="spec.kind === 'actions'" class="actions">
      <button
        v-for="(it, i) in spec.items"
        :key="i"
        type="button"
        class="act"
        :class="{ chosen: chosen === i }"
        :disabled="chosen !== null && chosen !== i"
        :title="it.prompt || it.title"
        @click="act(i, it)"
      >
        <component :is="icons[it.icon] ?? Send" :size="14" />
        <span class="text"><span>{{ it.title }}</span><small v-if="it.detail">{{ it.detail }}</small></span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.ui {
  width: 100%;
  color: var(--ink);
  font-size: var(--t-sm);
  text-align: left;
  white-space: normal;
}
.ui-title {
  margin-bottom: 8px;
  font-size: var(--t-md);
  font-weight: 600;
}
code {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--chip);
  font-family: var(--font-code);
  font-size: 0.92em;
}
.detail {
  margin-top: 3px;
  color: var(--ink-soft);
  line-height: 1.6;
}
.muted {
  color: var(--ink-faint);
}
/* 色调：状态 / 严重程度 */
.t-ok { --tone: var(--thread); }
.t-todo, .t-skip, .t-none { --tone: var(--ink-faint); }
.t-warn { --tone: var(--amber); }
.t-fail { --tone: var(--red); }
.t-info, .t-running { --tone: var(--indigo); }
.t-tip { --tone: var(--thread); }

/* 清单、提示 */
.rows {
  margin: 0;
  padding: 0;
  list-style: none;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
}
.row {
  display: flex;
  gap: 12px;
  padding: 12px 14px;
}
.row + .row {
  border-top: 1px solid var(--line);
}
.row .ico {
  flex: none;
  margin-top: 2px;
  color: var(--tone);
}
.row .name {
  font-weight: 600;
}
.body {
  min-width: 0;
}
.spin {
  animation: spin 1s linear infinite;
}

/* 步骤 */
.steps {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 10px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
}
.step {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 100%;
  padding: 10px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  text-align: center;
}
.step.edge {
  border-color: transparent;
  background: transparent;
}
.step.t-running,
.step.t-ok,
.step.t-fail,
.step.t-warn {
  border-color: color-mix(in srgb, var(--tone) 45%, var(--line));
}
.step .ico {
  margin-bottom: 4px;
  color: var(--tone, var(--ink-soft));
}
.step .name {
  font-weight: 600;
}
.arrow {
  color: var(--ink-faint);
}

/* 关键数字 */
.stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 8px;
}
.stat {
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
}
.stat .label {
  display: flex;
  align-items: center;
  gap: 5px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.stat .value {
  margin-top: 4px;
  font-size: 24px;
  font-weight: 650;
  font-variant-numeric: tabular-nums;
  line-height: 1.2;
}
.stat .value small {
  margin-left: 3px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 400;
}
.delta {
  display: flex;
  align-items: center;
  gap: 3px;
  margin-top: 4px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.delta.good {
  color: var(--thread);
}
.delta.bad {
  color: var(--red);
}

/* 时间线 */
.timeline {
  margin: 0;
  padding: 0 0 0 18px;
  list-style: none;
  border-left: 2px solid var(--line);
}
.timeline li {
  position: relative;
  padding: 0 0 12px 12px;
}
.timeline .dot {
  position: absolute;
  top: 5px;
  left: -25px;
  width: 10px;
  height: 10px;
  border: 2px solid var(--cloth);
  border-radius: 50%;
  background: var(--tone, var(--indigo));
}
.timeline .t-none .dot {
  background: var(--indigo);
}
.when {
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-variant-numeric: tabular-nums;
}
.timeline .name {
  font-weight: 600;
}

/* 对比 */
.compare {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: 8px;
}
.col {
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
}
.col.rec {
  border-color: color-mix(in srgb, var(--indigo) 45%, var(--line));
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--indigo) 25%, transparent);
}
.col-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}
.badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 7px;
  border-radius: 9px;
  background: var(--indigo-wash);
  color: var(--indigo);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-top: 6px;
}
.tags span {
  padding: 0 7px;
  border-radius: 9px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.pc {
  margin: 8px 0 0;
  padding: 0;
  list-style: none;
}
.pc li {
  display: flex;
  gap: 6px;
  margin-top: 3px;
  line-height: 1.5;
}
.pc li svg {
  flex: none;
  margin-top: 3px;
}
.pros svg {
  color: var(--thread);
}
.cons svg {
  color: var(--red);
}

/* 进度 */
.progress {
  display: grid;
  gap: 10px;
}
.bar-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}
.bar {
  height: 8px;
  margin-top: 4px;
  overflow: hidden;
  border-radius: 4px;
  background: var(--chip);
}
.bar span {
  display: block;
  height: 100%;
  border-radius: 4px;
  background: var(--tone, var(--indigo));
}
.bar-row.t-none .bar span {
  background: var(--indigo);
}

/* 要点 */
.facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 6px 16px;
  margin: 0;
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
}
.facts dt {
  color: var(--ink-soft);
}
.facts dd {
  margin: 0;
  word-break: break-word;
}

/* 快捷回复 */
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.act {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  max-width: 100%;
  padding: 7px 12px;
  border: 1px solid var(--line-strong);
  border-radius: 999px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  text-align: left;
  cursor: pointer;
}
.act:hover:not(:disabled) {
  border-color: var(--indigo);
  color: var(--indigo);
}
.act.chosen {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
}
.act:disabled:not(.chosen) {
  opacity: 0.45;
  cursor: default;
}
.act .text {
  display: flex;
  flex-direction: column;
}
.act small {
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@media (max-width: 520px) {
  .facts {
    grid-template-columns: 1fr;
  }
}
</style>
