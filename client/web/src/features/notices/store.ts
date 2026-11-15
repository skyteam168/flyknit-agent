import { reactive } from 'vue'
import { bridge } from '../../bridge'
import { registerFeature, state, toast } from '../../store'
import type { HostEvent, Notice } from '../../types'

/**
 * 消息通知（docs/notices.md）：左下角铃铛、消息中心页、重要通知弹窗。
 * 未读数由宿主推（notices.changed：IT 发布、撤回后服务端推「配置变了」，宿主重新拉一次）；
 * 列表、详情点开时再向宿主要。
 */
export const notices = reactive({
  supported: false,
  unread: 0,
  list: [] as Notice[],
  loading: false,
  loaded: false,
  /** 消息中心里正在看的那条 */
  detail: null as Notice | null,
  detailLoading: false,
  /** 打开程序时自动弹出的重要通知 */
  popup: null as Notice | null,
})

/** 进消息中心前在哪个页面：关掉时回去 */
let previousView: typeof state.view = 'chat'

export async function refreshNoticeStatus() {
  try {
    const s = await bridge.noticeStatus()
    notices.supported = s.supported
    notices.unread = s.unread
  } catch {
    // 宿主还没有这个接口（老版本）：不显示铃铛
  }
}

export async function loadNotices() {
  notices.loading = true
  try {
    const r = await bridge.listNotices()
    notices.list = r.items
    notices.unread = r.unread
    notices.loaded = true
  } catch (e) {
    toast(String((e as Error).message ?? e))
  } finally {
    notices.loading = false
  }
}

export function openNotices() {
  if (state.view !== 'notices') previousView = state.view
  state.view = 'notices'
  notices.detail = null
  void loadNotices()
}

export function closeNotices() {
  state.view = previousView === 'notices' ? 'chat' : previousView
  notices.detail = null
}

function markLocal(id: string) {
  const item = notices.list.find((n) => n.id === id)
  if (item && !item.read) {
    item.read = true
    notices.unread = Math.max(0, notices.unread - 1)
  }
}

export async function fetchNotice(id: string): Promise<Notice | null> {
  try {
    return await bridge.getNotice(id)
  } catch (e) {
    toast(String((e as Error).message ?? e))
    return null
  }
}

export async function openNotice(id: string) {
  notices.detailLoading = true
  notices.detail = notices.list.find((n) => n.id === id) ?? null
  try {
    const full = await fetchNotice(id)
    if (!full) {
      // 刚被撤回或过期了
      notices.detail = null
      notices.list = notices.list.filter((n) => n.id !== id)
      return
    }
    notices.detail = full
    if (!full.read) await markRead(id)
  } finally {
    notices.detailLoading = false
  }
}

export async function markRead(id: string) {
  markLocal(id)
  try {
    await bridge.readNotice(id)
  } catch {
    // 标已读失败不打扰员工，下次刷新时会回来
  }
}

export async function markAllRead() {
  for (const n of notices.list) n.read = true
  notices.unread = 0
  try {
    await bridge.readAllNotices()
  } catch (e) {
    toast(String((e as Error).message ?? e))
  }
}

async function showPopup(id: string) {
  const full = await fetchNotice(id)
  if (full && !full.read) notices.popup = full
}

export async function closePopup(open = false) {
  const n = notices.popup
  notices.popup = null
  if (!n) return
  await markRead(n.id)
  if (open) {
    openNotices()
    void openNotice(n.id)
  }
}

registerFeature({
  id: 'notices',
  eventPrefix: 'notices.',
  onEvent(e: HostEvent) {
    if (e.type !== 'notices.changed') return
    notices.supported = e.supported
    notices.unread = e.unread
    if (state.view === 'notices') void loadNotices()
    else notices.loaded = false
    if (e.popup) void showPopup(e.popup)
  },
})

void refreshNoticeStatus()
