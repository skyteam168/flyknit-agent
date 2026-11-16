<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowLeft, BellOff, CheckCheck, ChevronRight, X } from '@lucide/vue'
import NoticeBody from './NoticeBody.vue'
import { kindMeta } from './meta'
import { closeNotices, markAllRead, markRead, notices, openNotice } from './store'
import type { Notice } from '../../types'

/**
 * 消息中心（docs/notices.md）：按日期分组的通知列表，点开看富文本详情。
 * 列表右边的按钮：通知带了按钮就直接去（顺带标已读），没带就是「查看详情」。
 */
const { t, locale } = useI18n()
const filter = ref<'all' | 'unread'>('all')

const shown = computed(() => notices.list.filter((n) => filter.value === 'all' || !n.read))

function dayOf(n: Notice) {
  if (!n.published_at) return ''
  const d = new Date(n.published_at)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** 重要的单独一组放最上面，其余按日期分组 */
const groups = computed(() => {
  const out: { key: string; label: string; items: Notice[] }[] = []
  const pinned = shown.value.filter((n) => n.important)
  if (pinned.length) out.push({ key: 'pinned', label: t('ui.notices.pinned'), items: pinned })
  for (const n of shown.value.filter((x) => !x.important)) {
    const day = dayOf(n)
    const last = out[out.length - 1]
    if (last && last.key === day) last.items.push(n)
    else out.push({ key: day, label: day, items: [n] })
  }
  return out
})

const timeOf = (n: Notice) => (n.published_at ? new Date(n.published_at).toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' }) : '')
const hasAction = (n: Notice) => !!n.action_label && /^https?:\/\//i.test(n.action_url)

function act(n: Notice) {
  if (hasAction(n)) {
    void markRead(n.id)
    // 新窗口打开：宿主交给系统浏览器
    window.open(n.action_url, '_blank', 'noopener')
  } else {
    void openNotice(n.id)
  }
}
</script>

<template>
  <div class="notices-page">
    <div class="inner">
    <header class="head">
      <button v-if="notices.detail" type="button" class="back" @click="notices.detail = null">
        <ArrowLeft :size="15" /> {{ t('ui.notices.back') }}
      </button>
      <span v-else class="spacer" />
      <h2>{{ notices.detail ? '' : t('ui.notices.title') }}</h2>
      <div class="tools">
        <button v-if="!notices.detail && notices.unread" type="button" class="btn sm" @click="markAllRead">
          <CheckCheck :size="14" /> {{ t('ui.notices.readAll') }}
        </button>
        <button type="button" class="icon-btn" :title="t('ui.notices.close')" :aria-label="t('ui.notices.close')" @click="closeNotices">
          <X :size="18" />
        </button>
      </div>
    </header>

    <div v-if="notices.detail" class="detail">
      <NoticeBody :notice="notices.detail" :loading="notices.detailLoading" />
    </div>

    <template v-else>
      <div class="filters" role="tablist">
        <button type="button" role="tab" :aria-selected="filter === 'all'" @click="filter = 'all'">{{ t('ui.notices.all') }}</button>
        <button type="button" role="tab" :aria-selected="filter === 'unread'" @click="filter = 'unread'">
          {{ t('ui.notices.unread') }}<span v-if="notices.unread" class="count">{{ notices.unread }}</span>
        </button>
      </div>

      <div v-if="notices.loading && !notices.loaded" class="empty">{{ t('ui.notices.loading') }}</div>
      <div v-else-if="!shown.length" class="empty">
        <BellOff :size="28" />
        <p>{{ filter === 'unread' ? t('ui.notices.noneUnread') : t('ui.notices.none') }}</p>
      </div>
      <section v-for="g in groups" :key="g.key" class="group">
        <h3>{{ g.label }}</h3>
        <div v-for="n in g.items" :key="n.id" class="item" :class="{ unread: !n.read }" role="button" tabindex="0" @click="openNotice(n.id)" @keydown.enter="openNotice(n.id)">
          <span class="icon" :style="{ color: kindMeta(n.kind).color, background: kindMeta(n.kind).wash }">
            <component :is="kindMeta(n.kind).icon" :size="18" />
          </span>
          <span class="text">
            <span class="line1">
              <i v-if="!n.read" class="dot" :aria-label="t('ui.notices.unreadMark')" />
              <strong>{{ n.title }}</strong>
              <small class="kind">{{ t(`ui.notices.kind.${n.kind}`) }}</small>
              <small v-if="g.key === 'pinned'" class="when">{{ dayOf(n) }}</small>
              <small v-else class="when">{{ timeOf(n) }}</small>
            </span>
            <span class="summary">{{ n.summary }}</span>
          </span>
          <button type="button" class="btn sm go" @click.stop="act(n)">
            {{ hasAction(n) ? n.action_label : t('ui.notices.view') }}
            <ChevronRight v-if="!hasAction(n)" :size="13" />
          </button>
        </div>
      </section>
    </template>
    </div>
  </div>
</template>

<style scoped>
.notices-page {
  flex: 1 1 auto;
  width: 100%;
  min-width: 0;
  height: 100%;
  overflow: auto;
  padding: 18px 28px 40px;
}
.inner {
  width: 100%;
}
.head {
  position: sticky;
  top: -18px;
  z-index: 2;
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  margin: -18px -28px 6px;
  padding: 16px 20px 12px;
  background: var(--loom);
}
.head h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 650;
}
.back {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  justify-self: start;
  padding: 4px 8px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.back:hover {
  background: var(--cloth-sunk);
}
.tools {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
}
.btn.sm {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 30px;
  padding: 0 12px;
  font-size: var(--t-xs);
}
.filters {
  display: inline-flex;
  gap: 2px;
  margin: 4px 0 6px;
  padding: 3px;
  border-radius: 10px;
  background: var(--cloth-sunk);
}
.filters button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 14px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.filters button[aria-selected='true'] {
  background: var(--cloth);
  box-shadow: var(--shadow-card);
  color: var(--ink);
}
.count {
  min-width: 16px;
  padding: 0 5px;
  border-radius: 8px;
  background: var(--red);
  color: #fff;
  font-size: 10px;
  line-height: 16px;
  text-align: center;
}
.group {
  width: 100%;
}
.group h3 {
  margin: 16px 0 6px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-weight: 600;
}
.item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
  border-radius: 12px;
  cursor: pointer;
}
.item:hover {
  background: var(--cloth-sunk);
}
.icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 50%;
}
.text {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.line1 {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.line1 strong {
  overflow: hidden;
  min-width: 0;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.item:not(.unread) .line1 strong {
  font-weight: 500;
  color: var(--ink-soft);
}
.dot {
  flex: none;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--red);
}
.kind,
.when {
  flex: none;
  color: var(--ink-faint);
  font-size: 11px;
}
.kind {
  padding: 0 6px;
  border: 1px solid var(--line);
  border-radius: 8px;
}
.summary {
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.go {
  flex: none;
  border-radius: 9px;
}
.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 80px 0;
  color: var(--ink-faint);
}
.detail {
  width: 100%;
  margin: 8px 0 0;
  padding: 22px 26px 28px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
}
@media (max-width: 640px) {
  .notices-page {
    padding: 14px 16px 32px;
  }
  .head {
    margin: -14px -16px 6px;
  }
  .detail {
    padding: 16px;
  }
  .go {
    display: none;
  }
}
</style>
