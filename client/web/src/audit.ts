import type { SecurityEvent } from './types'

// 审计记录的分类。本机只存了工具名、命令原文和拦截原因，类型从这几样推出来：
// 原因由客户端用中文写入（不随界面语言变），所以按中文关键字认网络拦截是稳的。
export type AuditType = 'command' | 'file' | 'network' | 'config' | 'other'

export const auditTypes: AuditType[] = ['command', 'file', 'network', 'config', 'other']

const fileTools = new Set(['write_file', 'delete_path', 'read_file', 'list_dir', 'search_files'])
const hasUrl = (s: string) => /\b(https?|ftp):\/\//i.test(s)

export function auditType(e: SecurityEvent): AuditType {
  if (e.tool === 'security_settings') return 'config'
  if (e.decision === 'allowed' || /网络白名单|网址|访问网络/.test(e.reason) || (e.tool === 'open_app' && hasUrl(e.detail))) return 'network'
  if (fileTools.has(e.tool)) return 'file'
  if (e.tool === 'run_shell' || e.tool === 'open_app') return 'command'
  return 'other'
}

export type AuditRange = 'today' | 'week' | 'month'

export function inRange(e: SecurityEvent, range: '' | AuditRange) {
  if (!range) return true
  const at = new Date(e.createdAt).getTime()
  if (range === 'today') {
    const start = new Date()
    start.setHours(0, 0, 0, 0)
    return at >= start.getTime()
  }
  const days = range === 'week' ? 7 : 30
  return at >= Date.now() - days * 86400000
}
