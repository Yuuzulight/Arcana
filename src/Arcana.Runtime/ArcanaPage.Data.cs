using System.Text.Json;

namespace Arcana.Runtime;

public sealed partial class ArcanaPage
{
    public Task<JsonElement> GetLinksAsync(CancellationToken cancellationToken = default) =>
        EvaluateAsync("Array.from(document.querySelectorAll('a[href]')).map(a=>({text:(a.textContent||'').trim(),href:a.href}))", cancellationToken);

    public Task<JsonElement> DetectFormsAsync(CancellationToken cancellationToken = default) => EvaluateAsync("""
        Array.from(document.forms).map(form=>({action:form.action,method:form.method,
          fields:Array.from(form.elements).map(el=>({tag:el.tagName.toLowerCase(),name:el.name||'',id:el.id||'',type:el.type||'',
            required:!!el.required,disabled:!!el.disabled,options:el.tagName==='SELECT'?Array.from(el.options).map(o=>({value:o.value,text:o.textContent})):null}))}))
        """, cancellationToken);

    public Task<JsonElement> QueryAsync(string selector, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return EvaluateAsync($"Array.from(document.querySelectorAll({JsonSerializer.Serialize(selector)})).map(el=>el.textContent||'')", cancellationToken);
    }

    public Task<string?> GetAttributeAsync(string reference, string attribute, CancellationToken cancellationToken = default) =>
        EvaluateStringAsync(PageScripts.ElementOperation(_elementStore, reference, $"return el.getAttribute({JsonSerializer.Serialize(attribute)});"), cancellationToken);

    public async Task SelectOptionAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        await EvaluateAsync(PageScripts.ElementOperation(_elementStore, reference, $$"""
            if(el.tagName!=='SELECT') throw new Error('Element is not a select.');
            const value={{JsonSerializer.Serialize(value)}};
            const option=Array.from(el.options).find(o=>o.value===value || o.textContent.trim()===value);
            if(!option || option.disabled || option.parentElement?.disabled) throw new Error('Option unavailable.');
            el.value=option.value;
            el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true}));
            return el.value;
            """), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCheckedAsync(string reference, bool isChecked, CancellationToken cancellationToken = default)
    {
        await EvaluateAsync(PageScripts.ElementOperation(_elementStore, reference, $$"""
            if(el.tagName!=='INPUT' || !['checkbox','radio'].includes(el.type)) throw new Error('Element is not a checkbox or radio.');
            if(el.checked!=={{(isChecked ? "true" : "false")}}) el.click();
            if(el.checked!=={{(isChecked ? "true" : "false")}}) throw new Error('Control did not reach the requested checked state.');
            return el.checked;
            """), cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadFileAsync(string reference, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Upload file was not found.", path);
        var document = await SendCdpAsync("DOM.getDocument", new { depth = 0 }, cancellationToken).ConfigureAwait(false);
        var node = await SendCdpAsync("DOM.querySelector", new { nodeId = document.GetProperty("root").GetProperty("nodeId").GetInt32(), selector = $"[aria-ref={JsonSerializer.Serialize(reference)}]" }, cancellationToken).ConfigureAwait(false);
        var nodeId = node.GetProperty("nodeId").GetInt32();
        if (nodeId == 0) throw new InvalidOperationException("Element reference is stale.");
        await SendCdpAsync("DOM.setFileInputFiles", new { files = new[] { Path.GetFullPath(path) }, nodeId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task TypeAsync(string reference, string text, CancellationToken cancellationToken = default)
    {
        await EvaluateAsync(PageScripts.ElementOperation(_elementStore, reference, "el.focus(); return true;"), cancellationToken).ConfigureAwait(false);
        await TypeTextAsync(text, cancellationToken).ConfigureAwait(false);
    }

    public async Task FillFormAsync(IEnumerable<FormField> fields, string? submitReference = null, CancellationToken cancellationToken = default)
    {
        foreach (var field in fields)
        {
            switch (field.Kind)
            {
                case FormFieldKind.Text: await FillAsync(field.Reference, field.Value, cancellationToken).ConfigureAwait(false); break;
                case FormFieldKind.Select: await SelectOptionAsync(field.Reference, field.Value, cancellationToken).ConfigureAwait(false); break;
                case FormFieldKind.Check: await SetCheckedAsync(field.Reference, true, cancellationToken).ConfigureAwait(false); break;
                case FormFieldKind.Uncheck: await SetCheckedAsync(field.Reference, false, cancellationToken).ConfigureAwait(false); break;
                default: throw new ArgumentOutOfRangeException(nameof(fields));
            }
        }
        if (submitReference is not null) await ClickAsync(submitReference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exports the current origin's storage plus the browser context's cookie jar. Contains secrets.</summary>
    public async Task<PageStorageState> ExportStorageAsync(CancellationToken cancellationToken = default)
    {
        var local = await EvaluateAsync("({origin:location.origin,local:Object.fromEntries(Object.entries(localStorage)),session:Object.fromEntries(Object.entries(sessionStorage))})", cancellationToken).ConfigureAwait(false);
        return new PageStorageState(local.GetProperty("origin").GetString()!,
            JsonSerializer.Deserialize<Dictionary<string, string>>(local.GetProperty("local"))!,
            JsonSerializer.Deserialize<Dictionary<string, string>>(local.GetProperty("session"))!,
            (await GetCookiesAsync(cancellationToken).ConfigureAwait(false)).GetProperty("cookies").Clone());
    }

    /// <summary>Replaces current-origin storage. Navigate to the state's origin first. Merges cookies by engine semantics.</summary>
    public async Task RestoreStorageAsync(PageStorageState state, CancellationToken cancellationToken = default)
    {
        var origin = await EvaluateStringAsync("location.origin", cancellationToken).ConfigureAwait(false);
        if (origin != state.Origin) throw new ArgumentException("Navigate to the storage state's origin before restoring it.", nameof(state));
        await EvaluateAsync($$"""
            (()=>{const state={{JsonSerializer.Serialize(state)}}; localStorage.clear(); sessionStorage.clear();
              for(const [k,v] of Object.entries(state.LocalStorage)) localStorage.setItem(k,v);
              for(const [k,v] of Object.entries(state.SessionStorage)) sessionStorage.setItem(k,v); return true;})()
            """, cancellationToken).ConfigureAwait(false);
        await SendCdpAsync("Network.setCookies", new { cookies = state.Cookies }, cancellationToken).ConfigureAwait(false);
    }
}

public enum FormFieldKind { Text, Select, Check, Uncheck }
public sealed record FormField(string Reference, string Value = "", FormFieldKind Kind = FormFieldKind.Text);
public sealed record PageStorageState(string Origin, IReadOnlyDictionary<string, string> LocalStorage,
    IReadOnlyDictionary<string, string> SessionStorage, JsonElement Cookies);
