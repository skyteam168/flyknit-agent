<script setup lang="ts">
import { computed } from 'vue'
import { renderMarkdown, stripToolMarkup } from '../markdown'
import type { ConversationState } from '../store'

/**
 * 正在输出的那段字（思考过程或回答正文）。
 * 单独一个组件：流式字块每来一段只重画这里，不让整个消息列表（长任务里几百上千条）跟着重画。
 * 消息列表只读「有没有字」这类布尔值，字本身只在这里读。
 */
const props = defineProps<{ conv: ConversationState; part: 'reasoning' | 'content' }>()
const html = computed(() => (props.part === 'content' ? renderMarkdown(stripToolMarkup(props.conv.draft?.content ?? ''), { streaming: true }) : ''))
</script>

<template>
  <p v-if="part === 'reasoning'">{{ conv.draft?.reasoning }}</p>
  <div v-else class="md" v-html="html" />
</template>
