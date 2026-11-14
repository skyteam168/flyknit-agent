import { markRaw, type Component } from 'vue'
import type { Conversation } from './types'

/**
 * 消息列表的两个扩展点（docs/extension-architecture.md）：功能模块用它们把自己的东西画进对话，消息列表不认识具体功能。
 *
 * 1. 工具卡：某个工具调用不画成默认的工具卡片，而是画成功能自己的卡（团队的分工计划卡、团队报告卡）。
 *    组件收到 name（工具名）/ callId / args（调用参数 JSON 原文）/ conversationId / output（工具结果，还没结果时为空）。
 *    这些卡是给员工看的内容，所以这一轮做完后也不折进「已完成 N 步」里。
 * 2. 助手身份：某类对话里助手消息上方显示的头像和名字（团队对话里是「队长 · 主助手」）。
 */
const cards = new Map<string, Component>()

export function registerToolCard(toolName: string, component: Component) {
  if (cards.has(toolName)) throw new Error(`工具卡重复登记：${toolName}`)
  cards.set(toolName, markRaw(component))
}

export function toolCard(toolName: string): Component | undefined {
  return cards.get(toolName)
}

export interface AssistantIdentity {
  /** 头像上的一两个字 */
  initial: string
  label: string
}

type IdentityResolver = (conversation: Conversation) => AssistantIdentity | null
const identities: IdentityResolver[] = []

export function registerAssistantIdentity(resolve: IdentityResolver) {
  identities.push(resolve)
}

export function assistantIdentity(conversation: Conversation | null | undefined): AssistantIdentity | null {
  if (!conversation) return null
  for (const resolve of identities) {
    const found = resolve(conversation)
    if (found) return found
  }
  return null
}
