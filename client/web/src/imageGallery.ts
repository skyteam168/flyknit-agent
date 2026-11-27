/**
 * 对话里的图片预览（参照 Claude Code：一轮做完后，「已完成 N 步」那一行右边显示 AI 这一轮看过 / 生成的图片的缩略图，
 * 鼠标移上去看大图，点开进大图查看器、左右翻）。
 * 图片是本机文件（工具结果里存下来的截图、读过的图片、生成的图片），经宿主的 files.preview 读成 data URL，按路径缓存。
 */
import { reactive } from 'vue'
import { bridge } from './bridge'
import { state } from './store'

export interface GalleryImage {
  name: string
  path: string
}

/** 路径 → data URL；'' = 正在读或读不了 */
export const thumbs = reactive<Record<string, string>>({})
const pending = new Set<string>()

const IMAGE_EXT = /\.(png|jpe?g|gif|webp|bmp)$/i
export const isImagePath = (path: string) => IMAGE_EXT.test(path)

export async function loadThumb(path: string): Promise<string> {
  if (thumbs[path]) return thumbs[path]
  if (pending.has(path)) return ''
  pending.add(path)
  thumbs[path] = ''
  try {
    const doc = await bridge.previewFile(path)
    thumbs[path] = doc.kind === 'image' && doc.dataUrl ? doc.dataUrl : ''
  } catch {
    thumbs[path] = ''
  } finally {
    pending.delete(path)
  }
  return thumbs[path]
}

/** 打开大图查看器，从第 index 张开始，可以左右翻 */
export async function openGallery(items: GalleryImage[], index: number) {
  if (!items.length) return
  const i = Math.min(Math.max(index, 0), items.length - 1)
  const it = items[i]
  state.imageView = { src: thumbs[it.path] || '', name: it.name, path: it.path, items, index: i }
  if (!thumbs[it.path]) {
    const src = await loadThumb(it.path)
    if (state.imageView?.path === it.path) state.imageView.src = src
  }
}

/** 查看器里翻到上一张 / 下一张（到头了就停住） */
export function stepGallery(delta: number) {
  const v = state.imageView
  if (!v?.items?.length) return
  const next = (v.index ?? 0) + delta
  if (next < 0 || next >= v.items.length) return
  void openGallery(v.items, next)
}
