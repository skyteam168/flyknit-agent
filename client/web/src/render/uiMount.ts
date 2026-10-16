import { createApp } from 'vue'
import UiBlock from '../components/UiBlock.vue'
import { i18n } from '../i18n'
import type { UiSpec } from './ui'

/** 点了卡片里的快捷回复：聊天界面收到后把这句话发出去（见 store 里的 QuickReplyEvent） */
export const QuickReplyEvent = 'flyknit:quick-reply'

/**
 * 把一张 ```ui 卡片挂到占位节点上。卡片是独立的小 Vue 应用（v-html 里的内容主界面管不到），
 * 释放时卸载。快捷回复只在对话里能点——在预览、资料库里打开同一段内容时只是展示。
 */
export function mountUi(spec: UiSpec, el: HTMLElement) {
  const t = i18n.global.t
  const inChat = !!el.closest('.scroller')
  const host = document.createElement('div')
  host.style.width = '100%' // 不然会被缩成内容宽度，卡片挤在中间
  const app = createApp(UiBlock, {
    spec,
    labels: { recommended: t('message.uiRecommended'), pros: t('message.uiPros'), cons: t('message.uiCons') },
    onAction: inChat ? (text: string) => window.dispatchEvent(new CustomEvent(QuickReplyEvent, { detail: text })) : undefined,
  })
  app.mount(host)
  el.classList.add('ui-block')
  el.replaceChildren(host)
  ;(el as HTMLElement & { __dispose?: () => void }).__dispose = () => app.unmount()
}
