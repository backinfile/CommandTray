"use strict";

const utilitySections = [
  ["convert","文本转换"], ["compare","文本对比"], ["timestamp","时间戳"],
  ["qr","二维码"], ["checksum","文件校验"]
];
let utilitySection = "convert", disposeUtility;
const utilityDraft = {before:"",after:"",time:"",format:"seconds",qr:"",expected:""};

function resetUtilities() {
  utilitySection = "convert";
  Object.assign(utilityDraft,{before:"",after:"",time:"",format:"seconds",qr:"",expected:""});
  textDraft = "";
  textOperation = textOperations[0][0];
}

function renderUtilities() {
  const tabs = utilitySections.map(([id,label]) =>
    '<button type="button" data-utility="'+id+'" aria-pressed="'+(id===utilitySection)+'">'+label+'</button>').join("");
  const content = {convert:renderTextTools,compare:renderTextComparison,timestamp:renderTimestamp,qr:renderQrCode,checksum:renderFileChecksum};
  return '<div class="utility-tabs" id="utility-tabs" role="group" aria-label="实用工具">'+tabs+'</div>'+content[utilitySection]();
}

function connectUtilities() {
  el("utility-tabs").addEventListener("click",event=>{
    const next = event.target.closest("button")?.dataset.utility;
    if (!next || next===utilitySection) return;
    utilitySection = next;
    render();
    el("utility-tabs").querySelector('[aria-pressed="true"]').focus({preventScroll:true});
  });
  const connect = {convert:connectTextTools,compare:connectTextComparison,timestamp:connectTimestamp,qr:connectQrCode,checksum:connectFileChecksum};
  disposeUtility = connect[utilitySection]();
}

function utilityField(label,id,{multiline=false,readOnly=false,limit=textToolLimit}={}) {
  const attributes = ' id="'+id+'" aria-label="'+label+'" spellcheck="false" autocomplete="off"'+
    (readOnly?' readonly':' maxlength="'+limit+'"');
  const field = multiline ? '<textarea'+attributes+'></textarea>' : '<input type="text"'+attributes+'>';
  return '<div class="body">'+field+'</div>';
}

function utilityStatus() { return '<p class="utility-status muted" id="utility-status" role="status" hidden></p>'; }
function setUtilityStatus(message,tone="") {
  const status = el("utility-status");
  status.textContent = message;
  status.hidden = !message;
  status.classList.toggle("status-error",tone==="error");
  status.classList.toggle("status-success",tone==="success");
}
function utilityButton(id,label,style="",disabled=false) {
  return '<button type="button" id="'+id+'" class="'+style+'"'+(disabled?" disabled":"")+'>'+label+'</button>';
}

function renderTextComparison() {
  return '<div class="split text-tool-panels">'+
    surface("旧文本","",utilityField("旧文本","diff-before",{multiline:true}))+
    surface("新文本","",utilityField("新文本","diff-after",{multiline:true}))+
    '</div><div class="toolbar">'+utilityButton("diff-run","对比","primary")+ '</div>'+utilityStatus()+
    surface("差异",utilityButton("diff-copy","复制差异","secondary",true),
      utilityField("差异","diff-output",{multiline:true,readOnly:true}),"utility-panel utility-diff-result");
}

function connectTextComparison() {
  const before = el("diff-before"), after = el("diff-after"), output = el("diff-output");
  const run = el("diff-run"), copy = el("diff-copy");
  before.value = utilityDraft.before;
  after.value = utilityDraft.after;
  before.addEventListener("input",()=>{utilityDraft.before=before.value; clear();});
  after.addEventListener("input",()=>{utilityDraft.after=after.value; clear();});
  copy.addEventListener("click",()=>copyText(output.value));
  run.addEventListener("click",async ()=>{
    clear();
    const a = before.value, b = after.value;
    run.disabled = true;
    await new Promise(resolve=>setTimeout(resolve,0));
    try {
      if (!output.isConnected || a!==before.value || b!==after.value) return;
      const {lines,added,removed,finalNewlineChanged} = compareUtilityText(a,b);
      if (!added && !removed && !finalNewlineChanged) {setUtilityStatus("两段文本相同","success");return;}
      const newline = finalNewlineChanged ? " · 末尾换行有变化" : "";
      output.value = lines+(finalNewlineChanged?"\\ 末尾换行有变化\n":"");
      copy.disabled = false;
      setUtilityStatus("新增 "+added+" 行 · 删除 "+removed+" 行"+newline);
    } catch (error) { setUtilityStatus(error.message,"error"); }
    finally { run.disabled=false; }
  });
  function clear() { output.value=""; copy.disabled=true; setUtilityStatus(""); }
}

function compareUtilityText(before,after) {
  const lines = text=>{
    const normalized = normalizeTextLines(text);
    const final = normalized.endsWith("\n");
    return [normalized ? (final?normalized.slice(0,-1):normalized).split("\n") : [],final];
  };
  const [left,leftFinal] = lines(before), [right,rightFinal] = lines(after);
  if (before.length>textToolLimit || after.length>textToolLimit || left.length>2000 || right.length>2000)
    throw new Error("每段文本最多 2,000 行或 100 万字符。");

  const stride = right.length+1;
  const scores = new Uint16Array((left.length+1)*stride);
  for (let x=left.length-1;x>=0;x--) {
    for (let y=right.length-1;y>=0;y--) {
      const offset=x*stride+y;
      scores[offset] = left[x]===right[y] ? 1+scores[offset+stride+1] : Math.max(scores[offset+stride],scores[offset+1]);
    }
  }
  const output=[];
  let i=0,j=0,added=0,removed=0;
  while (i<left.length || j<right.length) {
    if (i<left.length && j<right.length && left[i]===right[j]) { output.push("  "+left[i++]); j++; }
    else if (i<left.length && (j===right.length || scores[(i+1)*stride+j]>=scores[i*stride+j+1])) {
      output.push("- "+left[i++]); removed++;
    } else { output.push("+ "+right[j++]); added++; }
  }
  return {lines:output.length?output.join("\n")+"\n":"",added,removed,finalNewlineChanged:leftFinal!==rightFinal};
}

function renderTimestamp() {
  return '<div class="toolbar"><select id="time-format" aria-label="输入格式">'+
    '<option value="seconds">Unix 秒</option><option value="millis">Unix 毫秒</option><option value="iso">ISO 8601（含时区）</option>'+
    '</select>'+utilityButton("time-run","转换时间","primary")+utilityButton("time-now","现在")+'</div>'+
    surface("时间","",utilityField("时间","time-input",{limit:64}),"utility-panel")+utilityStatus()+
    surface("转换结果",utilityButton("time-copy","复制结果","secondary",true),
      utilityField("转换结果","time-output",{multiline:true,readOnly:true}),"utility-panel utility-time-result");
}

function connectTimestamp() {
  const format=el("time-format"), input=el("time-input"), output=el("time-output"), copy=el("time-copy");
  format.value=utilityDraft.format;
  input.value=utilityDraft.time;
  format.addEventListener("change",()=>{utilityDraft.format=format.value; reset();});
  input.addEventListener("input",()=>{utilityDraft.time=input.value; reset();});
  el("time-run").addEventListener("click",run);
  el("time-now").addEventListener("click",()=>{
    const now=new Date();
    input.value=utilityDraft.time=format.value==="iso"?now.toISOString():String(format.value==="seconds"?Math.floor(+now/1000):+now);
    run();
  });
  copy.addEventListener("click",()=>copyText(output.value));
  reset();
  function reset() {
    output.value="";
    copy.disabled=true;
    input.placeholder={seconds:"1735689600",millis:"1735689600000",iso:"2025-01-01T08:00:00+08:00"}[format.value];
    setUtilityStatus("");
  }
  function run() {
    try {
      const time=readUtilityTimestamp(format.value,input.value);
      output.value="UTC  "+time.toISOString()+"\n本地时间  "+formatLocalTime(time)+
        "\nUnix 秒  "+Math.floor(+time/1000)+"\nUnix 毫秒  "+(+time);
      copy.disabled=false;
      setUtilityStatus("");
    } catch(error) { output.value="";copy.disabled=true;setUtilityStatus(error.message,"error"); }
  }
}

function readUtilityTimestamp(format,input) {
  let millis;
  if (format==="iso") {
    const error = "请输入带 Z 或时区偏移的有效时间。";
    const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,3}))?(Z|([+-])(\d{2}):(\d{2}))$/.exec(input.trim());
    if (!parts) throw new Error(error);
    const [,year,month,day,hour,minute,second,,zone,,zoneHour,zoneMinute]=parts;
    const marker=new Date(0);
    marker.setUTCFullYear(+year,+month-1,+day);
    if (+year<1 || marker.getUTCFullYear()!==+year || marker.getUTCMonth()!==+month-1 ||
        marker.getUTCDate()!==+day || +hour>=24 || +minute>=60 || +second>=60 ||
        (zone!=="Z"&&(+zoneHour>14 || +zoneMinute>=60 || (+zoneHour===14&&+zoneMinute>0))))
      throw new Error(error);
    millis=Date.parse(input.trim());
  } else if (/^[+-]?\d+$/.test(input.trim())) {
    millis=Number(input)*(format==="seconds"?1000:1);
  }
  if (!Number.isSafeInteger(millis) || millis< -62135596800000 || millis>253402300799999)
    throw new Error(format==="iso"?"时间超出支持范围。":"请输入有效范围内的整数时间戳。");
  return new Date(millis);
}

function formatLocalTime(date) {
  const pad=(n,length=2)=>String(n).padStart(length,"0");
  const minutes=-date.getTimezoneOffset();
  const zone=(minutes<0?"-":"+")+pad(Math.floor(Math.abs(minutes)/60))+":"+pad(Math.abs(minutes)%60);
  return pad(date.getFullYear(),4)+"-"+pad(date.getMonth()+1)+"-"+pad(date.getDate())+
    "T"+pad(date.getHours())+":"+pad(date.getMinutes())+":"+pad(date.getSeconds())+"."+pad(date.getMilliseconds(),3)+zone;
}

function renderQrCode() {
  return '<div class="split text-tool-panels">'+
    surface("文本或链接",utilityButton("qr-run","生成二维码","primary"),utilityField("文本或链接","qr-input",{multiline:true,limit:2000}))+
    surface("二维码预览",utilityButton("qr-save","保存 PNG","secondary",true),
      '<div class="body qr-preview"><canvas id="qr-preview" role="img" aria-label="二维码" hidden></canvas></div>')+
    '</div>'+utilityStatus();
}

function connectQrCode() {
  const input=el("qr-input"), preview=el("qr-preview"), save=el("qr-save");
  input.value=utilityDraft.qr;
  input.addEventListener("input",()=>{utilityDraft.qr=input.value; reset();});
  el("qr-run").addEventListener("click",()=>{
    reset();
    if (!input.value) {setUtilityStatus("请输入文本或链接。","error");return;}
    const bytes=new TextEncoder().encode(input.value);
    if (bytes.length>2000) {setUtilityStatus("二维码内容最多 2,000 个 UTF-8 字节。","error");return;}
    const {QrCode,QrSegment}=commandTrayUtilityPrimitives.qrcodegen;
    const code=QrCode.encodeSegments([QrSegment.makeEci(26),QrSegment.makeBytes(bytes)],QrCode.Ecc.MEDIUM);
    const scale=8,border=4;
    preview.height=preview.width=(code.size+border*2)*scale;
    const context=preview.getContext("2d");
    context.fillStyle="#fff";
    context.fillRect(0,0,preview.width,preview.height);
    context.fillStyle="#000";
    for (let y=0;y<code.size;y++) for (let x=0;x<code.size;x++)
      if (code.getModule(x,y)) context.fillRect((x+border)*scale,(y+border)*scale,scale,scale);
    preview.hidden=false;
    save.disabled=false;
  });
  save.addEventListener("click",()=>preview.toBlob(blob=>{if(blob) downloadBlob(blob,"qrcode.png");},"image/png"));
  function reset() { preview.hidden=true; save.disabled=true; setUtilityStatus(""); }
}

function renderFileChecksum() {
  return '<div class="toolbar">'+utilityButton("checksum-choose","选择文件","primary")+
    '<button type="button" id="checksum-cancel" hidden>取消计算</button>'+
    '<input type="file" id="checksum-file" aria-label="本地文件" hidden></div>'+
    surface("本地文件","",'<div class="body"><p id="checksum-name" class="checksum-name muted">尚未选择文件</p>'+
      '<progress id="checksum-progress" aria-label="校验进度" max="100" hidden></progress></div>',"utility-panel")+
    surface("预期 SHA-256","",utilityField("预期 SHA-256","checksum-expected",{multiline:true,limit:256}),"utility-panel utility-hash")+
    surface("计算出的 SHA-256",utilityButton("checksum-copy","复制 SHA-256","secondary",true),
      utilityField("计算出的 SHA-256","checksum-output",{multiline:true,readOnly:true}),"utility-panel utility-hash")+utilityStatus();
}

function connectFileChecksum() {
  const choose=el("checksum-choose"), picker=el("checksum-file"), cancel=el("checksum-cancel");
  const expected=el("checksum-expected"), output=el("checksum-output"), copy=el("checksum-copy");
  const progress=el("checksum-progress"), name=el("checksum-name");
  let active;
  expected.value=utilityDraft.expected;
  expected.addEventListener("input",()=>{utilityDraft.expected=expected.value;if(output.value) compare();});
  copy.addEventListener("click",()=>copyText(output.value));
  choose.addEventListener("click",()=>picker.click());
  cancel.addEventListener("click",()=>active?.abort());
  picker.addEventListener("change",async ()=>{
    const file=picker.files[0];
    picker.value="";
    if (!file) return;
    const operation=active=new AbortController();
    choose.disabled=copy.disabled=true;
    progress.hidden=cancel.hidden=false;
    progress.value=0;
    output.value="";
    name.textContent=file.name;
    setUtilityStatus("正在计算…");
    try {
      output.value=await hashUtilityFile(file,operation.signal,bytes=>{
        const percent=file.size?Math.floor(100*bytes/file.size):100;
        if (percent!==progress.value) progress.value=percent;
      });
      if (!output.isConnected) return;
      copy.disabled=false;
      compare();
    } catch {
      if (output.isConnected) setUtilityStatus(operation.signal.aborted?"已取消计算":"无法读取所选文件。",operation.signal.aborted?"":"error");
    } finally {
      if (active===operation) active=null;
      choose.disabled=false;
      progress.hidden=cancel.hidden=true;
    }
  });
  return ()=>active?.abort();

  function compare() {
    const value=expected.value.trim();
    if (!value) {setUtilityStatus("已计算 SHA-256","success");return;}
    if (!/^[0-9a-f]{64}$/i.test(value)) {setUtilityStatus("预期值需要 64 位十六进制字符。","error");return;}
    const matches=value.toLowerCase()===output.value;
    setUtilityStatus(matches?"SHA-256 一致":"SHA-256 不一致",matches?"success":"error");
  }
}

function utilityHex(bytes) {
  return Array.from(bytes,byte=>byte.toString(16).padStart(2,"0")).join("");
}

async function hashUtilityFile(file,signal,update) {
  const reader=file.stream().getReader();
  const digest=commandTrayUtilityPrimitives.sha256.create();
  const abort=()=>{reader.cancel().catch(()=>{});};
  signal.addEventListener("abort",abort,{once:true});
  let total=0;
  try {
    while (!signal.aborted) {
      const {done,value}=await reader.read();
      if (done || signal.aborted) break;
      digest.update(value);
      total+=value.length;
      update(total);
    }
    signal.throwIfAborted();
    return utilityHex(digest.digest());
  } finally {
    signal.removeEventListener("abort",abort);
    reader.releaseLock();
    digest.destroy();
  }
}
