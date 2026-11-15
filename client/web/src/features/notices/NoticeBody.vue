<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ExternalLink } from '@lucide/vue'
import { kindMeta } from './meta'
import { sanitizeNoticeHtml } from './sanitize'
import type { Notice } from '../../types'

/** 一条通知的正文（消息中心详情、重要通知弹窗共用）。正文渲染前再清洗一遍 */
const props = defineProps<{ notice: Notice; loading?: boolean }>()
const { t, locale } = useI18n()
const meta = computed(() => kindMeta(props.notice.kind))
const html = computed(() => sanitizeNoticeHtml(props.notice.body_html))
const date = computed(() => (props.notice.published_at ? new Date(props.notice.published_at).toLocaleString(locale.value, { dateStyle: 'medium', timeStyle: 'short' }) : ''))
const expires = computed(() => (props.notice.expire_at ? new Date(props.notice.expire_at).toLocaleString(locale.value, { dateStyle: 'medium', timeStyle: 'short' }) : ''))
const action = computed(() => (props.notice.action_label && /^https?:\/\//i.test(props.notice.action_url) ? props.notice : null))
</script>

<template>
  <article class="notice-body">
    <div class="meta">
      <span class="kind" :style="{ color: meta.color, background: meta.wash }">
        <component :is="meta.icon" :size="13" /> {{ t(`ui.notices.kind.${notice.kind}`) }}
      </span>
      <span v-if="notice.important" class="imp">{{ t('ui.notices.important') }}</span>
      <span class="date">{{ date }}</span>
    </div>
    <h1>{{ notice.title }}</h1>
    <p v-if="expires" class="expires">{{ t('ui.notices.expires', { time: expires }) }}</p>
    <div v-if="loading && !notice.body_html" class="loading">{{ t('ui.notices.loading') }}</div>
    <!-- eslint-disable-next-line vue/no-v-html -- 已经过服务端和 sanitizeNoticeHtml 两道白名单清洗 -->
    <div v-else class="rich" v-html="html" />
    <a v-if="action" class="btn primary action" :href="action.action_url" target="_blank" rel="noopener noreferrer">
      {{ action.action_label }} <ExternalLink :size="14" />
    </a>
  </article>
</template>

<style scoped>
.notice-body {
  min-width: 0;
}
.meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.kind,
.imp {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 10px;
  font-weight: 500;
}
.imp {
  background: var(--red-wash);
  color: var(--red);
}
h1 {
  margin: 10px 0 4px;
  font-size: 20px;
  font-weight: 650;
  line-height: 1.35;
}
.expires {
  margin: 0;
  color: var(--amber);
  font-size: var(--t-xs);
}
.loading {
  padding: 24px 0;
  color: var(--ink-faint);
}
.rich {
  margin-top: 14px;
  color: var(--ink);
  font-size: var(--t-md, 15px);
  line-height: 1.75;
  overflow-wrap: anywhere;
}
.rich :deep(h1),
.rich :deep(h2),
.rich :deep(h3),
.rich :deep(h4) {
  margin: 1.2em 0 0.5em;
  line-height: 1.4;
}
.rich :deep(h2) {
  font-size: 18px;
}
.rich :deep(h3) {
  font-size: 16px;
}
.rich :deep(p) {
  margin: 0.6em 0;
}
.rich :deep(img),
.rich :deep(video) {
  display: block;
  max-width: 100%;
  height: auto;
  margin: 12px 0;
  border-radius: 10px;
  background: var(--cloth-sunk);
}
.rich :deep(video) {
  width: 100%;
  max-height: 60vh;
  background: #000;
}
.rich :deep(figure) {
  margin: 12px 0;
}
.rich :deep(figcaption) {
  color: var(--ink-faint);
  font-size: var(--t-xs);
  text-align: center;
}
.rich :deep(blockquote) {
  margin: 12px 0;
  padding: 8px 14px;
  border-left: 3px solid var(--indigo);
  border-radius: 0 8px 8px 0;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
}
.rich :deep(a) {
  color: var(--indigo);
  text-decoration: underline;
}
.rich :deep(ul),
.rich :deep(ol) {
  padding-left: 1.4em;
}
.rich :deep(code) {
  padding: 1px 5px;
  border-radius: 5px;
  background: var(--cloth-sunk);
  font-size: 0.92em;
}
.rich :deep(pre) {
  overflow: auto;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--cloth-sunk);
}
.rich :deep(table) {
  width: 100%;
  margin: 12px 0;
  border-collapse: collapse;
  font-size: var(--t-sm);
}
.rich :deep(th),
.rich :deep(td) {
  padding: 6px 10px;
  border: 1px solid var(--line);
  text-align: left;
}
.rich :deep(th) {
  background: var(--cloth-sunk);
}
.rich :deep(hr) {
  margin: 16px 0;
  border: 0;
  border-top: 1px solid var(--line);
}
.action {
  display: inline-flex;
  margin-top: 18px;
  text-decoration: none;
}
</style>
