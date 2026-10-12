import { computed, reactive } from 'vue'
import { bridge } from './bridge'
import type { LibraryItem } from './types'

// 顶栏「本对话的文件」：资料库里按对话记着每个文件（上传的、粘贴的截图、AI 产出的），这里只取当前对话的。

export const convFiles = reactive({
  /** 列表属于哪个对话（切换对话时旧结果直接丢掉） */
  conversationId: null as string | null,
  items: [] as LibraryItem[],
  /** 正在弹窗里预览的那一组（输出 / 上传的文件）和选中的文件 */
  viewing: null as { title: string; items: LibraryItem[]; id: string } | null,
})

/** AI 产出的 */
export const outputFiles = computed(() => convFiles.items.filter((i) => i.source === 'output'))
/** 用户上传的文件和粘贴的截图 */
export const uploadedFiles = computed(() => convFiles.items.filter((i) => i.source === 'upload' || i.source === 'paste'))
export const convFileCount = computed(() => outputFiles.value.length + uploadedFiles.value.length)

let seq = 0
export async function loadConversationFiles(conversationId: string | null) {
  const mine = ++seq
  if (!conversationId) {
    convFiles.conversationId = null
    convFiles.items = []
    return
  }
  try {
    const r = await bridge.libraryList({ tab: 'all', conversationId, sort: 'updated' })
    if (mine !== seq) return
    convFiles.conversationId = conversationId
    convFiles.items = r.items
  } catch {
    // 只是个附加入口，加载失败就保持原样，不打扰用户
    if (mine === seq && convFiles.conversationId !== conversationId) {
      convFiles.conversationId = conversationId
      convFiles.items = []
    }
  }
}

/** 关掉预览弹窗（Esc 走全局快捷键「停止 / 关闭面板」，也调这里）；弹窗里可能删了文件，顺手刷新一下 */
export function closeFilesViewer() {
  convFiles.viewing = null
  void loadConversationFiles(convFiles.conversationId)
}

/** 右侧的类型标签：有扩展名就用扩展名（ZIP、PNG），没有就空着 */
export function extLabel(item: LibraryItem) {
  const m = /\.([0-9a-z]{1,6})$/i.exec(item.name)
  return m ? m[1].toUpperCase() : ''
}
