<script setup lang="ts">
/**
 * 对话里 AI 改文件的卡片下面，直接显示改了什么（参照 Cursor）：文件名、+n −m、改动的几行（红删绿增）。
 * 从工具参数算出来：edit_file 的 old_text / new_text，multi_edit 的每一处，write_file 的新内容。
 * 只是给人看的预览，真正的对比和还原在「改动」里（每一轮的改动可以一键还原）。
 */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileCode2 } from '@lucide/vue'

const props = defineProps<{ name: string; args?: string; state: string }>()
const { t } = useI18n()
const expanded = ref(false)

interface Line {
  kind: ' ' | '+' | '-' | '…'
  text: string
}

/** 一处替换 → 前后相同的行当上下文，中间的删掉 / 加上 */
function hunk(oldText: string, newText: string): Line[] {
  const a = oldText.split('\n')
  const b = newText === '' ? [] : newText.split('\n')
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let endA = a.length - 1
  let endB = b.length - 1
  while (endA >= start && endB >= start && a[endA] === b[endB]) {
    endA--
    endB--
  }
  const out: Line[] = []
  for (let i = Math.max(0, start - 2); i < start; i++) out.push({ kind: ' ', text: a[i] })
  for (let i = start; i <= endA; i++) out.push({ kind: '-', text: a[i] })
  for (let i = start; i <= endB; i++) out.push({ kind: '+', text: b[i] })
  for (let i = endA + 1; i < Math.min(a.length, endA + 3); i++) out.push({ kind: ' ', text: a[i] })
  return out
}

const parsed = computed(() => {
  try {
    const a = JSON.parse(props.args ?? '{}') as Record<string, unknown>
    const path = String(a.path ?? '')
    let lines: Line[] = []
    if (props.name === 'edit_file') lines = hunk(String(a.old_text ?? ''), String(a.new_text ?? ''))
    else if (props.name === 'multi_edit' && Array.isArray(a.edits)) {
      a.edits.forEach((e: { old_text?: string; new_text?: string }, i: number) => {
        if (i > 0) lines.push({ kind: '…', text: '' })
        lines.push(...hunk(String(e.old_text ?? ''), String(e.new_text ?? '')))
      })
    } else if (props.name === 'write_file' && typeof a.content === 'string') {
      lines = a.content.split('\n').map((text) => ({ kind: '+' as const, text }))
    }
    if (!path || !lines.length) return null
    return {
      name: path.split(/[\\/]/).pop() || path,
      path,
      lines,
      added: lines.filter((l) => l.kind === '+').length,
      removed: lines.filter((l) => l.kind === '-').length,
    }
  } catch {
    return null
  }
})

const Max = 14
const shown = computed(() => (parsed.value && !expanded.value ? parsed.value.lines.slice(0, Max) : (parsed.value?.lines ?? [])))
</script>

<template>
  <div v-if="parsed && state !== 'rejected' && state !== 'blocked'" class="edit-diff">
    <header :title="parsed.path">
      <FileCode2 :size="13" />
      <span class="name">{{ parsed.name }}</span>
      <span class="add">+{{ parsed.added }}</span>
      <span class="del">−{{ parsed.removed }}</span>
    </header>
    <pre><code v-for="(l, i) in shown" :key="i" :class="{ add: l.kind === '+', del: l.kind === '-', gap: l.kind === '…' }"><span class="mark">{{ l.kind === '…' ? '⋯' : l.kind }}</span>{{ l.text }}
</code></pre>
    <button v-if="parsed.lines.length > Max" type="button" class="more" @click="expanded = !expanded">
      {{ expanded ? t('ui.code.diffLess') : t('ui.code.diffMore', { n: parsed.lines.length - Max }) }}
    </button>
  </div>
</template>

<style scoped>
.edit-diff {
  margin: 4px 0 2px 26px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: var(--r-md, 8px);
  background: var(--cloth);
}
header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 10px;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
}
.name {
  color: var(--ink);
  font-weight: 500;
}
.add {
  color: var(--thread);
}
.del {
  color: var(--red);
}
pre {
  margin: 0;
  max-height: 360px;
  overflow: auto;
  font: calc(12px * var(--font-scale)) / 1.55 'Cascadia Code', Consolas, monospace;
}
code {
  display: block;
  padding: 0 10px 0 0;
  white-space: pre;
  color: var(--ink-soft);
}
code.add {
  background: color-mix(in srgb, var(--thread) 14%, transparent);
  color: var(--ink);
}
code.del {
  background: color-mix(in srgb, var(--red) 12%, transparent);
  color: var(--ink);
}
code.gap {
  color: var(--ink-faint);
}
.mark {
  display: inline-block;
  width: 22px;
  color: var(--ink-faint);
  text-align: center;
  user-select: none;
}
code.add .mark {
  color: var(--thread);
}
code.del .mark {
  color: var(--red);
}
.more {
  width: 100%;
  padding: 4px;
  border: 0;
  border-top: 1px solid var(--line);
  background: var(--loom);
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
</style>
