/**
 * 数据保护提示（docs/data-protection.md）。
 *
 * 服务端把要发给外部模型的身份证、银行卡、手机号、邮箱换成占位符、密码密钥直接隐藏，回答回来时再换回真实值。
 * 以前有「发现就拦下」的档，员工看到的是「调用模型失败」、任务停在半路；现在一律脱敏照常发出，
 * 这里只负责让员工知道：这次对话里有哪些敏感信息、已经怎么处理了。
 *
 * 员工端每次发的是完整历史，同一个邮箱每一步都会再数一遍，所以按类型取「这个对话里见过的最大数」，不累加。
 */
import { reactive } from 'vue'
import { registerFeature } from '../../store'
import type { HostEvent } from '../../types'

export type MaskedKind = 'id_card' | 'bank_card' | 'phone' | 'email' | 'secret'
export const MASKED_KINDS: MaskedKind[] = ['id_card', 'bank_card', 'phone', 'email', 'secret']

interface ConversationPrivacy {
  counts: Partial<Record<MaskedKind, number>>
  /** 员工点了「知道了」时的总数；之后又出现新的敏感内容（总数变大）才再显示 */
  dismissedAt: number
}

export const privacy = reactive<{ byConversation: Record<string, ConversationPrivacy> }>({ byConversation: {} })

export function totalOf(p: ConversationPrivacy): number {
  return MASKED_KINDS.reduce((n, k) => n + (p.counts[k] ?? 0), 0)
}

/** 当前对话要不要显示提示 */
export function visibleFor(conversationId: string | null | undefined): ConversationPrivacy | null {
  const p = conversationId ? privacy.byConversation[conversationId] : undefined
  if (!p) return null
  return totalOf(p) > p.dismissedAt ? p : null
}

export function dismissPrivacy(conversationId: string) {
  const p = privacy.byConversation[conversationId]
  if (p) p.dismissedAt = totalOf(p)
}

export function noteMasked(conversationId: string, counts: Record<string, number>) {
  const p = (privacy.byConversation[conversationId] ??= { counts: {}, dismissedAt: 0 })
  for (const k of MASKED_KINDS) {
    const n = Number(counts[k] ?? 0)
    if (n > (p.counts[k] ?? 0)) p.counts[k] = n
  }
}

registerFeature({
  id: 'privacy',
  eventPrefix: 'privacy.',
  onEvent(e: HostEvent) {
    if (e.type !== 'privacy.masked') return
    noteMasked(e.conversationId, e.counts)
  },
})
