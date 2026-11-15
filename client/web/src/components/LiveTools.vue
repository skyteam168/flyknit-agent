<script setup lang="ts">
import { computed } from 'vue'
import ToolCard from './ToolCard.vue'
import ExpertTaskCard from './ExpertTaskCard.vue'
import { toolCard } from '../toolCards'
import { isSubtaskTool } from '../subtasks'
import type { ConversationState } from '../store'

/**
 * 这一轮里还没挂到任何消息上的工具（宿主先发 tool.started、回答消息还没到时的兜底显示）。
 * 单独一个组件：工具一开始、一结束，只重画这几张卡，不让整个消息列表跟着重画。
 */
const props = defineProps<{ conv: ConversationState; conversationId: string }>()

const tools = computed(() => {
  const referenced = new Set(props.conv.messages.flatMap((m) => (m.toolCalls ?? []).map((c) => c.id)))
  // 专家执行时请求确认的那一步只用来驱动确认条，显示在专家任务卡片里，不单独画
  return Object.values(props.conv.tools).filter((t) => !referenced.has(t.callId) && !t.actor)
})
</script>

<template>
  <template v-for="tool in tools" :key="tool.callId">
    <component
      :is="toolCard(tool.name)"
      v-if="toolCard(tool.name)"
      :name="tool.name"
      :call-id="tool.callId"
      :args="tool.args ?? '{}'"
      :conversation-id="conversationId"
      :output="tool.state === 'running' || tool.state === 'waiting' ? '' : (tool.output ?? '')"
    />
    <ExpertTaskCard v-else-if="isSubtaskTool(tool.name)" :tool="tool" :conversation-id="conversationId" />
    <ToolCard v-else :tool="tool" :conversation-id="conversationId" />
  </template>
</template>
