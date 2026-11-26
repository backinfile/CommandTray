"use strict";

const textToolLimit = 1_000_000;
const textOperations = [
  ["json-format", "格式化 JSON"],
  ["json-compact", "压缩 JSON"],
  ["base64-encode", "Base64 编码"],
  ["base64-decode", "Base64 解码"],
  ["url-encode", "URL 组件编码"],
  ["url-decode", "URL 组件解码"],
  ["unique-lines", "按行去重"],
  ["sha256", "文本 SHA-256"],
  ["trim-lines", "去除每行首尾空格"],
  ["remove-blank-lines", "删除空行"],
  ["uuid-v4", "生成 UUID v4"]
];
let textDraft = "", textOperation = textOperations[0][0];

function renderTextTools() {
  const options = textOperations.map(([id,label]) =>
    '<option value="'+id+'"'+(id===textOperation?" selected":"")+'>'+label+'</option>').join("");
  return '<div class="toolbar"><select id="text-tool-mode" aria-label="文本操作">'+options+'</select>'+
    '<button type="button" class="primary" id="text-tool-run">转换</button></div>'+
    '<div class="split text-tool-panels">'+
    surface("输入", '<button type="button" id="text-tool-read">读取剪贴板</button>',
      '<div class="body"><textarea id="text-tool-input" aria-label="输入" maxlength="'+textToolLimit+
      '" spellcheck="false" autocomplete="off"></textarea></div>')+
    surface("结果", '<button type="button" id="text-tool-reuse" disabled>用作输入</button>'+
      '<button type="button" class="secondary" id="text-tool-copy" disabled>复制结果</button>',
      '<div class="body"><textarea id="text-tool-output" aria-label="结果" spellcheck="false" readonly></textarea></div>')+
    '</div><p class="muted text-tool-status" id="text-tool-status" role="status" hidden></p>';
}

function connectTextTools() {
  const input = el("text-tool-input"), output = el("text-tool-output");
  const mode = el("text-tool-mode"), run = el("text-tool-run");
  const copy = el("text-tool-copy"), reuse = el("text-tool-reuse"), status = el("text-tool-status");
  input.value = textDraft;
  let revision = 0;

  function setResult(value, message = "", error = false) {
    output.value = value;
    copy.disabled = !value;
    reuse.disabled = !value || value.length>textToolLimit;
    status.textContent = message;
    status.hidden = !message;
    status.classList.toggle("status-error",error);
  }
  function edit(value) {
    input.value = textDraft = value;
    revision++;
    setResult("");
  }

  input.addEventListener("input", () => { textDraft = input.value; revision++; setResult(""); });
  mode.addEventListener("change", () => { textOperation = mode.value; revision++; setResult(""); });
  reuse.addEventListener("click", () => { edit(output.value); input.focus(); });
  copy.addEventListener("click", () => copyText(output.value));
  el("text-tool-read").addEventListener("click", async () => {
    const current = revision;
    try {
      const text = await navigator.clipboard.readText();
      if (!input.isConnected || current !== revision) return;
      if (!text) { report("剪贴板没有文本"); return; }
      if (text.length > textToolLimit) { report("输入最多 100 万字符。",true); return; }
      edit(text);
      input.focus();
    } catch {
      if (input.isConnected) report("无法读取剪贴板，请将文本粘贴到输入区。",true);
    }
  });
  run.addEventListener("click", async () => {
    const current = ++revision;
    run.disabled = true;
    setResult("", "正在处理…");
    try {
      const text = mode.value==="uuid-v4" ? "" : input.value;
      if (text.length > textToolLimit) throw new Error("输入最多 100 万字符。");
      const result = await convertText(mode.value,text);
      if (input.isConnected && current === revision) setResult(result);
    } catch (error) {
      if (input.isConnected && current === revision) setResult("",error.message,true);
    } finally { run.disabled = false; }
  });
}

async function convertText(operation, input) {
  switch (operation) {
    case "json-format": return formatTextJson(input,true);
    case "json-compact": return formatTextJson(input,false);
    case "base64-encode": {
      const bytes = new TextEncoder().encode(input);
      let raw = "";
      for (let offset=0; offset<bytes.length; offset+=32768)
        raw += String.fromCharCode(...bytes.subarray(offset,offset+32768));
      return btoa(raw);
    }
    case "base64-decode": {
      try {
        const encoded = input.replace(/[ \r\n\t]/g,"");
        if (encoded.length%4) throw new Error();
        const bytes = Uint8Array.from(atob(encoded),letter=>letter.charCodeAt(0));
        return new TextDecoder("utf-8",{fatal:true,ignoreBOM:true}).decode(bytes);
      } catch { throw new Error("请输入有效的 Base64 UTF-8 文本。"); }
    }
    case "url-encode":
      return encodeURIComponent(input).replace(/[!'()*]/g,letter=>"%"+letter.charCodeAt(0).toString(16).toUpperCase());
    case "url-decode":
      try { return decodeURIComponent(input); }
      catch { throw new Error("请输入有效的 URL 编码。"); }
    case "unique-lines": {
      const normalized = normalizeTextLines(input);
      const end = normalized.endsWith("\n");
      const lines = (end ? normalized.slice(0,-1) : normalized).split("\n");
      return [...new Set(lines)].join("\n")+(end?"\n":"");
    }
    case "sha256": return utilityHex(commandTrayUtilityPrimitives.sha256(new TextEncoder().encode(input)));
    case "trim-lines":
      return normalizeTextLines(input).split("\n").map(line=>line.trim()).join("\n");
    case "remove-blank-lines": {
      const normalized = normalizeTextLines(input);
      const result = normalized.split("\n").filter(line=>line.trim()).join("\n");
      return result && normalized.endsWith("\n") ? result+"\n" : result;
    }
    case "uuid-v4": {
      const bytes=crypto.getRandomValues(new Uint8Array(16));
      bytes[6]=(bytes[6]&0x0f)|0x40;
      bytes[8]=(bytes[8]&0x3f)|0x80;
      const hex=utilityHex(bytes);
      return [hex.slice(0,8),hex.slice(8,12),hex.slice(12,16),hex.slice(16,20),hex.slice(20)].join("-");
    }
  }
}

function normalizeTextLines(input) {
  return input.replace(/\r\n|[\r\v\f\u0085\u2028\u2029]/g,"\n");
}

function formatTextJson(input, pretty) {
  try { JSON.parse(input); }
  catch { throw new Error("JSON 格式无效。"); }

  // Work from the original tokens so long numbers and repeated object keys are kept intact.
  const tokens = input.match(/"(?:\\[\s\S]|[^"\\])*"|[{}\[\],:]|[^{}\[\],:\s"]+/g) ?? [];
  const output = [];
  let depth = 0, hasContent = [];
  const line = () => pretty ? "\n"+"  ".repeat(depth) : "";
  for (let index=0; index<tokens.length; index++) {
    const token = tokens[index];
    if (token==="{"||token==="[") {
      if (++depth>256) throw new Error("JSON 嵌套过深。");
      const nonempty = tokens[index+1]!==(token==="{"?"}":"]");
      hasContent.push(nonempty);
      output.push(token,nonempty ? line() : "");
    } else if (token==="}"||token==="]") {
      depth--;
      output.push(hasContent.pop() ? line() : "",token);
    } else if (token===",") output.push(token,line());
    else if (token===":") output.push(pretty ? ": " : ":");
    else output.push(token);
  }
  return output.join("");
}
