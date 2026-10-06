<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, Eye, ExternalLink, FolderOpen, FileText } from '@lucide/vue'
import { launchFile, openPreview, revealFile, state } from '../store'
import type { OutputFile } from '../types'

// 任务产出的文件卡片：点一下在右侧预览，或者用默认应用打开 / 在资源管理器中定位
const props = defineProps<{ files: OutputFile[] }>()
const { t } = useI18n()

const menuFor = ref<string | null>(null)

const shown = computed(() => props.files.filter((f) => f.exists))

function size(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function toggleMenu(path: string) {
  menuFor.value = menuFor.value === path ? null : path
}

function act(fn: (f: OutputFile) => unknown, f: OutputFile) {
  menuFor.value = null
  void fn(f)
}
</script>

<template>
  <div v-if="shown.length" class="outputs">
    <div v-for="f in shown" :key="f.path" class="file">
      <span class="icon" :data-ext="f.extension">
        <FileText :size="16" />
      </span>
      <span class="meta">
        <strong :title="f.path">{{ f.name }}</strong>
        <small>
          {{ size(f.size) }}
          <template v-if="f.previewable"> · {{ t('ui.outputs.previewHint') }}</template>
        </small>
      </span>

      <button
        v-if="f.previewable"
        type="button"
        class="btn"
        :class="{ on: state.preview?.file.path === f.path }"
        @click="act(openPreview, f)"
      >
        <Eye :size="14" /> {{ t('ui.outputs.preview') }}
      </button>
      <button v-else type="button" class="btn" @click="act(launchFile, f)">
        <ExternalLink :size="14" /> {{ t('ui.outputs.open') }}
      </button>

      <div class="more">
        <button type="button" class="chev" :aria-label="t('ui.outputs.more')" @click="toggleMenu(f.path)">
          <ChevronDown :size="14" />
        </button>
        <div v-if="menuFor === f.path" class="menu" @mouseleave="menuFor = null">
          <button type="button" @click="act(launchFile, f)">
            <ExternalLink :size="14" /> {{ t('ui.outputs.openWithDefault') }}
          </button>
          <button type="button" @click="act(revealFile, f)">
            <FolderOpen :size="14" /> {{ t('ui.outputs.reveal') }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.outputs {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 10px 0 2px;
}
.file {
  position: relative;
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
}
.icon {
  flex: none;
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent) 12%, transparent);
  color: var(--accent);
}
/* 常见格式给个辨识色，加格式不用改这里也能正常显示 */
.icon[data-ext='xlsx'],
.icon[data-ext='xls'],
.icon[data-ext='csv'] {
  background: color-mix(in srgb, #1d6f42 14%, transparent);
  color: #1d6f42;
}
.icon[data-ext='docx'],
.icon[data-ext='doc'] {
  background: color-mix(in srgb, #2b579a 14%, transparent);
  color: #2b579a;
}
.icon[data-ext='pptx'],
.icon[data-ext='ppt'] {
  background: color-mix(in srgb, #c43e1c 14%, transparent);
  color: #c43e1c;
}
.icon[data-ext='pdf'] {
  background: color-mix(in srgb, #b30b00 12%, transparent);
  color: #b30b00;
}
.meta {
  flex: 1;
  min-width: 0;
}
.meta strong {
  display: block;
  overflow: hidden;
  font-size: 13.5px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.meta small {
  color: var(--ink-soft);
  font-size: 12px;
}
.btn {
  flex: none;
  display: inline-flex;
  gap: 5px;
  align-items: center;
  padding: 5px 11px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: inherit;
  font-size: 12.5px;
  cursor: pointer;
}
.btn:hover,
.btn.on {
  border-color: var(--accent);
  color: var(--accent);
}
.more {
  position: relative;
  flex: none;
}
.chev {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border: 1px solid var(--line);
  border-radius: 50%;
  background: var(--cloth);
  color: inherit;
  cursor: pointer;
}
.chev:hover {
  border-color: var(--accent);
  color: var(--accent);
}
.menu {
  position: absolute;
  top: calc(100% + 6px);
  right: 0;
  z-index: 20;
  min-width: 190px;
  padding: 5px;
  border: 1px solid var(--line);
  border-radius: var(--r-md, 10px);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.menu button {
  display: flex;
  gap: 8px;
  align-items: center;
  width: 100%;
  padding: 8px 10px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}
.menu button:hover {
  background: color-mix(in srgb, var(--ink) 7%, transparent);
}
</style>
