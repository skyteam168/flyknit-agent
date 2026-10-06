import type { AgentTaskKind, RunStatus } from '@/api/types'
import { isOnline } from './format'

/** 任务目录：和服务端 agent_tasks.CATALOG 对应。 */
export const TASK_KINDS: { kind: AgentTaskKind; label: string; desc: string }[] = [
  { kind: 'collect_info', label: '采集电脑信息', desc: '收集硬件、系统、已装软件等，回传到台账。员工无感。' },
  { kind: 'clean', label: '清理缓存', desc: '清理临时文件、回收站、浏览器与缩略图缓存等。' },
  { kind: 'optimize', label: '系统提速', desc: '清缓存 + 刷新 DNS + 优化磁盘（SSD 做 TRIM，机械盘整理碎片）。' },
  { kind: 'install', label: '安装软件', desc: '下发安装包到电脑静默安装。' },
  { kind: 'repair', label: '系统修复', desc: 'DISM / SFC 修复系统，可选重置网络与更新组件。' },
  { kind: 'restart', label: '重启电脑', desc: '带倒计时提醒员工后重启。' },
]

export const KIND_LABELS: Record<string, string> = Object.fromEntries(TASK_KINDS.map((t) => [t.kind, t.label]))
export const kindLabel = (k: string) => KIND_LABELS[k] ?? k

export const CLEAN_TARGETS: { value: string; label: string }[] = [
  { value: 'windows_temp', label: '系统临时文件' },
  { value: 'user_temp', label: '用户临时文件' },
  { value: 'recycle_bin', label: '回收站' },
  { value: 'browser_cache', label: '浏览器缓存' },
  { value: 'thumbnails', label: '缩略图缓存' },
  { value: 'update_cache', label: 'Windows 更新下载缓存' },
]

export const REPAIR_ACTIONS: { value: string; label: string }[] = [
  { value: 'dism', label: '修复系统映像（DISM）' },
  { value: 'sfc', label: '检查系统文件（SFC）' },
  { value: 'network', label: '重置网络' },
  { value: 'windows_update', label: '重置 Windows 更新组件' },
]

type TagType = 'danger' | 'success' | 'info' | 'warning' | 'primary'

export const STATUS_META: Record<RunStatus, { label: string; type: TagType }> = {
  pending: { label: '待执行', type: 'info' },
  running: { label: '执行中', type: 'warning' },
  succeeded: { label: '成功', type: 'success' },
  failed: { label: '失败', type: 'danger' },
  cancelled: { label: '已取消', type: 'info' },
  expired: { label: '已过期', type: 'info' },
}
export const statusMeta = (s: string) => STATUS_META[s as RunStatus] ?? { label: s, type: 'info' as TagType }

/** 运维代理在线：2 分钟内拉过任务。 */
export const agentOnline = (lastSeen: string | null) => isOnline(lastSeen, 2)
