<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Plus, Refresh, UploadFilled } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { AgentJob, AgentJobDetail, SoftwarePackage } from '@/api/types'
import type { UploadFile } from 'element-plus'
import { auth } from '@/store/auth'
import { bytes, dateTime, relative } from '@/utils/format'
import { kindLabel, statusMeta } from '@/utils/tasks'

const tab = ref<'jobs' | 'packages'>('jobs')
const canDispatch = computed(() => !!auth.user?.can_dispatch && !auth.user?.must_change_password)

// ---------- 任务 ----------
const jobs = ref<AgentJob[]>([])
const jobsLoading = ref(false)
const offset = ref(0)
const limit = 30
const hasMore = ref(false)

async function loadJobs(reset = true) {
  jobsLoading.value = true
  try {
    if (reset) offset.value = 0
    const list = await api.jobs({ limit, offset: offset.value })
    jobs.value = reset ? list : [...jobs.value, ...list]
    hasMore.value = list.length === limit
  } finally {
    jobsLoading.value = false
  }
}

function progress(job: AgentJob): number {
  if (!job.total) return 0
  const done = (job.counts.succeeded ?? 0) + (job.counts.failed ?? 0) + (job.counts.cancelled ?? 0) + (job.counts.expired ?? 0)
  return Math.round((done / job.total) * 100)
}
function progressStatus(job: AgentJob): '' | 'success' | 'exception' | 'warning' {
  if (progress(job) < 100) return ''
  if (job.counts.failed) return 'exception'
  if (job.counts.cancelled || job.counts.expired) return 'warning'
  return 'success'
}

// ---------- 任务详情 ----------
const drawer = ref(false)
const detail = ref<AgentJobDetail | null>(null)
const detailLoading = ref(false)

async function openJob(id: number) {
  drawer.value = true
  detailLoading.value = true
  try {
    detail.value = await api.job(id)
  } finally {
    detailLoading.value = false
  }
}

async function cancel(job: AgentJob) {
  await ElMessageBox.confirm(
    '只会取消还没开始执行的电脑；已经在执行的（比如正在安装）不会中断。',
    `取消任务「${job.title}」`,
    { type: 'warning', confirmButtonText: '取消任务', cancelButtonText: '关闭' },
  )
  const updated = await api.cancelJob(job.id)
  Object.assign(job, updated)
  if (detail.value?.id === job.id) detail.value = await api.job(job.id)
  ElMessage.success('已取消待执行的任务')
}

const canCancel = (job: AgentJob) => canDispatch.value && (job.counts.pending ?? 0) > 0

// ---------- 安装包 ----------
const packages = ref<SoftwarePackage[]>([])
const pkgLoading = ref(false)

async function loadPackages() {
  pkgLoading.value = true
  try {
    packages.value = await api.packages()
  } finally {
    pkgLoading.value = false
  }
}

const uploadDialog = ref(false)
const uploading = ref(false)
const form = reactive({ name: '', version: '', silent_args: '', file: null as File | null })

function openUpload() {
  Object.assign(form, { name: '', version: '', silent_args: '', file: null })
  uploadDialog.value = true
}
function onFile(file: UploadFile) {
  if (!file.raw) return
  form.file = file.raw
  if (!form.name) form.name = file.raw.name.replace(/\.(msi|exe)$/i, '')
}
const isExe = computed(() => form.file?.name.toLowerCase().endsWith('.exe') ?? false)

async function upload() {
  if (!form.file) {
    ElMessage.warning('请选择 .msi 或 .exe 安装包')
    return
  }
  if (isExe.value && !form.silent_args.trim()) {
    ElMessage.warning('exe 安装包必须填写静默安装参数，否则会卡在安装界面')
    return
  }
  uploading.value = true
  try {
    await api.uploadPackage(form.file, { name: form.name.trim(), version: form.version.trim(), silent_args: form.silent_args.trim() })
    ElMessage.success('上传成功')
    uploadDialog.value = false
    await loadPackages()
  } finally {
    uploading.value = false
  }
}

async function removePackage(p: SoftwarePackage) {
  await ElMessageBox.confirm(`删除安装包「${p.name} ${p.version}」？已下发的安装任务不受影响。`, '删除安装包', {
    type: 'warning',
    confirmButtonText: '删除',
  })
  await api.deletePackage(p.id)
  packages.value = packages.value.filter((x) => x.id !== p.id)
  ElMessage.success('已删除')
}

onMounted(() => {
  loadJobs()
  loadPackages()
})
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>任务中心</h1>
        <p>查看每一次下发的运维任务进度，管理可下发的安装包。下发入口在「设备列表」里勾选电脑。</p>
      </div>
      <div class="toolbar">
        <el-button
          v-if="tab === 'packages' && canDispatch"
          type="primary"
          :icon="Plus"
          @click="openUpload"
        >上传安装包</el-button>
        <el-button :icon="Refresh" @click="tab === 'jobs' ? loadJobs() : loadPackages()" />
      </div>
    </div>

    <el-tabs v-model="tab" class="panel">
      <el-tab-pane label="下发记录" name="jobs">
        <el-table :data="jobs" v-loading="jobsLoading" empty-text="还没有下发过任务" @row-click="(row: AgentJob) => openJob(row.id)">
          <el-table-column label="任务" min-width="220">
            <template #default="{ row }">
              <strong class="link">{{ row.title }}</strong>
              <small class="by">{{ kindLabel(row.kind) }} · {{ row.created_by }}</small>
            </template>
          </el-table-column>
          <el-table-column label="进度" min-width="200">
            <template #default="{ row }">
              <el-progress :percentage="progress(row as AgentJob)" :status="progressStatus(row as AgentJob)" :stroke-width="10" />
              <small class="counts">
                共 {{ row.total }}
                <span v-if="row.counts.succeeded"> · 成功 {{ row.counts.succeeded }}</span>
                <span v-if="row.counts.failed" class="bad"> · 失败 {{ row.counts.failed }}</span>
                <span v-if="row.counts.running"> · 执行中 {{ row.counts.running }}</span>
                <span v-if="row.counts.pending"> · 待执行 {{ row.counts.pending }}</span>
                <span v-if="row.counts.cancelled"> · 取消 {{ row.counts.cancelled }}</span>
                <span v-if="row.counts.expired"> · 过期 {{ row.counts.expired }}</span>
              </small>
            </template>
          </el-table-column>
          <el-table-column label="下发时间" width="150">
            <template #default="{ row }">
              <el-tooltip :content="dateTime(row.created_at)" placement="top"><span>{{ relative(row.created_at) }}</span></el-tooltip>
            </template>
          </el-table-column>
          <el-table-column width="90" align="right">
            <template #default="{ row }">
              <el-button v-if="canCancel(row as AgentJob)" link type="warning" @click.stop="cancel(row as AgentJob)">取消</el-button>
            </template>
          </el-table-column>
        </el-table>
        <div v-if="hasMore" class="more">
          <el-button text @click="(offset += limit), loadJobs(false)">加载更多</el-button>
        </div>
      </el-tab-pane>

      <el-tab-pane label="安装包" name="packages">
        <el-table :data="packages" v-loading="pkgLoading" empty-text="还没有安装包，点右上角上传">
          <el-table-column label="软件" min-width="200">
            <template #default="{ row }">
              <strong>{{ row.name }}</strong>
              <small class="by">{{ row.filename }}</small>
            </template>
          </el-table-column>
          <el-table-column label="版本" prop="version" width="120" />
          <el-table-column label="类型" width="80">
            <template #default="{ row }"><el-tag size="small" effect="plain">{{ row.kind.toUpperCase() }}</el-tag></template>
          </el-table-column>
          <el-table-column label="大小" width="100" align="right">
            <template #default="{ row }">{{ bytes(row.size) }}</template>
          </el-table-column>
          <el-table-column label="静默参数" min-width="140">
            <template #default="{ row }"><code class="muted">{{ row.silent_args || (row.kind === 'msi' ? '/qn /norestart' : '—') }}</code></template>
          </el-table-column>
          <el-table-column label="上传" width="150">
            <template #default="{ row }"><small>{{ row.uploaded_by }} · {{ relative(row.created_at) }}</small></template>
          </el-table-column>
          <el-table-column width="70" align="right">
            <template #default="{ row }">
              <el-button v-if="canDispatch" link type="danger" :icon="Delete" @click="removePackage(row as SoftwarePackage)" />
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>

    <!-- 任务详情 -->
    <el-drawer v-model="drawer" :title="detail?.title" size="640px">
      <div v-if="detail" v-loading="detailLoading">
        <p class="drawer-sub">{{ kindLabel(detail.kind) }} · 由 {{ detail.created_by }} 于 {{ dateTime(detail.created_at) }} 下发<template v-if="detail.cancelled_by"> · 已由 {{ detail.cancelled_by }} 取消</template></p>
        <el-table :data="detail.runs" size="small">
          <el-table-column label="电脑" prop="machine_name" min-width="140" show-overflow-tooltip />
          <el-table-column label="状态" width="90">
            <template #default="{ row }"><el-tag :type="statusMeta(row.status).type" size="small" effect="plain">{{ statusMeta(row.status).label }}</el-tag></template>
          </el-table-column>
          <el-table-column label="退出码" width="70" align="center">
            <template #default="{ row }">{{ row.exit_code ?? '—' }}</template>
          </el-table-column>
          <el-table-column label="结果" min-width="200">
            <template #default="{ row }">
              <el-tooltip v-if="row.output" :content="row.output" placement="top" raw-content>
                <span class="output-peek">{{ row.output }}</span>
              </el-tooltip>
              <span v-else class="muted">—</span>
            </template>
          </el-table-column>
          <el-table-column label="完成" width="110">
            <template #default="{ row }"><small>{{ row.finished_at ? relative(row.finished_at) : '—' }}</small></template>
          </el-table-column>
        </el-table>
      </div>
    </el-drawer>

    <!-- 上传安装包 -->
    <el-dialog v-model="uploadDialog" title="上传安装包" width="480px" :close-on-click-modal="false">
      <el-upload
        drag
        :auto-upload="false"
        :show-file-list="false"
        accept=".msi,.exe"
        :on-change="onFile"
      >
        <el-icon class="el-icon--upload"><UploadFilled /></el-icon>
        <div class="el-upload__text">
          <template v-if="form.file">已选择：<strong>{{ form.file.name }}</strong></template>
          <template v-else>拖拽或点击选择 <em>.msi / .exe</em> 安装包</template>
        </div>
      </el-upload>
      <el-form label-position="top" style="margin-top: 16px">
        <el-form-item label="软件名称"><el-input v-model="form.name" placeholder="如 WPS Office" /></el-form-item>
        <el-form-item label="版本"><el-input v-model="form.version" placeholder="如 12.1" /></el-form-item>
        <el-form-item v-if="isExe" label="静默安装参数">
          <el-input v-model="form.silent_args" placeholder="如 /S、/silent、/quiet" />
          <p class="hint">exe 各家约定不同，必须填对应的静默参数，否则会卡在安装界面。MSI 固定用 /qn /norestart，无需填写。</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="uploadDialog = false">取消</el-button>
        <el-button type="primary" :loading="uploading" @click="upload">上传</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.link {
  font-weight: 500;
  color: var(--thread);
  cursor: pointer;
  display: block;
}
.by {
  display: block;
  color: var(--ink-faint);
  font-size: 12px;
}
.counts {
  display: block;
  color: var(--ink-faint);
  font-size: 12px;
  margin-top: 2px;
}
.counts .bad {
  color: var(--red);
}
.more {
  text-align: center;
  margin-top: 10px;
}
.drawer-sub {
  color: var(--ink-faint);
  font-size: 13px;
  margin: 0 0 16px;
}
.output-peek {
  display: inline-block;
  max-width: 100%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--ink-faint);
  font-size: 12px;
}
.hint {
  margin: 6px 0 0;
  color: var(--ink-faint);
  font-size: 12px;
  line-height: 1.5;
}
</style>
