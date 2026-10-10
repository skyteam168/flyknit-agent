import { ElMessage } from 'element-plus'
import { auth, clearSession } from '@/store/auth'
import router from '@/router'

const BASE = '/api/v1/admin'

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message)
  }
}

function detailOf(body: unknown, fallback: string): string {
  if (body && typeof body === 'object' && 'detail' in body) {
    const d = (body as { detail: unknown }).detail
    if (typeof d === 'string') return d
    // FastAPI 的校验错误是一个数组，取第一条说明
    if (Array.isArray(d) && d[0]?.msg) return `${(d[0].loc ?? []).slice(1).join('.')}：${d[0].msg}`
  }
  return fallback
}

/** 登录失效：401，或共享令牌校验那一层报的 403 */
function expired(status: number, message: string) {
  return status === 401 || (status === 403 && /令牌无效|已被停用/.test(message))
}

export interface RequestOptions {
  method?: string
  body?: unknown
  query?: Record<string, string | number | boolean | undefined | null>
  /** 出错时不弹全局提示，交给调用方处理 */
  silent?: boolean
  raw?: boolean
}

export async function request<T = unknown>(path: string, opts: RequestOptions = {}): Promise<T> {
  const url = new URL(BASE + path, location.origin)
  for (const [k, v] of Object.entries(opts.query ?? {})) {
    if (v !== undefined && v !== null && v !== '') url.searchParams.set(k, String(v))
  }
  const headers: Record<string, string> = {}
  if (auth.token) headers.Authorization = `Bearer ${auth.token}`
  let body: BodyInit | undefined
  if (opts.body instanceof FormData) body = opts.body
  else if (opts.body !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(opts.body)
  }

  let resp: Response
  try {
    resp = await fetch(url, { method: opts.method ?? 'GET', headers, body })
  } catch {
    const message = '连不上服务端，请检查服务是否启动'
    if (!opts.silent) ElMessage.error(message)
    throw new ApiError(message, 0)
  }

  if (!resp.ok) {
    const parsed = await resp.json().catch(() => null)
    const message = detailOf(parsed, `请求失败（HTTP ${resp.status}）`)
    if (expired(resp.status, message) && path !== '/login') {
      clearSession()
      if (router.currentRoute.value.name !== 'login') {
        ElMessage.warning('登录已失效，请重新登录')
        router.replace({ name: 'login', query: { redirect: router.currentRoute.value.fullPath } })
      }
    } else if (!opts.silent) {
      ElMessage.error(message)
    }
    throw new ApiError(message, resp.status)
  }
  if (opts.raw) return resp as unknown as T
  if (resp.status === 204) return undefined as T
  return (await resp.json()) as T
}

export const get = <T>(path: string, query?: RequestOptions['query']) => request<T>(path, { query })
export const post = <T>(path: string, body?: unknown, query?: RequestOptions['query']) =>
  request<T>(path, { method: 'POST', body, query })
export const put = <T>(path: string, body?: unknown) => request<T>(path, { method: 'PUT', body })
export const patch = <T>(path: string, body?: unknown) => request<T>(path, { method: 'PATCH', body })
export const del = <T = void>(path: string) => request<T>(path, { method: 'DELETE' })

/**
 * 上传大文件并回报进度。
 *
 * 用 XMLHttpRequest 不是怀旧：fetch 报不了上传进度，而客户端安装包有一百多兆，
 * 传两分钟没有任何反馈，人会以为卡死了然后去点第二次。
 */
export function upload<T>(path: string, form: FormData, onProgress?: (percent: number) => void): Promise<T> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', BASE + path)
    if (auth.token) xhr.setRequestHeader('Authorization', `Bearer ${auth.token}`)
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) onProgress?.(Math.round((e.loaded / e.total) * 100))
    }
    xhr.onload = () => {
      let parsed: unknown = null
      try {
        parsed = JSON.parse(xhr.responseText)
      } catch {
        parsed = null
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(parsed as T)
        return
      }
      const message = detailOf(parsed, `上传失败（HTTP ${xhr.status}）`)
      ElMessage.error(message)
      reject(new ApiError(message, xhr.status))
    }
    xhr.onerror = () => {
      ElMessage.error('上传中断，请检查网络')
      reject(new ApiError('上传中断', 0))
    }
    xhr.send(form)
  })
}

/** 下载文件（审计导出等），带上登录令牌 */
export async function download(path: string, query: RequestOptions['query'], fallbackName: string) {
  await save(await request<Response>(path, { query, raw: true }), fallbackName)
}

/** POST 之后下载返回的文件（比如生成员工端安装包）。返回保存的文件名 */
export async function downloadPost(path: string, body: unknown, fallbackName: string): Promise<string> {
  return save(await request<Response>(path, { method: 'POST', body, raw: true }), fallbackName)
}

async function save(resp: Response, fallbackName: string): Promise<string> {
  const blob = await resp.blob()
  const match = /filename="?([^"]+)"?/.exec(resp.headers.get('Content-Disposition') ?? '')
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = match?.[1] ?? fallbackName
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 1000)
  return a.download
}
