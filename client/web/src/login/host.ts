// 登录窗口和宿主（LoginWindow.cs）之间的消息。没登录时还没有主界面那套 bridge，这里单独一份，越简单越好：
// 页面发 { id, type, ... }，宿主回 { id, ok, data | error }。

export type LoginKind = 'domain' | 'local'

export interface Account {
  /** DOMAIN\user */
  display: string
  user: string
  domain: string
  kind: LoginKind
}

export interface InitInfo {
  account: Account
  /** 安装包带了服务器地址和安装凭证：可以「授权登录」。没有就只能手动连接服务器 */
  provisioned: boolean
  server: string
  lang: string
  version: string
}

export interface LegalDoc {
  kind: 'terms' | 'privacy'
  title: string
  content: string
  version: string
}

export interface LegalInfo {
  versions: string
  docs: LegalDoc[]
}

export interface LoginRequest {
  /** windows：用当前 Windows 账号（域账号一键）；password：输入账号密码 */
  mode: 'windows' | 'password'
  username?: string
  password?: string
  /** 同意的协议版本（GET /legal 给的 versions） */
  agreed: string
}

/** 宿主报错时带的代码，界面据此决定怎么提示 */
export type LoginErrorCode = 'need_password' | 'wrong_password' | 'legal_outdated' | 'server' | 'other'

export class HostError extends Error {
  constructor(message: string, readonly code: LoginErrorCode = 'other') {
    super(message)
  }
}

interface WebView {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void
}

const webview: WebView | undefined = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview
const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>()
let seq = 0

webview?.addEventListener('message', (e) => {
  const msg = (typeof e.data === 'string' ? JSON.parse(e.data) : e.data) as { id?: string; ok?: boolean; data?: unknown; error?: string; code?: LoginErrorCode }
  const waiter = msg.id ? pending.get(msg.id) : undefined
  if (!waiter) return
  pending.delete(msg.id!)
  if (msg.ok) waiter.resolve(msg.data)
  else waiter.reject(new HostError(msg.error ?? 'error', msg.code ?? 'other'))
})

function call<T>(type: string, payload: Record<string, unknown> = {}): Promise<T> {
  if (!webview) return mock<T>(type, payload)
  const id = `l${++seq}`
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject })
    webview.postMessage({ id, type, ...payload })
  })
}

export const host = {
  init: () => call<InitInfo>('init'),
  legal: () => call<LegalInfo>('legal'),
  login: (req: LoginRequest) => call<void>('login', { ...req }),
  manual: (server: string, key: string) => call<void>('manual', { server, key }),
  /** 登录成功、动画放完：关窗进主界面 */
  done: () => call<void>('done'),
  window: (action: 'close' | 'minimize') => call<void>('window', { action }),
  isMock: !webview,
}

// ---------- 浏览器里开发预览用的假宿主 ----------
// ?kind=local 看本机账号，?provisioned=0 看手动连接，?fail=1 看出错

const params = new URLSearchParams(location.search)
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

async function mock<T>(type: string, payload: Record<string, unknown>): Promise<T> {
  await sleep(type === 'init' ? 50 : 700)
  const kind: LoginKind = params.get('kind') === 'local' ? 'local' : 'domain'
  switch (type) {
    case 'init':
      return {
        account: kind === 'domain'
          ? { display: 'SHENZHOU\\nguyen.van.a', user: 'nguyen.van.a', domain: 'SHENZHOU', kind }
          : { display: 'PC-QC-017\\worker', user: 'worker', domain: 'PC-QC-017', kind },
        provisioned: params.get('provisioned') !== '0',
        server: 'http://10.0.0.5:8000',
        lang: params.get('lang') ?? 'zh-CN',
        version: '0.3.0',
      } as T
    case 'legal': {
      const doc = (k: 'terms' | 'privacy', title: string): LegalDoc => ({
        kind: k,
        title,
        version: 'default',
        content: `# FlyknitBuddy ${title}\n\n欢迎使用 FlyknitBuddy（以下简称“本服务”）。本服务是由您所在的公司部署和管理的智能办公助手。\n\n## 一、服务内容\n\n1. 本服务通过人工智能模型帮助您完成日常办公事务。\n2. 本服务的功能由公司 IT 部门统一配置。\n\n## 二、账号与登录\n\n1. 您使用本人的 Windows 账号登录本服务。\n2. 以账号密码方式登录时，密码仅由本机 Windows 系统校验，**不会被保存或上传**。\n\n## 三、联系我们\n\n如有疑问，请联系公司 IT 部门。`,
      })
      return { versions: 'terms@default;privacy@default', docs: [doc('terms', '用户协议'), doc('privacy', '隐私政策')] } as T
    }
    case 'login':
      if (params.get('fail')) throw new HostError('无法连接服务器：连接超时', 'server')
      if (payload.mode === 'windows' && kind === 'local') throw new HostError('need password', 'need_password')
      if (payload.mode === 'password' && payload.password !== '1234') throw new HostError('wrong', 'wrong_password')
      return undefined as T
    case 'manual':
      if (!String(payload.server).startsWith('http')) throw new HostError('服务器地址不对', 'other')
      return undefined as T
    default:
      return undefined as T
  }
}
