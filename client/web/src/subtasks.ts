import { i18n } from './i18n'
import { launchFile, mcpToolLabel, openPreview, state } from './store'
import type { FindingVerification, OutputFile, SubtaskDto, SubtaskExpert } from './types'

// 专家任务卡片和详情弹窗共用的小工具

/** 主助手派遣专家用的工具名 */
export const AskExpertTool = 'ask_expert'

/** 专家调用的工具名换成给人看的（内置工具按名字表翻译，MCP 工具显示「厂商 · 工具」），不认识的原样显示 */
export function toolLabel(name: string): string {
  if (!name) return ''
  const mcp = mcpToolLabel(name)
  if (mcp) return mcp
  const key = `tool.names.${name}`
  return i18n.global.te(key) ? i18n.global.t(key) : name
}

/** 「12 秒」「3 分 5 秒」 */
export function formatDuration(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000))
  if (total < 60) return i18n.global.t('ui.delegate.sec', { n: total })
  return i18n.global.t('ui.delegate.min', { m: Math.floor(total / 60), s: total % 60 })
}

export const VerificationOrder: FindingVerification[] = ['supported', 'source_verified', 'unverified', 'verification_failed']

/** 每种核验结果各有几条结论 */
export function verificationCounts(dto: SubtaskDto): Record<FindingVerification, number> {
  const counts = { supported: 0, source_verified: 0, unverified: 0, verification_failed: 0 }
  for (const f of dto.findings ?? []) if (f.verification in counts) counts[f.verification]++
  return counts
}

/** ask_expert 的调用参数：专家还没开始（没收到 expert.started）时卡片靠它显示派给谁、做什么 */
export function askExpertArgs(json: string | undefined): { expert: SubtaskExpert | null; task: string } {
  let a: { expert?: unknown; task?: unknown } = {}
  try {
    a = JSON.parse(json ?? '{}') ?? {}
  } catch {
    // 参数不是合法 JSON 时只显示「正在派遣…」
  }
  const key = typeof a.expert === 'string' ? a.expert : ''
  const known = key ? state.experts.find((e) => e.id === key || e.name === key) : undefined
  const expert = known
    ? { id: known.id, name: known.name, avatar: known.avatar, version: known.version }
    : key
      ? { id: key, name: key, avatar: '', version: 0 }
      : null
  return { expert, task: typeof a.task === 'string' ? a.task : '' }
}

/** 一行任务摘要：只取第一行，太长截断 */
export function oneLine(text: string, max = 120): string {
  const line = (text ?? '').trim().split(/\r?\n/)[0] ?? ''
  return line.length > max ? line.slice(0, max) + '…' : line
}

// 宿主能在分屏里预览的格式（和 OutputFiles 的 previewable 同一套判断，这里只能按扩展名猜）
const Previewable = new Set(['md', 'txt', 'csv', 'json', 'log', 'xlsx', 'xls', 'docx', 'pptx', 'pdf', 'png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'html', 'htm'])

function asOutputFile(a: { path: string; name: string; exists: boolean }): OutputFile {
  const extension = (/\.([^.\\/]+)$/.exec(a.name || a.path)?.[1] ?? '').toLowerCase()
  return { path: a.path, name: a.name, extension, size: 0, modifiedAt: '', previewable: Previewable.has(extension), exists: a.exists }
}

/** 点产出文件：能预览的在右侧分屏打开，其余用默认应用打开（和 OutputFiles 一样） */
export function openArtifact(a: { path: string; name: string; exists: boolean }) {
  if (!a.exists) return
  const f = asOutputFile(a)
  // 窄窗口没有分屏，直接用默认应用打开
  if (f.previewable && window.innerWidth >= 760) void openPreview(f)
  else void launchFile(f)
}

export function artifactExt(a: { path: string; name: string }): string {
  return asOutputFile({ ...a, exists: true }).extension
}
