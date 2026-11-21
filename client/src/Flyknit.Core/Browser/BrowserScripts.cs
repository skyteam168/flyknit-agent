using System.Text.Json;

namespace Flyknit.Core.Browser;

/// <summary>
/// 在网页里执行的脚本。都是自包含的表达式，返回字符串，WebView2 的 ExecuteScriptAsync 直接能跑。
/// 读页面时给每个能操作的元素打上 data-fk-idx 编号，后面的点击、填写都按编号找元素。
/// </summary>
public static class BrowserScripts
{
    public const int MaxElements = 150;
    public const int MaxText = 6000;

    public static readonly string Snapshot = """
        (() => {
          const MAX = __MAX__, MAXTEXT = __MAXTEXT__;
          const sel = 'a[href],button,input:not([type=hidden]),select,textarea,[role=button],[role=link],[role=tab],[role=menuitem],[role=checkbox],[role=radio],[contenteditable=""],[contenteditable=true],summary';
          document.querySelectorAll('[data-fk-idx]').forEach(e => e.removeAttribute('data-fk-idx'));
          const out = [];
          let i = 0;
          for (const el of document.querySelectorAll(sel)) {
            const r = el.getBoundingClientRect();
            const st = getComputedStyle(el);
            if (r.width < 1 || r.height < 1 || st.visibility === 'hidden' || st.display === 'none' || el.disabled) continue;
            i++;
            el.setAttribute('data-fk-idx', String(i));
            const tag = el.tagName.toLowerCase();
            const type = (el.getAttribute('type') || '').toLowerCase();
            let label = el.getAttribute('aria-label') || el.getAttribute('title') || el.getAttribute('placeholder') || '';
            if (!label && el.labels && el.labels.length) label = el.labels[0].innerText || '';
            if (!label && tag !== 'select') label = (el.innerText || (type === 'submit' || type === 'button' ? el.value : '') || '').trim();
            const item = { index: i, tag, type, role: el.getAttribute('role') || '', name: el.getAttribute('name') || '',
              label: label.replace(/\s+/g, ' ').trim().slice(0, 80), inForm: !!el.closest('form') };
            if (tag === 'a') item.href = el.href;
            if (tag === 'input' || tag === 'textarea') item.value = type === 'password' ? (el.value ? '******' : '') : String(el.value || '').slice(0, 80);
            if (type === 'checkbox' || type === 'radio') item.value = el.checked ? '已勾选' : '未勾选';
            if (tag === 'select') { item.options = [...el.options].slice(0, 30).map(o => o.text.trim()); item.value = el.options[el.selectedIndex] ? el.options[el.selectedIndex].text.trim() : ''; }
            out.push(item);
            if (out.length >= MAX) break;
          }
          const text = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n').slice(0, MAXTEXT);
          return JSON.stringify({ url: location.href, title: document.title, text, elements: out });
        })()
        """.Replace("__MAX__", MaxElements.ToString()).Replace("__MAXTEXT__", MaxText.ToString());

    private static string Find(int index) => $"document.querySelector('[data-fk-idx=\"{index}\"]')";

    public static string Click(int index) => $$"""
        (() => {
          const el = {{Find(index)}};
          if (!el) return 'NOTFOUND';
          el.scrollIntoView({ block: 'center' });
          el.click();
          return 'OK';
        })()
        """;

    public static string Type(int index, string text, bool submit) => $$"""
        (() => {
          const el = {{Find(index)}};
          if (!el) return 'NOTFOUND';
          const text = {{JsonSerializer.Serialize(text)}};
          el.scrollIntoView({ block: 'center' });
          el.focus();
          if (el.isContentEditable) {
            el.innerText = text;
          } else {
            const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
            setter.call(el, text);
          }
          el.dispatchEvent(new Event('input', { bubbles: true }));
          el.dispatchEvent(new Event('change', { bubbles: true }));
          if ({{(submit ? "true" : "false")}}) {
            const opts = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true };
            el.dispatchEvent(new KeyboardEvent('keydown', opts));
            el.dispatchEvent(new KeyboardEvent('keyup', opts));
            const form = el.form || el.closest('form');
            if (form) { if (form.requestSubmit) form.requestSubmit(); else form.submit(); }
          }
          return 'OK';
        })()
        """;

    public static string Select(int index, string option) => $$"""
        (() => {
          const el = {{Find(index)}};
          if (!el || el.tagName !== 'SELECT') return 'NOTFOUND';
          const want = {{JsonSerializer.Serialize(option)}}.trim();
          const opt = [...el.options].find(o => o.text.trim() === want || o.value === want)
            || [...el.options].find(o => o.text.trim().includes(want));
          if (!opt) return 'NOOPTION';
          el.value = opt.value;
          el.dispatchEvent(new Event('input', { bubbles: true }));
          el.dispatchEvent(new Event('change', { bubbles: true }));
          return 'OK';
        })()
        """;

    public static string Scroll(bool down) => $"(() => {{ window.scrollBy(0, {(down ? "" : "-")}Math.round(window.innerHeight * 0.8)); return 'OK'; }})()";

    public const string Back = "(() => { history.back(); return 'OK'; })()";

    /// <summary>员工点遮罩上的「接管」时，页面发给宿主的消息。页面自己也能发，但最多只是把遮罩去掉，不涉及权限。</summary>
    public const string TakeControlMessage = "fk:take-control";

    /// <summary>
    /// AI 操作时盖在网页上的遮罩（参照 Cursor 的 Take Control）。每个新页面创建时注入一次，定义 window.__fkGuard(on, text, button)。
    /// - 放在 closed shadow DOM 里：读页面（querySelectorAll、body.innerText）看不到它，不会被当成可点的元素；
    /// - 挡的是员工的真实点击和按键（isTrusted）：AI 用脚本触发的 click / input / keydown 不受影响；
    /// - 滚轮不挡，员工可以上下看 AI 在做什么。
    /// </summary>
    public static readonly string Guard = """
        (() => {
          if (window.__fkGuard) return;
          let host = null, on = false, label = '', button = '';
          const blocked = ['mousedown', 'mouseup', 'click', 'dblclick', 'contextmenu', 'pointerdown', 'pointerup', 'touchstart', 'keydown', 'keypress', 'keyup', 'paste', 'drop'];
          const stop = (e) => {
            if (!on || !e.isTrusted || e.target === host) return;
            e.preventDefault(); e.stopImmediatePropagation();
          };
          blocked.forEach((t) => window.addEventListener(t, stop, true));
          const render = () => {
            if (!document.documentElement) return;
            if (!on) { if (host) host.style.display = 'none'; return; }
            if (!host) {
              host = document.createElement('fk-ai-guard');
              const root = host.attachShadow({ mode: 'closed' });
              root.innerHTML = `
                <style>
                  .veil { position: fixed; inset: 0; z-index: 2147483647; cursor: not-allowed;
                    background: rgba(24, 28, 48, .10); backdrop-filter: saturate(.9);
                    box-shadow: inset 0 0 0 3px rgba(52, 70, 201, .55);
                    display: flex; align-items: flex-end; justify-content: center; padding-bottom: 28px;
                    font: 13px/1.4 "Segoe UI", "Microsoft YaHei", system-ui, sans-serif; }
                  .pill { display: flex; align-items: center; gap: 10px; padding: 8px 8px 8px 14px; cursor: default;
                    border-radius: 999px; background: rgba(22, 24, 34, .92); color: #fff;
                    box-shadow: 0 8px 28px -6px rgba(0, 0, 0, .45); }
                  .mouse { width: 18px; height: 18px; color: #9fb0ff; animation: nudge 1.6s ease-in-out infinite; }
                  @keyframes nudge { 0%, 100% { transform: translate(0, 0); } 50% { transform: translate(3px, 3px); } }
                  button { border: 0; border-radius: 999px; padding: 6px 14px; cursor: pointer;
                    background: #3446c9; color: #fff; font: inherit; font-weight: 600; }
                  button:hover { background: #2a3aad; }
                </style>
                <div class="veil"><div class="pill">
                  <svg class="mouse" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M5 3l14 7.5-6.2 1.7L10 18.5z"/></svg>
                  <span class="label"></span><button type="button"></button>
                </div></div>`;
              root.querySelector('button').addEventListener('click', () => {
                try { window.chrome.webview.postMessage('__MSG__'); } catch (_) {}
              });
              host.__fk = root;
            }
            host.__fk.querySelector('.label').textContent = label;
            host.__fk.querySelector('button').textContent = button;
            host.style.display = '';
            if (!host.isConnected) document.documentElement.appendChild(host);
          };
          window.__fkGuard = (next, text, btn) => { on = !!next; label = text || ''; button = btn || ''; render(); return 'OK'; };
          document.addEventListener('DOMContentLoaded', render);
        })();
        """.Replace("__MSG__", TakeControlMessage);

    /// <summary>打开 / 去掉遮罩（文字由宿主按界面语言给）。页面还没注入过时什么都不做。</summary>
    public static string SetGuard(bool on, string text, string button) =>
        $"(() => window.__fkGuard ? window.__fkGuard({(on ? "true" : "false")}, {JsonSerializer.Serialize(text)}, {JsonSerializer.Serialize(button)}) : 'NONE')()";

    /// <summary>读页面脚本返回的 JSON。</summary>
    public static BrowserPage ParsePage(string json) =>
        JsonSerializer.Deserialize<BrowserPage>(json) ?? new BrowserPage();
}
