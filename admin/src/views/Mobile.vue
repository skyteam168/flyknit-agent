<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Iphone, Refresh, Upload } from '@element-plus/icons-vue'
import type { UploadFile, UploadRawFile } from 'element-plus'
import { api } from '@/api'
import type { MobileConfig, MobilePhone, MobileRelease, MobileWebSearchConfig } from '@/api/types'
import { bytes, dateTime, relative } from '@/utils/format'

const cfg = ref<MobileConfig | null>(null)
const searchCfg = ref<MobileWebSearchConfig | null>(null)
const phones = ref<MobilePhone[]>([])
const releases = ref<MobileRelease[]>([])
const loading = ref(false)
const form = reactive({ enabled: false, allow_approval: true, approval_timeout_min: 5 })
const searchForm = reactive({
  enabled: false,
  provider: 'bocha',
  count: 5,
  api_key: '',
})

async function load() {
  loading.value = true
  try {
    const [c, s, p, r] = await Promise.all([
      api.mobile(),
      api.mobileWebSearch(),
      api.mobilePhones(),
      api.mobileReleases(),
    ])
    cfg.value = c
    searchCfg.value = s
    phones.value = p
    releases.value = r
    Object.assign(form, { enabled: c.enabled, allow_approval: c.allow_approval, approval_timeout_min: c.approval_timeout_min })
    Object.assign(searchForm, {
      enabled: s.enabled,
      provider: s.provider || 'bocha',
      count: s.count || 5,
      api_key: '',
    })
  } finally {
    loading.value = false
  }
}
onMounted(load)

const saving = ref(false)
async function save() {
  if (form.enabled && !cfg.value?.enabled) {
    try {
      await ElMessageBox.confirm(
        '开启后，员工可以用手机扫自己电脑上的二维码绑定 FlyknitBuddy App：在手机上聊天、翻译，并让自己电脑上的 FlyknitBuddy 执行操作（读写文件、运行命令等，仍受安全中心策略约束）。确定开启吗？',
        '开启手机端',
        { type: 'warning', confirmButtonText: '开启', cancelButtonText: '取消' },
      )
    } catch {
      return
    }
  }
  saving.value = true
  try {
    cfg.value = await api.saveMobile({ ...form })
    ElMessage.success(form.enabled ? '已保存' : '已保存，手机端已关闭，已绑定的手机暂时都不能用')
  } finally {
    saving.value = false
  }
}

const savingSearch = ref(false)
async function saveSearch() {
  savingSearch.value = true
  try {
    const body: { enabled: boolean; provider: string; count: number; api_key?: string | null } = {
      enabled: searchForm.enabled,
      provider: searchForm.provider,
      count: searchForm.count,
    }
    if (searchForm.api_key.trim() !== '') {
      body.api_key = searchForm.api_key.trim()
    }
    searchCfg.value = await api.saveMobileWebSearch(body)
    searchForm.api_key = ''
    ElMessage.success(searchCfg.value.ready ? '联网搜索已保存并可用' : '已保存（还需开启并填写 API Key）')
  } finally {
    savingSearch.value = false
  }
}

async function clearSearchKey() {
  try {
    await ElMessageBox.confirm('清空联网搜索的 API Key？手机和电脑上将无法使用联网。', '清空密钥', {
      type: 'warning',
      confirmButtonText: '清空',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }
  searchCfg.value = await api.saveMobileWebSearch({
    enabled: searchForm.enabled,
    provider: searchForm.provider,
    count: searchForm.count,
    api_key: '',
  })
  ElMessage.success('已清空密钥')
}

// ---------- App 版本 ----------
const dialog = ref(false)
const file = ref<UploadRawFile | null>(null)
const versionName = ref('')
const versionCode = ref<number | undefined>(undefined)
const notes = ref('')
const progress = ref(-1)

function openUpload() {
  file.value = null
  const top = releases.value[0]
  versionCode.value = top ? top.version_code + 1 : 100
  versionName.value = ''
  notes.value = ''
  dialog.value = true
}

// 不自动上传时 el-upload 不会调 before-upload，选中的文件要从 on-change 里拿
function pick(picked: UploadFile) {
  if (picked.raw) file.value = picked.raw
}

async function submitUpload() {
  if (!file.value || !versionName.value.trim() || !versionCode.value) {
    ElMessage.warning('请选择 APK，并填写和 manifest.json 一致的版本名称、版本号')
    return
  }
  progress.value = 0
  try {
    await api.uploadMobileRelease(file.value as unknown as File, versionName.value.trim(), versionCode.value, notes.value,
                                  (p) => (progress.value = p))
    dialog.value = false
    ElMessage.success('已上传。确认无误后点「发布」，员工打开 App 时就会提示升级')
    await load()
  } finally {
    progress.value = -1
  }
}

async function setPublished(r: MobileRelease, published: boolean) {
  if (published) {
    try {
      await ElMessageBox.confirm(`发布 ${r.version_name}（版本号 ${r.version_code}）？员工打开 App 时会提示下载安装。`, '发布 App', {
        type: 'warning',
        confirmButtonText: '发布',
        cancelButtonText: '取消',
      })
    } catch {
      return
    }
  }
  await api.updateMobileRelease(r.id, { published })
  await load()
}

async function removeRelease(r: MobileRelease) {
  try {
    await ElMessageBox.confirm(`删除 ${r.version_name}？安装包文件也会删掉。`, '删除版本', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' })
  } catch {
    return
  }
  await api.deleteMobileRelease(r.id)
  await load()
}

const fullLink = (r: MobileRelease) => `${window.location.origin}${r.download_path}`

async function copyLink(r: MobileRelease) {
  await navigator.clipboard.writeText(fullLink(r))
  ElMessage.success('下载链接已复制')
}

async function revoke(p: MobilePhone) {
  try {
    await ElMessageBox.confirm(`吊销「${p.name}」（绑定在 ${p.machine_name}）？这部手机会立刻断开，需要重新扫码才能再用。`, '吊销手机', {
      type: 'warning',
      confirmButtonText: '吊销',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }
  await api.revokeMobilePhone(p.id)
  ElMessage.success('已吊销')
  await load()
}
</script>

<template>
  <div class="page" v-loading="loading && !cfg">
    <div class="page-head">
      <div>
        <h1>手机端</h1>
        <p>员工用 FlyknitBuddy 安卓 App 扫自己电脑上的二维码绑定：在手机上聊天、翻译；「办事」交给他电脑上的 FlyknitBuddy 执行，进度和结果实时回到手机。手机继承那台电脑的身份和额度，只在公司网络内可用。</p>
      </div>
      <el-button :icon="Refresh" @click="load">刷新</el-button>
    </div>

    <div class="layout">
      <section class="panel">
        <div class="panel-title">
          <h3><el-icon><Iphone /></el-icon> 开关与策略</h3>
          <template v-if="cfg">
            <el-tag v-if="!cfg.enabled" type="info" effect="plain">未开启</el-tag>
            <el-tag v-else type="success" effect="plain">已开启 · {{ cfg.connected }}/{{ cfg.phones }} 在线</el-tag>
          </template>
        </div>
        <el-form :model="form" label-width="140px">
          <el-form-item label="开启手机端">
            <el-switch v-model="form.enabled" />
            <p class="hint">关闭后已绑定的手机都不能用（绑定保留，重新开启后恢复）。</p>
          </el-form-item>
          <el-form-item label="允许在手机上确认">
            <el-switch v-model="form.allow_approval" />
            <p class="hint">电脑上执行到需要确认的操作（运行脚本、修改工作区外的文件等）时，推到发起任务的手机上由本人批准。关闭则一律拒绝，回到电脑上处理。被安全策略「阻止」的命令无论如何都不会执行。</p>
          </el-form-item>
          <el-form-item label="确认等待时间">
            <el-input-number v-model="form.approval_timeout_min" :min="1" :max="30" />
            <span class="unit">分钟，超时未答当作拒绝</span>
          </el-form-item>
          <el-form-item>
            <el-button type="primary" :loading="saving" @click="save">保存</el-button>
          </el-form-item>
        </el-form>
      </section>

      <section class="panel guide">
        <div class="panel-title"><h3>使用步骤</h3></div>
        <ol>
          <li>在左边开启手机端。</li>
          <li>员工手机安装 FlyknitBuddy 安卓 App，连上公司 Wi-Fi。</li>
          <li>员工在电脑上的 FlyknitBuddy「设置 → 手机」点「绑定手机」，显示二维码。</li>
          <li>手机 App 里扫码（PDA 可以直接用扫描键），绑定完成。</li>
          <li>手机丢了：在电脑上或下面的列表里吊销，令牌立刻作废。</li>
        </ol>
      </section>
    </div>

    <section class="panel">
      <div class="panel-title">
        <h3>联网搜索 <small>手机 / 电脑「对话」；流量从服务器出网</small></h3>
        <el-tag v-if="searchCfg?.ready" type="success" effect="plain">可用</el-tag>
        <el-tag v-else type="info" effect="plain">未就绪</el-tag>
      </div>
      <el-form :model="searchForm" label-width="140px" style="max-width: 640px">
        <el-form-item label="开启联网搜索">
          <el-switch v-model="searchForm.enabled" />
          <p class="hint">开启后，员工可在手机和电脑的「对话」里打开联网开关；翻译与办事不受影响。</p>
        </el-form-item>
        <el-form-item label="提供商">
          <el-select v-model="searchForm.provider" style="width: 260px">
            <el-option label="博查 Bocha（国内）" value="bocha" />
            <el-option label="Serper（国际）" value="serper" />
            <el-option label="Tavily（国际）" value="tavily" />
          </el-select>
          <p class="hint">服务器在国外访问不了博查时，改选 Serper 或 Tavily，并填对应 Key。</p>
        </el-form-item>
        <el-form-item label="API Key">
          <el-input
            v-model="searchForm.api_key"
            type="password"
            show-password
            placeholder="不改请留空"
            style="max-width: 360px"
          />
          <el-button v-if="searchCfg?.api_key_set" link type="danger" style="margin-left: 8px" @click="clearSearchKey">清空</el-button>
          <p class="hint" v-if="searchCfg?.api_key_set">当前：{{ searchCfg.api_key_masked }}</p>
        </el-form-item>
        <el-form-item label="结果条数">
          <el-input-number v-model="searchForm.count" :min="1" :max="10" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :loading="savingSearch" @click="saveSearch">保存联网搜索</el-button>
        </el-form-item>
      </el-form>
    </section>

    <section class="panel">
      <div class="panel-title">
        <h3>App 版本 <small>员工打开 App 时按版本号提示升级</small></h3>
        <el-button type="primary" :icon="Upload" @click="openUpload">上传 APK</el-button>
      </div>
      <el-table :data="releases" empty-text="还没有上传过 App">
        <el-table-column label="版本" min-width="120">
          <template #default="{ row }"><strong>{{ row.version_name }}</strong> <small class="muted">（{{ row.version_code }}）</small></template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag v-if="row.file_missing" type="danger" size="small">文件丢失</el-tag>
            <el-tag v-else-if="row.published" type="success" size="small">下发中</el-tag>
            <el-tag v-else type="info" size="small">未发布</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="更新说明" min-width="220" show-overflow-tooltip prop="notes" />
        <el-table-column label="大小" width="100">
          <template #default="{ row }">{{ bytes(row.size) }}</template>
        </el-table-column>
        <el-table-column label="上传" min-width="150">
          <template #default="{ row }">{{ dateTime(row.created_at) }} · {{ row.uploaded_by }}</template>
        </el-table-column>
        <el-table-column width="220" align="right">
          <template #default="{ row }">
            <el-button v-if="row.published" link @click="copyLink(row as MobileRelease)">复制下载链接</el-button>
            <el-button v-if="!row.published" link type="primary" :disabled="row.file_missing" @click="setPublished(row as MobileRelease, true)">发布</el-button>
            <el-button v-else link @click="setPublished(row as MobileRelease, false)">撤回</el-button>
            <el-button link type="danger" @click="removeRelease(row as MobileRelease)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
      <p class="hint">还没装 App 的员工：在电脑上的 FlyknitBuddy「设置 → 手机」里扫「下载 App」的二维码即可下载最新发布的版本。</p>
    </section>

    <section class="panel">
      <div class="panel-title"><h3>已绑定的手机 <small>{{ phones.length }} 部</small></h3></div>
      <el-table :data="phones" empty-text="还没有手机绑定">
        <el-table-column label="手机" prop="name" min-width="140" />
        <el-table-column label="电脑" prop="machine_name" min-width="130" />
        <el-table-column label="员工" prop="user_name" min-width="110" />
        <el-table-column label="App 版本" prop="app_version" width="100" />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.connected ? 'success' : 'info'" effect="plain" size="small">{{ row.connected ? '在线' : '离线' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="绑定时间" min-width="150">
          <template #default="{ row }">{{ dateTime(row.created_at) }}</template>
        </el-table-column>
        <el-table-column label="最近使用" min-width="120">
          <template #default="{ row }">{{ row.last_seen ? relative(row.last_seen) : '—' }}</template>
        </el-table-column>
        <el-table-column width="90" align="right">
          <template #default="{ row }"><el-button link type="danger" @click="revoke(row as MobilePhone)">吊销</el-button></template>
        </el-table-column>
      </el-table>
    </section>
    <el-dialog v-model="dialog" title="上传 App 安装包" width="520px">
      <el-form label-position="top">
        <el-form-item label="APK 文件">
          <el-upload drag :auto-upload="false" :show-file-list="false" accept=".apk" :on-change="pick">
            <div class="drop">{{ file ? file.name : '把 HBuilderX 云打包生成的 .apk 拖到这里，或点击选择' }}</div>
          </el-upload>
        </el-form-item>
        <el-form-item label="版本名称（manifest.json 里的「应用版本名称」）">
          <el-input v-model="versionName" placeholder="例如 0.1.1" />
        </el-form-item>
        <el-form-item label="版本号（manifest.json 里的「应用版本号」，每次打包都要加 1）">
          <el-input-number v-model="versionCode" :min="1" :step="1" />
        </el-form-item>
        <el-form-item label="更新说明">
          <el-input v-model="notes" type="textarea" :rows="3" placeholder="员工在 App 里看到的升级提示" />
        </el-form-item>
      </el-form>
      <el-progress v-if="progress >= 0" :percentage="progress" />
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="progress >= 0" @click="submitUpload">上传</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
  gap: 16px;
  align-items: start;
}
@media (max-width: 1100px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
.panel-title h3 {
  display: flex;
  gap: 6px;
  align-items: center;
}
.hint {
  margin-top: 4px;
}
.muted {
  color: var(--ink-soft);
}
.drop {
  padding: 18px 12px;
  color: var(--ink-soft);
}
.unit {
  margin-left: 8px;
  color: var(--ink-soft);
}
.guide ol {
  margin: 0;
  padding-left: 20px;
  line-height: 1.8;
}
</style>
