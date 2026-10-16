/**
 * 粘贴的长文本：超过一定长度就不塞进输入框，折叠成一张卡片随消息发出去（宿主存成 .txt，
 * 发给模型时把全文放进消息）。这里只放和界面无关的判断。
 */
import type { AttachmentRef } from './types'

/** 超过这么多字或这么多行就折叠成卡片 */
export const FoldChars = 2000
export const FoldLines = 40

export function shouldFold(text: string): boolean {
  if (text.length >= FoldChars) return true
  let lines = 1
  for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10 && ++lines >= FoldLines) return true
  return false
}

export function lineCount(text: string): number {
  return text ? text.split('\n').length : 0
}

/** UTF-8 再转 base64；分段拼，几 MB 的文本也不会把参数栈撑爆 */
export function utf8Base64(text: string): string {
  const bytes = new TextEncoder().encode(text)
  let bin = ''
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return btoa(bin)
}

const textExts = new Set(['txt', 'md', 'markdown', 'log', 'json', 'xml', 'yaml', 'yml', 'ini', 'conf', 'cfg', 'sql'])

/** 点开时在客户端里看全文的附件：粘贴的文本和常见纯文本文件；其他的还是交给系统程序 */
export function isTextAttachment(a: AttachmentRef): boolean {
  if (a.pasted) return true
  const ext = a.fileName.split('.').pop()?.toLowerCase() ?? ''
  return textExts.has(ext) || a.mime === 'text/plain' || a.mime === 'text/markdown'
}

/** 发给宿主时去掉只给界面用的全文，免得同一段文字在桥上传两遍 */
export function forHost(a: AttachmentRef): AttachmentRef {
  const { pasteText: _drop, ...rest } = a
  return rest
}

/** 卡片上显示的开头几行 */
export function snippet(text: string, max = 240): string {
  return text.slice(0, max).replace(/\n{3,}/g, '\n\n')
}
