using System.Text.Json;

namespace Arcana.Runtime;

internal static class PageScripts
{
    public static string ElementOperation(string store, string reference, string operation) => $$"""
        (() => {
          const state = window[{{JsonSerializer.Serialize(store)}}];
          const el = state?.document === document ? state.elements.get({{JsonSerializer.Serialize(reference)}}) : null;
          if (!el || !el.isConnected) throw new Error('Stale element reference; capture a new snapshot.');
          if (el.matches(':disabled') || el.getAttribute('aria-disabled') === 'true' || el.closest('[hidden],[inert],[aria-hidden="true"]'))
            throw new Error('Element is hidden or disabled.');
          for (let node = el; node; node = node.parentElement) {
            const style = getComputedStyle(node);
            if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse')
              throw new Error('Element is hidden or disabled.');
          }
          {{operation}}
        })()
        """;

    public static string Snapshot(string store) => $$"""
        (() => {
          const key = {{JsonSerializer.Serialize(store)}};
          let state = window[key];
          if (!state || state.document !== document) {
            state = { document, prefix: Math.random().toString(36).slice(2), next: 0, ids: new WeakMap(), elements: new Map() };
            window[key] = state;
          }
          for (const [id, el] of state.elements) if (!el.isConnected) state.elements.delete(id);
          const visible = el => {
            if (el.closest('[hidden],[inert],[aria-hidden="true"]')) return false;
            for (let node = el; node; node = node.parentElement) {
              const style = getComputedStyle(node);
              if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
            }
            return true;
          };
          const controls = Array.from(document.querySelectorAll('a[href],button,input,textarea,select,[role="button"],[role="link"],[contenteditable="true"]')).filter(visible);
          const elements = controls.slice(0, 500).map(el => {
            let id = state.ids.get(el);
            if (!id) { id = state.prefix + '-e' + (++state.next); state.ids.set(el, id); }
            state.elements.set(id, el);
            el.setAttribute('aria-ref', id);
            const tag = el.tagName.toLowerCase();
            const labelled = (el.getAttribute('aria-labelledby') || '').split(/\s+/).map(id => document.getElementById(id)?.textContent || '').join(' ').trim();
            const labels = Array.from(el.labels || []).map(l => l.textContent || '').join(' ').trim();
            const name = el.getAttribute('aria-label') || labelled || labels || el.getAttribute('placeholder') ||
              (tag === 'input' ? el.getAttribute('title') || '' : el.textContent || '');
            return { reference: id, tag, role: el.getAttribute('role') || ({ a: 'link', button: 'button', textarea: 'textbox', select: 'combobox', input: 'input' }[tag] || 'textbox'),
              name: name.replace(/\s+/g, ' ').trim().slice(0, 300), href: tag === 'a' ? el.href : null,
              type: tag === 'input' ? el.type : null, disabled: el.matches(':disabled') || !!el.disabled || el.getAttribute('aria-disabled') === 'true' };
          });
          const text = document.body?.innerText ?? '';
          return JSON.stringify({ url: location.href, title: document.title, elements, truncated: controls.length > 500,
            text: text.slice(0,12000), textTruncated: text.length > 12000 });
        })()
        """;

    public static string Action(string store, string reference, string? value) => $$"""
        (() => {
          const state = window[{{JsonSerializer.Serialize(store)}}];
          const el = state?.document === document ? state.elements.get({{JsonSerializer.Serialize(reference)}}) : null;
          if (!el || !el.isConnected) throw new Error('Stale element reference; capture a new snapshot.');
          if (el.matches(':disabled') || el.disabled || el.getAttribute('aria-disabled') === 'true' || el.closest('[hidden],[inert],[aria-hidden="true"]'))
            throw new Error('Element is hidden or disabled.');
          for (let node = el; node; node = node.parentElement) {
            const style = getComputedStyle(node);
            if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse')
              throw new Error('Element is hidden or disabled.');
          }
          const value = {{JsonSerializer.Serialize(value)}};
          if (value === null) { el.click(); return 'clicked'; }
          if (el.readOnly) throw new Error('Element is read-only.');
          if (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && ['text','search','email','url','tel','password','number'].includes(el.type))) {
            const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            el.focus();
            Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
          } else if (el.getAttribute('contenteditable') === 'true') { el.focus(); el.textContent = value; }
          else throw new Error('Element is not a supported text control.');
          el.dispatchEvent(new Event('input', { bubbles: true }));
          el.dispatchEvent(new Event('change', { bubbles: true }));
          return 'filled';
        })()
        """;
}
