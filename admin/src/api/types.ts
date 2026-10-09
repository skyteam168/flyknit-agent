export interface AdminUser {
  id: number
  username: string
  display_name: string
  can_read_chats: boolean
  can_dispatch: boolean
  /** 超级管理员：能建号改权限、能改安全策略 */
  is_owner: boolean
  must_change_password: boolean
  disabled: boolean
  created_at: string
  last_login: string | null
}

export interface LoginResult {
  token: string
  display_name: string
  can_read_chats: boolean
  must_change_password: boolean
}

export interface Provider {
  id: number
  name: string
  base_url: string
  api_key_masked: string
  enabled: boolean
  created_at: string
}

export interface ModelConfig {
  id: number
  provider_id: number
  name: string
  model: string
  supports_tools: boolean
  supports_vision: boolean
  context_length: number
  extra_body: Record<string, unknown>
  enabled: boolean
}

export interface RouteRule {
  scene: string
  model_id: number | null
  fallback_model_id: number | null
}

export interface Quota {
  daily_tokens: number
  contact_name: string
  contact_email: string
  contact_phone: string
}

export interface DeviceUsage {
  device_id: number | null
  machine_name: string
  user_name: string
  owner?: string
  department?: string
  disabled?: boolean
  tokens: number
  requests: number
  today_tokens: number
  /** 单独设置的每日上限；null 表示跟全局走，0 表示不限 */
  daily_tokens: number | null
  /** 实际生效的每日上限，0 表示不限 */
  daily_limit: number
}

export interface Skill {
  name: string
  description: string
  version: string
  author: string
  origin: string
  size: number
  file_count: number
  required: boolean
  enabled: boolean
  updated_at: string
}

/** MCP 连接器里员工连接时要填的一项 */
export interface McpField {
  key: string
  label: string
  secret: boolean
  required: boolean
  placeholder: string
  help: string
}

/** 管理员引入的一个 MCP 厂商 */
export interface McpVendor {
  id: string
  name: string
  description: string
  detail: string
  icon: string
  publisher: string
  category: string
  homepage: string
  transport: 'http' | 'sse' | 'stdio'
  url: string
  command: string
  args: string[]
  env: Record<string, string>
  headers: Record<string, string>
  auth: 'none' | 'fields' | 'oauth'
  fields: McpField[]
  preset_masked: Record<string, string>
  oauth: Record<string, string>
  examples: string[]
  timeout_ms: number
  sort_order: number
  enabled: boolean
  updated_at: string
}

/** 保存时提交的内容：预填值传明文，不传表示不改，传空字符串表示清掉 */
export type McpVendorIn = Omit<McpVendor, 'preset_masked' | 'updated_at'> & { preset?: Record<string, string> }

export interface McpDraft {
  id: string
  name: string
  transport: 'http' | 'sse' | 'stdio'
  url: string
  command: string
  args: string[]
  env: Record<string, string>
  headers: Record<string, string>
  auth: 'none' | 'fields' | 'oauth'
  fields: McpField[]
  preset: Record<string, string>
  exists: boolean
  description: string
  homepage: string
  icon: string
  /** config / page / endpoint / registry / server.json */
  source: string
  notes: string[]
}

export interface McpTestResult {
  ok: boolean
  server_name: string
  server_version: string
  tools: { name: string; description: string }[]
  error: string
}

export interface Device {
  id: number
  machine_name: string
  user_name: string
  os_version: string
  client_version: string
  ui_language: string
  disabled: boolean
  created_at: string
  last_seen: string | null
  domain: string
  ip_addresses: string
  observed_ip: string
  mac_address: string
  machine_guid: string
  owner: string
  department: string
  note: string
}

// ---------- 运维代理与下发任务 ----------

/** 装了运维代理（Windows 服务）的电脑。machine_guid 与设备台账对应同一台机器。 */
export interface MachineAgent {
  id: number
  machine_guid: string
  machine_name: string
  os_version: string
  agent_version: string
  disabled: boolean
  created_at: string
  last_seen: string | null
  inventory_at: string | null
}

export interface MachineAgentDetail extends MachineAgent {
  inventory: Record<string, unknown>
}

export type AgentTaskKind = 'collect_info' | 'clean' | 'optimize' | 'install' | 'repair' | 'restart'

export type RunStatus = 'pending' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'expired'

export interface AgentJob {
  id: number
  kind: AgentTaskKind
  title: string
  params: Record<string, unknown>
  created_by: string
  created_at: string
  cancelled_by: string
  total: number
  counts: Partial<Record<RunStatus, number>>
}

export interface AgentRun {
  id: number
  job_id: number
  agent_id: number
  machine_name: string
  status: RunStatus
  created_at: string
  started_at: string | null
  finished_at: string | null
  exit_code: number | null
  output: string
  result: Record<string, unknown>
  kind: string
  title: string
}

export interface AgentJobDetail extends AgentJob {
  runs: AgentRun[]
}

export interface SoftwarePackage {
  id: number
  name: string
  version: string
  filename: string
  kind: 'msi' | 'exe'
  size: number
  sha256: string
  silent_args: string
  uploaded_by: string
  created_at: string
}

// ---------- 下发给员工端 AI agent 的远程指令 ----------

/** 一条自然语言指令，下发给若干台电脑的 AI agent 去理解并执行。 */
export interface RemoteInstruction {
  id: number
  prompt: string
  title: string
  created_by: string
  created_at: string
  cancelled_by: string
  total: number
  counts: Partial<Record<RunStatus, number>>
}

/** 指令在某一台电脑上的执行记录。 */
export interface InstructionRun {
  id: number
  instruction_id: number
  device_id: number
  machine_name: string
  user_name: string
  status: RunStatus
  created_at: string
  started_at: string | null
  finished_at: string | null
  answer: string
  error: string
  conversation_id: string
}

export interface RemoteInstructionDetail extends RemoteInstruction {
  runs: InstructionRun[]
}

export interface InstructionTemplate {
  id: number
  name: string
  prompt: string
  created_by: string
  created_at: string
}

// ---------- 安全中心（IT 配置，下发到员工端） ----------

/** 安全中心一项的目录定义（来自服务端 /security/catalog）。 */
export interface SecurityCatalogItem {
  key: string
  kind: 'bool' | 'int'
  default: boolean | number
  locked_by_default: boolean
  title: string
  risk: string
}

/** 某一项最终生效的值与锁状态。 */
export interface SecurityEffectiveItem {
  value: boolean | number
  locked: boolean
  kind: 'bool' | 'int'
  title: string
  risk: string
  default: boolean | number
  min?: number
  max?: number
}

export interface SecurityDefaults {
  values: Record<string, boolean | number>
  locks: Record<string, boolean>
  effective: Record<string, SecurityEffectiveItem>
}

export interface AuditLog {
  id: number
  device_id: number | null
  machine_name: string
  user_name: string
  conversation_id: string
  tool_name: string
  arguments: string
  scene: string
  risk: string
  decision: string
  status: string
  summary: string
  occurred_at: string
  created_at: string
}

export interface UsageTotals {
  prompt: number
  completion: number
  requests: number
  tokens: number
}

export interface UserUsage {
  device_id: number | null
  machine_name: string
  user_name: string
  label: string
  tokens: number
  requests: number
  today_tokens: number
}

export interface Dashboard {
  generated_at: string
  day: string
  online_minutes: number
  online_devices: number
  total_devices: number
  disabled_devices: number
  active_today: number
  daily_limit: number
  /** 单独设置了每日上限的电脑数 */
  custom_limits?: number
  today: UsageTotals
  yesterday: UsageTotals
  days: string[]
  daily: ({ day: string } & UsageTotals)[]
  users: UserUsage[]
  user_series: (Omit<UserUsage, 'tokens' | 'requests' | 'today_tokens'> & { data: number[] })[]
  scenes_today: { scene: string; tokens: number; requests: number }[]
  scenes: { scene: string; tokens: number; requests: number }[]
  online: { device_id: number; machine_name: string; user_name: string; client_version: string; last_seen: string }[]
}

export interface ChatConversation {
  conversation_id: string
  device_id: number | null
  machine_name: string
  user_name: string
  scene: string
  turns: number
  tokens: number
  model: string
  started_at: string
  last_at: string
}

export interface ChatRecord {
  id: number
  conversation_id: string
  machine_name: string
  user_name: string
  scene: string
  model: string
  user_content: string
  assistant_content: string
  attachments: number
  prompt_tokens: number
  completion_tokens: number
  created_at: string
}

export interface ChatAccess {
  username: string
  action: string
  target: string
  detail: string
  created_at: string
}

export interface AsrConfig {
  model_id: number | null
  model_name: string
  upstream_model: string
  provider: string
  base_url: string
  transport: string
  language: string
  hotwords: string
  vocabulary_id: string
  shared_hotwords: string[]
}

export interface AsrProbe {
  ok: boolean
  transport: string | null
  notes: Record<string, string>
  saved: boolean
}


/** 员工端的一个版本。发布之后全厂电脑会自己装上 */
export interface Release {
  id: number
  version: string
  notes: string
  filename: string
  size: number
  sha256: string
  published: boolean
  uploaded_by: string
  created_at: string
}

/** 安装凭证：每下载一次员工端安装包生成一张，吊销后用那个包新装的电脑注册不上 */
export interface EnrollmentTicket {
  id: number
  label: string
  server_url: string
  created_by: string
  created_at: string
  revoked: boolean
  uses: number
  last_used_at: string | null
}
