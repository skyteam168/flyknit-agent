<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowLeft, ImagePlus, Loader, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { renderMarkdown } from '../markdown'
import { toast } from '../store'

// 设置 → 意见反馈：写问题、贴截图（按钮 / 粘贴 / 拖入，最多 6 张），可选附上最近几天的日志。
// 截图和日志由宿主上传到服务端，IT 在后台「意见反馈」里看。
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()

const MAX_CONTENT = 10000
const MAX_IMAGES = 6
/** 服务端单张上限 5 MB；超过这个或者分辨率特别大的截图先在本地缩一下 */
const MAX_BYTES = 5 * 1024 * 1024
const MAX_SIDE = 2560
const TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

const content = ref('')
const images = ref<string[]>([])
const logs = ref(true)
const submitting = ref(false)
const dragging = ref(false)
const textarea = ref<HTMLTextAreaElement>()
const picker = ref<HTMLInputElement>()

/** 「隐私保护声明」：在这个对话框里翻到声明全文，看完点返回 */
const privacy = ref<{ title: string; html: string } | null>(null)
const privacyLoading = ref(false)

const canSubmit = computed(() => !submitting.value && (content.value.trim().length > 0 || images.value.length > 0))

onMounted(() => textarea.value?.focus())

function readAsDataUrl(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result))
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(file)
  })
}

/** 太大的截图缩到 2560 以内再传：4K 屏的整屏截图动辄十几 MB，IT 看问题用不着那么清楚 */
async function shrink(file: File): Promise<string | null> {
  const url = await readAsDataUrl(file)
  if (file.type === 'image/gif') return file.size <= MAX_BYTES ? url : null
  const img = new Image()
  img.src = url
  try {
    await img.decode()
  } catch {
    return null
  }
  const scale = Math.min(1, MAX_SIDE / Math.max(img.naturalWidth, img.naturalHeight))
  if (scale === 1 && file.size <= MAX_BYTES) return url
  const canvas = document.createElement('canvas')
  canvas.width = Math.round(img.naturalWidth * scale)
  canvas.height = Math.round(img.naturalHeight * scale)
  canvas.getContext('2d')?.drawImage(img, 0, 0, canvas.width, canvas.height)
  for (const quality of [0.9, 0.75, 0.6]) {
    const out = canvas.toDataURL('image/jpeg', quality)
    if (out.length * 0.75 <= MAX_BYTES) return out
  }
  return null
}

async function addFiles(files: Iterable<File>) {
  for (const file of files) {
    if (!TYPES.includes(file.type)) {
      toast(t('feedback.badImage'))
      continue
    }
    if (images.value.length >= MAX_IMAGES) {
      toast(t('feedback.tooMany', { max: MAX_IMAGES }))
      return
    }
    const url = await shrink(file)
    if (url) images.value.push(url)
    else toast(t('feedback.badImage'))
  }
}

function onPick(e: Event) {
  const input = e.target as HTMLInputElement
  void addFiles(Array.from(input.files ?? []))
  input.value = ''
}

function onPaste(e: ClipboardEvent) {
  const files = Array.from(e.clipboardData?.files ?? []).filter((f) => f.type.startsWith('image/'))
  if (!files.length) return
  e.preventDefault()
  void addFiles(files)
}

function onDrop(e: DragEvent) {
  dragging.value = false
  void addFiles(Array.from(e.dataTransfer?.files ?? []))
}

async function openPrivacy() {
  privacyLoading.value = true
  try {
    const doc = await bridge.legal('privacy')
    if (!doc) throw new Error('404')
    privacy.value = { title: doc.title, html: renderMarkdown(doc.content) }
  } catch (e) {
    toast(t('feedback.privacyFailed', { msg: e instanceof Error ? e.message : String(e) }))
  } finally {
    privacyLoading.value = false
  }
}

function close() {
  if (!submitting.value) emit('close')
}

/** 点到对话框外面：写了东西就不关，免得手一滑丢了 */
function onScrim() {
  if (!content.value.trim() && !images.value.length) close()
}

async function submit() {
  if (!canSubmit.value) {
    toast(t('feedback.empty'))
    return
  }
  submitting.value = true
  try {
    const r = await bridge.submitFeedback({ content: content.value.trim(), images: [...images.value], logs: logs.value })
    if (r.ok) {
      toast(t('feedback.thanks'))
      emit('close')
      return
    }
    const known: Record<string, string> = {
      timeout: t('feedback.timeout'),
      empty: t('feedback.empty'),
      bad_image: t('feedback.badImage'),
    }
    toast(known[r.message] ?? t('feedback.failed', { msg: r.message }))
  } catch (e) {
    toast(t('feedback.failed', { msg: e instanceof Error ? e.message : String(e) }))
  } finally {
    submitting.value = false
  }
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') {
    e.stopPropagation()
    if (privacy.value) privacy.value = null
    else close()
  } else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
    void submit()
  }
}
</script>

<template>
  <div class="scrim" @mousedown.self="onScrim" @keydown="onKey">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('feedback.title')">
      <header>
        <button v-if="privacy" type="button" class="icon" :aria-label="t('feedback.close')" @click="privacy = null">
          <ArrowLeft :size="18" />
        </button>
        <h2>{{ privacy ? privacy.title : t('feedback.title') }}</h2>
        <button type="button" class="icon" :aria-label="t('feedback.close')" :title="t('feedback.close')" @click="close">
          <X :size="18" />
        </button>
      </header>

      <!-- eslint-disable-next-line vue/no-v-html -- renderMarkdown 先转义再加标签 -->
      <article v-if="privacy" class="doc" v-html="privacy.html" />

      <template v-else>
        <div
          class="box"
          :class="{ dragging }"
          @dragover.prevent="dragging = true"
          @dragleave="dragging = false"
          @drop.prevent="onDrop"
        >
          <textarea
            ref="textarea"
            v-model="content"
            :placeholder="t('feedback.placeholder')"
            :maxlength="MAX_CONTENT"
            @paste="onPaste"
          />
          <div class="counter">{{ content.length }}/{{ MAX_CONTENT }}</div>
        </div>

        <div class="images">
          <div v-for="(src, i) in images" :key="i" class="thumb">
            <img :src="src" alt="" />
            <button type="button" class="remove" :aria-label="t('feedback.remove')" :title="t('feedback.remove')" @click="images.splice(i, 1)">
              <X :size="12" />
            </button>
          </div>
          <button
            v-if="images.length < MAX_IMAGES"
            type="button"
            class="upload"
            :title="t('feedback.dropHint')"
            @click="picker?.click()"
          >
            <ImagePlus :size="16" />
            <span>{{ t('feedback.upload', { n: images.length, max: MAX_IMAGES }) }}</span>
          </button>
          <input ref="picker" type="file" :accept="TYPES.join(',')" multiple hidden @change="onPick" />
        </div>

        <label class="logs">
          <input v-model="logs" type="checkbox" />
          <span>
            {{ t('feedback.logs') }}
            <button type="button" class="link" :disabled="privacyLoading" @click.prevent="openPrivacy">{{ t('feedback.privacy') }}</button>
          </span>
        </label>

        <footer>
          <button type="button" class="btn primary submit" :disabled="!canSubmit" @click="submit">
            <Loader v-if="submitting" :size="15" class="spin" />
            {{ submitting ? t('feedback.submitting') : t('feedback.submit') }}
          </button>
        </footer>
      </template>
    </div>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(560px, 100%);
  max-height: calc(100vh - 32px);
  padding: 18px 22px 20px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
header {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-bottom: 14px;
}
h2 {
  flex: 1;
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
}
.icon {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: var(--r-sm);
  color: var(--ink-soft);
}
.icon:hover {
  background: var(--chip);
  color: var(--ink);
}

.box {
  position: relative;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  transition: border-color 0.15s;
}
.box:focus-within,
.box.dragging {
  border-color: var(--indigo);
}
.box.dragging {
  background: var(--indigo-wash);
}
textarea {
  display: block;
  width: 100%;
  height: 200px;
  padding: 12px 14px 28px;
  border: 0;
  background: transparent;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-md);
  line-height: 1.6;
  resize: none;
  outline: 0;
}
textarea::placeholder {
  color: var(--ink-faint);
}
.counter {
  position: absolute;
  right: 12px;
  bottom: 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-variant-numeric: tabular-nums;
  pointer-events: none;
}

.images {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 12px;
}
.thumb {
  position: relative;
  width: 64px;
  height: 64px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  overflow: hidden;
  background: var(--cloth-sunk);
}
.thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.remove {
  position: absolute;
  top: 3px;
  right: 3px;
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: color-mix(in srgb, #000 60%, transparent);
  color: #fff;
  opacity: 0;
  transition: opacity 0.15s;
}
.thumb:hover .remove,
.remove:focus-visible {
  opacity: 1;
}
.upload {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  height: 64px;
  padding: 0 14px;
  border: 1px dashed var(--line-strong);
  border-radius: var(--r-sm);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.upload:hover {
  border-color: var(--indigo);
  color: var(--indigo);
}
.images:has(.thumb) .upload {
  width: 64px;
  padding: 0;
  justify-content: center;
}
.images:has(.thumb) .upload span {
  display: none;
}

.logs {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  margin-top: 16px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.6;
  cursor: pointer;
}
.logs input {
  flex: none;
  margin-top: 4px;
  accent-color: var(--indigo);
}
.link {
  padding: 0;
  color: var(--indigo);
  font-size: inherit;
}
.link:hover {
  text-decoration: underline;
}

footer {
  display: flex;
  justify-content: flex-end;
  margin-top: 18px;
}
.submit {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  justify-content: center;
  min-width: 96px;
}
.spin {
  animation: spin 0.9s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.doc {
  min-height: 300px;
  overflow: auto;
  padding: 0 4px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.8;
}
.doc :deep(h1) {
  display: none; /* 标题已经在对话框顶上 */
}
.doc :deep(h2) {
  margin: 18px 0 6px;
  color: var(--ink);
  font-size: var(--t-md);
}
.doc :deep(ol),
.doc :deep(ul) {
  padding-left: 22px;
}
</style>
