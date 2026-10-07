/**
 * 浏览器预览用的模拟宿主（npm run dev）。
 * 模拟会话存储、流式回复、工具调用与确认流程，便于在没有 Windows 的情况下开发界面。
 */
import type { HostTransport } from './bridge'
import type { AttachmentRef, Conversation, HostEvent, McpVendor, MemoryItem, Mode, UiMessage } from './types'

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))

// 安全中心：锁住的那几项照着真实默认值来，开发预览里才看得出区别
const mockSecurity = [
  { key: 'sandbox', value: true, locked: true, kind: 'bool', title: '工作区隔离', risk: '关闭后 AI 可以在工作区之外读写文件。危险命令仍会拦截，但范围限制没有了。', min: null, max: null },
  { key: 'network_allowlist', value: true, locked: true, kind: 'bool', title: '网络访问白名单', risk: '关闭后 AI 可以访问任意网址。', min: null, max: null },
  { key: 'system_tools', value: false, locked: false, kind: 'bool', title: '系统级工具（注册表 / 服务 / 计划任务 / WMI）', risk: '开启后 AI 可以调用这些工具，它们能绕过一部分文件和进程限制。', min: null, max: null },
  { key: 'delete_protection', value: true, locked: true, kind: 'bool', title: '删除保护（回收站）', risk: '关闭后 AI 删除的文件直接永久删除，不进回收站，无法恢复。', min: null, max: null },
  { key: 'auto_backup', value: true, locked: false, kind: 'bool', title: '修改文件前自动备份', risk: '关闭后 AI 覆盖文件将无法还原。删除仍会进回收站，但覆盖写不会。', min: null, max: null },
  { key: 'backup_quota_mb', value: 512, locked: false, kind: 'int', title: '备份容量上限（MB）', risk: '调小之后较早的备份会被提前清掉。', min: 64, max: 20480 },
  { key: 'batch_delete_threshold', value: 20, locked: true, kind: 'int', title: '批量删除确认阈值', risk: '调大之后，一次删除更多文件也不再额外确认。', min: 1, max: 10000 },
  { key: 'notifications', value: true, locked: false, kind: 'bool', title: '任务完成通知', risk: '关闭后任务跑完不会提醒，需要自己回来看。', min: null, max: null },
  { key: 'notification_sound', value: false, locked: false, kind: 'bool', title: '通知提示音', risk: '', min: null, max: null },
]

// 伪造录音的计时器（只在开发预览里用）
let speechTimer: number | null = null
let speechElapsed = 0
function stopSpeechTimer() {
  if (speechTimer !== null) clearInterval(speechTimer)
  speechTimer = null
}
const now = () => new Date().toISOString()
const uid = () => Math.random().toString(36).slice(2, 10)

export function createMockHost(): HostTransport {
  let handler: (data: unknown) => void = () => {}
  const emit = (event: HostEvent) => handler({ kind: 'event', event })

  // ---------- MCP 连接器（模拟管理员上架的几家） ----------
  const mcpVendor = (id: string, name: string, description: string, extra: Partial<McpVendor> = {}): McpVendor => ({
    id, name, description, detail: '', icon: '', publisher: '', category: '', homepage: '', transport: 'http', auth: 'none',
    examples: [], fields: [], needsInput: [], status: 'disconnected', error: '', enabled: false, serverName: '',
    transportUsed: '', connectedAt: null, tools: [], ...extra,
  })
  const mcpVendors: McpVendor[] = [
    mcpVendor('tencent-docs', '腾讯文档', '创建、编辑和协作腾讯文档。用自然语言管理在线表格、文档和幻灯片，轻松完成内容查询、数据整理和团队协同。', {
      publisher: '腾讯', category: '文档', auth: 'fields', icon: "data:image/svg+xml;utf8,%3Csvg xmlns='http://www.w3.org/2000/svg' width='120' height='40'%3E%3Crect width='120' height='40' rx='6' fill='%233b82f6'/%3E%3Ctext x='60' y='27' font-size='18' text-anchor='middle' fill='white'%3EDOCS%3C/text%3E%3C/svg%3E",
      detail: '连接后，AI 可以：\n\n- 新建在线表格、文档并填入内容\n- 读取、总结你最近编辑的文档\n- 在表格里按条件查找、排序数据',
      fields: [{ key: 'API_KEY', label: 'API Key', secret: true, required: true, placeholder: '在腾讯文档开放平台获取', help: '腾讯文档 → 设置 → 开放平台 → 创建密钥', preset: false, hasValue: false, value: '' }],
      needsInput: ['API_KEY'],
      examples: ['帮我在腾讯文档里新建一个在线表格，包含姓名、部门、入职日期三列，并填入示例数据', '打开我最近编辑的腾讯文档，帮我总结文档的主要内容和关键要点', '在腾讯文档的表格里查找所有【销售额】大于 10 万的记录，按金额从高到低排序', '帮我把这份会议纪要整理成腾讯文档，按议题分段并标注负责人和截止日期'],
    }),
    mcpVendor('wecom', '企业微信', '企业微信官方 CLI 套件，覆盖消息、邮件、文档、待办、日程、会议、微盘、通讯录等业务功能。', { publisher: '腾讯', category: '办公', icon: "data:image/svg+xml;utf8,%3Csvg xmlns='http://www.w3.org/2000/svg' width='24' height='36'%3E%3Ccircle cx='12' cy='10' r='8' fill='%23f24e1e'/%3E%3Ccircle cx='12' cy='26' r='8' fill='%230acf83'/%3E%3C/svg%3E", examples: ['给生产部群发一条明天停电检修的通知'] }),
    mcpVendor('tencent-meeting', '腾讯会议', '通过命令行创建、查询和管理腾讯会议。支持快速发起会议、查看日程安排、管理参会人员。', { publisher: '腾讯', auth: 'oauth', category: '会议' }),
    mcpVendor('qq-mail', 'QQ邮箱', '收发、搜索和整理 QQ 邮件。用自然语言读取邮件内容、汇总邮件线程、管理文件夹。', { publisher: '腾讯', category: '邮件' }),
    mcpVendor('feishu', '飞书', '通过命令行管理飞书/Lark 全产品能力：即时通讯、邮箱、日历、云文档、电子表格、多维表格（Base）、任务等。', { publisher: '字节跳动', category: '办公' }),
    mcpVendor('lexiang', '乐享知识库', '搜索、创建和管理乐享知识库中的文档。支持导入 Markdown、按标签整理内容、追踪团队文档的更新。', { category: '知识库' }),
  ]
  const conversations = new Map<string, Conversation>()
  const messages = new Map<string, UiMessage[]>()
  const confirmWaiters = new Map<string, (choice: string) => void>()
  const stopped = new Set<string>()
  let language = 'zh-CN'

  // 更新条在浏览器里没法真的更新，用 ?update=ready / ?update=needsit 看样子
  const wanted = new URLSearchParams(location.search).get('update') ?? 'none'
  const mockUpdate = {
    stage: wanted,
    version: '0.3.0',
    notes: '· 语音输入支持越南语\n· 修复了大表格偶尔卡住的问题\n· 安全中心可以看到最近被拦下来的操作',
    progress: 1,
    message: 'C:\\Program Files\\FlyknitBuddy',
  }
  const workspaces = [
    { path: 'C:\\Users\\demo\\Documents\\Flyknit', name: 'Flyknit', exists: true, isDefault: true },
    { path: 'D:\\agentwork', name: 'agentwork', exists: true, isDefault: false },
    { path: 'D:\\', name: 'D:\\', exists: true, isDefault: false },
  ]
  let defaultWorkspace = workspaces[1].path
  let maximized = false
  const ago = (min: number) => new Date(Date.now() - min * 60000).toISOString()
  const securityEvents = [
    { id: 9, conversationId: '', title: '', scene: '', tool: 'security_settings', detail: '「操作完成通知」：开启 → 关闭', decision: 'changed', reason: '', createdAt: ago(1) },
    { id: 8, conversationId: '', title: '下载供应商报价', scene: 'agent', tool: 'run_shell', detail: 'Invoke-WebRequest https://pastebin.com/raw/abc -OutFile D:\\a.ps1', decision: 'blocked', reason: '要访问的 pastebin.com 不在网络白名单内，已阻止', createdAt: ago(2) },
    { id: 6, conversationId: '', title: '整理 D 盘的日报文件', scene: 'agent', tool: 'run_shell', detail: 'Get-ChildItem D:\\日报\\*.xlsx | ForEach-Object { … Move-Item … }', decision: 'approved', reason: '', createdAt: ago(3) },
    { id: 5, conversationId: '', title: '清理临时文件', scene: 'agent', tool: 'run_shell', detail: 'Remove-Item -Recurse -Force C:\\Windows\\Temp', decision: 'blocked', reason: '命中安全规则（Remove-Item -Recurse -Force），此类命令可能损害系统，已阻止', createdAt: ago(26) },
    { id: 4, conversationId: '', title: '质检数据周报', scene: 'agent', tool: 'write_file', detail: 'E:\\备份\\质检周报.xlsx', decision: 'rejected', reason: '', createdAt: ago(95) },
    { id: 7, conversationId: '', title: '打开 ERP 网页', scene: 'agent', tool: 'open_app', detail: 'msedge https://erp.shenzhougroup.com/report', decision: 'allowed', reason: '', createdAt: ago(150) },
    { id: 3, conversationId: '', title: '安装 ERP 客户端', scene: 'agent', tool: 'run_shell', detail: 'msiexec /i \\\\fileserver\\soft\\erp.msi /qn', decision: 'remembered', reason: '', createdAt: ago(180) },
    { id: 2, conversationId: '', title: 'Email gửi nhà cung cấp', scene: 'chat', tool: 'write_file', detail: 'D:\\草稿\\供应商邮件.txt', decision: 'approved', reason: '', createdAt: ago(3 * 1440) },
    { id: 1, conversationId: '', title: '车间排班表翻译成越南语', scene: 'translate', tool: 'delete_path', detail: 'D:\\排班\\旧排班表.docx', decision: 'approved', reason: '', createdAt: ago(12 * 1440) },
  ]
  // 资料库（演示数据）：图片用 SVG 渐变代替
  const svg = (c1: string, c2: string, w: number, h: number, label: string) =>
    'data:image/svg+xml;utf8,' +
    encodeURIComponent(
      `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${c1}"/><stop offset="1" stop-color="${c2}"/></linearGradient></defs><rect width="100%" height="100%" fill="url(#g)"/><text x="50%" y="52%" font-size="${Math.round(w / 10)}" fill="white" text-anchor="middle" font-family="sans-serif">${label}</text></svg>`,
    )
  type MockLib = import('./types').LibraryItem
  const libItem = (id: string, name: string, kind: MockLib['kind'], source: string, days: number, extra: Partial<MockLib> = {}): MockLib => ({
    id, name, path: `C:\\Users\\demo\\Documents\\Flyknit\\${name}`, kind, mime: '', size: 1024 * (40 + id.length * 37), source,
    conversationId: null, folderId: null, favorite: false, managed: source !== 'output', hidden: false, exists: true,
    createdAt: new Date(Date.now() - days * 86400000).toISOString(), updatedAt: new Date(Date.now() - days * 86400000).toISOString(),
    deletedAt: null, url: '', thumbUrl: null, ...extra,
  })
  const libFolders: import('./types').LibraryFolder[] = [{ id: 'f1', name: '品牌素材', count: 0, createdAt: now(), updatedAt: now() }]
  const library: MockLib[] = [
    libItem('l1', 'FlyknitBuddy-logo.png', 'image', 'upload', 1, { thumbUrl: svg('#1d4ed8', '#60a5fa', 400, 400, 'Logo'), url: svg('#1d4ed8', '#60a5fa', 800, 800, 'Logo'), favorite: true }),
    libItem('l2', '安全海报.png', 'image', 'output', 2, { thumbUrl: svg('#0f172a', '#2563eb', 400, 560, '海报'), url: svg('#0f172a', '#2563eb', 800, 1120, '海报') }),
    libItem('l3', '录音.m4a', 'audio', 'upload', 4),
    libItem('l4', '粘贴的 markdown.md', 'document', 'library', 5),
    libItem('l5', '服装OEM产业AI数字化转型专家访谈准备稿.docx', 'document', 'output', 7),
    libItem('l6', '城市夜景.png', 'image', 'paste', 3, { thumbUrl: svg('#312e81', '#0ea5e9', 480, 300, '截图'), url: svg('#312e81', '#0ea5e9', 960, 600, '截图') }),
    libItem('l7', '九月产量汇总.xlsx', 'sheet', 'output', 8),
    libItem('l8', 'Q3 汇报.pptx', 'slides', 'output', 9),
    libItem('l9', '会议要点.md', 'note', 'note', 0),
    libItem('l10', 'run.py', 'code', 'output', 1, { hidden: true }),
  ]
  const libTrash: MockLib[] = []
  const memory = {
    items: [
      { id: 'm1', kind: 'preference', text: '报表默认保存到 D:\\报表，文件名带日期', date: '2026-09-28', lastSeen: '2026-10-05', proofCount: 3, history: ['报表保存到桌面'], slot: 'save_folder', slotLabel: '文件默认保存位置' },
      { id: 'm2', kind: 'preference', text: '给越南同事的通知用中越双语', date: '2026-10-02' },
      { id: 'm3', kind: 'fact', text: '日报放在 D:\\日报，按月份分文件夹', date: '2026-10-05' },
      { id: 'm6', kind: 'fact', text: 'ERP 测试环境地址是 http://10.2.0.15:8080', date: '2026-05-10', lastSeen: '2026-06-01', validUntil: '2026-08-30', expired: true },
      { id: 'm4', kind: 'success', text: '质检周报先按车间汇总再画折线图，阅读最清楚', date: '2026-10-01' },
      { id: 'm5', kind: 'lesson', text: '.xls 旧格式要先另存为 .xlsx 再读取，否则会乱码', date: '2026-09-30' },
    ] as MemoryItem[],
    episodes: [
      {
        id: 'e1', conversationId: '', title: '生成质检周报', task: '根据 9 月质检数据生成周报', summary: '读取 D:\\质检\\9月.xlsx，按车间汇总不良率，生成 Excel 周报并保存到 D:\\报表',
        outcome: 'success', procedure: '1. 读取质检数据\n2. 按车间汇总不良率\n3. 生成折线图\n4. 保存到 D:\\报表\\质检周报_日期.xlsx', lessons: [], feedback: 1, uses: 3, createdAt: '2026-10-01T10:00:00+08:00',
      },
      {
        id: 'e2', conversationId: '', title: '安装 ERP 客户端', task: '从共享盘安装 ERP 客户端', summary: '安装程序被杀毒软件拦截', outcome: 'failure',
        procedure: '1. 先暂停实时防护\n2. 以管理员身份运行安装包', lessons: ['安装前先确认杀毒软件不会拦截'], feedback: 0, uses: 0, createdAt: '2026-09-25T15:00:00+08:00',
      },
    ],
    skills: [{ name: 'qc-weekly-report', description: '生成质检周报（按车间汇总、折线图、保存到 D:\\报表）', path: 'C:\\Users\\demo\\AppData\\Roaming\\Flyknit\\skills\\learned\\qc-weekly-report', status: 'active' as const, version: 2, uses: 5, successes: 4, failures: 1 },
      { name: 'erp-install', description: '安装 ERP 客户端（先关杀毒软件）', path: 'C:\\Users\\demo\\AppData\\Roaming\\Flyknit\\skills\\learned\\erp-install', status: 'candidate' as const, version: 1, uses: 0, successes: 0, failures: 0 },
      { name: 'merge-invoices', description: '合并多张发票 PDF', path: 'C:\\Users\\demo\\AppData\\Roaming\\Flyknit\\skills\\learned\\merge-invoices', status: 'retired' as const, version: 1, uses: 3, successes: 0, failures: 3 }],
  }
  const makeSkill = (name: string, description: string, extra: Record<string, any> = {}) => ({
    name,
    description,
    version: '1.0.0',
    author: 'IT',
    license: 'MIT',
    homepage: '',
    origin: '公司技能库',
    source: 'personal',
    organization: false,
    learned: false,
    required: false,
    enabled: true,
    directory: `C:\\Users\\demo\\AppData\\Roaming\\Flyknit\\skills\\${name}`,
    files: ['references/template.xlsx'],
    scripts: [] as string[],
    bytes: 48000,
    ...extra,
  })
  let skills = [
    makeSkill('excel-report', '按公司模板生成 Excel 周报和月报。需要汇总质检或生产数据时使用。', {
      source: 'organization', organization: true, required: true, version: '1.2.0', origin: '公司技能库',
      files: ['references/周报模板.xlsx', 'scripts/build.py'], scripts: ['scripts/build.py'], bytes: 182000,
    }),
    makeSkill('factory-terms', '工厂专业术语中越柬对照表，翻译时保持用词统一。', {
      source: 'organization', organization: true, version: '3.1.0', files: ['references/terms.csv'], bytes: 96000,
    }),
    makeSkill('meeting-notes', '把会议录音转写的文字整理成结构化会议纪要。', { origin: 'meeting-notes.zip' }),
    makeSkill('qc-weekly-report', '生成质检周报（按车间汇总、折线图、保存到 D:\\报表）', {
      source: 'learned', learned: true, origin: '任务复盘自动总结', version: '', files: [], bytes: 3200,
    }),
  ]
  const libraryItems = [
    { name: 'excel-report', description: '按公司模板生成 Excel 周报和月报', version: '1.2.0', author: 'IT', origin: '上传：excel-report.zip', size: 182000, required: true, installed: true, updatable: false },
    { name: 'pdf-toolkit', description: 'PDF 合并、拆分、提取文字和表格', version: '2.0.1', author: 'community', origin: 'https://github.com/acme/agent-skills', size: 64000, required: false, installed: false, updatable: false },
    { name: 'erp-install', description: '在新电脑上安装并配置 ERP 客户端', version: '1.0.0', author: 'IT', origin: '上传：erp-install.zip', size: 24000, required: false, installed: false, updatable: false },
  ]
  let schedules: any[] = [
    {
      id: 's1', name: '整理日报', instructions: '把 D:\\日报 里的 Excel 文件按月份归档到对应的子文件夹，完成后告诉我整理了多少个文件。',
      kind: 'weekdays', hour: 8, minute: 30, weekday: 1, dayOfMonth: 1, at: null, enabled: true,
      workspace: null, permission: 'workspace', modelId: null, catchUp: true,
      nextRunAt: new Date(Date.now() + 9 * 3600000).toISOString(), lastRunAt: new Date(Date.now() - 15 * 3600000).toISOString(),
      lastStatus: 'ok', lastSummary: '已整理 34 个文件', lastConversationId: null, runCount: 12,
    },
    {
      id: 's2', name: '生成质检周报', instructions: '汇总本周的质检数据，按车间做成 Excel 周报，保存到 D:\\报表，文件名带上日期。',
      kind: 'weekly', hour: 16, minute: 0, weekday: 5, dayOfMonth: 1, at: null, enabled: true,
      workspace: null, permission: 'workspace', modelId: null, catchUp: true,
      nextRunAt: new Date(Date.now() + 3 * 86400000).toISOString(), lastRunAt: new Date(Date.now() - 4 * 86400000).toISOString(),
      lastStatus: 'failed', lastSummary: '读取 9月.xls 失败：旧格式需要先另存为 .xlsx', lastConversationId: null, runCount: 3,
    },
    {
      id: 's3', name: '清理临时文件', instructions: '检查工作区里超过 30 天没有改动的临时文件，列出清单让我确认后再删除。',
      kind: 'monthly', hour: 9, minute: 0, weekday: 1, dayOfMonth: 1, at: null, enabled: false,
      workspace: null, permission: 'readonly', modelId: null, catchUp: false,
      nextRunAt: null, lastRunAt: null, lastStatus: '', lastSummary: '', lastConversationId: null, runCount: 0,
    },
  ]
  // 开发预览用的假产出文件
  const mockOutputs = [
    { path: 'D:\\工作区\\日报\\营业部Q3需求沟通会.pptx', name: '营业部Q3需求沟通会.pptx', extension: 'pptx', size: 2_418_000, modifiedAt: now(), previewable: true, exists: true },
    { path: 'D:\\工作区\\日报\\九月产量汇总.xlsx', name: '九月产量汇总.xlsx', extension: 'xlsx', size: 184_000, modifiedAt: now(), previewable: true, exists: true },
    { path: 'D:\\工作区\\日报\\说明.md', name: '说明.md', extension: 'md', size: 2_400, modifiedAt: now(), previewable: true, exists: true },
  ]

  const mockPreviews: Record<string, unknown> = {
    'D:\\工作区\\日报\\营业部Q3需求沟通会.pptx': {
      kind: 'sections',
      sections: [
        { title: '第 1 页 · 营业部 Q3 需求沟通会', text: '时间：9 月 30 日 15:25\n地点：三楼会议室\n参会：营业部、IT、生产计划' },
        { title: '第 2 页 · 本季度待解决的三件事', text: '1. 排班表翻译成越南语的时效\n2. 质检数据周报自动化\n3. 共享盘权限梳理' },
        { title: '第 3 页 · 下一步', text: '10 月 8 日前出方案，10 月 20 日试点一条产线。' },
      ],
    },
    'D:\\工作区\\日报\\九月产量汇总.xlsx': {
      kind: 'table',
      sections: [
        { title: '九月产量', rows: [['车间', '计划', '实际', '达成率'], ['一车间', '12000', '12480', '104%'], ['二车间', '9500', '9120', '96%'], ['三车间', '7800', '8010', '103%']] },
        { title: '异常明细', rows: [['日期', '车间', '原因'], ['09-12', '二车间', '设备检修半天']] },
      ],
    },
    'D:\\工作区\\日报\\说明.md': {
      kind: 'markdown',
      text: '# 九月产量汇总说明\n\n数据来自共享盘 `\\\\fs01\\报表\\九月`，按车间汇总。\n\n- 达成率 = 实际 / 计划\n- 二车间 09-12 设备检修，已在异常明细里标注\n',
    },
  }

  async function fakeCompaction(conversationId: string) {
    emit({ type: 'context.compacting', conversationId, phase: 'scanning', percent: 5, messages: 34 })
    for (let pct = 12; pct <= 95; pct += 7) {
      await sleep(260)
      emit({ type: 'context.compacting', conversationId, phase: 'summarizing', percent: pct, messages: 34 })
    }
    await sleep(300)
    emit({ type: 'context.compacting', conversationId, phase: 'done', percent: 100, messages: 34 })
  }

  const mockShortcuts = [{"id": "conversation.new", "group": "task", "binding": "Ctrl+N", "default": "Ctrl+N", "global": false, "fixed": false, "customized": false}, {"id": "conversation.prev", "group": "task", "binding": "Ctrl+[", "default": "Ctrl+[", "global": false, "fixed": false, "customized": false}, {"id": "conversation.next", "group": "task", "binding": "Ctrl+]", "default": "Ctrl+]", "global": false, "fixed": false, "customized": false}, {"id": "conversation.rename", "group": "task", "binding": "F2", "default": "F2", "global": false, "fixed": false, "customized": false}, {"id": "conversation.delete", "group": "task", "binding": "Ctrl+Delete", "default": "Ctrl+Delete", "global": false, "fixed": false, "customized": false}, {"id": "chat.send", "group": "chat", "binding": "Enter", "default": "Enter", "global": false, "fixed": true, "customized": false}, {"id": "chat.newline", "group": "chat", "binding": "Shift+Enter", "default": "Shift+Enter", "global": false, "fixed": true, "customized": false}, {"id": "chat.stop", "group": "chat", "binding": "Escape", "default": "Escape", "global": false, "fixed": false, "customized": false}, {"id": "chat.search", "group": "chat", "binding": "Ctrl+F", "default": "Ctrl+F", "global": false, "fixed": false, "customized": false}, {"id": "chat.regenerate", "group": "chat", "binding": "Ctrl+R", "default": "Ctrl+R", "global": false, "fixed": false, "customized": false}, {"id": "chat.focusInput", "group": "chat", "binding": "Ctrl+L", "default": "Ctrl+L", "global": false, "fixed": false, "customized": false}, {"id": "sidebar.toggle", "group": "view", "binding": "Ctrl+B", "default": "Ctrl+B", "global": false, "fixed": false, "customized": false}, {"id": "preview.toggle", "group": "view", "binding": "Ctrl+Shift+B", "default": "Ctrl+Shift+B", "global": false, "fixed": false, "customized": false}, {"id": "font.increase", "group": "view", "binding": "Ctrl+=", "default": "Ctrl+=", "global": false, "fixed": false, "customized": false}, {"id": "font.decrease", "group": "view", "binding": "Ctrl+-", "default": "Ctrl+-", "global": false, "fixed": false, "customized": false}, {"id": "font.reset", "group": "view", "binding": "Ctrl+0", "default": "Ctrl+0", "global": false, "fixed": false, "customized": false}, {"id": "settings.open", "group": "panel", "binding": "Ctrl+,", "default": "Ctrl+,", "global": false, "fixed": false, "customized": false}, {"id": "skills.open", "group": "panel", "binding": "Ctrl+Shift+S", "default": "Ctrl+Shift+S", "global": false, "fixed": false, "customized": false}, {"id": "memory.open", "group": "panel", "binding": "Ctrl+Shift+M", "default": "Ctrl+Shift+M", "global": false, "fixed": false, "customized": false}, {"id": "schedules.open", "group": "panel", "binding": "Ctrl+Shift+T", "default": "Ctrl+Shift+T", "global": false, "fixed": false, "customized": false}, {"id": "usage.open", "group": "panel", "binding": "Ctrl+Shift+U", "default": "Ctrl+Shift+U", "global": false, "fixed": false, "customized": false}, {"id": "window.fullscreen", "group": "window", "binding": "F11", "default": "F11", "global": false, "fixed": false, "customized": false}, {"id": "window.toggle", "group": "window", "binding": "Ctrl+Alt+Space", "default": "Ctrl+Alt+Space", "global": true, "fixed": false, "customized": false}, {"id": "selection.translate", "group": "window", "binding": "Ctrl+Alt+T", "default": "Ctrl+Alt+T", "global": true, "fixed": false, "customized": false}]

  const approvals: { id: string; tool: string; shell: string; prefix: string; scope: string; display: string; approvedAt: string; lastUsedAt: string; uses: number }[] = [
    { id: 'r1', tool: 'run_shell', shell: 'powershell', prefix: 'npm run', scope: 'D:\\工作区\\日报', display: 'npm run *', approvedAt: now(), lastUsedAt: now(), uses: 6 },
    { id: 'r2', tool: 'run_shell', shell: '*', prefix: 'git status', scope: '*', display: 'git status *', approvedAt: now(), lastUsedAt: now(), uses: 23 },
  ]

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
      modelId: null,
      workspace: defaultWorkspace,
      permission: 'workspace',
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

  async function reply(c: Conversation, text: string, attachments: AttachmentRef[], messageId?: string, addUser = true) {
    const id = c.id
    stopped.delete(id)
    const list = messages.get(id)!
    if (addUser) list.push({ id: messageId ?? uid(), role: 'user', content: text, attachments, createdAt: now() })

    let finalText: string
    if (/图表|chart|趋势|占比/i.test(text)) {
      await sleep(400)
      finalText = [
        '九月三个车间的产量对比如下：',
        '',
        '```chart',
        JSON.stringify({
          type: 'bar',
          title: '九月各车间产量（件）',
          categories: ['一车间', '二车间', '三车间'],
          series: [
            { name: '计划', data: [12000, 9500, 7800] },
            { name: '实际', data: [12480, 9120, 8010] },
          ],
        }),
        '```',
        '',
        '三个车间的产量占比：',
        '',
        '```chart',
        JSON.stringify({
          type: 'donut',
          title: '产量占比',
          categories: ['一车间', '二车间', '三车间'],
          series: [{ data: [12480, 9120, 8010] }],
        }),
        '```',
        '',
        '近七天的趋势：',
        '',
        '```chart',
        JSON.stringify({
          type: 'line',
          categories: ['9-24', '9-25', '9-26', '9-27', '9-28', '9-29', '9-30'],
          series: [{ name: '日产量', data: [980, 1020, 960, 1110, 1075, 1130, 1180] }],
          yLabel: '件',
        }),
        '```',
        '',
        '整个汇总流程是这样走的：',
        '',
        '```mermaid',
        'flowchart TD',
        '  A[共享盘日报] --> B{格式对吗}',
        '  B -- 对 --> C[提取产量]',
        '  B -- 不对 --> D[标记异常]',
        '  C --> E[按车间汇总]',
        '  D --> E',
        '  E --> F[(生成 Excel)]',
        '```',
        '',
        '涉及的几台机器：',
        '',
        '```dot',
        'digraph G { rankdir=LR; node [shape=box, style=rounded];',
        '  "本机" -> "fs01 共享盘" [label="读日报"];',
        '  "本机" -> "Flyknit 服务端" [label="模型调用"];',
        '  "Flyknit 服务端" -> "Qwen3.5-397B"; }',
        '```',
      ].join('\n')
      await stream(id, finalText)
      const msg: UiMessage = {
        id: uid(), role: 'assistant', content: finalText, createdAt: now(),
        modelName: 'Qwen3.5-397B', promptTokens: 1840, completionTokens: 620,
      }
      list.push(msg)
      emit({ type: 'chat.message', conversationId: id, message: msg })
      emit({ type: 'chat.done', conversationId: id, stopReason: 'Completed', modelName: 'Qwen3.5-397B' })
      return
    }
    if (c.mode === 'translate') {
      await sleep(300)
      finalText =
        c.translateTo === 'zh-CN'
          ? '请各班组长在周五前提交下周的排班表。'
          : 'Đề nghị các tổ trưởng nộp bảng phân ca tuần sau trước thứ Sáu.'
      await stream(id, finalText)
    } else if (c.mode === 'agent' && /^(使用连接器|Use the|Dùng)/.test(text)) {
      // 模拟一次 MCP 调用：看卡片上的厂商图标、参数和返回结果
      const lead = '好的，我在腾讯文档里新建这张表格。'
      await stream(id, lead)
      const call = uid()
      const args = JSON.stringify({ title: '新员工名单', columns: ['姓名', '部门', '入职日期'], rows: 3 })
      list.push({ id: uid(), role: 'assistant', content: lead, toolCalls: [{ id: call, name: 'mcp__tencent-docs__create_sheet', arguments: args }], createdAt: now() })
      emit({ type: 'chat.message', conversationId: id, message: list[list.length - 1] })
      emit({ type: 'tool.started', conversationId: id, callId: call, name: 'mcp__tencent-docs__create_sheet', summary: 'title：新员工名单，columns：[3 项]，rows：3', args, risk: 'auto' })
      await sleep(900)
      const output = '已创建表格「新员工名单」\nhttps://docs.qq.com/sheet/DEMO123\n写入 3 行示例数据'
      emit({ type: 'tool.finished', conversationId: id, callId: call, ok: true, output, decision: 'auto' })
      list.push({ id: uid(), role: 'tool', content: output, toolCallId: call, createdAt: now() })
      finalText = '表格建好了：[新员工名单](https://docs.qq.com/sheet/DEMO123)，包含姓名、部门、入职日期三列和 3 行示例数据。'
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
      const skillCall = uid()
      emit({ type: 'tool.started', conversationId: id, callId: skillCall, name: 'load_skill', summary: 'excel-report', risk: 'auto' })
      await sleep(500)
      emit({ type: 'tool.finished', conversationId: id, callId: skillCall, ok: true, output: '# 技能：excel-report\n按公司模板生成周报…', decision: 'auto' })
      const lead = '我先看一下 D:\\日报 里有哪些文件。'
      await stream(id, lead)
      const call1 = uid()
      const call2 = uid()
      list.push({
        id: uid(),
        role: 'assistant',
        content: lead,
        toolCalls: [
          { id: skillCall, name: 'load_skill', arguments: '{"name":"excel-report"}' },
          { id: call1, name: 'list_dir', arguments: '{"path":"D:\\\\日报"}' },
        ],
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
      const command = 'Get-ChildItem D:\\日报\\*.xlsx | ForEach-Object { … Move-Item … }'
      const remembered = approvals.find((a) => command.toLowerCase().startsWith(a.prefix))
      const choice = c.permission === 'full' || remembered
        ? 'allowOnce'
        : c.permission === 'readonly'
          ? 'blocked'
          : await new Promise<string>((resolve) => {
              confirmWaiters.set(requestId, (choice) => {
                if (choice === 'allowAlways')
                  approvals.push({ id: uid(), tool: 'run_shell', shell: 'powershell', prefix: 'get-childitem', scope: c.workspace ?? '*', display: 'Get-ChildItem *', approvedAt: now(), lastUsedAt: now(), uses: 0 })
                resolve(choice)
              })
              emit({
                type: 'tool.confirm',
                conversationId: id,
                requestId,
                callId: call2,
                reason: 'Move-Item 会改动或丢失已有内容',
                rationale: lead2,
                rememberable: false,
                effect: 'destructive',
                ruleDisplay: '',
              })
            })
      if (remembered) remembered.uses++
      if (choice === 'blocked') {
        emit({ type: 'tool.finished', conversationId: id, callId: call2, ok: false, output: '已被安全策略阻止：当前是“仅可查看”权限，只能执行查询类命令。请不要尝试绕过', decision: 'blocked' })
        finalText = '当前是“仅可查看”权限，我不能移动文件。需要整理时请把权限切换为“工作区内修改”。'
      } else if (choice === 'reject') {
        emit({ type: 'tool.finished', conversationId: id, callId: call2, ok: false, output: '用户拒绝了这个操作', decision: 'rejected' })
        finalText = '好的，我没有移动任何文件。你希望按什么方式整理？'
      } else {
        await sleep(700)
        emit({ type: 'tool.finished', conversationId: id, callId: call2, ok: true, output: '退出码：0\n已移动 34 个文件', decision: remembered ? 'remembered' : c.permission === 'full' ? 'auto' : 'approved' })
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
    const model = c.modelId === 2 ? 'qwen3.8-max' : 'Qwen3.5-397B'
    const assistant: UiMessage = {
      id: uid(), role: 'assistant', content: finalText, createdAt: now(),
      modelName: model, promptTokens: 2860 + text.length * 2, completionTokens: 180 + finalText.length,
    }
    // 和宿主一致：回答先推给界面（那会儿链路还没走完），整轮结束后才补发链路
    const traceJson = JSON.stringify({"id": "a1b2c3", "startedAt": "2026-10-06T13:05:00+08:00", "durationMs": 14820, "stopReason": "Completed", "steps": 6, "modelCalls": 3, "toolCalls": 2, "errors": 1, "promptTokens": 9840, "completionTokens": 612, "slowest": "run_shell", "items": [{"index": 1, "kind": "model", "name": "Qwen3.5-397B", "summary": "决定调用 list_dir", "status": "ok", "startedAt": "", "durationMs": 1840, "promptTokens": 2860, "completionTokens": 96}, {"index": 2, "kind": "tool", "name": "list_dir", "summary": "查看目录 D:\\日报", "status": "ok", "startedAt": "", "durationMs": 62, "promptTokens": 0, "completionTokens": 0}, {"index": 3, "kind": "model", "name": "Qwen3.5-397B", "summary": "内容审核拦截，省略后重试", "status": "blocked", "startedAt": "", "durationMs": 2210, "promptTokens": 0, "completionTokens": 0}, {"index": 4, "kind": "compact", "name": "context", "summary": "压缩 18 条，41200 → 12800 tokens", "status": "ok", "startedAt": "", "durationMs": 2960, "promptTokens": 0, "completionTokens": 0}, {"index": 5, "kind": "tool", "name": "run_shell", "summary": "执行命令 Get-ChildItem … Move-Item …", "status": "ok", "startedAt": "", "durationMs": 6140, "promptTokens": 0, "completionTokens": 0}, {"index": 6, "kind": "model", "name": "Qwen3.5-397B", "summary": "给出回答", "status": "ok", "startedAt": "", "durationMs": 1608, "promptTokens": 6980, "completionTokens": 516}]})
    list.push({ ...assistant, trace: traceJson })
    emit({ type: 'chat.message', conversationId: id, message: assistant })
    emit({ type: 'chat.done', conversationId: id, stopReason: 'Completed', modelName: model })
    emit({ type: 'chat.trace', conversationId: id, messageId: assistant.id, trace: traceJson })
    if (c.mode === 'agent') {
      setTimeout(() => emit({ type: 'memory.learned', conversationId: id, items: [{ kind: 'preference', text: '日报按月份整理' }, { kind: 'success', text: '先列目录再批量移动' }], skill: null }), 1500)
    }
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
          micAvailable: true,
          sandboxed: true,
          notificationSound: 'none',
          fontScale: 1,
          autoStart: false,
          proxyMode: 'system',
          proxyUrl: '',
          proxyUser: '',
          dataDir: 'C:\\Users\\yangxiaowei\\AppData\\Roaming\\Flyknit',
          shortcuts: mockShortcuts,
          connected: true,
          serverMessage: '',
          modelName: 'Qwen3.5-397B',
          defaultModelId: null,
          defaultWorkspace,
          defaultPermission: 'workspace',
          workspaces,
          learning: true,
          maxSteps: 100,
          notifications: true,
          maximized,
        }
      case 'schedules.list':
        return schedules
      case 'schedules.save': {
        const existing = schedules.find((x: any) => x.id === p.id)
        const task = existing ?? { id: uid(), lastRunAt: null, lastStatus: '', lastSummary: '', lastConversationId: null, runCount: 0 }
        Object.assign(task, p, { nextRunAt: new Date(Date.now() + 3600000).toISOString() })
        if (!existing) schedules.push(task)
        return task
      }
      case 'schedules.setEnabled': {
        const task = schedules.find((x: any) => x.id === p.id)
        if (task) task.enabled = p.enabled
        return task ?? null
      }
      case 'schedules.delete':
        schedules = schedules.filter((x: any) => x.id !== p.id)
        return
      case 'schedules.run': {
        const task = schedules.find((x: any) => x.id === p.id)
        if (task) {
          task.lastStatus = 'running'
          task.runCount++
        }
        return { ok: true, message: '', conversationId: null }
      }
      case 'files.preview': {
        await sleep(250)
        const base = { path: p.path, name: String(p.path).split('\\').pop(), sections: [], text: null, language: null, dataUrl: null, notice: null, error: null }
        return { ...base, ...(mockPreviews[p.path] ?? { kind: 'none', error: '这种格式不能在这里预览' }) }
      }
      case 'files.launch':
      case 'files.reveal':
        return { ok: true, message: '' }
      case 'ui.activeConversation':
        return null
      case 'shortcuts.list':
        return mockShortcuts
      case 'shortcuts.set': {
        const hit = mockShortcuts.find((x) => x.id === p.id)
        if (hit) { hit.binding = String(p.binding); hit.customized = true }
        return { ok: true, reason: '', shortcuts: mockShortcuts }
      }
      // 语音输入：开发预览里用定时器伪造响度和时长，走的事件和真宿主完全一样
      case 'security.settings':
        return mockSecurity
      case 'security.setItem': {
        const item = mockSecurity.find((i) => i.key === p.key)
        if (!item) return { ok: false, message: '没有这一项设置', items: mockSecurity }
        if (item.locked) return { ok: false, message: '这一项由 IT 统一配置，本机不能修改', items: mockSecurity }
        const text = (v: unknown) => (typeof v === 'number' ? String(v) : v ? '开启' : '关闭')
        securityEvents.unshift({ id: Date.now(), conversationId: '', title: '', scene: '', tool: 'security_settings', detail: `「${item.title}」：${text(item.value)} → ${text(p.value)}`, decision: 'changed', reason: '', createdAt: ago(0) })
        item.value = p.value
        return { ok: true, message: '', items: mockSecurity }
      }
      case 'security.openBackups':
        return
      case 'speech.start':
        speechElapsed = 0
        speechTimer = setInterval(() => {
          speechElapsed += 100
          emit({
            type: 'speech.tick',
            level: 0.25 + 0.55 * Math.abs(Math.sin(speechElapsed / 420)),
            elapsedMs: speechElapsed,
            maxMs: 180000,
          })
        }, 100) as unknown as number
        return { ok: true }
      case 'speech.stop': {
        stopSpeechTimer()
        await sleep(700) // 装作在等服务端转写
        if (speechElapsed < 400) return { ok: false, text: '', reason: 'tooShort', message: '' }
        return { ok: true, text: '把九月的日报按月份整理到 D 盘', reason: '', message: '' }
      }
      case 'speech.cancel':
        stopSpeechTimer()
        return
      case 'shortcuts.reset': {
        for (const c of mockShortcuts) {
          if (!p.id || c.id === p.id) { c.binding = c.default; c.customized = false }
        }
        return mockShortcuts
      }
      case 'settings.setFontScale':
        return p.scale
      case 'settings.setNotificationSound':
        return { ok: true, message: '', sound: p.sound }
      case 'settings.previewSound':
        return
      case 'settings.setNotifications':
        return { ok: true, message: '', enabled: !!p.enabled }
      case 'settings.setAutoStart':
        return { ok: true, message: '', enabled: !!p.enabled }
      case 'settings.setProxy':
        return { ok: true, message: '' }
      case 'settings.testProxy':
        await sleep(800)
        return { ok: true, message: '' }
      case 'storage.openDataFolder':
        return null
      case 'storage.info':
        await sleep(400)
        return {
          dataDir: 'C:\\Users\\yangxiaowei\\AppData\\Roaming\\Flyknit',
          bytes: 740_100_000, files: 18240,
          diskTotal: 512_000_000_000, diskUsed: 349_000_000_000, diskFree: 163_000_000_000,
          workspace: 'C:\\Users\\yangxiaowei\\Documents\\Flyknit',
        }
      case 'usage.stats':
        await sleep(300)
        return {
          day: now().slice(0, 10),
          todayTokens: 184300,
          dailyLimit: 200000,
          remaining: 15700,
          exceeded: false,
          byScene: [
            { scene: 'agent', prompt: 52100, completion: 6800, total: 58900, requests: 14 },
            { scene: 'chat', prompt: 14200, completion: 3900, total: 18100, requests: 9 },
            { scene: 'translate', prompt: 7600, completion: 1820, total: 9420, requests: 21 },
          ],
          byDay: [
            { day: '2026-09-29', tokens: 42000 }, { day: '2026-09-30', tokens: 128000 },
            { day: '2026-10-01', tokens: 96000 }, { day: '2026-10-02', tokens: 151000 },
            { day: '2026-10-03', tokens: 23000 }, { day: '2026-10-04', tokens: 61000 },
            { day: '2026-10-05', tokens: 86420 },
          ],
          contactName: 'IT 管理员',
          contactEmail: 'jamesyang@shenzhougroup.com',
          contactPhone: '7815',
        }
      case 'security.list': {
        const all = securityEvents
        return p.decision ? all.filter((x) => x.decision === p.decision) : all
      }
      case 'security.clear':
        securityEvents.length = 0
        return
      case 'security.export':
        return { ok: true, cancelled: false, message: '', path: 'C:\\Users\\nguyen\\Documents\\flyknit-security-20261006-2200.csv', count: p.ids?.length ?? securityEvents.length }
      case 'security.network':
        return { enabled: true, domains: ['localhost', 'shenzhougroup.com', 'github.com'], allowPrivate: true }
      case 'window.startResize':
        return
      case 'window.toggleMaximize':
        maximized = !maximized
        return maximized
      case 'library.list': {
        const pool = p.tab === 'trash' ? libTrash : library
        let items = pool.filter((i) => p.hidden || !i.hidden || p.tab === 'trash')
        if (p.folderId) items = items.filter((i) => i.folderId === p.folderId)
        if (p.tab === 'favorites') items = items.filter((i) => i.favorite)
        if (p.tab === 'images') items = items.filter((i) => i.kind === 'image')
        if (p.kind) items = items.filter((i) => i.kind === p.kind)
        if (p.search) items = items.filter((i) => i.name.toLowerCase().includes(String(p.search).toLowerCase()))
        if (p.sort === 'name') items = [...items].sort((a, b) => a.name.localeCompare(b.name))
        else if (p.sort === 'size') items = [...items].sort((a, b) => b.size - a.size)
        else items = [...items].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
        for (const f of libFolders) f.count = library.filter((i) => i.folderId === f.id).length
        return { items, folders: libFolders }
      }
      case 'library.upload':
      case 'library.addDropped':
        return []
      case 'library.addNote': {
        const item = libItem(uid(), (p.title || '新备注') + '.md', 'note', 'note', 0, { folderId: p.folderId ?? null })
        library.unshift(item)
        return item
      }
      case 'library.updateNote':
        return true
      case 'library.rename': {
        const item = library.find((i) => i.id === p.id)
        if (item) item.name = p.name
        return !!item
      }
      case 'library.favorite':
        for (const i of library) if (p.ids.includes(i.id)) i.favorite = p.favorite
        return p.ids.length
      case 'library.move':
        for (const i of library) if (p.ids.includes(i.id)) i.folderId = p.folderId ?? null
        return p.ids.length
      case 'library.delete':
        for (const id of p.ids) {
          const i = library.findIndex((x) => x.id === id)
          if (i >= 0) libTrash.push({ ...library.splice(i, 1)[0], deletedAt: now() })
        }
        return p.ids.length
      case 'library.restore':
        for (const id of p.ids) {
          const i = libTrash.findIndex((x) => x.id === id)
          if (i >= 0) library.push({ ...libTrash.splice(i, 1)[0], deletedAt: null })
        }
        return p.ids.length
      case 'library.purge':
        for (const id of p.ids) {
          const i = libTrash.findIndex((x) => x.id === id)
          if (i >= 0) libTrash.splice(i, 1)
        }
        return p.ids.length
      case 'library.emptyTrash': {
        const n = libTrash.length
        libTrash.length = 0
        return n
      }
      case 'library.createFolder': {
        const f = { id: uid(), name: p.name, count: 0, createdAt: now(), updatedAt: now() }
        libFolders.push(f)
        return f
      }
      case 'library.renameFolder': {
        const f = libFolders.find((x) => x.id === p.id)
        if (f) f.name = p.name
        return !!f
      }
      case 'library.deleteFolder': {
        const i = libFolders.findIndex((x) => x.id === p.id)
        if (i >= 0) libFolders.splice(i, 1)
        for (const it of library) if (it.folderId === p.id) it.folderId = null
        return i >= 0
      }
      case 'library.download':
      case 'library.share':
        return p.ids.length
      case 'library.reveal':
        return { ok: true, message: '' }
      case 'library.preview': {
        const item = library.find((i) => i.id === p.id)
        const text = '# ' + (item?.name ?? '') + '\n\n下面是演示内容：\n\n- engine.py 76-84\n- build_ast → chunk → metadata\n\n```python\nast = build_ast(body)\nchunks = chunk_document(doc_id, body, ast=ast)\n```\n'
        return { path: item?.path ?? '', name: item?.name ?? '', kind: item?.kind === 'note' ? 'markdown' : 'text', text, sections: [] }
      }
      case 'library.attachments':
        return library.filter((i) => p.ids.includes(i.id)).map((i) => ({ fileName: i.name, localPath: i.path, mime: i.mime, size: i.size }))
      case 'window.setBackground':
        return
      case 'settings.setMaxSteps':
        return p.value
      case 'memory.list':
        return { ...memory, learning: true }
      case 'memory.pin': {
        const item = memory.items.find((i) => i.id === p.id) as { pinned?: boolean } | undefined
        if (item) item.pinned = !!p.pinned
        return !!item
      }
      case 'memory.delete':
        memory.items = memory.items.filter((i) => i.id !== p.id)
        return
      case 'memory.renew': {
        const item = memory.items.find((i) => i.id === p.id) as { expired?: boolean; validUntil?: string } | undefined
        if (item) {
          item.expired = false
          item.validUntil = new Date(Date.now() + 90 * 864e5).toISOString().slice(0, 10)
        }
        return !!item
      }
      case 'memory.add':
        if (memory.items.some((i) => i.text === p.text)) return { outcome: 'reinforced', reason: null }
        memory.items.push({ id: uid(), kind: p.kind, text: p.text, date: now().slice(0, 10), proofCount: 1, lastSeen: now().slice(0, 10), history: [] })
        return { outcome: 'added', reason: null }
      case 'memory.consolidate':
        return { groups: 0, merged: 0, errors: [] }
      case 'episodes.delete':
        memory.episodes = memory.episodes.filter((e) => e.id !== p.id)
        return
      case 'skills.deleteLearned':
        memory.skills = memory.skills.filter((k) => k.name !== p.name)
        return
      case 'skills.setLearnedStatus': {
        const k = memory.skills.find((x) => x.name === p.name)
        if (k) k.status = p.status
        return !!k
      }
      case 'memory.metrics':
        return {
          days: 30, active: memory.items.length, pinned: 1, inferred: 2,
          answers: 42, answersWithMemory: 31, avgItems: 4.2, avgTokens: 310, maxTokens: 820,
          usedRecently: 3, usedShare: 0.6, neverUsed: 1, liked: 6, disliked: 1,
          fresh30: 4, fresh90: 1, stale: 1, medianAgeDays: 21,
          episodes: memory.episodes.length, episodesReused: 1, episodesRecent: 2,
          skillsActive: 1, skillsCandidate: 1, skillsRetired: 1,
          expired: memory.items.filter((i) => (i as { expired?: boolean }).expired).length, semantic: true,
        }
      case 'conversations.plan':
        return []
      case 'workspaces.list':
        return workspaces
      case 'workspaces.add': {
        const path = p.path ?? 'E:\\生产报表'
        if (!workspaces.some((w) => w.path === path)) workspaces.push({ path, name: path.split('\\').pop()!, exists: true, isDefault: false })
        return path
      }
      case 'workspaces.remove':
        workspaces.splice(workspaces.findIndex((w) => w.path === p.path && !w.isDefault) >>> 0, 1)
        return workspaces
      case 'settings.setDefaultWorkspace':
        defaultWorkspace = p.path
        return
      case 'conversation.setWorkspace':
        conversations.get(p.id)!.workspace = p.path
        return
      case 'conversation.setPermission':
        conversations.get(p.id)!.permission = p.permission
        return
      case 'approvals.list':
        return approvals
      case 'approvals.revoke':
        approvals.splice(approvals.findIndex((a) => a.id === p.id) >>> 0, 1)
        return
      case 'approvals.clear':
        approvals.length = 0
        return
      case 'message.feedback':
        for (const list of messages.values()) for (const m of list) if (m.id === p.id) m.feedback = p.value
        return
      case 'chat.regenerate': {
        const list = messages.get(p.conversationId)!
        let i = list.length - 1
        while (i >= 0 && list[i].role !== 'user') i--
        const user = list[i]
        list.splice(i + 1)
        void reply(conversations.get(p.conversationId)!, user.content, user.attachments ?? [], user.id, false)
        return
      }
      case 'chat.edit': {
        const list = messages.get(p.conversationId)!
        const i = list.findIndex((m) => m.id === p.messageId)
        const attachments = list[i]?.attachments ?? []
        if (i >= 0) list.splice(i)
        void reply(conversations.get(p.conversationId)!, p.text, attachments, p.newMessageId)
        return
      }
      case 'models.list':
        return [
          { id: 1, name: 'Qwen3.5-397B', model: 'qwen3.5-397b', provider: '内部模型', supportsTools: true, supportsVision: false },
          { id: 2, name: 'qwen3.8-max', model: 'qwen3.8-max', provider: '阿里云百炼', supportsTools: true, supportsVision: false },
          { id: 3, name: 'qwen-plus', model: 'qwen-plus', provider: '阿里云百炼', supportsTools: true, supportsVision: false },
          { id: 4, name: 'qwen-vl-max', model: 'qwen-vl-max', provider: '阿里云百炼', supportsTools: true, supportsVision: true },
        ]
      case 'skills.list':
        return skills
      case 'skills.setEnabled': {
        const s = skills.find((x) => x.name === p.name)!
        if (s.required && !p.enabled) return { ok: false, message: '这是企业要求安装的技能，不能停用', skills }
        s.enabled = p.enabled
        return { ok: true, message: '', skills }
      }
      case 'skills.uninstall': {
        const s = skills.find((x) => x.name === p.name)!
        if (s.organization) return { ok: false, message: '企业下发的技能不能卸载', skills }
        skills = skills.filter((x) => x.name !== p.name)
        return { ok: true, message: `已卸载 ${p.name}`, skills }
      }
      case 'skills.install':
      case 'skills.installFolder':
      case 'skills.installFromUrl':
      case 'skills.installFromLibrary': {
        await sleep(900)
        const name = p.name ?? 'pdf-toolkit'
        if (!skills.some((x) => x.name === name)) {
          skills = [...skills, makeSkill(name, 'PDF 合并、拆分、提取文字和表格', { version: '2.0.1', origin: p.url ?? '公司技能库', scripts: ['scripts/merge.py'] })]
          const lib = libraryItems.find((x) => x.name === name)
          if (lib) lib.installed = true
        }
        return { ok: true, installed: [name], messages: [`已安装技能 ${name}`], warnings: ['包含 1 个脚本，运行时仍会按权限逐条确认'], skills }
      }
      case 'mcp.list':
        await sleep(200)
        return mcpVendors
      case 'mcp.connect': {
        const v = mcpVendors.find((x) => x.id === p.id)!
        const filled = Object.values((p.values ?? {}) as Record<string, string>).some((x) => x)
        if (v.fields.some((f) => f.required && !f.hasValue) && !filled) {
          return { ok: false, message: '请先填写：API Key', vendor: v }
        }
        v.status = v.auth === 'oauth' ? 'authorizing' : 'connecting'
        emit({ type: 'mcp.changed', id: v.id, vendor: { ...v } })
        await sleep(v.auth === 'oauth' ? 2500 : 900)
        for (const f of v.fields) if (filled) f.hasValue = true
        Object.assign(v, {
          status: 'connected', enabled: true, error: '', needsInput: [], connectedAt: now(), transportUsed: v.transport,
          tools: [
            { name: 'create_sheet', title: '新建在线表格', description: '新建一个在线表格并写入数据', readOnly: false },
            { name: 'search_docs', title: '搜索文档', description: '按标题和内容搜索文档', readOnly: true },
            { name: 'read_doc', title: '读取文档', description: '读取文档正文', readOnly: true },
          ],
        })
        return { ok: true, message: '', vendor: { ...v } }
      }
      case 'mcp.cancel':
        return
      case 'mcp.disconnect': {
        const v = mcpVendors.find((x) => x.id === p.id)!
        Object.assign(v, { status: 'disconnected', enabled: false, tools: [], connectedAt: null })
        if (p.forget) for (const f of v.fields) f.hasValue = false
        return { ...v }
      }
      case 'skills.library':
        await sleep(500)
        return libraryItems
      case 'conversation.setModel':
        conversations.get(p.id)!.modelId = p.modelId
        return
      case 'settings.setDefaultModel':
        return
      case 'update.state':
        return mockUpdate
      case 'update.apply':
        return { ok: true, message: '' }
      case 'update.check':
        // 浏览器里调试更新条：?update=ready 或 ?update=needsit
        return
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
        c.modelId = p.modelId ?? null
        c.workspace = p.workspace ?? defaultWorkspace
        c.permission = p.permission ?? 'workspace'
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
        // 开发预览：输入里带「压缩」就演示一次上下文压缩的进度条
        if (/压缩|compact/i.test(p.text ?? '')) void fakeCompaction(p.conversationId)
        if (/产出|交付|文件|ppt|报表/i.test(p.text ?? '')) {
          setTimeout(() => emit({ type: 'files.produced', conversationId: p.conversationId, files: mockOutputs }), 2600)
        }
        void reply(conversations.get(p.conversationId)!, p.text, p.attachments, p.messageId)
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
