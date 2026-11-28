/**
 * AI 专用文件夹（宿主 Security/TrustedFolders.cs，docs/trusted-folders.md）：
 * 员工把一个文件夹标成「专门给 AI 干活用的」，IT 在安全中心允许时，任务在这个文件夹里的删除、运行脚本不再一个个确认。
 * 这里只是界面状态：标没标、能不能标；真正放不放行由宿主在每次工具调用时判定。
 */
import { reactive } from 'vue'
import { bridge } from '../../bridge'
import { toast } from '../../store'

export interface TrustState {
  allowed: boolean
  trusted: boolean
  /** 不能标的原因（磁盘根目录、系统目录……），能标为空 */
  reason: string
}

export const trust = reactive<Record<string, TrustState>>({})
const key = (path: string) => path.toLowerCase()

export function trustOf(path: string): TrustState | undefined {
  return trust[key(path)]
}

export async function loadTrust(path: string) {
  if (!path) return
  try {
    trust[key(path)] = await bridge.trustGet(path)
  } catch {
    // 旧版宿主没有这个功能：不显示
  }
}

export async function setTrust(path: string, on: boolean, done?: (on: boolean) => void) {
  try {
    const r = await bridge.trustSet(path, on)
    if (!r.ok) {
      toast(r.message)
      return
    }
    const cur = trust[key(path)]
    if (cur) cur.trusted = r.trusted
    done?.(r.trusted)
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  }
}
