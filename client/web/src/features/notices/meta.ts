import { CalendarDays, ChartColumn, CircleArrowUp, FileText, Lightbulb, Megaphone, ShieldAlert, Sparkles, Wrench } from '@lucide/vue'
import type { Component } from 'vue'
import type { NoticeKind } from '../../types'

/** 每种通知的图标和颜色（颜色用主题变量，明暗主题都对） */
export const NOTICE_KINDS: Record<NoticeKind, { icon: Component; color: string; wash: string }> = {
  upgrade: { icon: CircleArrowUp, color: 'var(--indigo)', wash: 'var(--indigo-wash)' },
  feature: { icon: Sparkles, color: 'var(--thread)', wash: 'var(--thread-wash)' },
  maintenance: { icon: Wrench, color: 'var(--amber)', wash: 'var(--amber-wash)' },
  security: { icon: ShieldAlert, color: 'var(--red)', wash: 'var(--red-wash)' },
  policy: { icon: FileText, color: 'var(--ink-soft)', wash: 'var(--cloth-sunk)' },
  event: { icon: CalendarDays, color: 'var(--indigo)', wash: 'var(--indigo-wash)' },
  tip: { icon: Lightbulb, color: 'var(--amber)', wash: 'var(--amber-wash)' },
  general: { icon: Megaphone, color: 'var(--thread)', wash: 'var(--thread-wash)' },
  // 无人值守任务的结果（docs/unattended-jobs.md）
  report: { icon: ChartColumn, color: 'var(--indigo)', wash: 'var(--indigo-wash)' },
}

export const kindMeta = (kind: string) => NOTICE_KINDS[kind as NoticeKind] ?? NOTICE_KINDS.general
