/**
 * 编码模式底部终端里员工自己的 PowerShell（宿主 Core/Terminal/UserTerminalFeature，Windows 伪控制台）。
 * 每个终端留一段最近的输出（切到别的终端再切回来时重放给 xterm），新输出推给正在显示的那个。
 * 员工在这里敲的命令和自己打开 PowerShell 一样，不经过 AI、不进 AI 的上下文。
 */
import { reactive } from 'vue'
import { bridge } from '../../bridge'
import { registerFeature, toast } from '../../store'
import type { HostEvent } from '../../types'

export interface PtySession {
  id: string
  title: string
  cwd: string
  conversationId: string
  exited: boolean
  exitCode?: number
}

/** 每个终端最多留这么多字符的输出用于重放 */
const MaxScrollback = 400_000

export const pty = reactive<{ sessions: PtySession[] }>({ sessions: [] })
const scrollback = new Map<string, string>()
const listeners = new Map<string, (data: string) => void>()

export async function openPty(conversationId: string, cols: number, rows: number): Promise<PtySession | null> {
  try {
    const r = await bridge.openPty(conversationId, cols, rows)
    if (!r.ok) {
      toast(r.message)
      return null
    }
    const session: PtySession = { id: r.id, title: r.title, cwd: r.cwd ?? '', conversationId, exited: false }
    pty.sessions.push(session)
    return session
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
    return null
  }
}

export async function closePty(id: string) {
  const i = pty.sessions.findIndex((x) => x.id === id)
  if (i >= 0) pty.sessions.splice(i, 1)
  scrollback.delete(id)
  listeners.delete(id)
  try {
    await bridge.closePty(id)
  } catch {
    // 已经关了
  }
}

// ---------- AI 的后台命令（伪控制台里跑的）：同样用 xterm 画，员工能往里敲字 ----------
const runKey = (conversationId: string, runId: string) => conversationId + '\n' + runId

/** xterm 挂到 AI 的一条后台命令上：先重放（事件里攒的，或者 terminal.list 带来的 seed），之后的新输出直接给它 */
export function attachRun(conversationId: string, runId: string, seed: string | null | undefined, write: (data: string) => void) {
  const key = runKey(conversationId, runId)
  if (!scrollback.has(key) && seed) scrollback.set(key, seed)
  listeners.set(key, write)
  const old = scrollback.get(key)
  if (old) write(old)
}

export function detachRun(conversationId: string, runId: string) {
  listeners.delete(runKey(conversationId, runId))
}

/** xterm 挂上来：先重放留着的输出，之后的新输出直接给它 */
export function attachPty(id: string, write: (data: string) => void) {
  listeners.set(id, write)
  const old = scrollback.get(id)
  if (old) write(old)
}

export function detachPty(id: string) {
  listeners.delete(id)
}

registerFeature({
  id: 'pty',
  eventPrefix: 'pty.',
  onEvent(e: HostEvent) {
    if (e.type === 'pty.runOutput') {
      const key = runKey(e.conversationId, e.runId)
      const next = (scrollback.get(key) ?? '') + e.data
      scrollback.set(key, next.length > MaxScrollback ? next.slice(next.length - MaxScrollback) : next)
      listeners.get(key)?.(e.data)
    } else if (e.type === 'pty.output') {
      const next = (scrollback.get(e.id) ?? '') + e.data
      scrollback.set(e.id, next.length > MaxScrollback ? next.slice(next.length - MaxScrollback) : next)
      listeners.get(e.id)?.(e.data)
    } else if (e.type === 'pty.exit') {
      const s = pty.sessions.find((x) => x.id === e.id)
      if (s) {
        s.exited = true
        s.exitCode = e.exitCode
      }
    }
  },
})
