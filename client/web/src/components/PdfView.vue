<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * 自己画 PDF（pdf.js），不用 WebView2 自带的阅读器：
 * 自带的那个在 iframe 里打不开 data: 地址（一片空白），公司策略也可能把它关掉。
 * 每页画成一张 canvas，滚到附近才画，几十页的文件也不卡。
 */
const props = defineProps<{ src: string; name?: string }>()
const { t } = useI18n()

const root = ref<HTMLElement | null>(null)
const pages = ref<{ n: number; ratio: number }[]>([])
const status = ref<'loading' | 'ready' | 'error'>('loading')
const error = ref('')
const zoom = ref(1)

type PdfDoc = import('pdfjs-dist').PDFDocumentProxy
let doc: PdfDoc | null = null
let observer: IntersectionObserver | null = null
let generation = 0
const drawn = new Map<number, number>() // 页号 → 画的时候的宽度

let pdfjsReady: Promise<typeof import('pdfjs-dist')> | null = null
function loadPdfjs() {
  // worker 让 Vite 打成普通的 .js（.mjs 在 WebView2 本地映射里不一定按脚本类型返回）
  pdfjsReady ??= Promise.all([import('pdfjs-dist'), import('pdfjs-dist/build/pdf.worker.min.mjs?worker')]).then(([lib, worker]) => {
    lib.GlobalWorkerOptions.workerPort = new worker.default()
    return lib
  })
  return pdfjsReady
}

async function bytesOf(src: string): Promise<Uint8Array> {
  const res = await fetch(src)
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return new Uint8Array(await res.arrayBuffer())
}

async function open() {
  const mine = ++generation
  close()
  status.value = 'loading'
  error.value = ''
  pages.value = []
  if (!props.src) return
  try {
    const pdfjs = await loadPdfjs()
    const data = await bytesOf(props.src)
    const base = new URL('./pdfjs/', document.baseURI).href
    const loaded = await pdfjs.getDocument({
      data,
      cMapUrl: `${base}cmaps/`,
      cMapPacked: true,
      standardFontDataUrl: `${base}standard_fonts/`,
      isEvalSupported: false,
    }).promise
    if (mine !== generation) {
      void loaded.destroy()
      return
    }
    doc = loaded
    // 先按第一页的比例占好位置，滚动条一开始就是对的
    const first = await loaded.getPage(1)
    const vp = first.getViewport({ scale: 1 })
    const ratio = vp.height / vp.width
    pages.value = Array.from({ length: loaded.numPages }, (_, i) => ({ n: i + 1, ratio }))
    status.value = 'ready'
    await nextTick()
    watchPages()
  } catch (e) {
    if (mine !== generation) return
    status.value = 'error'
    error.value = (e as Error)?.message ?? String(e)
  }
}

function watchPages() {
  observer?.disconnect()
  observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) void draw(Number((entry.target as HTMLElement).dataset.page))
      }
    },
    { root: root.value, rootMargin: '600px 0px' },
  )
  root.value?.querySelectorAll('.page').forEach((el) => observer!.observe(el))
}

async function draw(n: number) {
  const current = doc
  const holder = root.value?.querySelector<HTMLElement>(`.page[data-page="${n}"]`)
  if (!current || !holder) return
  const width = holder.clientWidth
  if (!width || drawn.get(n) === width) return
  drawn.set(n, width)
  try {
    const page = await current.getPage(n)
    const base = page.getViewport({ scale: 1 })
    const scale = width / base.width
    const vp = page.getViewport({ scale: scale * (window.devicePixelRatio || 1) })
    const canvas = document.createElement('canvas')
    canvas.width = Math.floor(vp.width)
    canvas.height = Math.floor(vp.height)
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    await page.render({ canvasContext: ctx, viewport: vp }).promise
    if (current !== doc) return
    const entry = pages.value[n - 1]
    if (entry) entry.ratio = base.height / base.width
    holder.replaceChildren(canvas)
  } catch {
    drawn.delete(n)
  }
}

function setZoom(z: number) {
  zoom.value = Math.min(3, Math.max(0.5, Math.round(z * 100) / 100))
  drawn.clear()
  void nextTick(watchPages)
}

function close() {
  observer?.disconnect()
  observer = null
  drawn.clear()
  const old = doc
  doc = null
  void old?.destroy()
}

watch(() => props.src, open, { immediate: true })
onBeforeUnmount(() => {
  generation++
  close()
})
</script>

<template>
  <div class="pdf-view">
    <div v-if="status === 'ready'" class="bar">
      <span>{{ t('ui.preview.pdfPages', { n: pages.length }) }}</span>
      <span class="grow" />
      <button type="button" :title="t('ui.preview.zoomOut')" @click="setZoom(zoom - 0.25)">−</button>
      <span class="zoom">{{ Math.round(zoom * 100) }}%</span>
      <button type="button" :title="t('ui.preview.zoomIn')" @click="setZoom(zoom + 0.25)">+</button>
    </div>
    <div ref="root" class="scroll">
      <div v-if="status === 'loading'" class="hint">{{ t('ui.preview.pdfLoading') }}</div>
      <div v-else-if="status === 'error'" class="hint">{{ t('ui.preview.pdfFailed', { error }) }}</div>
      <div
        v-for="p in pages"
        :key="p.n"
        class="page"
        :data-page="p.n"
        :style="{ width: `${zoom * 100}%`, aspectRatio: `1 / ${p.ratio}` }"
      />
    </div>
  </div>
</template>

<style scoped>
.pdf-view {
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  min-height: 0;
  background: var(--paper-2, #eef0f4);
}
.bar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: 12px;
}
.bar .grow {
  flex: 1;
}
.bar button {
  width: 24px;
  height: 24px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth, #fff);
  color: var(--ink);
  cursor: pointer;
}
.zoom {
  min-width: 40px;
  text-align: center;
}
.scroll {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 12px;
}
.page {
  max-width: none;
  margin: 0 auto 12px;
  background: #fff;
  box-shadow: 0 1px 4px rgb(0 0 0 / 0.12);
}
.page :deep(canvas) {
  display: block;
  width: 100%;
  height: 100%;
}
.hint {
  padding: 24px;
  color: var(--ink-soft);
  text-align: center;
}
</style>
