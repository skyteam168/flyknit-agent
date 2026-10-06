import { bridge } from './bridge'
import { state } from './store'
import type { ShortcutInfo } from './types'

/**
 * 快捷键分派。
 *
 * 命令表来自宿主（Flyknit.Core/Settings/Shortcuts.cs），这里只负责：
 * 把一次按键翻成统一写法 → 查表找到命令 id → 执行注册的处理函数。
 *
 * 加一条快捷键 = 宿主表里加一行 + 这里注册一个 handler + i18n 加个名字，
 * 不用动设置面板，也不用在各个组件里散落 keydown 判断。
 */

type Handler = () => void

const handlers = new Map<string, Handler>()

export function registerShortcut(id: string, run: Handler) {
  handlers.set(id, run)
}

export function hasHandler(id: string) {
  return handlers.has(id)
}

/** 浏览器 KeyboardEvent → "Ctrl+Shift+B"，和宿主的 Normalize 保持同一种写法 */
export function describeEvent(e: KeyboardEvent): string {
  const parts: string[] = []
  if (e.ctrlKey) parts.push('Ctrl')
  if (e.altKey) parts.push('Alt')
  if (e.shiftKey) parts.push('Shift')
  if (e.metaKey) parts.push('Meta')

  let key = e.key
  if (key === ' ') key = 'Space'
  else if (key === 'Escape') key = 'Escape'
  else if (key.startsWith('Arrow')) key = key.slice(5)
  else if (key === '+') key = '='
  else if (key.length === 1) key = key.toUpperCase()
  else key = key.charAt(0).toUpperCase() + key.slice(1)

  // 只按住修饰键不算一个快捷键
  if (['Control', 'Alt', 'Shift', 'Meta'].includes(e.key)) return ''
  parts.push(key)
  return parts.join('+')
}

/** 正在输入框里打字时，除了带修饰键的快捷键都让给输入 */
function isTyping(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null
  if (!el) return false
  const tag = el.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable
}

let bindings: ShortcutInfo[] = []

export function setBindings(list: ShortcutInfo[]) {
  bindings = list
}

/** 正在录制新按键时，所有快捷键暂停，否则按下的键会被当成命令执行 */
let capturing = false

export function setCapturing(on: boolean) {
  capturing = on
}

function onKeyDown(e: KeyboardEvent) {
  if (capturing) return
  const pressed = describeEvent(e)
  if (!pressed) return

  const hit = bindings.find((b) => b.binding && b.binding.toLowerCase() === pressed.toLowerCase())
  if (!hit) return

  // Enter / Shift+Enter 由输入框自己处理，这里不抢
  if (hit.fixed) return

  const bare = !e.ctrlKey && !e.altKey && !e.metaKey
  if (isTyping(e.target) && bare) return

  const run = handlers.get(hit.id)
  if (!run) return
  e.preventDefault()
  e.stopPropagation()
  run()
}

export function startShortcuts() {
  window.addEventListener('keydown', onKeyDown, { capture: true })
}

export async function loadShortcuts() {
  const list = await bridge.listShortcuts().catch(() => [])
  state.shortcuts = list
  setBindings(list)
}
