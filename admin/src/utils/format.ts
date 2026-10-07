const nf = new Intl.NumberFormat('zh-CN')

export const num = (n: number | null | undefined) => nf.format(n ?? 0)

/** Token 数量的简写：12,345 → 1.2 万，123,456,789 → 1.23 亿 */
export function short(n: number | null | undefined): string {
  const v = n ?? 0
  if (Math.abs(v) >= 1e8) return `${(v / 1e8).toFixed(2).replace(/\.?0+$/, '')} 亿`
  if (Math.abs(v) >= 1e4) return `${(v / 1e4).toFixed(1).replace(/\.0$/, '')} 万`
  return nf.format(v)
}

/** 服务端时间都是 UTC，不带时区的按 UTC 解析 */
export function parseTime(s: string | null | undefined): Date | null {
  if (!s) return null
  return new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(s) ? s : `${s}Z`)
}

export function dateTime(s: string | null | undefined): string {
  const d = parseTime(s)
  if (!d) return '—'
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}

export function relative(s: string | null | undefined): string {
  const d = parseTime(s)
  if (!d) return '从未'
  const sec = Math.round((Date.now() - d.getTime()) / 1000)
  if (sec < 60) return '刚刚'
  if (sec < 3600) return `${Math.floor(sec / 60)} 分钟前`
  if (sec < 86400) return `${Math.floor(sec / 3600)} 小时前`
  if (sec < 86400 * 30) return `${Math.floor(sec / 86400)} 天前`
  return dateTime(s)
}

export function bytes(n: number): string {
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`
  if (n >= 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${n} B`
}

/** 环比变化：返回百分比，昨天为 0 时返回 null（没法比） */
export function change(today: number, yesterday: number): number | null {
  if (!yesterday) return null
  return Math.round(((today - yesterday) / yesterday) * 100)
}

export const SCENE_LABELS: Record<string, string> = {
  chat: '对话',
  agent: '任务',
  translate: '翻译',
  title: '标题生成',
  vision: '识图',
  asr: '语音转文字',
  embedding: '语义检索',
}
export const sceneLabel = (s: string) => SCENE_LABELS[s] ?? s

export const DECISION_LABELS: Record<string, { label: string; type: 'danger' | 'success' | 'info' | 'warning' | 'primary' }> = {
  blocked: { label: '已拦截', type: 'danger' },
  approved: { label: '用户放行', type: 'success' },
  remembered: { label: '规则放行', type: 'primary' },
  rejected: { label: '用户拒绝', type: 'warning' },
  allowed: { label: '白名单放行', type: 'success' },
  changed: { label: '设置变更', type: 'info' },
  auto: { label: '自动执行', type: 'info' },
}

export function isOnline(lastSeen: string | null, minutes = 15) {
  const d = parseTime(lastSeen)
  return !!d && Date.now() - d.getTime() < minutes * 60 * 1000
}

export function randomPassword(length = 14) {
  const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#%-_'
  const buf = new Uint32Array(length)
  crypto.getRandomValues(buf)
  return Array.from(buf, (v) => chars[v % chars.length]).join('')
}
