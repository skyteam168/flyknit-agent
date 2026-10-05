/**
 * 浏览器预览用的模拟宿主（npm run dev）。
 * 模拟会话存储、流式回复、工具调用与确认流程，便于在没有 Windows 的情况下开发界面。
 */
import type { HostTransport } from './bridge'
import type { AttachmentRef, Conversation, HostEvent, Mode, UiMessage } from './types'

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))
const now = () => new Date().toISOString()
const uid = () => Math.random().toString(36).slice(2, 10)

export function createMockHost(): HostTransport {
  let handler: (data: unknown) => void = () => {}
  const emit = (event: HostEvent) => handler({ kind: 'event', event })
  const conversations = new Map<string, Conversation>()
  const messages = new Map<string, UiMessage[]>()
  const confirmWaiters = new Map<string, (choice: string) => void>()
  const stopped = new Set<string>()
  let language = 'zh-CN'

  const seed = (title: string, mode: Mode, daysAgo: number, pinned = false) => {
    const c = makeConversation(mode)
    c.title = title
    c.pinned = pinned
    c.messageCount = 2
    c.updatedAt = new Date(Date.now() - daysAgo * 86400000).toISOString()
    conversations.set(c.id, c)
    messages.set(c.id, [
      { id: uid(), role: 'user', content: title, createdAt: c.updatedAt },
      { id: uid(), role: 'assistant', content: '这是一段历史对话的示例内容。', createdAt: c.updatedAt },
    ])
  }

  function makeConversation(mode: Mode): Conversation {
    return {
      id: uid(),
      title: '',
      titleSource: 'auto',
      mode,
      pinned: false,
      translateFrom: 'auto',
      translateTo: 'vi',
      createdAt: now(),
      updatedAt: now(),
      deletedAt: null,
      messageCount: 0,
    }
  }

  seed('车间排班表翻译成越南语', 'translate', 0, true)
  seed('整理 D 盘的日报文件', 'agent', 0)
  seed('质检数据周报', 'agent', 1)
  seed('Email gửi nhà cung cấp', 'chat', 3)
  seed('安装 ERP 客户端', 'agent', 12)

  async function stream(conversationId: string, text: string) {
    for (const piece of text.match(/.{1,6}/gsu) ?? []) {
      if (stopped.has(conversationId)) return
      emit({ type: 'chat.delta', conversationId, text: piece })
      await sleep(18)
    }
  }

  async function reply(c: Conversation, text: string, attachments: AttachmentRef[]) {
    const id = c.id
    stopped.delete(id)
    const list = messages.get(id)!
    list.push({ id: uid(), role: 'user', content: text, attachments, createdAt: now() })

    let finalText: string
    if (c.mode === 'translate') {
      await sleep(300)
      finalText =
        c.translateTo === 'zh-CN'
          ? '请各班组长在周五前提交下周的排班表。'
          : 'Đề nghị các tổ trưởng nộp bảng phân ca tuần sau trước thứ Sáu.'
      await stream(id, finalText)
    } else if (c.mode === 'agent') {
      emit({ type: 'chat.reasoning', conversationId: id, text: '用户想整理文件。先查看目录，再列出计划，移动前需要确认。' })
      await sleep(400)
      emit({
        type: 'plan.updated',
        conversationId: id,
        plan: [
          { step: '查看 D:\\日报 目录', status: 'in_progress' },
          { step: '按月份新建文件夹', status: 'pending' },
          { step: '移动文件并汇报结果', status: 'pending' },
        ],
      })
      const lead = '我先看一下 D:\\日报 里有哪些文件。'
      await stream(id, lead)
      const call1 = uid()
      const call2 = uid()
      list.push({
        id: uid(),
        role: 'assistant',
        content: lead,
        toolCalls: [{ id: call1, name: 'list_dir', arguments: '{"path":"D:\\\\日报"}' }],
        createdAt: now(),
      })
      emit({ type: 'chat.message', conversationId: id, message: list[list.length - 1] })
      emit({ type: 'tool.started', conversationId: id, callId: call1, name: 'list_dir', summary: 'D:\\日报', risk: 'auto' })
      await sleep(600)
      const listing = '日报_2026-09-01.xlsx\t18 KB\n日报_2026-09-02.xlsx\t17 KB\n…共 34 个文件'
      emit({ type: 'tool.finished', conversationId: id, callId: call1, ok: true, output: listing, decision: 'auto' })
      emit({
        type: 'plan.updated',
        conversationId: id,
        plan: [
          { step: '查看 D:\\日报 目录', status: 'completed' },
          { step: '按月份新建文件夹', status: 'in_progress' },
          { step: '移动文件并汇报结果', status: 'pending' },
        ],
      })
      const lead2 = '共有 34 个日报，我按月份建立文件夹并移动进去。'
      await stream(id, lead2)
      list.push({
        id: uid(),
        role: 'assistant',
        content: lead2,
        toolCalls: [{ id: call2, name: 'run_shell', arguments: '{}' }],
        createdAt: now(),
      })
      emit({ type: 'chat.message', conversationId: id, message: list[list.length - 1] })
      emit({
        type: 'tool.started',
        conversationId: id,
        callId: call2,
        name: 'run_shell',
        summary: 'Get-ChildItem D:\\日报\\*.xlsx | ForEach-Object { … Move-Item … }',
        risk: 'confirm',
      })
      const requestId = uid()
      const choice = await new Promise<string>((resolve) => {
        confirmWaiters.set(requestId, resolve)
        emit({
          type: 'tool.confirm',
          conversationId: id,
          requestId,
          callId: call2,
          reason: '需要用户确认后执行',
          rationale: lead2,
        })
      })
      if (choice === 'reject') {
        emit({ type: 'tool.finished', conversationId: id, callId: call2, ok: false, output: '用户拒绝了这个操作', decision: 'rejected' })
        finalText = '好的，我没有移动任何文件。你希望按什么方式整理？'
      } else {
        await sleep(700)
        emit({ type: 'tool.finished', conversationId: id, callId: call2, ok: true, output: '退出码：0\n已移动 34 个文件', decision: 'approved' })
        emit({
          type: 'plan.updated',
          conversationId: id,
          plan: [
            { step: '查看 D:\\日报 目录', status: 'completed' },
            { step: '按月份新建文件夹', status: 'completed' },
            { step: '移动文件并汇报结果', status: 'completed' },
          ],
        })
        finalText = '整理完成：\n\n| 文件夹 | 文件数 |\n| --- | --- |\n| 2026-08 | 12 |\n| 2026-09 | 22 |\n\n文件都在 `D:\\日报` 下对应的月份文件夹里。'
      }
      await stream(id, '\n\n' + finalText)
    } else {
      await sleep(250)
      finalText =
        '可以这样写这封邮件：\n\n**主题：** 关于十月面料交期的确认\n\n1. 说明需要确认的订单号和数量\n2. 给出期望的交货日期\n3. 请对方在周三前回复\n\n```text\nDear Supplier,\nPlease confirm the delivery date for PO-2026-1031.\n```'
      await stream(id, finalText)
    }

    if (stopped.has(id)) {
      emit({ type: 'chat.done', conversationId: id, stopReason: 'Cancelled' })
      return
    }
    const assistant: UiMessage = { id: uid(), role: 'assistant', content: finalText, createdAt: now() }
    list.push(assistant)
    emit({ type: 'chat.message', conversationId: id, message: assistant })
    emit({ type: 'chat.done', conversationId: id, stopReason: 'Completed', modelName: 'Qwen3.5-397B' })
    c.messageCount += 2
    c.updatedAt = now()
    if (!c.title) {
      await sleep(400)
      c.title = text.length > 14 ? text.slice(0, 14) : text
      emit({ type: 'conversation.updated', conversation: { ...c } })
    }
  }

  const handle = async (method: string, p: Record<string, any>, files: File[]): Promise<unknown> => {
    switch (method) {
      case 'app.init':
        return {
          version: '0.1.0-dev',
          uiLanguage: language,
          theme: 'system',
          userName: 'nguyen.van.a',
          machineName: 'PC-QC-017',
          connected: true,
          serverMessage: '',
          modelName: 'Qwen3.5-397B',
        }
      case 'settings.setLanguage':
        language = p.language
        return
      case 'conversations.list': {
        const q = String(p.query || '').toLowerCase()
        return [...conversations.values()]
          .filter((c) => (p.trash ? c.deletedAt : !c.deletedAt))
          .filter((c) => !q || c.title.toLowerCase().includes(q))
          .sort((a, b) => Number(b.pinned) - Number(a.pinned) || b.updatedAt.localeCompare(a.updatedAt))
          .map((c) => ({ ...c }))
      }
      case 'conversation.create': {
        const c = makeConversation(p.mode)
        conversations.set(c.id, c)
        messages.set(c.id, [])
        return { ...c }
      }
      case 'conversation.rename': {
        const c = conversations.get(p.id)!
        c.title = p.title
        c.titleSource = 'manual'
        return
      }
      case 'conversation.delete':
        conversations.get(p.id)!.deletedAt = now()
        return
      case 'conversation.restore':
        conversations.get(p.id)!.deletedAt = null
        return
      case 'conversation.purge':
        conversations.delete(p.id)
        return
      case 'conversation.pin':
        conversations.get(p.id)!.pinned = p.pinned
        return
      case 'conversation.setMode':
        conversations.get(p.id)!.mode = p.mode
        return
      case 'conversation.setTranslate': {
        const c = conversations.get(p.id)!
        c.translateFrom = p.from
        c.translateTo = p.to
        return
      }
      case 'messages.load':
        return messages.get(p.id) ?? []
      case 'chat.send':
        void reply(conversations.get(p.conversationId)!, p.text, p.attachments)
        return
      case 'chat.stop':
        stopped.add(p.conversationId)
        return
      case 'tool.confirm':
        confirmWaiters.get(p.requestId)?.(p.choice)
        confirmWaiters.delete(p.requestId)
        return
      case 'files.pick':
        return [{ fileName: '9月质检数据.xlsx', localPath: 'D:\\质检\\9月质检数据.xlsx', mime: 'application/vnd.ms-excel', size: 284000 }]
      case 'files.dropped':
        return files.map((f) => ({
          fileName: f.name,
          localPath: `C:\\Users\\demo\\Downloads\\${f.name}`,
          mime: f.type || 'application/octet-stream',
          size: f.size,
        }))
      case 'files.saveBlob':
        return { fileName: p.fileName, localPath: `C:\\Users\\demo\\AppData\\Local\\Temp\\${p.fileName}`, mime: p.mime, size: Math.round((p.base64.length * 3) / 4) }
      case 'window.toggleTopmost':
        return true
      default:
        return
    }
  }

  const dispatch = (message: any, files: File[] = []) => {
    void (async () => {
      try {
        const result = await handle(message.method, message.params ?? {}, files)
        handler({ kind: 'response', id: message.id, ok: true, result })
      } catch (e) {
        handler({ kind: 'response', id: message.id, ok: false, error: String(e) })
      }
    })()
  }

  return {
    send: (m) => dispatch(m),
    sendWithFiles: (m, files) => dispatch(m, files),
    onMessage: (h) => {
      handler = h
    },
  }
}
