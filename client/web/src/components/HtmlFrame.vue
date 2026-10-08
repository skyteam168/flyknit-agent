<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'

// 网页预览：放进沙箱 iframe 渲染，右上角能切回源码。
//
// sandbox 只开 allow-scripts（图表、交互要靠脚本）和 allow-popups（target=_blank 的链接交给宿主
// 用系统浏览器打开），不开 allow-same-origin：页面拿到的是一个空白的独立源，碰不到界面本身、
// 读不到本机文件，也调不了宿主桥。网页默认白底，深色主题下 iframe 也固定白底，不然黑字看不见。
// srcdoc 没有文件所在目录，页面里用相对路径引用的本地图片和样式出不来——要完整效果就用浏览器打开。
defineProps<{ html: string; title: string }>()

const { t } = useI18n()
const mode = ref<'page' | 'source'>('page')
</script>

<template>
  <div class="html-frame">
    <div class="view-toggle" role="tablist">
      <button type="button" role="tab" :aria-selected="mode === 'page'" :class="{ on: mode === 'page' }" @click="mode = 'page'">
        {{ t('ui.preview.page') }}
      </button>
      <button type="button" role="tab" :aria-selected="mode === 'source'" :class="{ on: mode === 'source' }" @click="mode = 'source'">
        {{ t('ui.preview.source') }}
      </button>
    </div>
    <iframe
      v-if="mode === 'page'"
      :srcdoc="html"
      sandbox="allow-scripts allow-popups allow-forms"
      referrerpolicy="no-referrer"
      class="page"
      :title="title"
    />
    <slot v-else name="source" />
  </div>
</template>

<style scoped>
.html-frame {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}
.html-frame > :slotted(*) {
  max-width: 100%;
  min-width: 0;
}
.view-toggle {
  display: inline-flex;
  align-self: flex-end;
  padding: 2px;
  border-radius: 8px;
  background: var(--chip);
}
.view-toggle button {
  padding: 3px 12px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink-soft);
  font: inherit;
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.view-toggle button.on {
  background: var(--cloth);
  color: var(--ink);
  box-shadow: 0 1px 2px rgb(0 0 0 / 12%);
}
.page {
  width: 100%;
  height: calc(100vh - 200px);
  border: 1px solid var(--line);
  border-radius: 8px;
  background: #fff;
}
</style>
