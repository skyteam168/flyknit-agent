<script setup lang="ts">
/**
 * 通知正文的富文本编辑器（不引第三方编辑器库）：contenteditable + 工具栏。
 * - 标题、粗体、斜体、下划线、删除线、文字颜色、高亮、列表、引用、对齐、链接、表格、分隔线、清除格式、撤销重做；
 * - 图片、视频先传到服务端（/admin/notices/media），插入的是服务端给的临时预览地址，
 *   保存时服务端会换回正式地址并按白名单清洗整段 HTML（脚本、外部图片等都留不下）；
 * - 粘贴、拖进来的图片文件直接上传；
 * - 可以切到 HTML 源码改。
 */
import { nextTick, onMounted, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { api } from '@/api'

const props = defineProps<{ modelValue: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const box = ref<HTMLDivElement>()
const source = ref(false)
const html = ref(props.modelValue)
const uploading = ref('')
const imageInput = ref<HTMLInputElement>()
const videoInput = ref<HTMLInputElement>()
let saved: Range | null = null

onMounted(() => {
  if (box.value) box.value.innerHTML = props.modelValue || '<p><br></p>'
})
watch(
  () => props.modelValue,
  (v) => {
    // 外面换了内容（打开另一条通知、套模板）才重设，自己打字引起的不动光标
    if (v !== html.value) {
      html.value = v
      if (box.value && !source.value) box.value.innerHTML = v || '<p><br></p>'
    }
  },
)

function sync() {
  html.value = box.value?.innerHTML ?? ''
  emit('update:modelValue', html.value)
}

function remember() {
  const sel = window.getSelection()
  if (sel && sel.rangeCount && box.value?.contains(sel.anchorNode)) saved = sel.getRangeAt(0).cloneRange()
}

function restore() {
  box.value?.focus()
  if (!saved) return
  const sel = window.getSelection()
  sel?.removeAllRanges()
  sel?.addRange(saved)
}

function exec(cmd: string, value?: string) {
  restore()
  document.execCommand(cmd, false, value)
  remember()
  sync()
}

function insertHtml(snippet: string) {
  restore()
  document.execCommand('insertHTML', false, snippet)
  remember()
  sync()
}

const block = (tag: string) => exec('formatBlock', tag)

async function link() {
  remember()
  try {
    const { value } = await ElMessageBox.prompt('链接地址（http:// 或 https:// 开头，员工点了会用系统浏览器打开）', '插入链接', {
      inputPattern: /^https?:\/\/\S+$/i,
      inputErrorMessage: '只支持 http:// 或 https:// 开头的网址',
    })
    const sel = saved?.toString()
    if (sel) exec('createLink', value)
    else insertHtml(`<a href="${value.replace(/"/g, '&quot;')}">${value.replace(/</g, '&lt;')}</a>`)
  } catch {
    // 取消
  }
}

function table() {
  const cells = (tag: string) => `<tr>${`<${tag}>&nbsp;</${tag}>`.repeat(3)}</tr>`
  insertHtml(`<table><thead>${cells('th')}</thead><tbody>${cells('td')}${cells('td')}</tbody></table><p><br></p>`)
}

async function uploadFile(file: File) {
  const video = file.type.startsWith('video/')
  uploading.value = video ? '视频上传中…' : '图片上传中…'
  try {
    const media = await api.uploadNoticeMedia(file, (p) => (uploading.value = `${video ? '视频' : '图片'}上传中… ${p}%`))
    if (media.mime.startsWith('video/')) insertHtml(`<p><video src="${media.preview}" controls></video></p><p><br></p>`)
    else insertHtml(`<img src="${media.preview}" alt="${file.name.replace(/["<>]/g, '')}">`)
  } finally {
    uploading.value = ''
  }
}

async function picked(e: Event) {
  const input = e.target as HTMLInputElement
  for (const f of Array.from(input.files ?? [])) await uploadFile(f)
  input.value = ''
}

function onPaste(e: ClipboardEvent) {
  const files = Array.from(e.clipboardData?.files ?? []).filter((f) => f.type.startsWith('image/'))
  if (files.length) {
    e.preventDefault()
    remember()
    void (async () => {
      for (const f of files) await uploadFile(f)
    })()
  }
}

function onDrop(e: DragEvent) {
  const files = Array.from(e.dataTransfer?.files ?? []).filter((f) => /^(image|video)\//.test(f.type))
  if (!files.length) return
  e.preventDefault()
  remember()
  void (async () => {
    for (const f of files) await uploadFile(f)
  })()
}

async function toggleSource() {
  if (source.value) {
    source.value = false
    await nextTick()
    if (box.value) box.value.innerHTML = html.value || '<p><br></p>'
    sync()
  } else {
    sync()
    source.value = true
  }
}

function onSource(v: string) {
  html.value = v
  emit('update:modelValue', v)
}

const color = ref('#dc2626')
const mark = ref('#fef08a')

function chooseImage() {
  remember()
  imageInput.value?.click()
}
function chooseVideo() {
  remember()
  videoInput.value?.click()
}
defineExpose({ focus: () => box.value?.focus() })
</script>

<template>
  <div class="rich-editor" :class="{ source }">
    <div class="toolbar" @mousedown.prevent>
      <template v-if="!source">
        <el-tooltip content="正文段落" :show-after="400"><button type="button" @click="block('p')">正文</button></el-tooltip>
        <el-tooltip content="大标题" :show-after="400"><button type="button" class="b" @click="block('h2')">H2</button></el-tooltip>
        <el-tooltip content="小标题" :show-after="400"><button type="button" class="b" @click="block('h3')">H3</button></el-tooltip>
        <i class="sep" />
        <el-tooltip content="粗体" :show-after="400"><button type="button" class="b" @click="exec('bold')">B</button></el-tooltip>
        <el-tooltip content="斜体" :show-after="400"><button type="button" class="i" @click="exec('italic')">I</button></el-tooltip>
        <el-tooltip content="下划线" :show-after="400"><button type="button" class="u" @click="exec('underline')">U</button></el-tooltip>
        <el-tooltip content="删除线" :show-after="400"><button type="button" class="s" @click="exec('strikeThrough')">S</button></el-tooltip>
        <el-tooltip content="文字颜色" :show-after="400">
          <span class="color">
            <button type="button" :style="{ color }" @click="exec('foreColor', color)">A</button>
            <input v-model="color" type="color" aria-label="文字颜色" />
          </span>
        </el-tooltip>
        <el-tooltip content="背景高亮" :show-after="400">
          <span class="color">
            <button type="button" :style="{ background: mark }" @click="exec('hiliteColor', mark)">高亮</button>
            <input v-model="mark" type="color" aria-label="高亮颜色" />
          </span>
        </el-tooltip>
        <i class="sep" />
        <el-tooltip content="项目列表" :show-after="400"><button type="button" @click="exec('insertUnorderedList')">• 列表</button></el-tooltip>
        <el-tooltip content="编号列表" :show-after="400"><button type="button" @click="exec('insertOrderedList')">1. 列表</button></el-tooltip>
        <el-tooltip content="引用" :show-after="400"><button type="button" @click="block('blockquote')">❝ 引用</button></el-tooltip>
        <el-tooltip content="左对齐" :show-after="400"><button type="button" @click="exec('justifyLeft')">⇤</button></el-tooltip>
        <el-tooltip content="居中" :show-after="400"><button type="button" @click="exec('justifyCenter')">⇔</button></el-tooltip>
        <el-tooltip content="右对齐" :show-after="400"><button type="button" @click="exec('justifyRight')">⇥</button></el-tooltip>
        <i class="sep" />
        <el-tooltip content="插入链接" :show-after="400"><button type="button" @click="link">🔗 链接</button></el-tooltip>
        <el-tooltip content="插入图片（PNG / JPG / GIF / WebP，10MB 以内；也可以直接粘贴、拖进来）" :show-after="400">
          <button type="button" @click="chooseImage">🖼 图片</button>
        </el-tooltip>
        <el-tooltip content="插入视频（MP4 / WebM，300MB 以内）" :show-after="400"><button type="button" @click="chooseVideo">🎬 视频</button></el-tooltip>
        <el-tooltip content="插入 3 列表格" :show-after="400"><button type="button" @click="table">▦ 表格</button></el-tooltip>
        <el-tooltip content="分隔线" :show-after="400"><button type="button" @click="exec('insertHorizontalRule')">—</button></el-tooltip>
        <el-tooltip content="清除格式" :show-after="400"><button type="button" @click="exec('removeFormat')">⌫ 格式</button></el-tooltip>
        <i class="sep" />
        <el-tooltip content="撤销" :show-after="400"><button type="button" @click="exec('undo')">↶</button></el-tooltip>
        <el-tooltip content="重做" :show-after="400"><button type="button" @click="exec('redo')">↷</button></el-tooltip>
      </template>
      <span class="grow" />
      <span v-if="uploading" class="busy">{{ uploading }}</span>
      <button type="button" :class="{ on: source }" @click="toggleSource">{{ source ? '回到可视编辑' : '</> HTML' }}</button>
    </div>
    <div
      v-show="!source"
      ref="box"
      class="content"
      contenteditable="true"
      spellcheck="false"
      @input="sync"
      @keyup="remember"
      @mouseup="remember"
      @blur="remember"
      @paste="onPaste"
      @drop="onDrop"
    />
    <el-input
      v-if="source"
      type="textarea"
      :model-value="html"
      :autosize="{ minRows: 14, maxRows: 30 }"
      class="code"
      @update:model-value="onSource"
    />
    <input ref="imageInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden @change="picked" />
    <input ref="videoInput" type="file" accept="video/mp4,video/webm" hidden @change="picked" />
  </div>
</template>

<style scoped>
.rich-editor {
  width: 100%;
  border: 1px solid var(--el-border-color);
  border-radius: 8px;
  background: var(--el-bg-color);
}
.toolbar {
  position: sticky;
  top: 0;
  z-index: 1;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px;
  padding: 6px;
  border-bottom: 1px solid var(--el-border-color-lighter);
  border-radius: 8px 8px 0 0;
  background: var(--el-fill-color-light);
}
.toolbar button {
  height: 28px;
  min-width: 28px;
  padding: 0 7px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--el-text-color-regular);
  font-size: 13px;
  cursor: pointer;
}
.toolbar button:hover,
.toolbar button.on {
  background: var(--el-fill-color-darker);
}
.toolbar .b {
  font-weight: 700;
}
.toolbar .i {
  font-style: italic;
}
.toolbar .u {
  text-decoration: underline;
}
.toolbar .s {
  text-decoration: line-through;
}
.color {
  display: inline-flex;
  align-items: center;
}
.color input {
  width: 18px;
  height: 18px;
  padding: 0;
  border: 0;
  background: none;
  cursor: pointer;
}
.sep {
  width: 1px;
  height: 18px;
  margin: 0 4px;
  background: var(--el-border-color);
}
.grow {
  flex: 1;
}
.busy {
  margin-right: 8px;
  color: var(--el-color-primary);
  font-size: 12px;
}
.content {
  min-height: 280px;
  max-height: 560px;
  overflow: auto;
  padding: 12px 16px;
  outline: none;
  font-size: 14px;
  line-height: 1.75;
}
.content :deep(img) {
  display: block;
}
.content :deep(img),
.content :deep(video) {
  max-width: 100%;
  border-radius: 6px;
}
.content :deep(blockquote) {
  margin: 8px 0;
  padding: 6px 12px;
  border-left: 3px solid var(--el-color-primary);
  background: var(--el-fill-color-light);
}
.content :deep(table) {
  border-collapse: collapse;
}
.content :deep(th),
.content :deep(td) {
  min-width: 60px;
  padding: 4px 8px;
  border: 1px solid var(--el-border-color);
}
.code :deep(textarea) {
  border-radius: 0 0 8px 8px;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12.5px;
}
</style>
