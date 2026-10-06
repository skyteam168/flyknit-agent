<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { FormInstance, FormRules } from 'element-plus'
import { Lock, User } from '@element-plus/icons-vue'
import logoUrl from '@/assets/logo.png'
import { api } from '@/api'
import { setSession } from '@/store/auth'

const router = useRouter()
const route = useRoute()

const formRef = ref<FormInstance>()
const form = reactive({ username: '', password: '', remember: false })
const loading = ref(false)
const error = ref('')

const rules: FormRules = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }],
}

async function submit() {
  if (!(await formRef.value?.validate().catch(() => false))) return
  loading.value = true
  error.value = ''
  try {
    const r = await api.login(form.username.trim(), form.password)
    setSession(r.token, form.username.trim(), r.display_name, form.remember)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/dashboard'
    router.replace(redirect)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login">
    <section class="intro">
      <div class="intro-brand">
        <img :src="logoUrl" alt="" />
        <span>FlyknitBuddy</span>
      </div>
      <h1>让每台电脑上的 AI 助手<br />安全、可控、看得见</h1>
      <ul>
        <li><b>模型路由</b>对话、任务、翻译、语音分别走哪个模型，主备一键切换</li>
        <li><b>配额与用量</b>每台电脑每天能用多少 token，谁用了多少一目了然</li>
        <li><b>技能库与设备</b>统一下发技能包，异常设备随时停用</li>
        <li><b>审计留痕</b>被拦截的危险操作、谁看了谁的对话，全部可查</li>
      </ul>
      <div class="weave" aria-hidden="true" />
    </section>

    <section class="form-side">
      <div class="card">
        <img :src="logoUrl" alt="" class="card-logo" />
        <h2>登录管理后台</h2>
        <p class="sub">使用 IT 为你创建的管理员账号</p>

        <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="err" />

        <el-form ref="formRef" :model="form" :rules="rules" size="large" @submit.prevent="submit">
          <el-form-item prop="username">
            <el-input v-model="form.username" placeholder="用户名" :prefix-icon="User" autocomplete="username" autofocus />
          </el-form-item>
          <el-form-item prop="password">
            <el-input
              v-model="form.password"
              type="password"
              placeholder="密码"
              :prefix-icon="Lock"
              show-password
              autocomplete="current-password"
              @keyup.enter="submit"
            />
          </el-form-item>
          <div class="row">
            <el-checkbox v-model="form.remember">在这台电脑上保持登录</el-checkbox>
          </div>
          <el-button type="primary" size="large" class="submit" :loading="loading" @click="submit">登录</el-button>
        </el-form>

        <p class="foot">
          还没有账号？请 IT 在服务器上执行<br />
          <code>python -m scripts.setup_admin --add 用户名 --name "姓名"</code>
        </p>
      </div>
    </section>
  </div>
</template>

<style scoped>
.login {
  display: grid;
  grid-template-columns: minmax(420px, 1.1fr) 1fr;
  min-height: 100%;
  background: var(--cloth);
}
.intro {
  position: relative;
  display: flex;
  flex-direction: column;
  justify-content: center;
  padding: 56px 64px;
  overflow: hidden;
  background: linear-gradient(150deg, #2a3aad 0%, #3446c9 55%, #4b5bd6 100%);
  color: #fff;
}
.intro-brand {
  position: absolute;
  top: 32px;
  left: 40px;
  display: flex;
  gap: 10px;
  align-items: center;
  font-size: 17px;
  font-weight: 600;
}
.intro-brand img {
  width: 36px;
  height: 36px;
  padding: 3px;
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.92);
}
.intro h1 {
  position: relative;
  margin: 0 0 28px;
  font-size: 32px;
  font-weight: 600;
  line-height: 1.35;
  letter-spacing: -0.01em;
}
.intro ul {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 14px;
  margin: 0;
  padding: 0;
  list-style: none;
}
.intro li {
  display: flex;
  flex-direction: column;
  padding-left: 16px;
  border-left: 2px solid rgba(255, 255, 255, 0.35);
  color: rgba(255, 255, 255, 0.78);
  font-size: 13.5px;
}
.intro li b {
  color: #fff;
  font-size: 14.5px;
  font-weight: 500;
}
/* 针织纹理：两组斜线交叉，呼应飞织鞋面 */
.weave {
  position: absolute;
  inset: 0;
  opacity: 0.09;
  background-image:
    repeating-linear-gradient(45deg, #fff 0 1px, transparent 1px 14px),
    repeating-linear-gradient(-45deg, #fff 0 1px, transparent 1px 14px);
  mask-image: radial-gradient(ellipse at 80% 90%, #000 0%, transparent 70%);
}
.form-side {
  display: grid;
  place-items: center;
  padding: 40px 24px;
  background: var(--cloth-sunk);
}
.card {
  width: min(400px, 100%);
  padding: 36px 36px 28px;
  border: 1px solid var(--line);
  border-radius: 20px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.card-logo {
  width: 56px;
  height: 56px;
  object-fit: contain;
}
.card h2 {
  margin: 10px 0 2px;
  font-size: 22px;
  font-weight: 600;
}
.sub {
  margin: 0 0 22px;
  color: var(--ink-faint);
  font-size: 13px;
}
.err {
  margin-bottom: 16px;
}
.row {
  display: flex;
  justify-content: space-between;
  margin: -6px 0 16px;
}
.submit {
  width: 100%;
}
.foot {
  margin: 22px 0 0;
  color: var(--ink-faint);
  font-size: 12px;
  line-height: 1.7;
}
.foot code {
  display: inline-block;
  margin-top: 4px;
  padding: 2px 6px;
  border-radius: 4px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: 11.5px;
}
@media (max-width: 900px) {
  .login {
    grid-template-columns: 1fr;
  }
  .intro {
    display: none;
  }
}
</style>
