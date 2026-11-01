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

    /// <summary>读页面脚本返回的 JSON。</summary>
    public static BrowserPage ParsePage(string json) =>
        JsonSerializer.Deserialize<BrowserPage>(json) ?? new BrowserPage();
}
