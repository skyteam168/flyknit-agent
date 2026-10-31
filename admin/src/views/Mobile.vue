<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Iphone, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { MobileConfig, MobilePhone } from '@/api/types'
import { dateTime, relative } from '@/utils/format'

const cfg = ref<MobileConfig | null>(null)
const phones = ref<MobilePhone[]>([])
const loading = ref(false)
const form = reactive({ enabled: false, allow_approval: true, approval_timeout_min: 5 })

async function load() {
  loading.value = true
  try {
    const [c, p] = await Promise.all([api.mobile(), api.mobilePhones()])
    cfg.value = c
    phones.value = p
    Object.assign(form, { enabled: c.enabled, allow_approval: c.allow_approval, approval_timeout_min: c.approval_timeout_min })
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
