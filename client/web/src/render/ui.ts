/**
 * ```ui 代码块：模型自己判断什么时候用、用哪一种，把回答里的一部分画成卡片，而不是一大段文字。
 * 说明给模型的部分在 PromptBuilder.ChartAndDiagramGuide，两边的 kind 和字段要一致。
 *
 * 这里只做校验和整理：模型写的东西一律当纯文字显示（不插 HTML），字段缺了就用默认值，
 * 结构不对就报错，由调用方退回成普通代码块。
 */

export type UiKind = 'checklist' | 'callouts' | 'steps' | 'stats' | 'timeline' | 'compare' | 'progress' | 'facts' | 'actions'

export const UiKinds: UiKind[] = ['checklist', 'callouts', 'steps', 'stats', 'timeline', 'compare', 'progress', 'facts', 'actions']

/** 状态 / 级别统一成这几种，决定颜色和默认图标 */
export type UiTone = 'ok' | 'todo' | 'warn' | 'fail' | 'info' | 'tip' | 'running' | 'skip'

export interface UiItem {
  title: string
  detail: string
  icon: string
  tone: UiTone | ''
  /** stats：数值；timeline：时间；facts：值；progress：百分比 0～100 */
  value: string
  unit: string
  /** stats：变化，如 +12%；趋势 up / down / flat；good 表示哪个方向是好事 */
  delta: string
  trend: 'up' | 'down' | 'flat' | ''
  good: 'up' | 'down'
  percent: number
  /** actions：点了以后发出去的话；不写就发按钮上的字 */
  prompt: string
  /** compare */
  pros: string[]
  cons: string[]
  tags: string[]
  recommended: boolean
}

export interface UiSpec {
  kind: UiKind
  title: string
  items: UiItem[]
}

export class UiSpecError extends Error {}

export const MaxItems = 24

const toneAliases: Record<string, UiTone> = {
  ok: 'ok', done: 'ok', pass: 'ok', passed: 'ok', success: 'ok', completed: 'ok', yes: 'ok',
  todo: 'todo', pending: 'todo', open: 'todo', wait: 'todo', waiting: 'todo',
  warn: 'warn', warning: 'warn', medium: 'warn', caution: 'warn',
  fail: 'fail', failed: 'fail', error: 'fail', critical: 'fail', high: 'fail', danger: 'fail', blocked: 'fail', no: 'fail',
  info: 'info', note: 'info', low: 'info',
  tip: 'tip', idea: 'tip', suggestion: 'tip',
  running: 'running', doing: 'running', current: 'running', in_progress: 'running', active: 'running',
  skip: 'skip', skipped: 'skip', na: 'skip',
}

const str = (v: unknown, max: number) => (typeof v === 'string' || typeof v === 'number' ? String(v).trim().slice(0, max) : '')
const list = (v: unknown, max: number) => (Array.isArray(v) ? v.map((x) => str(x, max)).filter(Boolean).slice(0, 8) : [])

function tone(v: unknown): UiTone | '' {
  const key = str(v, 20).toLowerCase().replace(/[\s-]/g, '_')
  return toneAliases[key] ?? ''
}

function trend(v: unknown): UiItem['trend'] {
  const s = str(v, 10).toLowerCase()
  return s === 'up' || s === 'down' || s === 'flat' ? s : ''
}

function item(raw: unknown): UiItem {
  // 只给了一个字符串：当标题
  const o = (typeof raw === 'object' && raw !== null ? raw : { title: raw }) as Record<string, unknown>
  const title = str(o.title ?? o.label ?? o.name ?? o.step, 200)
  const value = str(o.value ?? o.time ?? o.date, 120)
  const pct = Number(o.percent ?? o.progress ?? (typeof o.value === 'number' ? o.value : NaN))
  const max = Number(o.max ?? 100) || 100
  return {
    title,
    detail: str(o.detail ?? o.description ?? o.desc ?? o.text, 600),
    icon: str(o.icon, 30).toLowerCase(),
    tone: tone(o.status ?? o.level ?? o.tone ?? o.severity),
    value,
    unit: str(o.unit, 20),
    delta: str(o.delta ?? o.change, 30),
    trend: trend(o.trend),
    good: str(o.good, 6).toLowerCase() === 'down' ? 'down' : 'up',
    percent: Number.isFinite(pct) ? Math.max(0, Math.min(100, (pct / max) * 100)) : NaN,
    prompt: str(o.prompt ?? o.message ?? o.send, 500),
    pros: list(o.pros, 200),
    cons: list(o.cons, 200),
    tags: list(o.tags, 30),
    recommended: o.recommended === true,
  }
}

export function parseUiSpec(code: string): UiSpec {
  let raw: unknown
  try {
    raw = JSON.parse(code)
  } catch (e) {
    throw new UiSpecError(`界面卡片的内容不是合法的 JSON：${(e as Error).message}`)
  }
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) throw new UiSpecError('界面卡片应该是一个对象')
  const o = raw as Record<string, unknown>
  const kind = str(o.kind ?? o.type, 20).toLowerCase() as UiKind
  if (!UiKinds.includes(kind)) throw new UiSpecError(`不认识的卡片类型 ${String(o.kind ?? o.type ?? '')}，可用：${UiKinds.join(' / ')}`)
  const source = Array.isArray(o.items) ? o.items : Array.isArray(o.columns) ? o.columns : Array.isArray(o.steps) ? o.steps : []
  const items = source.slice(0, MaxItems).map(item).filter((x) => x.title || x.value)
  if (items.length === 0) throw new UiSpecError('卡片里没有内容（items 是空的）')
  if (kind === 'progress' && items.some((x) => Number.isNaN(x.percent))) throw new UiSpecError('progress 的每一项都要有数字 value（0～100）')
  return { kind, title: str(o.title, 200), items }
}
