<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { api } from '@/api'
import { auth, setSession } from '@/store/auth'

// forced：首次登录用初始密码进来，必须改掉才能继续，弹窗关不掉
const props = defineProps<{ forced?: boolean; initialPassword?: string }>()
const visible = defineModel<boolean>({ default: false })
const emit = defineEmits<{ done: [] }>()

const formRef = ref<FormInstance>()
const saving = ref(false)
const form = reactive({ old: '', next: '', confirm: '' })

const rules: FormRules = {
  old: [{ required: true, message: '请输入当前密码', trigger: 'blur' }],
  next: [
    { required: true, message: '请输入新密码', trigger: 'blur' },
    { min: 8, message: '至少 8 位', trigger: 'blur' },
    {
      validator: (_r, v, cb) => (v && v === form.old ? cb(new Error('不能和当前密码一样')) : cb()),
      trigger: 'blur',
    },
  ],
  confirm: [
    {
      validator: (_r, v, cb) => (v !== form.next ? cb(new Error('两次输入不一致')) : cb()),
      trigger: 'blur',
    },
  ],
}

function onOpen() {
  form.old = props.initialPassword ?? ''
  form.next = ''
  form.confirm = ''
}

async function submit() {
  if (!(await formRef.value?.validate().catch(() => false))) return
  saving.value = true
  try {
    await api.changePassword(form.old, form.next)
    // 改密会让所有会话失效（包括当前这个），用新密码重新换一个令牌
    const r = await api.login(auth.username, form.next)
    setSession(r.token, auth.username, r.display_name, !!localStorage.getItem('flyknit-admin-session'))
    ElMessage.success('密码已修改')
    visible.value = false
    emit('done')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="forced ? '请先修改初始密码' : '修改密码'"
    width="420px"
    :close-on-click-modal="!forced"
    :close-on-press-escape="!forced"
    :show-close="!forced"
    append-to-body
    @open="onOpen"
  >
    <p v-if="forced" class="hint" style="margin-bottom: 14px">
      这是 IT 建号时生成的初始密码。改成只有你自己知道的密码后才能继续——否则建号的人一直知道你的密码，操作记录就失去意义了。
    </p>
    <el-form ref="formRef" :model="form" :rules="rules" label-position="top" @submit.prevent="submit">
      <el-form-item label="当前密码" prop="old">
        <el-input v-model="form.old" type="password" show-password autocomplete="current-password" />
      </el-form-item>
      <el-form-item label="新密码" prop="next">
        <el-input v-model="form.next" type="password" show-password autocomplete="new-password" placeholder="至少 8 位" />
      </el-form-item>
      <el-form-item label="确认新密码" prop="confirm">
        <el-input v-model="form.confirm" type="password" show-password autocomplete="new-password" @keyup.enter="submit" />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button v-if="!forced" @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="saving" @click="submit">确认修改</el-button>
    </template>
  </el-dialog>
</template>
