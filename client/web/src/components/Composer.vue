<script setup lang="ts">
import SplitOffer from './SplitOffer.vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowLeftRight,
  ArrowUp,
  Brain,
  Check,
  ChevronDown,
  ChevronRight,
  CornerDownRight,
  Globe,
  ListPlus,
  FileText,
  ImageIcon,
  Languages,
  Loader2,
  MessageSquare,
  Mic,
  Paperclip,
  Pause,
  Pencil,
  Play,
  Plug,
  Puzzle,
  Box,
  MoreHorizontal,
  Check as CheckIcon,
  RotateCcw,
  Sparkles,
  Timer,
  ClipboardList,
  Square,
  Wrench,
  Zap,
  X,
} from '@lucide/vue'
import Popover from './Popover.vue'
import VendorIcon from './VendorIcon.vue'
import ModelPicker from './ModelPicker.vue'
import QuotaRing from './QuotaRing.vue'
import WorkspacePicker from './WorkspacePicker.vue'
import PermissionPicker from './PermissionPicker.vue'
import ConfirmBar from './ConfirmBar.vue'
import AskBar from './AskBar.vue'
import CompactingCard from './CompactingCard.vue'
import UsageWarning from './UsageWarning.vue'
import ExpertAvatar from './ExpertAvatar.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import { useOverflow } from '../overflow'
import { bridge } from '../bridge'
import {
  addPastedImage,
  addPastedText,
  cancelSpeech,
  current,
  currentExpert,
  currentState,
  draftMode,
  finishSpeech,
  interject,
  loadCommands,
  setPermission,
  dismissInterrupted,
  interruptedRun,
  resumeInterrupted,
  setLongMode,
  setSandbox,
  setPlanMode,
  executePlan,
  togglePause,
  withdrawPending,
  onSpeechText,
  pickFiles,
  send,
  setMode,
  setTranslate,
  startSpeech,
  state,
  stop,
  toast, openConnectors, openAttachment,
} from '../store'
import type { InterjectMode, Mode, PendingInput, SlashCommand } from '../types'
import { shouldFold, snippet } from '../pasted'
import { expandCommand, findCommand, parseSlash, templateParams } from '../slash'
import { availableBuiltins, BUILTIN_NAMES, describeBuiltin, findBuiltin, runBuiltin, type BuiltinContext } from '../builtinCommands'
import FullAccessDialog from './FullAccessDialog.vue'

const props = defineProps<{ home?: boolean }>()
const { t, locale } = useI18n()
const text = ref('')
const box = ref<HTMLTextAreaElement>()

const mode = computed<Mode>(() => current.value?.mode ?? draftMode.mode)
/** 专家对话：固定是「办事」，模式不能切；只读专家的权限也不用选 */
const expertMode = computed(() => !!(current.value?.expertId ?? draftMode.expertId))
const placeholder = computed(() => {
  if (offline.value) return t('input.disconnected')
  if (paused.value && !props.home) return t('ui.pause.placeholder')
  if (busy.value && !props.home) return t('ui.interject.placeholder')
  if (mode.value === 'translate') return t('input.placeholderTranslate')
  if (currentExpert.value) return t('ui.experts.placeholder', { name: currentExpert.value.name })
  return t('input.placeholder')
})
const from = computed(() => current.value?.translateFrom ?? draftMode.translateFrom)
const to = computed(() => current.value?.translateTo ?? draftMode.translateTo)
const busy = computed(() => currentState.value?.busy ?? false)
const paused = computed(() => !!currentState.value?.paused)
const stopping = computed(() => !!currentState.value?.stopping)
/** 计划模式（先出方案）：当前对话的，或者新对话打算开的 */
const planMode = computed(() => (current.value ? !!state.planMode[current.value.id] : draftMode.planMode))
/** 方案好了：计划模式下这一轮跑完、最后一条是 AI 的回答 */
const planReady = computed(() => {
  if (!planMode.value || busy.value || !current.value) return false
  const msgs = currentState.value?.messages ?? []
  return msgs.length > 0 && msgs[msgs.length - 1].role === 'assistant'
})
/** 长任务模式：当前对话的，或者还没建的新对话打算开的 */
const longMode = computed(() => (current.value ? !!current.value.longMode : draftMode.longMode))
const longAllowed = computed(() => state.app?.longTasks !== false)
// 沙箱任务：IT 开了「开发者工具」和「沙箱任务」才显示；关掉会删容器（没推出去的改动就没了），先确认
const sandboxAllowed = computed(() => !!state.app?.developerTools && !!state.app?.sandboxTasks)
const sandboxOn = computed(() => (current.value ? !!current.value.sandbox : draftMode.sandbox))
const confirmSandboxOff = ref(false)
const sandboxBusy = ref(false)
async function toggleSandbox() {
  if (sandboxBusy.value) return
  if (sandboxOn.value && current.value) {
    confirmSandboxOff.value = true
    return
  }
  await applySandbox(!sandboxOn.value)
}
async function applySandbox(on: boolean) {
  confirmSandboxOff.value = false
  sandboxBusy.value = true
  try {
    await setSandbox(on)
  } finally {
    sandboxBusy.value = false
  }
}
/** 这个对话上次没做完（程序被关了），而且现在没在跑 */
const interrupted = computed(() => (!busy.value && current.value ? interruptedRun(current.value.id) : null))
const offline = computed(() => state.app !== null && !state.app.connected)
const canSend = computed(
  () => !offline.value && (busy.value ? text.value.trim().length > 0 : text.value.trim().length > 0 || state.pending.length > 0),
)
/** AI 还没读到的插话、排到本轮之后的话 */
const inputs = computed(() => currentState.value?.inputs ?? [])

const langs = ['zh-CN', 'vi', 'en', 'km', 'th', 'id', 'ja', 'ko']
const modes: { key: Mode; icon: unknown }[] = [
  { key: 'agent', icon: Wrench },
  { key: 'chat', icon: MessageSquare },
  { key: 'translate', icon: Languages },
]
const modeIcon = computed(() => modes.find((m) => m.key === mode.value)!.icon)

function resize() {
  const el = box.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 260)}px`
}

async function submit(how?: InterjectMode) {
  menuOpen.value = false
  if (!canSend.value) return
  const value = text.value
  text.value = ''
  await nextTick()
  resize()
  let outgoing = value
  const slash = parseSlash(value)
  // 内置命令优先（/compact、/permission、/model……见 builtinCommands.ts）：大多在本地办完、不发给模型
  if (slash) {
    const outcome = await runBuiltin(slash.name, slash.rest, builtinCtx.value)
    if (outcome.kind === 'done') return
    if (outcome.kind === 'confirmFull') {
      askFull.value = true // 和权限选择器一样，先弹风险确认
      return
    }
    if (outcome.kind === 'confirmSandboxOff') {
      confirmSandboxOff.value = true // 和沙箱按钮一样：关掉会删容器，先确认
      return
    }
    if (outcome.kind === 'send') outgoing = outcome.text
  }
  // 快捷指令：「/名字 参数…」展开成模板发出去；参数没填全就放回输入框，让员工补上
  const cmd = slash && outgoing === value ? findCommand(slash.name, state.commands.mine, state.commands.it) : null
  // 内置命令在这个模式下用不了（比如翻译对话里的 /compact）、也没有同名快捷指令：说一声，不当成普通文字发出去
  if (slash && !cmd && outgoing === value && BUILTIN_NAMES.includes(slash.name.toLowerCase())) {
    text.value = value
    toast(t('ui.slash.run.unavailable', { name: slash.name.toLowerCase() }))
    return
  }
  if (slash && cmd) {
    const r = expandCommand(cmd, slash.rest)
    if (r.missing.length) {
      text.value = r.text
      await nextTick()
      resize()
      toast(t('ui.slash.missing', { names: r.missing.join('、') }))
      return
    }
    outgoing = r.text
  }
  // 正在运行：回车 / 点发送是插话；长按或右键发送可以选「打断并优先」「排到本轮之后」
  const sent = busy.value ? await interject(outgoing, how ?? 'steer') : await send(outgoing)
  // 没发出去（例如新对话没建成）就把文字放回输入框；附件在发送成功前不会从待发列表里拿走
  if (!sent && !text.value) {
    text.value = value
    await nextTick()
    resize()
  }
}

const deepThinking = computed(() => !!state.app?.deepThinking)
const webSearchAvailable = computed(() => !!state.app?.webSearchEnabled)
const webSearchOn = computed(() => !!state.app?.webSearch && webSearchAvailable.value)

async function toggleDeep() {
  if (mode.value === 'translate') {
    toast(t('input.noDeepTranslate'))
    return
  }
  const next = !deepThinking.value
  if (state.app) state.app.deepThinking = next
  await bridge.setChatPrefs({ deepThinking: next })
}

async function toggleWeb() {
  if (mode.value === 'translate') {
    toast(t('input.noWebTranslate'))
    return
  }
  if (mode.value === 'agent') {
    toast(t('input.noWebAgent'))
    return
  }
  if (!webSearchAvailable.value) {
    toast(t('input.webSearchOff'))
    return
  }
  const next = !webSearchOn.value
  if (state.app) state.app.webSearch = next
  await bridge.setChatPrefs({ webSearch: next })
}

// ---------- 运行中发送：长按 / 右键出菜单 ----------
const menuOpen = ref(false)
let pressTimer = 0
let longPressed = false

function onSendDown(e: PointerEvent) {
  if (!busy.value || e.button !== 0) return
  longPressed = false
  pressTimer = window.setTimeout(() => {
    longPressed = true
    if (canSend.value) menuOpen.value = true
  }, 450)
}

function onSendUp() {
  window.clearTimeout(pressTimer)
}

function onSendClick() {
  // 长按出了菜单：松手时的这次点击不算发送
  if (longPressed) {
    longPressed = false
    return
  }
  void submit()
}

function onSendMenu(e: MouseEvent) {
  if (!busy.value) return
  e.preventDefault()
  if (canSend.value) menuOpen.value = true
}

function closeMenu(e: PointerEvent) {
  if (menuOpen.value && !(e.target as HTMLElement).closest('.send-menu, .send-go')) menuOpen.value = false
}

onMounted(() => document.addEventListener('pointerdown', closeMenu, true))
onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', closeMenu, true)
  window.clearTimeout(pressTimer)
})

/** 撤回待发的话，放回输入框里改（输入框里已经有字就接在后面） */
async function editInput(item: PendingInput) {
  const back = await withdrawPending(item)
  if (back === null) return
  text.value = text.value.trim() ? `${text.value}\n${back}` : back
  await nextTick()
  resize()
  box.value?.focus()
}

// ---------- 斜杠列表：输入框里只有「/」加几个字时列出命令；「/命令 」后面列出可选参数 ----------
interface SlashItem {
  name: string
  title: string
  template: string
  source: 'builtin' | 'it' | 'mine'
  /** 内置命令的参数提示（如「只读 | 工作区 | 完全」） */
  hint?: string
  /** 有值 = 这是参数补全：选中后执行「/name arg」 */
  arg?: string
  /** 内置命令选中后要不要等员工继续打参数 */
  wantsArg?: boolean
}
const initAvailable = computed(() => !!state.app?.developerTools && mode.value === 'agent')
const builtinCtx = computed<BuiltinContext>(() => ({
  mode: mode.value,
  busy: busy.value,
  initAvailable: initAvailable.value,
  planMode: planMode.value,
  longMode: longMode.value,
  sandboxOn: sandboxOn.value,
}))
const askFull = ref(false)
async function confirmFull() {
  askFull.value = false
  await setPermission('full')
  toast(t('ui.slash.run.permissionSet', { name: t('ui.permission.full') }))
}
const slashIndex = ref(0)
const slashDismissed = ref('')
const slashItems = computed<SlashItem[]>(() => {
  if (slashDismissed.value === text.value) return []
  // 「/permission 」「/model gp」：列出这个命令的可选参数
  const withArg = /^\/(\S+) (\S*)$/.exec(text.value)
  if (withArg) {
    const cmd = findBuiltin(withArg[1], builtinCtx.value)
    if (!cmd?.args) return []
    const q = withArg[2].toLowerCase()
    return cmd
      .args()
      .filter((a) => !q || a.value.toLowerCase().includes(q) || a.label.toLowerCase().includes(q))
      .slice(0, 30)
      .map((a) => ({ name: `${cmd.name} ${a.value}`, title: a.label, template: '', source: 'builtin' as const, arg: a.value }))
  }
  const m = /^\/([^\s/]*)$/.exec(text.value)
  if (!m) return []
  const q = m[1].toLowerCase()
  const builtins = availableBuiltins(builtinCtx.value)
  const taken = new Set(builtins.flatMap((c) => [c.name, ...(c.aliases ?? [])]))
  // 分组排列：内置 → 公司 → 我的。和内置命令重名的快捷指令不生效，也就不列出来
  const all: SlashItem[] = [
    ...builtins.map((c) => ({
      name: c.name,
      title: describeBuiltin(c),
      template: '',
      source: 'builtin' as const,
      hint: c.argHint?.() ?? '',
      wantsArg: !!c.argHint,
    })),
    ...state.commands.it
      .filter((c) => !taken.has(c.name.toLowerCase()) && !state.commands.mine.some((x) => x.name.toLowerCase() === c.name.toLowerCase()))
      .map((c) => ({ ...c, source: 'it' as const })),
    ...state.commands.mine.filter((c) => !taken.has(c.name.toLowerCase())).map((c) => ({ ...c, source: 'mine' as const })),
  ]
  return all.filter((c) => !q || c.name.toLowerCase().includes(q) || c.title.toLowerCase().includes(q)).slice(0, 40)
})
watch(slashItems, () => (slashIndex.value = 0))
// 方向键选到列表外面时滚过去
watch(slashIndex, async () => {
  await nextTick()
  document.querySelector('.slash-item.on')?.scrollIntoView({ block: 'nearest' })
})
// 第一次打出 / 时刷新一下（后台刚发布的新指令也能马上用上）
watch(
  () => text.value === '/',
  (on) => {
    if (on) void loadCommands()
  },
)

/**
 * 选中一条：
 * - 内置命令：不要参数的直接执行；要参数的先放「/名字 」，接着列出可选参数或让员工打；选中参数直接执行。
 * - 快捷指令：有参数的先放「/名字 」让员工接着打参数；没有参数的直接把模板放进输入框，看一眼再发。
 */
async function pickSlash(c: SlashItem) {
  if (c.source === 'builtin') {
    if (c.arg !== undefined || !c.wantsArg) {
      text.value = `/${c.name}`
      await submit()
      return
    }
    text.value = `/${c.name} `
    await nextTick()
    resize()
    box.value?.focus()
    return
  }
  const params = templateParams(c.template).length > 0
  text.value = params ? `/${c.name} ` : c.template
  await nextTick()
  resize()
  box.value?.focus()
}

function slashHint(c: SlashItem) {
  if (c.source === 'builtin') return c.arg !== undefined ? '' : c.hint ?? ''
  const params = templateParams(c.template)
  return params.length ? params.map((p) => `{${p}}`).join(' ') : ''
}

function onKey(e: KeyboardEvent) {
  if (slashItems.value.length && !e.isComposing) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      const n = slashItems.value.length
      slashIndex.value = (slashIndex.value + (e.key === 'ArrowDown' ? 1 : n - 1)) % n
      return
    }
    if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault()
      void pickSlash(slashItems.value[slashIndex.value])
      return
    }
    if (e.key === 'Escape') {
      slashDismissed.value = text.value
      return
    }
  }
  if (e.key === 'Escape' && menuOpen.value) {
    menuOpen.value = false
    return
  }
  // 录音时键盘先归录音条：回车结束并识别，Esc 放弃
  if (speech.value.phase !== 'idle') {
    if (e.key === 'Escape') {
      e.preventDefault()
      cancelSpeech()
    } else if (e.key === 'Enter' && !e.isComposing) {
      e.preventDefault()
      if (speech.value.phase === 'recording') void finishSpeech()
    }
    return
  }
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault()
    void submit()
  }
}

function onPaste(e: ClipboardEvent) {
  const items = [...(e.clipboardData?.items ?? [])].filter((i) => i.kind === 'file' && i.type.startsWith('image/'))
  if (items.length === 0) {
    // 很长的文字不塞进输入框，折叠成一张卡片；点卡片上的笔可以放回输入框
    const pasted = e.clipboardData?.getData('text/plain') ?? ''
    if (shouldFold(pasted)) {
      e.preventDefault()
      void addPastedText(pasted.replace(/\r\n/g, '\n'))
    }
    return
  }
  e.preventDefault()
  for (const i of items) {
    const blob = i.getAsFile()
    if (blob) void addPastedImage(blob)
  }
}

/** 粘贴卡片上的笔：卡片拿掉，全文插回输入框光标处 */
function expandPaste(i: number) {
  const a = state.pending[i]
  if (a?.pasteText === undefined) return
  state.pending.splice(i, 1)
  const el = box.value
  const at = el ? (el.selectionStart ?? text.value.length) : text.value.length
  const before = text.value.slice(0, at)
  text.value = before + a.pasteText + text.value.slice(at)
  const caret = before.length + a.pasteText.length
  void nextTick(() => {
    resize()
    el?.focus()
    el?.setSelectionRange(caret, caret)
  })
}

function swap() {
  if (from.value === 'auto') return
  void setTranslate(to.value, from.value)
}

async function chooseMode(m: Mode, close: () => void) {
  close()
  await setMode(m)
  focus()
}

// ---- 连接器（MCP）：列出已连上的服务，选一个就在输入框前面加上「使用连接器「X」：」 ----
const connected = computed(() => state.mcp.filter((v) => v.status === 'connected'))
const mcpExpanded = ref<string | null>(null)

function useConnector(name: string, tool: string | null, close: () => void) {
  close()
  mcpExpanded.value = null
  const prefix = tool ? t('ui.mcp.useTool', { name, tool }) : t('ui.mcp.use', { name })
  if (!text.value.startsWith(prefix)) text.value = prefix + text.value
  void nextTick(() => {
    resize()
    focus()
  })
}

function manageConnectors(close: () => void) {
  close()
  openConnectors()
}

function useSkill(name: string, close?: () => void) {
  close?.()
  const prefix = t('ui.skills.use', { name })
  if (!text.value.startsWith(prefix)) text.value = prefix + text.value
  void nextTick(() => {
    resize()
    focus()
  })
}

// ---- 语音输入 ----
const speech = computed(() => state.speech)
const micReady = computed(() => state.app?.micAvailable !== false)

/** 00:00 / 01:23 */
const spoken = computed(() => {
  const s = Math.floor(speech.value.elapsedMs / 1000)
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
})

/** 快到上限时把计时变红，提醒用户收一下 */
const nearLimit = computed(() => speech.value.maxMs > 0 && speech.value.elapsedMs > speech.value.maxMs - 20000)

/** 一排竖条，中间高两头低，再乘上当前响度 */
const bars = Array.from({ length: 13 }, (_, i) => 0.45 + 0.55 * Math.sin((Math.PI * (i + 1)) / 14))

function barHeight(shape: number) {
  const level = speech.value.phase === 'recording' ? speech.value.level : 0
  return `${Math.round(3 + shape * level * 15)}px`
}

/** 识别出来的文字插到光标处，而不是覆盖已经打了一半的内容 */
function insert(said: string) {
  const el = box.value
  const at = el ? (el.selectionStart ?? text.value.length) : text.value.length
  const before = text.value.slice(0, at)
  const after = text.value.slice(at)
  const glue = before && !/\s$/.test(before) ? ' ' : ''
  text.value = before + glue + said + after
  const caret = (before + glue + said).length
  void nextTick(() => {
    resize()
    el?.focus()
    el?.setSelectionRange(caret, caret)
  })
}

/** 由首页快捷入口或外部调用填入文字 */
function fill(value: string) {
  text.value = value
  void nextTick(() => {
    resize()
    box.value?.focus()
    box.value?.setSelectionRange(value.length, value.length)
  })
}
function focusInput() {
  box.value?.focus()
}

defineExpose({ fill, useSkill, focusInput })

// ---------- 增强提示词：AI 把这句话改写清楚，原文留着可以一键恢复 ----------
const enhancing = ref(false)
/** 增强前的原文；为空表示没有可恢复的 */
const original = ref<string | null>(null)
/** 增强后的文字。用户一改动，就不再提供「恢复原文」 */
let enhanced = ''
const canEnhance = computed(
  () => mode.value !== 'translate' && !busy.value && !offline.value && original.value === null && text.value.trim().length >= 2,
)

async function enhance() {
  if (!canEnhance.value || enhancing.value) return
  const before = text.value
  enhancing.value = true
  try {
    const r = await bridge.enhancePrompt(before, mode.value, current.value?.id ?? null, current.value ? null : draftMode.expertId)
    if (text.value !== before) return // 等结果的时候用户又改了，不覆盖
    original.value = before
    enhanced = r.text
    fill(r.text)
  } catch (e) {
    toast(e instanceof Error ? e.message : String(e))
  } finally {
    enhancing.value = false
  }
}

function restoreOriginal() {
  if (original.value === null) return
  const back = original.value
  original.value = null
  fill(back)
}

watch(text, (value) => {
  if (original.value !== null && value !== enhanced) original.value = null
})

// 别处要往输入框里放的文字（专家详情里点了示例问题），接过来就清掉
watch(
  () => state.composerFill,
  (value) => {
    if (!value) return
    state.composerFill = null
    fill(value)
  },
  { immediate: true },
)

const focus = () => box.value?.focus()
let offSpeech: (() => void) | null = null
onMounted(() => {
  window.addEventListener('flyknit:focus-input', focus)
  offSpeech = onSpeechText(insert)
  focus()
})
onBeforeUnmount(() => {
  window.removeEventListener('flyknit:focus-input', focus)
  offSpeech?.()
  // 组件卸载时还在录，就别留着麦克风开着
  if (state.speech.phase !== 'idle') cancelSpeech()
})

// 输入框底部两行工具栏放不下时，次要按钮按顺序收进「…」，变宽再逐个放回来（不换行）
const barEl = ref<HTMLElement>()
const footEl = ref<HTMLElement>()
const skillsPop = ref<InstanceType<typeof Popover>>()
const mcpPop = ref<InstanceType<typeof Popover>>()
// 顺序 = 越靠前越晚收：右边的附件、语音、润色、额度只在左边都收完还放不下时才收（窗口极窄、右侧面板很宽时）
const barKeys = () => [
  'attach',
  ...(micReady.value ? ['mic'] : []),
  ...(original.value === null && mode.value !== 'translate' ? ['enhance'] : []),
  'quota',
  ...(mode.value !== 'translate' ? ['deep'] : []),
  ...(mode.value === 'chat' ? ['web'] : []),
  ...(mode.value === 'agent' ? ['skills', 'mcp'] : []),
]
const footKeys = () =>
  mode.value === 'agent'
    ? ['plan', ...(longAllowed.value ? ['long'] : []), ...(sandboxAllowed.value ? ['sandbox'] : []), 'note']
    : ['note']
const barOv = useOverflow(barEl, barKeys, [() => state.app?.modelName, () => busy.value, () => connected.value.length])
const footOv = useOverflow(footEl, footKeys, [() => state.app?.defaultWorkspace, () => current.value?.id])
watch(locale, () => {
  barOv.remeasure()
  footOv.remeasure()
})
// 从「…」里点技能 / 连接器：关掉菜单，打开原来那个面板（收起时它就挂在「…」旁边）
function openFromMore(which: 'skills' | 'mcp', close: () => void) {
  close()
  void nextTick(() => (which === 'skills' ? skillsPop : mcpPop).value?.show())
}
</script>

<template>
  <div class="composer-wrap" :class="{ home: props.home }">
    <SplitOffer />
    <CompactingCard v-if="!props.home" />
    <ConfirmBar v-if="!props.home" />
    <AskBar v-if="!props.home" />
    <UsageWarning />
    <div class="card" :class="{ busy }">
      <!-- 上次没做完：已经做过的步骤都存着，可以接着做 -->
      <div v-if="interrupted && !props.home" class="paused-bar interrupted-bar">
        <span>{{ t('ui.longTask.interruptedBar') }}<template v-if="interrupted.stage"> {{ t('ui.tasks.stoppedAt', { stage: interrupted.stage }) }}</template></span>
        <button type="button" class="paused-go ghost" @click="dismissInterrupted(interrupted.conversationId)">{{ t('ui.tasks.dismiss') }}</button>
        <button type="button" class="paused-go" @click="resumeInterrupted(interrupted.conversationId)"><Play :size="13" /> {{ t('ui.tasks.resume') }}</button>
      </div>
      <!-- 计划模式：方案好了，员工确认后才动手 -->
      <div v-if="planReady && !props.home" class="paused-bar plan-bar">
        <ClipboardList :size="14" />
        <span>{{ t('ui.planMode.ready') }}</span>
        <button type="button" class="paused-go ghost" @click="box?.focus()">{{ t('ui.planMode.revise') }}</button>
        <button type="button" class="paused-go" @click="executePlan"><Play :size="13" /> {{ t('ui.planMode.execute') }}</button>
      </div>
      <!-- 暂停中：做完手上这一步后停住，点继续接着往下走 -->
      <div v-if="paused" class="paused-bar">
        <Pause :size="14" />
        <span>{{ t('ui.pause.banner') }}</span>
        <button type="button" class="paused-go" @click="togglePause"><Play :size="13" /> {{ t('ui.pause.resume') }}</button>
      </div>
      <!-- 运行中又发的话：AI 读到之前可以撤回、拿回输入框改 -->
      <div v-if="inputs.length" class="inputs">
        <div v-for="item in inputs" :key="item.id" class="input-row">
          <span class="input-tag" :class="item.mode">
            <component :is="item.mode === 'queue' ? ListPlus : CornerDownRight" :size="12" />
            {{ t(item.mode === 'queue' ? 'ui.interject.queued' : 'ui.interject.steered') }}
          </span>
          <span class="input-text" :title="item.text">{{ item.text }}</span>
          <button type="button" class="input-btn" :title="t('ui.interject.edit')" :aria-label="t('ui.interject.edit')" @click="editInput(item)"><Pencil :size="13" /></button>
          <button type="button" class="input-btn" :title="t('ui.interject.withdraw')" :aria-label="t('ui.interject.withdraw')" @click="withdrawPending(item)"><X :size="13" /></button>
        </div>
      </div>
      <div v-if="state.pending.length" class="pending">
        <template v-for="(a, i) in state.pending" :key="a.localPath + i">
        <div v-if="a.pasted" class="paste-card">
          <button type="button" class="paste-body" :title="t('ui.paste.view')" @click="state.textView = a">{{ snippet(a.pasteText ?? '') }}</button>
          <button type="button" class="paste-remove" :title="t('ui.paste.remove')" :aria-label="t('ui.paste.remove')" @click="state.pending.splice(i, 1)"><X :size="12" /></button>
          <div class="paste-foot">
            <span class="paste-badge">{{ t('ui.paste.badge') }}</span>
            <button type="button" class="paste-edit" :title="t('ui.paste.expand')" :aria-label="t('ui.paste.expand')" @click="expandPaste(i)"><Pencil :size="13" /></button>
          </div>
        </div>
        <div v-else class="chip" :class="{ saving: a.saving }" :title="a.saving ? t('ui.attach.saving') : a.localPath">
          <!-- 点开看：图片、PDF、Office 等按类型在客户端里打开（见 openAttachment） -->
          <span
            class="chip-open"
            role="button"
            :tabindex="a.saving ? -1 : 0"
            :aria-disabled="a.saving"
            :aria-label="t('ui.attach.open', { name: a.fileName })"
            @click="openAttachment(a)"
            @keydown.enter.prevent="openAttachment(a)"
          >
            <img v-if="a.preview" :src="a.preview" alt="" />
            <component :is="a.mime.startsWith('image/') ? ImageIcon : FileText" v-else :size="15" />
            <span class="chip-name">{{ a.fileName }}</span>
            <Loader2 v-if="a.saving" :size="13" class="spin" />
          </span>
          <button type="button" :aria-label="t('menu.delete')" @click="state.pending.splice(i, 1)"><X :size="13" /></button>
        </div>
        </template>
      </div>

      <!-- 快捷指令：打 / 时列出来，方向键选、回车或 Tab 选中 -->
      <div v-if="slashItems.length" class="slash-menu" role="listbox" :aria-label="t('ui.slash.title')">
        <button
          v-for="(c, i) in slashItems"
          :key="c.source + c.name + (c.arg ?? '')"
          type="button"
          role="option"
          class="slash-item"
          :class="{ on: i === slashIndex }"
          :aria-selected="i === slashIndex"
          @mouseenter="slashIndex = i"
          @mousedown.prevent="pickSlash(c)"
        >
          <span class="slash-name">/{{ c.name }}</span>
          <span class="slash-args">{{ slashHint(c) }}</span>
          <span class="slash-title">{{ c.title || c.template }}</span>
          <span v-if="c.arg === undefined" class="slash-src" :class="c.source">{{ t(`ui.slash.source.${c.source}`) }}</span>
        </button>
      </div>
      <textarea
        ref="box"
        v-model="text"
        :rows="props.home ? 3 : 1"
        :placeholder="placeholder"
        @input="resize"
        @keydown="onKey"
        @paste="onPaste"
      />

      <!-- 录音条：录音中盖住输入区，和打字互不干扰 -->
      <div v-if="speech.phase !== 'idle'" class="recorder" role="status" aria-live="polite">
        <span class="rec-ico" :class="{ live: speech.phase === 'recording' }">
          <Mic v-if="speech.phase === 'recording'" :size="15" />
          <Loader2 v-else :size="15" class="spin" />
        </span>
        <span class="wave" aria-hidden="true">
          <i v-for="(shape, i) in bars" :key="i" :style="{ height: barHeight(shape) }" />
        </span>
        <span class="elapsed" :class="{ warn: nearLimit }">{{ spoken }}</span>
        <span class="rec-hint">{{ t(speech.phase === 'recording' ? 'ui.speech.listening' : 'ui.speech.working') }}</span>
        <button type="button" class="rec-btn" :title="t('ui.speech.cancel')" @click="cancelSpeech">
          <X :size="15" />
        </button>
        <button
          type="button"
          class="rec-btn done"
          :disabled="speech.phase !== 'recording'"
          :title="t('ui.speech.finish')"
          @click="finishSpeech"
        >
          <Check :size="15" />
        </button>
      </div>

      <div ref="barEl" class="bar">
        <!-- 专家对话：显示是哪位专家，不能切模式 -->
        <span v-if="expertMode && currentExpert" class="tool-btn expert-chip" :title="currentExpert.tagline">
          <ExpertAvatar :avatar="currentExpert.avatar" :name="currentExpert.name" :id="currentExpert.id" :size="18" />
          <span>{{ currentExpert.name }}</span>
        </span>
        <!-- 模式 -->
        <Popover v-else :width="260">
          <template #trigger="{ toggle, open }">
            <button type="button" class="tool-btn mode-btn" :class="{ open }" @click="toggle">
              <component :is="modeIcon" :size="15" />
              <span>{{ t(`mode.${mode}`) }}</span>
              <ChevronDown :size="14" class="chev" />
            </button>
          </template>
          <template #default="{ close }">
            <p v-if="current && current.messageCount > 0" class="pop-note">{{ t('ui.modeLocked') }}</p>
            <button v-for="m in modes" :key="m.key" type="button" class="pop-opt" @click="chooseMode(m.key, close)">
              <component :is="m.icon" :size="16" class="ico" :class="{ on: mode === m.key }" />
              <span class="pop-text">
                <strong>{{ t(`mode.${m.key}`) }}</strong>
                <small>{{ t(`mode.${m.key}Hint`) }}</small>
              </span>
              <Check v-if="mode === m.key" :size="15" class="tick" />
            </button>
          </template>
        </Popover>

        <!-- 模型 -->
        <ModelPicker :mode="mode" class="ov-shrink" data-ov-shrink="model" />

        <!-- 深度思考 / 联网：对齐手机端；火花图标仍是「增强提示词」 -->
        <button
          v-if="mode !== 'translate'"
          type="button"
          class="tool-btn"
          data-ov="deep"
          :class="{ on: deepThinking, 'ov-hide': barOv.isHidden('deep') }"
          :title="t('input.deepThinking')"
          @click="toggleDeep"
        >
          <Brain :size="15" />
          <span class="hide-narrow">{{ t('input.deepThinking') }}</span>
        </button>
        <button
          v-if="mode === 'chat'"
          type="button"
          class="tool-btn"
          data-ov="web"
          :class="{ on: webSearchOn, 'ov-hide': barOv.isHidden('web') }"
          :title="webSearchAvailable ? t('input.webSearch') : t('input.webSearchOff')"
          @click="toggleWeb"
        >
          <Globe :size="15" />
          <span class="hide-narrow">{{ t('input.webSearch') }}</span>
        </button>

        <!-- 技能（办事模式） -->
        <Popover v-if="mode === 'agent'" ref="skillsPop" :width="300" data-ov="skills" :class="{ 'ov-hide': barOv.isHidden('skills') }">
          <template #trigger="{ toggle, open }">
            <button type="button" class="tool-btn" :class="{ open }" :title="t('ui.skills.button')" @click="toggle">
              <Puzzle :size="15" />
              <span class="hide-narrow">{{ t('ui.skills.button') }}</span>
            </button>
          </template>
          <template #default="{ close }">
            <p v-if="state.skills.length === 0" class="pop-note">{{ t('ui.skills.empty') }}</p>
            <button v-for="s in state.skills" :key="s.name" type="button" class="pop-opt" @click="useSkill(s.name, close)">
              <Puzzle :size="15" class="ico" />
              <span class="pop-text">
                <strong>{{ s.name }}</strong>
                <small>{{ s.description }}</small>
              </span>
            </button>
          </template>
        </Popover>

        <!-- 连接器（办事模式）：和技能并列，但是两套东西 -->
        <Popover v-if="mode === 'agent'" ref="mcpPop" :width="320" data-ov="mcp" :class="{ 'ov-hide': barOv.isHidden('mcp') }">
          <template #trigger="{ toggle, open }">
            <button type="button" class="tool-btn" :class="{ open }" :title="t('ui.mcp.title')" @click="toggle">
              <Plug :size="15" />
              <span class="hide-narrow">{{ t('ui.mcp.title') }}</span>
              <em v-if="connected.length" class="count">{{ connected.length }}</em>
            </button>
          </template>
          <template #default="{ close }">
            <p v-if="connected.length === 0" class="pop-empty">{{ t('ui.mcp.noneConnected') }}</p>
            <template v-for="v in connected" :key="v.id">
              <div class="mcp-row">
                <button type="button" class="pop-opt" @click="useConnector(v.name, null, close)">
                  <VendorIcon :icon="v.icon" :name="v.name" :id="v.id" :size="26" />
                  <span class="pop-text">
                    <strong>{{ v.name }}</strong>
                    <small>{{ t('ui.mcp.toolCount', { n: v.tools.length }) }}</small>
                  </span>
                </button>
                <button
                  v-if="v.tools.length"
                  type="button"
                  class="expand"
                  :aria-expanded="mcpExpanded === v.id"
                  :title="t('ui.mcp.pickTool')"
                  @click="mcpExpanded = mcpExpanded === v.id ? null : v.id"
                >
                  <ChevronRight :size="15" :class="{ down: mcpExpanded === v.id }" />
                </button>
              </div>
              <div v-if="mcpExpanded === v.id" class="mcp-tools">
                <button v-for="tool in v.tools" :key="tool.name" type="button" class="mcp-tool" @click="useConnector(v.name, tool.name, close)">
                  <code>{{ tool.title || tool.name }}</code>
                  <small>{{ tool.description }}</small>
                </button>
              </div>
            </template>
            <button type="button" class="pop-link" @click="manageConnectors(close)">{{ t('ui.mcp.manage') }}</button>
          </template>
        </Popover>

        <!-- 放不下的按钮收在这里，变宽后逐个放回去 -->
        <Popover v-if="[...barOv.hidden].some((k) => k !== 'quota')" class="ov-more" :width="220">
          <template #trigger="{ toggle, open }">
            <button type="button" class="tool-btn icon-only" :class="{ open }" :title="t('ui.toolbarMore')" :aria-label="t('ui.toolbarMore')" @click="toggle">
              <MoreHorizontal :size="17" />
            </button>
          </template>
          <template #default="{ close }">
            <button v-if="barOv.isHidden('deep')" type="button" class="pop-opt" @click="toggleDeep">
              <Brain :size="16" class="ico" :class="{ on: deepThinking }" />
              <span class="pop-text"><strong>{{ t('input.deepThinking') }}</strong></span>
              <CheckIcon v-if="deepThinking" :size="15" class="tick" />
            </button>
            <button v-if="barOv.isHidden('web')" type="button" class="pop-opt" @click="toggleWeb">
              <Globe :size="16" class="ico" :class="{ on: webSearchOn }" />
              <span class="pop-text"><strong>{{ t('input.webSearch') }}</strong></span>
              <CheckIcon v-if="webSearchOn" :size="15" class="tick" />
            </button>
            <button v-if="barOv.isHidden('skills')" type="button" class="pop-opt" @click="openFromMore('skills', close)">
              <Puzzle :size="16" class="ico" />
              <span class="pop-text"><strong>{{ t('ui.skills.button') }}</strong></span>
              <ChevronRight :size="15" class="tick" />
            </button>
            <button v-if="barOv.isHidden('mcp')" type="button" class="pop-opt" @click="openFromMore('mcp', close)">
              <Plug :size="16" class="ico" />
              <span class="pop-text"><strong>{{ t('ui.mcp.title') }}</strong></span>
              <em v-if="connected.length" class="count">{{ connected.length }}</em>
              <ChevronRight :size="15" class="tick" />
            </button>
            <button v-if="barOv.isHidden('enhance')" type="button" class="pop-opt" :disabled="!canEnhance || enhancing" @click="close(); enhance()">
              <Sparkles :size="16" class="ico" />
              <span class="pop-text"><strong>{{ t('input.enhance') }}</strong></span>
            </button>
            <button v-if="barOv.isHidden('attach')" type="button" class="pop-opt" @click="close(); pickFiles()">
              <Paperclip :size="16" class="ico" />
              <span class="pop-text"><strong>{{ t('input.attach') }}</strong></span>
            </button>
            <button
              v-if="barOv.isHidden('mic')"
              type="button"
              class="pop-opt"
              :disabled="offline || speech.phase === 'working'"
              @click="close(); speech.phase === 'idle' ? startSpeech() : cancelSpeech()"
            >
              <Mic :size="16" class="ico" :class="{ on: speech.phase !== 'idle' }" />
              <span class="pop-text"><strong>{{ t(speech.phase === 'idle' ? 'ui.speech.start' : 'ui.speech.cancel') }}</strong></span>
            </button>
          </template>
        </Popover>

        <!-- 翻译语言 -->
        <div v-if="mode === 'translate'" class="langs ov-shrink" data-ov-shrink="langs">
          <select :value="from" :aria-label="t('translate.from')" @change="setTranslate(($event.target as HTMLSelectElement).value, to)">
            <option value="auto">{{ t('translate.auto') }}</option>
            <option v-for="l in langs" :key="l" :value="l">{{ t(`translate.langs.${l}`) }}</option>
          </select>
          <button type="button" class="swap" :disabled="from === 'auto'" :title="t('translate.swap')" @click="swap">
            <ArrowLeftRight :size="14" />
          </button>
          <select :value="to" :aria-label="t('translate.to')" @change="setTranslate(from, ($event.target as HTMLSelectElement).value)">
            <option v-for="l in langs" :key="l" :value="l">{{ t(`translate.langs.${l}`) }}</option>
          </select>
        </div>

        <span class="spacer" />
        <!-- 今日额度圆环：后台设了每日上限才显示 -->
        <QuotaRing data-ov="quota" :class="{ 'ov-hide': barOv.isHidden('quota') }" />
        <button
          v-if="original !== null"
          type="button"
          class="tool-btn icon-only"
          :title="t('input.restoreOriginal')"
          @click="restoreOriginal"
        >
          <RotateCcw :size="17" />
        </button>
        <button
          v-else-if="mode !== 'translate'"
          type="button"
          class="tool-btn icon-only enhance"
          data-ov="enhance"
          :class="{ on: enhancing, 'ov-hide': barOv.isHidden('enhance') }"
          :disabled="!canEnhance || enhancing"
          :title="enhancing ? t('input.enhancing') : t('input.enhance')"
          @click="enhance"
        >
          <Loader2 v-if="enhancing" :size="17" class="spin" />
          <Sparkles v-else :size="17" />
        </button>
        <button type="button" class="tool-btn icon-only" data-ov="attach" :class="{ 'ov-hide': barOv.isHidden('attach') }" :title="t('input.attach')" @click="pickFiles">
          <Paperclip :size="17" />
        </button>
        <button
          v-if="micReady"
          type="button"
          class="tool-btn icon-only"
          data-ov="mic"
          :class="{ on: speech.phase !== 'idle', 'ov-hide': barOv.isHidden('mic') }"
          :disabled="offline || speech.phase === 'working'"
          :title="t(speech.phase === 'idle' ? 'ui.speech.start' : 'ui.speech.cancel')"
          @click="speech.phase === 'idle' ? startSpeech() : cancelSpeech()"
        >
          <Mic :size="17" />
        </button>
        <template v-if="busy">
          <span class="send-wrap">
            <button
              type="button"
              class="send send-go"
              :disabled="!canSend"
              :title="t('ui.interject.sendHint')"
              @pointerdown="onSendDown"
              @pointerup="onSendUp"
              @pointerleave="onSendUp"
              @click="onSendClick"
              @contextmenu="onSendMenu"
            >
              <ArrowUp :size="18" />
            </button>
            <div v-if="menuOpen" class="send-menu" role="menu">
              <button type="button" class="pop-opt" role="menuitem" @click="submit('steer')">
                <CornerDownRight :size="15" />
                <span class="opt-body"><b>{{ t('ui.interject.steer') }}</b><small>{{ t('ui.interject.steerHint') }}</small></span>
              </button>
              <button type="button" class="pop-opt" role="menuitem" @click="submit('interrupt')">
                <Zap :size="15" />
                <span class="opt-body"><b>{{ t('ui.interject.interrupt') }}</b><small>{{ t('ui.interject.interruptHint') }}</small></span>
              </button>
              <button type="button" class="pop-opt" role="menuitem" @click="submit('queue')">
                <ListPlus :size="15" />
                <span class="opt-body"><b>{{ t('ui.interject.queue') }}</b><small>{{ t('ui.interject.queueHint') }}</small></span>
              </button>
            </div>
          </span>
          <button type="button" class="tool-btn icon-only" :class="{ on: paused }" :title="t(paused ? 'ui.pause.resume' : 'ui.pause.pause')" @click="togglePause">
            <component :is="paused ? Play : Pause" :size="16" />
          </button>
          <button
            type="button"
            class="send stop"
            :class="{ stopping }"
            :disabled="stopping"
            :title="t(stopping ? 'input.stopping' : 'input.stop')"
            :aria-label="t(stopping ? 'input.stopping' : 'input.stop')"
            @click="stop"
          >
            <Loader2 v-if="stopping" :size="15" class="spin" />
            <Square v-else :size="13" fill="currentColor" />
          </button>
        </template>
        <button v-else type="button" class="send" :disabled="!canSend" :title="t('input.send')" @click="submit()">
          <ArrowUp :size="18" />
        </button>
      </div>
    </div>
    <div ref="footEl" class="foot">
      <template v-if="mode === 'agent'">
        <WorkspacePicker />
        <PermissionPicker v-if="!currentExpert?.readOnly" />
        <button
          type="button"
          class="long-btn"
          data-ov="plan"
          :class="{ on: planMode, 'ov-hide': footOv.isHidden('plan') }"
          :aria-pressed="planMode"
          :title="planMode ? t('ui.planMode.on') : t('ui.planMode.off')"
          @click="setPlanMode(!planMode)"
        >
          <ClipboardList :size="13" /> {{ t('ui.planMode.label') }}
        </button>
        <button
          v-if="longAllowed"
          type="button"
          class="long-btn"
          data-ov="long"
          :class="{ on: longMode, 'ov-hide': footOv.isHidden('long') }"
          :aria-pressed="longMode"
          :title="longMode ? t('ui.longTask.on', { steps: 300, n: 5 }) : t('ui.longTask.off')"
          @click="setLongMode(!longMode)"
        >
          <Timer :size="13" /> {{ t('ui.longTask.label') }}
        </button>
        <button
          v-if="sandboxAllowed"
          type="button"
          class="long-btn"
          data-ov="sandbox"
          :class="{ on: sandboxOn, 'ov-hide': footOv.isHidden('sandbox') }"
          :aria-pressed="sandboxOn"
          :disabled="sandboxBusy || busy"
          :title="sandboxOn ? t('ui.sandboxTask.on') : t('ui.sandboxTask.off')"
          @click="toggleSandbox"
        >
          <Loader2 v-if="sandboxBusy" :size="13" class="spin" /><Box v-else :size="13" /> {{ t('ui.sandboxTask.label') }}
        </button>
        <Popover v-if="[...footOv.hidden].some((k) => k !== 'note')" class="ov-more" :width="220">
          <template #trigger="{ toggle, open }">
            <button type="button" class="long-btn" :class="{ on: open }" :title="t('ui.toolbarMore')" :aria-label="t('ui.toolbarMore')" @click="toggle">
              <MoreHorizontal :size="14" />
            </button>
          </template>
          <template #default="{ close }">
            <button v-if="footOv.isHidden('plan')" type="button" class="pop-opt" @click="setPlanMode(!planMode)">
              <ClipboardList :size="16" class="ico" :class="{ on: planMode }" />
              <span class="pop-text"><strong>{{ t('ui.planMode.label') }}</strong></span>
              <CheckIcon v-if="planMode" :size="15" class="tick" />
            </button>
            <button v-if="footOv.isHidden('long')" type="button" class="pop-opt" @click="setLongMode(!longMode)">
              <Timer :size="16" class="ico" :class="{ on: longMode }" />
              <span class="pop-text"><strong>{{ t('ui.longTask.label') }}</strong></span>
              <CheckIcon v-if="longMode" :size="15" class="tick" />
            </button>
            <button
              v-if="footOv.isHidden('sandbox')"
              type="button"
              class="pop-opt"
              :disabled="sandboxBusy || busy"
              @click="close(); toggleSandbox()"
            >
              <Box :size="16" class="ico" :class="{ on: sandboxOn }" />
              <span class="pop-text"><strong>{{ t('ui.sandboxTask.label') }}</strong></span>
              <CheckIcon v-if="sandboxOn" :size="15" class="tick" />
            </button>
          </template>
        </Popover>
      </template>
      <span class="spacer" />
      <span class="ai-note" data-ov="note" :class="{ 'ov-hide': footOv.isHidden('note') }">{{ t('ui.safety.other') }}</span>
    </div>
    <ConfirmDialog
      v-if="confirmSandboxOff"
      :title="t('ui.sandboxTask.offTitle')"
      :body="t('ui.sandboxTask.offBody')"
      :ok="t('ui.sandboxTask.offOk')"
      danger
      @ok="applySandbox(false)"
      @cancel="confirmSandboxOff = false"
    />
    <!-- /permission full：和权限选择器一样先确认风险 -->
    <FullAccessDialog v-if="askFull" @ok="confirmFull" @cancel="askFull = false" />
  </div>
</template>

<style scoped>
.composer-wrap {
  padding: 0 32px 14px;
}
.composer-wrap.home {
  padding: 0;
}
/* 录音条：跟着输入框的圆角走，像是从卡片里长出来的 */
.recorder {
  display: flex;
  gap: 10px;
  align-items: center;
  margin: 10px 12px 2px;
  padding: 7px 8px 7px 14px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth-sunk);
}
.rec-ico {
  display: grid;
  flex: none;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  color: var(--ink-soft);
}
.rec-ico.live {
  background: color-mix(in srgb, var(--red) 14%, transparent);
  color: var(--red);
  animation: pulse 1.6s ease-in-out infinite;
}
@keyframes pulse {
  50% {
    background: color-mix(in srgb, var(--red) 30%, transparent);
  }
}
.spin {
  animation: spin 900ms linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
/* 说话时跳动的竖条。高度由实际响度算出来，不是固定动画 */
.wave {
  display: flex;
  flex: none;
  gap: 3px;
  align-items: center;
  height: 20px;
}
.wave i {
  width: 3px;
  min-height: 3px;
  border-radius: 999px;
  background: var(--indigo);
  transition: height 110ms ease-out;
}
.elapsed {
  flex: none;
  color: var(--ink);
  font-size: calc(13px * var(--font-scale));
  font-variant-numeric: tabular-nums;
}
.elapsed.warn {
  color: var(--red);
}
.rec-hint {
  flex: 1;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.rec-btn {
  display: grid;
  flex: none;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 1px solid var(--line);
  border-radius: 50%;
  background: var(--cloth);
  color: var(--ink-soft);
  cursor: pointer;
}
.rec-btn:hover:not(:disabled) {
  border-color: var(--ink-soft);
  color: var(--ink);
}
.rec-btn.done:hover:not(:disabled) {
  border-color: var(--indigo);
  color: var(--indigo);
}
.rec-btn:disabled {
  opacity: 0.45;
  cursor: default;
}
@media (prefers-reduced-motion: reduce) {
  .rec-ico.live,
  .spin {
    animation: none;
  }
  .wave i {
    transition: none;
  }
}

.slash-menu {
  display: flex;
  flex-direction: column;
  gap: 1px;
  max-height: 260px;
  margin: 6px 8px 0;
  padding: 4px;
  overflow: auto;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--loom);
}
.slash-item {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
  padding: 6px 8px;
  border-radius: 6px;
  color: var(--ink);
  font-size: var(--t-sm);
  text-align: left;
}
.slash-item.on {
  background: var(--cloth-sunk);
}
.slash-name {
  flex: none;
  color: var(--indigo);
  font-weight: 600;
}
.slash-args {
  flex: none;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.slash-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.slash-src {
  flex: none;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
  font-size: 10px;
  line-height: 16px;
}
.slash-src.it {
  color: var(--indigo);
}
.card {
  max-width: var(--column);
  margin: 0 auto;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-xl);
  background: var(--cloth);
  box-shadow: var(--shadow-card);
  transition:
    border-color 120ms,
    box-shadow 120ms;
}
.home .card {
  max-width: none;
}
.card:focus-within {
  border-color: color-mix(in srgb, var(--indigo) 55%, var(--line-strong));
  box-shadow:
    0 0 0 4px var(--indigo-wash),
    var(--shadow-card);
}
.pending {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 12px 14px 0;
}
.chip {
  display: flex;
  align-items: center;
  gap: 6px;
  max-width: 240px;
  height: 32px;
  padding: 0 4px 0 8px;
  border-radius: 8px;
  background: var(--chip);
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.chip img {
  width: 22px;
  height: 22px;
  object-fit: cover;
  border-radius: 4px;
}
.chip-open {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  height: 100%;
  cursor: pointer;
}
.chip-open:hover .chip-name {
  color: var(--ink);
  text-decoration: underline;
}
.chip.saving .chip-open {
  cursor: progress;
}
.chip.saving img {
  opacity: 0.6;
}
.chip-name {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.chip button {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 4px;
  color: var(--ink-faint);
}
.chip button:hover {
  background: var(--line);
  color: var(--ink);
}
.paste-card {
  position: relative;
  display: flex;
  flex-direction: column;
  width: 140px;
  height: 104px;
  padding: 8px 8px 6px 10px;
  border-radius: 10px;
  border: 1px solid var(--line-strong);
  background: var(--cloth);
}
.paste-body {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  padding: 0 10px 0 0;
  font-size: 11px;
  line-height: 1.45;
  color: var(--ink-faint);
  text-align: left;
  white-space: pre-wrap;
  word-break: break-all;
  cursor: zoom-in;
}
.paste-remove {
  position: absolute;
  top: -7px;
  right: -7px;
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  border: 1px solid var(--line-strong);
  background: var(--cloth);
  color: var(--ink-soft);
  opacity: 0;
  transition: opacity 0.15s;
}
.paste-card:hover .paste-remove,
.paste-remove:focus-visible {
  opacity: 1;
}
.paste-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 4px;
}
.paste-badge {
  padding: 1px 6px;
  border-radius: 4px;
  border: 1px solid var(--line-strong);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.04em;
  color: var(--ink-soft);
}
.paste-edit {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-soft);
}
.paste-edit:hover {
  background: var(--chip);
  color: var(--ink);
}
textarea {
  display: block;
  width: 100%;
  min-height: 54px;
  max-height: 260px;
  padding: 16px 18px 6px;
  border: 0;
  outline: 0;
  resize: none;
  background: transparent;
  line-height: 1.6;
}
.home textarea {
  min-height: 96px;
}
textarea::placeholder {
  color: var(--ink-faint);
}
.bar {
  display: flex;
  align-items: center;
  gap: 4px;
  /* 不换行：放不下的按钮由 useOverflow 收进「…」 */
  flex-wrap: nowrap;
  min-width: 0;
  padding: 6px 10px 10px;
}
.bar > *,
.foot > * {
  flex-shrink: 0;
}
.bar > .spacer,
.foot > .spacer {
  flex-shrink: 1;
}
/* 窗口极窄、能收的都收了还放不下时，模型名缩成省略号，保证发送按钮看得见 */
.bar > .ov-shrink {
  display: flex;
  flex-shrink: 1;
  min-width: 40px;
}
.bar > .ov-shrink > :deep(.trigger) {
  flex: 0 1 auto;
  min-width: 0;
}
/* 收起的按钮：不显示、不占位；技能 / 连接器的弹出面板还挂在原处，能从「…」打开 */
.tool-btn.ov-hide,
.quota.ov-hide,
.long-btn.ov-hide,
.ai-note.ov-hide,
.ov-hide > .tool-btn {
  display: none;
}
.pop-root.ov-hide {
  margin-left: -4px;
}
.tool-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 10px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 500;
}
.tool-btn:hover,
.tool-btn.open {
  background: var(--chip);
  color: var(--ink);
}
.tool-btn.on {
  background: var(--indigo-wash, color-mix(in srgb, var(--indigo) 14%, transparent));
  color: var(--indigo);
}
.expert-chip {
  max-width: 220px;
  color: var(--ink);
  cursor: default;
}
.expert-chip:hover {
  background: transparent;
}
.expert-chip > span:last-child {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.mode-btn {
  background: var(--chip);
  color: var(--ink);
}
.icon-only {
  width: 34px;
  padding: 0;
  justify-content: center;
}
.chev {
  color: var(--ink-faint);
}
.langs {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-left: 4px;
}
.langs select {
  min-width: 0;
  flex: 0 1 auto;
  height: 30px;
  padding: 0 6px;
  border-radius: 8px;
  border: 1px solid var(--line);
  background: var(--cloth);
  font-size: var(--t-sm);
}
.swap {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  color: var(--ink-soft);
}
.swap:disabled {
  opacity: 0.35;
  cursor: default;
}
.spacer {
  flex: 1;
}
.send {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  margin-left: 4px;
  border-radius: 50%;
  background: var(--pill);
  color: var(--pill-ink);
}
.send:hover:not(:disabled) {
  background: var(--indigo);
  color: #fff;
}
.send:disabled {
  background: var(--line-strong);
  color: var(--cloth);
  cursor: default;
}
.send.stop {
  background: var(--pill);
}
.paused-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 8px 10px 0;
  padding: 6px 6px 6px 10px;
  border-radius: 8px;
  background: var(--amber-wash);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.paused-bar > span {
  flex: 1;
}
.paused-go {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 10px;
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
}
.interrupted-bar {
  background: var(--indigo-wash);
}
.paused-go.ghost {
  background: transparent;
  color: var(--ink-soft);
}
.long-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 999px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.long-btn:hover {
  background: var(--chip);
  color: var(--ink-soft);
}
.long-btn.on {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.paused-go:hover {
  background: var(--chip);
}
.send-wrap {
  position: relative;
  display: inline-flex;
}
.send-menu {
  position: absolute;
  right: 0;
  bottom: calc(100% + 8px);
  z-index: 30;
  width: 260px;
  padding: 4px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop, 0 8px 24px rgb(0 0 0 / 0.14));
}
.send-menu .pop-opt {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  width: 100%;
  padding: 8px 10px;
  border-radius: 7px;
  text-align: left;
  color: var(--ink);
}
.send-menu .pop-opt:hover {
  background: var(--cloth-sunk);
}
.send-menu .pop-opt > svg {
  flex: none;
  margin-top: 2px;
  color: var(--ink-soft);
}
.opt-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.opt-body b {
  font-weight: 600;
  font-size: var(--t-sm);
}
.opt-body small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.inputs {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 10px 0;
}
.input-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 30px;
  padding: 3px 4px 3px 8px;
  border: 1px dashed var(--line-strong);
  border-radius: 8px;
  background: var(--cloth-sunk);
  font-size: var(--t-sm);
}
.input-tag {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 3px;
  font-size: var(--t-xs);
  color: var(--indigo);
}
.input-tag.queue {
  color: var(--ink-soft);
}
.input-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--ink);
}
.input-btn {
  display: grid;
  flex: none;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.input-btn:hover {
  background: var(--cloth);
  color: var(--ink);
}
.foot {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: nowrap;
  min-width: 0;
  max-width: var(--column);
  min-height: 26px;
  margin: 6px auto 0;
  padding: 0 2px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.home .foot {
  max-width: none;
}
.safety {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}
.count {
  padding: 0 6px;
  border-radius: 8px;
  background: var(--thread-wash);
  color: var(--thread);
  font-style: normal;
  font-size: calc(11px * var(--font-scale));
}
.pop-empty {
  margin: 6px 8px 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  line-height: 1.5;
}
.mcp-row {
  display: flex;
  align-items: center;
}
.mcp-row .pop-opt {
  flex: 1;
  min-width: 0;
}
.expand {
  display: grid;
  place-items: center;
  flex: none;
  width: 30px;
  height: 30px;
  border-radius: 7px;
  color: var(--ink-faint);
}
.expand:hover {
  background: var(--chip);
  color: var(--ink);
}
.expand svg {
  transition: transform 140ms;
}
.expand .down {
  transform: rotate(90deg);
}
.mcp-tools {
  margin: 0 0 4px 44px;
  padding-left: 8px;
  border-left: 2px solid var(--line);
}
.mcp-tool {
  display: flex;
  flex-direction: column;
  gap: 1px;
  width: 100%;
  padding: 6px 8px;
  border-radius: 7px;
  text-align: left;
}
.mcp-tool:hover {
  background: var(--chip);
}
.mcp-tool code {
  color: var(--ink);
  font-family: var(--font-code);
  font-size: var(--t-xs);
}
.mcp-tool small {
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pop-link {
  display: block;
  width: calc(100% - 8px);
  margin: 4px;
  padding: 7px 8px;
  border-top: 1px solid var(--line);
  color: var(--indigo);
  font-size: var(--t-xs);
  text-align: left;
}
.pop-link:hover {
  text-decoration: underline;
}
.pop-note {
  margin: 4px 8px 6px;
  font-size: var(--t-xs);
  color: var(--amber);
}
.pop-opt {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 8px;
  border-radius: 8px;
  text-align: left;
}
.pop-opt:hover {
  background: var(--chip);
}
.pop-opt .ico {
  flex: none;
  color: var(--ink-faint);
}
.pop-opt .ico.on {
  color: var(--indigo);
}
.pop-text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.pop-text strong {
  font-weight: 500;
  font-size: var(--t-sm);
}
.pop-text small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.tick {
  flex: none;
  color: var(--indigo);
}
@media (max-width: 720px) {
  .composer-wrap {
    padding: 0 12px 10px;
  }
  .hide-narrow,
  .ai-note {
    display: none;
  }
}
</style>
