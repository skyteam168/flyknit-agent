<script setup lang="ts">
/**
 * 一轮做完后「已完成 N 步」那一行右边的图片缩略图（参照 Claude Code）：这一轮 AI 看过的截图 / 图片、生成的图片。
 * 鼠标移上去在上方显示大图（文件名 +「第 i 项，共 n 项」），点一下进大图查看器、左右翻。
 * 最多露 4 张，多的显示「+N」，点开同样能翻到。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { loadThumb, openGallery, thumbs, type GalleryImage } from '../imageGallery'

const props = defineProps<{ images: GalleryImage[] }>()
const { t } = useI18n()

const MaxShown = 4
const shown = computed(() => props.images.slice(0, MaxShown))
const extra = computed(() => props.images.length - shown.value.length)

function load() {
  for (const img of shown.value) void loadThumb(img.path)
}
onMounted(load)
watch(() => props.images.map((i) => i.path).join('|'), load)

// 悬停大图：放在缩略图上方，靠右对齐，不超出窗口
// 上面放不下（缩略图靠近窗口顶部）就放在下面
const PeekHeight = 300
const hover = ref<{ i: number; left: number; top?: number; bottom?: number } | null>(null)
function enter(e: MouseEvent, i: number) {
  const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
  const width = 340
  const left = Math.max(8, Math.min(r.right - width, window.innerWidth - width - 8))
  hover.value = r.top > PeekHeight + 16 ? { i, left, bottom: window.innerHeight - r.top + 8 } : { i, left, top: r.bottom + 8 }
}
</script>

<template>
  <span class="strip" :title="t('ui.imageView.stripTip')" @mouseleave="hover = null">
    <button
      v-for="(img, i) in shown"
      :key="img.path"
      type="button"
      class="thumb"
      :aria-label="img.name"
      @mouseenter="enter($event, i)"
      @click.stop="openGallery(images, i)"
    >
      <img v-if="thumbs[img.path]" :src="thumbs[img.path]" alt="" />
    </button>
    <button v-if="extra > 0" type="button" class="thumb more" :title="t('ui.imageView.more', { n: extra })" @click.stop="openGallery(images, MaxShown)">
      +{{ extra }}
    </button>
    <Teleport to="body">
      <div v-if="hover && shown[hover.i]" class="peek" :style="{ left: hover.left + 'px', top: hover.top !== undefined ? hover.top + 'px' : undefined, bottom: hover.bottom !== undefined ? hover.bottom + 'px' : undefined }">
        <img v-if="thumbs[shown[hover.i].path]" :src="thumbs[shown[hover.i].path]" alt="" />
        <div v-else class="peek-loading">{{ t('ui.imageView.loading') }}</div>
        <div class="peek-foot">
          <span class="peek-name">{{ shown[hover.i].name }}</span>
          <span class="peek-count">{{ t('ui.imageView.count', { i: hover.i + 1, n: images.length }) }}</span>
        </div>
      </div>
    </Teleport>
  </span>
</template>

<style scoped>
.strip {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 5px;
}
.thumb {
  display: grid;
  place-items: center;
  width: 38px;
  height: 26px;
  padding: 0;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: 5px;
  background: var(--cloth-sunk, var(--loom));
  color: var(--ink-soft);
  font-size: calc(11px * var(--font-scale));
  cursor: zoom-in;
}
.thumb:hover {
  border-color: var(--indigo);
}
.thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.thumb.more {
  cursor: pointer;
}
.peek {
  position: fixed;
  z-index: 70;
  width: 340px;
  overflow: hidden;
  border: 1px solid var(--line-strong, var(--line));
  border-radius: 10px;
  background: var(--cloth);
  box-shadow: var(--shadow-card, 0 8px 30px rgb(0 0 0 / 25%));
  pointer-events: none;
  animation: peek 100ms ease-out;
}
.peek img {
  display: block;
  width: 100%;
  max-height: 240px;
  object-fit: contain;
  background: var(--loom);
}
.peek-loading {
  display: grid;
  place-items: center;
  height: 120px;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
}
.peek-foot {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 12px;
  font-size: calc(12.5px * var(--font-scale));
}
.peek-name {
  min-width: 0;
  overflow: hidden;
  color: var(--ink);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.peek-count {
  flex: none;
  color: var(--ink-faint);
}
@keyframes peek {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
}
</style>
