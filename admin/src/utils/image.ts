/** 上传前能接受的原图大小：再大的图片也没必要（头像只显示几十像素） */
export const MAX_SOURCE_BYTES = 20 * 1024 * 1024

/** 压好后的上限，和服务端字段长度留足余量 */
const MAX_RESULT_CHARS = 300_000

function load(file: File): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file)
    const img = new Image()
    img.onload = () => {
      URL.revokeObjectURL(url)
      resolve(img)
    }
    img.onerror = () => {
      URL.revokeObjectURL(url)
      reject(new Error('读不了这张图片'))
    }
    img.src = url
  })
}

/**
 * 把上传的图片做成方形小图（居中裁剪、缩到 size×size），转成 data URL。
 * 原图几 MB 也行，压完一般只有十几到几十 KB；透明背景会保留（WebP，浏览器不支持时退回 PNG）。
 */
export async function toSquareDataUrl(file: File, size = 256): Promise<string> {
  if (file.size > MAX_SOURCE_BYTES) throw new Error('图片太大了，请换一张 20MB 以内的')
  const img = await load(file)
  const w = img.naturalWidth || size
  const h = img.naturalHeight || size
  const side = Math.min(w, h)
  const target = Math.min(size, side) || size
  const canvas = document.createElement('canvas')
  canvas.width = target
  canvas.height = target
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('浏览器不支持处理图片')
  ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(img, (w - side) / 2, (h - side) / 2, side, side, 0, 0, target, target)
  for (const quality of [0.9, 0.8, 0.65, 0.5]) {
    const url = canvas.toDataURL('image/webp', quality)
    if (url.length <= MAX_RESULT_CHARS) return url
  }
  throw new Error('图片压缩后仍然太大，请换一张简单些的图片')
}
