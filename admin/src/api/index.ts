import { del, download, get, patch, post, put, request, upload } from './http'
import type {
  AdminUser,
  Release,
  AgentJob,
  AgentJobDetail,
  AgentRun,
  AgentTaskKind,
  AsrConfig,
  AsrProbe,
  AuditLog,
  ChatAccess,
  ChatConversation,
  ChatRecord,
  Dashboard,
  Device,
  DeviceUsage,
  LoginResult,
  MachineAgent,
  MachineAgentDetail,
  ModelConfig,
  Provider,
  Quota,
  RemoteInstruction,
  RemoteInstructionDetail,
  InstructionTemplate,
  RouteRule,
  SecurityCatalogItem,
  SecurityDefaults,
  Skill,
  McpDraft,
  McpTestResult,
  McpVendor,
  McpVendorIn,
  SoftwarePackage,
} from './types'

export const api = {
  // 账号
  login: (username: string, password: string) =>
    request<LoginResult>('/login', { method: 'POST', body: { username, password }, silent: true }),
  logout: () => request<void>('/logout', { method: 'POST', silent: true }),
  me: () => get<AdminUser>('/me'),
  changePassword: (old_password: string, new_password: string) =>
    request<void>('/password', { method: 'POST', body: { old_password, new_password }, silent: true }),
  users: () => get<AdminUser[]>('/users'),
  createUser: (body: { username: string; password: string; display_name: string; can_read_chats: boolean; can_dispatch: boolean; is_owner: boolean }) =>
    post<AdminUser>('/users', body),
  updateUser: (id: number, body: Partial<{ display_name: string; can_read_chats: boolean; can_dispatch: boolean; is_owner: boolean; disabled: boolean; password: string }>) =>
    patch<AdminUser>(`/users/${id}`, body),

  // 首页
  dashboard: (days: number) => get<Dashboard>('/dashboard', { days }),

  // 模型
  providers: () => get<Provider[]>('/providers'),
  createProvider: (body: { name: string; base_url: string; api_key: string; enabled: boolean }) =>
    post<Provider>('/providers', body),
  updateProvider: (id: number, body: Partial<{ name: string; base_url: string; api_key: string; enabled: boolean }>) =>
    patch<Provider>(`/providers/${id}`, body),
  deleteProvider: (id: number) => del(`/providers/${id}`),
  syncModels: (id: number) => post<{ total: number; added: string[]; skipped: number }>(`/providers/${id}/sync-models`),
  models: () => get<ModelConfig[]>('/models'),
  createModel: (body: Omit<ModelConfig, 'id'>) => post<ModelConfig>('/models', body),
  updateModel: (id: number, body: Partial<Omit<ModelConfig, 'id'>>) => patch<ModelConfig>(`/models/${id}`, body),
  deleteModel: (id: number) => del(`/models/${id}`),
  testModel: (id: number) =>
    post<{ ok: boolean; status?: number; error?: string; elapsed_ms?: number }>(`/models/${id}/test`),
  routes: () => get<RouteRule[]>('/routes'),
  setRoute: (scene: string, model_id: number | null, fallback_model_id: number | null) =>
    put<RouteRule>(`/routes/${scene}`, { model_id, fallback_model_id }),

  // 配额与用量
  quota: () => get<Quota>('/quota'),
  setQuota: (body: Partial<Quota>) => put<Quota>('/quota', body),
  usage: (days: number) => get<DeviceUsage[]>('/usage', { days }),
  /** 单独设置一台电脑的每日上限：null 跟全局走，0 不限制 */
  setDeviceQuota: (deviceId: number, daily_tokens: number | null) =>
    put<DeviceUsage>(`/devices/${deviceId}/quota`, { daily_tokens }),

  // 技能库
  skills: () => get<Skill[]>('/skills'),
  importSkill: (url: string, required: boolean) => post<{ imported: string[] }>('/skills/import', { url, required }),
  uploadSkill: (file: File, required: boolean) => {
    const form = new FormData()
    form.append('file', file)
    return request<{ imported: string[] }>('/skills/upload', { method: 'POST', body: form, query: { required } })
  },
  updateSkill: (name: string, body: Partial<{ required: boolean; enabled: boolean }>) =>
    patch<Skill>(`/skills/${encodeURIComponent(name)}`, body),
  deleteSkill: (name: string) => del(`/skills/${encodeURIComponent(name)}`),

  // MCP 连接器（和技能库是两套接口）
  mcpVendors: () => get<McpVendor[]>('/mcp/vendors'),
  createMcpVendor: (body: McpVendorIn) => post<McpVendor>('/mcp/vendors', body),
  updateMcpVendor: (id: string, body: McpVendorIn) => put<McpVendor>(`/mcp/vendors/${encodeURIComponent(id)}`, body),
  patchMcpVendor: (id: string, body: Partial<{ enabled: boolean; sort_order: number }>) =>
    patch<McpVendor>(`/mcp/vendors/${encodeURIComponent(id)}`, body),
  deleteMcpVendor: (id: string) => del(`/mcp/vendors/${encodeURIComponent(id)}`),
  parseMcpConfig: (text: string) => post<McpDraft[]>('/mcp/parse', { text }),
  testMcpVendor: (id: string, values: Record<string, string>) => post<McpTestResult>(`/mcp/vendors/${encodeURIComponent(id)}/test`, { values }),

  // 员工端版本（自动更新）
  releases: () => get<Release[]>('/releases'),
  uploadRelease: (file: File, version: string, notes: string, onProgress?: (p: number) => void) => {
    const form = new FormData()
    form.append('file', file)
    form.append('version', version)
    form.append('notes', notes)
    return upload<Release>('/releases', form, onProgress)
  },
  updateRelease: (id: number, body: Partial<{ published: boolean; notes: string }>) =>
    patch<Release>(`/releases/${id}`, body),
  deleteRelease: (id: number) => del(`/releases/${id}`),

  // 语音
  asr: () => get<AsrConfig>('/asr'),
  setAsrHotwords: (hotwords: string[]) => put<{ shared_hotwords: string[] }>('/asr/hotwords', { hotwords }),
  probeAsr: (save: boolean) => post<AsrProbe>('/asr/probe', undefined, { save }),

  // 设备与审计
  devices: () => get<Device[]>('/devices'),
  setDeviceDisabled: (id: number, disabled: boolean) => patch<Device>(`/devices/${id}`, { disabled }),
  updateDevice: (id: number, body: Partial<{ disabled: boolean; owner: string; department: string; note: string }>) =>
    patch<Device>(`/devices/${id}`, body),
  audit: (query: { decision?: string; device_id?: number | null; conversation_id?: string; limit: number; offset: number }) =>
    get<AuditLog[]>('/audit', query),

  // 聊天记录：列表只有元数据；取正文会在服务端留痕
  chats: (query: { days: number; device_id?: number | null; scene?: string; keyword?: string; limit: number; offset: number }) =>
    get<ChatConversation[]>('/chats', query),
  readChat: (conversationId: string) => get<ChatRecord[]>(`/chats/${encodeURIComponent(conversationId)}`),
  chatAccess: (limit = 200) => get<ChatAccess[]>('/chat-access', { limit }),
  exportAudit: (query: { decision?: string; device_id?: number | null; since?: string; until?: string }) =>
    download('/audit/export', query, 'audit.csv'),

  // 运维代理与下发任务
  agents: () => get<MachineAgent[]>('/agents'),
  agent: (id: number) => get<MachineAgentDetail>(`/agents/${id}`),
  agentRuns: (id: number, limit = 20) => get<AgentRun[]>(`/agents/${id}/runs`, { limit }),
  setAgentDisabled: (id: number, disabled: boolean) => patch<MachineAgent>(`/agents/${id}`, { disabled }),
  jobs: (query: { limit: number; offset: number }) => get<AgentJob[]>('/jobs', query),
  job: (id: number) => get<AgentJobDetail>(`/jobs/${id}`),
  createJob: (body: { kind: AgentTaskKind; params?: Record<string, unknown>; agent_ids: number[] }) =>
    post<AgentJob>('/jobs', body),
  cancelJob: (id: number) => post<AgentJob>(`/jobs/${id}/cancel`),
  packages: () => get<SoftwarePackage[]>('/packages'),
  uploadPackage: (file: File, body: { name: string; version: string; silent_args: string }) => {
    const form = new FormData()
    form.append('file', file)
    form.append('name', body.name)
    form.append('version', body.version)
    form.append('silent_args', body.silent_args)
    return request<SoftwarePackage>('/packages', { method: 'POST', body: form })
  },
  deletePackage: (id: number) => del(`/packages/${id}`),

  // 下发给员工端 AI agent 的远程指令
  instructions: (query: { limit: number; offset: number }) =>
    get<RemoteInstruction[]>('/instructions', query),
  instruction: (id: number) => get<RemoteInstructionDetail>(`/instructions/${id}`),
  createInstruction: (body: { prompt: string; title?: string; device_ids: number[] }) =>
    post<RemoteInstruction>('/instructions', body),
  cancelInstruction: (id: number) => post<RemoteInstruction>(`/instructions/${id}/cancel`),
  instructionTemplates: () => get<InstructionTemplate[]>('/instruction-templates'),
  createInstructionTemplate: (body: { name: string; prompt: string }) =>
    post<InstructionTemplate>('/instruction-templates', body),
  deleteInstructionTemplate: (id: number) => del(`/instruction-templates/${id}`),

  // 安全中心（IT 配置，下发到员工端）
  securityCatalog: () => get<SecurityCatalogItem[]>('/security/catalog'),
  security: () => get<SecurityDefaults>('/security'),
  setSecurity: (values: Record<string, boolean | number>, locks: Record<string, boolean>) =>
    put<SecurityDefaults>('/security', { values, locks }),
}
