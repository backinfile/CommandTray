"use strict";

const clientVersion = document.querySelector('meta[name="commandtray-version"]').content;
const versionHeader = "X-CommandTray-Client-Version";

const pages = {
  workspace: ["工作区", "M3 4h18v16H3z M3 9h18 M10 9v11"],
  files: ["共享文件", "M3 6h7l2 2h9v12H3z"],
  clipboard: ["剪贴板", "M8 5H5v15h14V5h-3 M8 4h8v4H8z M8 12h8 M8 16h6"],
  "text-tools": ["实用工具", "M4 5h16M7 5v3m10-3v3m-5-3v12m-3 0h6M4 21h16"],
  settings: ["设置", "M9 3h6l1 3 3 1 2 4-2 2v3l-3 3-3-1-2 3-4-1-1-3-3-2 1-4 3-1z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0"]
};
const componentKinds = [
  ["board","画板","M4 19l1-5L16 3l5 5-11 11z m10-14 5 5"],
  ["todo","待办","m3 7 2 2 3-4m3 2h10m-18 7 2 2 3-4m3 2h10"],
  ["note","便签","M5 3h10l4 4v14H5z m10-18v5h4m-11 4h8m-8 4h6"]
];
const el = id => document.getElementById(id);
const escape = text => String(text ?? "").replace(/[&<>"']/g, char => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
})[char]);
const svg = path => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${path}"/></svg>`;
const button = (label, action, appearance = "") =>
  `<button type="button" data-action="${escape(action)}"${appearance ? ` class="${appearance === true ? "primary" : appearance}"` : ""}>${escape(label)}</button>`;
const moreActions = (label, actions) =>
  `<button type="button" class="more-actions" aria-label="${escape(label)}" title="${escape(label)}" aria-haspopup="menu"
    aria-controls="action-menu" aria-expanded="false" data-menu="${escape(JSON.stringify(actions))}"><span aria-hidden="true">⋯</span></button>`;
const pageActions = (actions, caption = "") =>
  `<div class="page-actions">${caption ? `<p class="page-caption">${escape(caption)}</p>` : ""}<div class="actions">${actions}</div></div>`;
const surface = (label, actions, body, kind = "", componentId = null) =>
  `<section class="surface ${kind}"${componentId === null ? "" : ` data-component-id="${escape(componentId)}" tabindex="-1" aria-label="${escape(label)}"`}><header class="bar"><h2>${escape(label)}</h2><div class="actions">${actions}</div></header>${body}</section>`;
const empty = message => `<p class="empty muted">${escape(message)}</p>`;
const localTime = value => {
  const moment = new Date(value);
  return value && Number.isFinite(moment.valueOf()) ? moment.toLocaleString() : "";
};
const sizeText = bytes => bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + " MB" :
  bytes >= 1024 ? (bytes / 1024).toFixed(1) + " KB" : bytes + " B";
const uuid = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2,"0")).join("");
const object = value => value && typeof value === "object" && !Array.isArray(value);
const sessionKey = "commandtray.web.device";
let device = null;
try { device = JSON.parse(sessionStorage.getItem(sessionKey)); } catch {}
let page = "workspace", workspace = null, selected = null;
let files = [], history = [], devices = [], pinnedClips = new Map();
let fileQuery = "", fileFolder = "", clipQuery = "", clipSource = "", clipSelection = null;
let collapsed = false, loading = false, navigation = 0, pageRequest, feedbackTimer, conflict;
const transfers = [];
let activeTransfer = null;
let pendingWrite = Promise.resolve();
let clientDatabase;
let conflictPersisted = false;
let menuTrigger = null;

function closeActionMenu(restoreFocus = false) {
  if (!menuTrigger) return;
  const trigger = menuTrigger;
  menuTrigger = null;
  trigger.setAttribute("aria-expanded", "false");
  el("action-menu").hidden = true;
  if (restoreFocus && trigger.isConnected) trigger.focus({preventScroll:true});
}

function showActionMenu(trigger, last = false) {
  if (trigger === menuTrigger) { closeActionMenu(true); return; }
  closeActionMenu();
  menuTrigger = trigger;
  trigger.setAttribute("aria-expanded", "true");
  const menu = el("action-menu");
  menu.setAttribute("aria-label", trigger.getAttribute("aria-label"));
  menu.innerHTML = JSON.parse(trigger.dataset.menu).map(([label, action, danger]) =>
    `<button type="button" role="menuitem" tabindex="-1" data-action="${escape(action)}"${danger ? " data-danger" : ""}>${escape(label)}</button>`).join("");
  menu.style.maxHeight = "none";
  menu.hidden = false;
  const anchor = trigger.getBoundingClientRect();
  const { width, height } = menu.getBoundingClientRect();
  const below = innerHeight - anchor.bottom - 12, above = anchor.top - 12;
  const opensDown = height <= below || below >= above;
  const maximum = Math.max(28, opensDown ? below : above);
  menu.style.maxHeight = maximum + "px";
  menu.style.left = Math.max(8, Math.min(anchor.right - width, innerWidth - width - 8)) + "px";
  menu.style.top = (opensDown ? anchor.bottom + 4 : Math.max(8, anchor.top - Math.min(height, maximum) - 4)) + "px";
  (last ? menu.lastElementChild : menu.firstElementChild)?.focus({preventScroll:true});
}

function clientStorage(mode, operation) {
  clientDatabase ??= new Promise((resolve, reject) => {
    const request = indexedDB.open("commandtray.web", 1);
    request.onupgradeneeded = () => request.result.createObjectStore("saved");
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  return clientDatabase.then(database => new Promise((resolve, reject) => {
    const transaction = database.transaction("saved", mode);
    const request = operation(transaction.objectStore("saved"));
    transaction.oncomplete = () => resolve(request?.result);
    transaction.onabort = () => reject(transaction.error);
  }));
}

const conflictKey = identity => ["conflict", identity.id];
async function saveConflict(value, identity) {
  conflict = value;
  try {
    await clientStorage("readwrite", store => store.put(value, conflictKey(identity)));
    if (device === identity) conflictPersisted = true;
    return true;
  } catch {
    return false;
  }
}

async function clearConflict(identity) {
  if (conflictPersisted) await clientStorage("readwrite", store => store.delete(conflictKey(identity)));
  if (device === identity) {
    conflict = null;
    conflictPersisted = false;
  }
}

async function restoreWorkspace() {
  const identity = device;
  try {
    const saved = await clientStorage("readonly", store => store.get(conflictKey(identity)));
    if (device === identity && saved) {
      conflict = saved;
      conflictPersisted = true;
    }
  }
  catch { /* Pairing and workspace access remain available without browser storage. */ }
  if (device === identity) await open("workspace");
}

function setDevice(value) {
  device = value;
  try {
    if (value) sessionStorage.setItem(sessionKey, JSON.stringify(value));
    else sessionStorage.removeItem(sessionKey);
  } catch {}
}

function dropDevice() {
  navigation++;
  pageRequest?.abort();
  const previousTransfer = activeTransfer;
  activeTransfer = null;
  transfers.length = 0;
  previousTransfer?.xhr?.abort();
  if (device && conflict) clearConflict(device).catch(() => {});
  setDevice(null);
  workspace = null;
  selected = null;
  files = [];
  history = [];
  resetUtilities();
  pinnedClips.clear();
  devices = [];
  conflict = null;
  conflictPersisted = false;
  el("dialog").close();
  render();
}

class WorkspaceConflict extends Error {
  constructor(snapshot) {
    super("空间在另一台设备上发生更改。");
    this.snapshot = snapshot;
  }
}
async function request(path, { method = "GET", body, anonymous = false, signal } = {}) {
  const identity = device;
  const headers = new Headers();
  headers.set(versionHeader, clientVersion);
  if (!anonymous && identity) headers.set("Authorization", "Bearer " + identity.token);
  if (body !== undefined) headers.set("Content-Type", "application/json");
  const timeout = AbortSignal.timeout(30_000);
  const reply = await fetch("/v1/" + path, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store", signal: signal ? AbortSignal.any([signal, timeout]) : timeout
  });
  if (reply.ok) return reply;
  if (reply.status === 401) {
    if (anonymous) throw new Error("配对码不正确。");
    if (device === identity) dropDevice();
    throw new Error("配对已失效，请重新连接。");
  }
  const error = await reply.json().catch(() => null);
  if (reply.status === 409 && path.startsWith("workspace") && error?.document) throw new WorkspaceConflict(error);
  throw new Error(error?.error ?? "中转请求失败（" + reply.status + "）。");
}

function report(message, error = false) {
  clearTimeout(feedbackTimer);
  const status = el("status");
  status.textContent = message;
  status.classList.toggle("error", error);
  status.hidden = !message;
  if (message) feedbackTimer = setTimeout(() => status.hidden = true, 3800);
}

function selectedSpace() {
  return workspace?.document?.spaces?.find(space => space.id === selected);
}

function navLink(id) {
  return `<button class="nav-button" type="button" data-page="${id}" aria-label="${pages[id][0]}" title="${pages[id][0]}"${
    page === id ? ' aria-current="page"' : ""}>${svg(pages[id][1])}<span>${pages[id][0]}</span></button>`;
}

const toolPages = ["files", "clipboard", "text-tools"];
const spacesOptions = () => (workspace?.document?.spaces ?? []).map(space => ({label:space.name, space:space.id, scope:"空间"}));
const toolOptions = () => toolPages.map(id => ({label:pages[id][0], page:id, scope:"工具"}));

function render() {
  disposeUtility?.();
  disposeUtility = undefined;
  closeActionMenu();
  unmountWorkspaceBoards();
  el("shell").classList.toggle("unpaired", !device);
  el("shell").classList.toggle("collapsed", collapsed);
  if (!device) {
    el("top").replaceChildren();
    el("sidebar").replaceChildren();
    el("content").innerHTML = '<div class="login"><h1>连接中转服务器</h1><form id="pair" class="surface">' +
      '<label class="field">设备名称<input name="name" value="Web 浏览器" maxlength="120" required autocomplete="off"></label>' +
      '<label class="field">配对码<input name="code" type="password" minlength="16" maxlength="512" required autocomplete="off"></label>' +
      '<div class="actions"><button class="primary" type="submit">配对</button><button id="pair-cancel" type="button" hidden>取消</button></div>' +
      '<p class="form-status" role="status"></p></form></div>';
    el("pair").addEventListener("submit", pair);
    return;
  }
  el("top").innerHTML = `<div class="crumb"><button class="rail-toggle" type="button" data-action="sidebar" aria-label="${
    collapsed ? "展开侧栏" : "收起侧栏"}" title="${collapsed ? "展开侧栏" : "收起侧栏"}">${svg("M3 4h18v16H3z M9 4v16")}</button>${
    `<h1 class="page-title">${escape(page === "workspace" ? selectedSpace()?.name ?? "空间" : pages[page][0])}</h1>`}</div><button class="search-trigger" type="button" data-action="search">${
    svg("M16 10a6 6 0 1 1-12 0 6 6 0 0 1 12 0m-2 4 6 6")}快速查找 <kbd>Ctrl K</kbd></button>`;
  const spaces = (workspace?.document?.spaces ?? []).map(space => `<button type="button" class="nav-button" data-space="${
    escape(space.id)}"${space.id === selected && page === "workspace" ? ' aria-current="page"' : ""} aria-label="${escape(space.name)}" title="${escape(space.name)}">${
    svg(pages.workspace[1])}<span>${escape(space.name)}</span></button>`).join("");
  const chooser = (label, icon, name, active) => `<button type="button" class="nav-button ${name}" data-action="${name}" title="${label}" aria-label="${label}" aria-haspopup="dialog"${active ? ' aria-current="page"' : ""}>${svg(icon)}<span>${label}</span></button>`;
  el("sidebar").innerHTML = '<div class="space-group"><div class="group-title"><small>空间</small><button type="button" class="new-space" data-action="new-space" title="新建空间" aria-label="新建空间">＋</button></div>' + spaces + "</div>" +
    chooser("空间", pages.workspace[1], "choose-space", page === "workspace") +
    '<div class="tool-group group"><small>工具</small>' + toolPages.map(navLink).join("") + "</div>" +
    chooser("工具", pages.files[1], "choose-tool", toolPages.includes(page)) +
    '<div class="nav-footer"><small class="paired">已配对</small>' + navLink("settings") + "</div>";
  el("content").innerHTML = loading ? '<p class="muted" role="status">正在加载…</p>' :
    ({ workspace: renderWorkspace, files: renderFiles, clipboard: renderClipboard, "text-tools": renderUtilities, settings: renderSettings }[page])();
  if (!loading && page === "workspace") mountWorkspaceBoards();
  if (!loading && page === "text-tools") connectUtilities();
}

async function pair(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const submit = form.querySelector('[type="submit"]');
  if (submit.disabled) return;
  const values = new FormData(form);
  const pending = new AbortController();
  const cancel = el("pair-cancel");
  cancel.hidden = false;
  cancel.onclick = () => pending.abort();
  submit.disabled = true;
  submit.textContent = "正在配对…";
  const status = form.querySelector(".form-status");
  status.classList.remove("error");
  status.textContent = "";
  try {
    const name = values.get("name").trim();
    const reply = await request("devices/pair", { method: "POST", anonymous: true, signal: pending.signal,
      body: { deviceName: name, pairingCode: values.get("code") } });
    const paired = await reply.json();
    pending.signal.throwIfAborted();
    if (!paired.deviceId || !paired.token) throw new Error("中转未返回有效凭据。");
    setDevice({ id: paired.deviceId, token: paired.token, name });
    await open("workspace");
  } catch (failure) {
    status.classList.toggle("error", !pending.signal.aborted);
    status.textContent = pending.signal.aborted ? "配对已取消" : failure.message;
  } finally {
    submit.disabled = false;
    submit.textContent = "配对";
    cancel.hidden = true;
    cancel.onclick = null;
  }
}

async function open(target) {
  pageRequest?.abort();
  const controller = pageRequest = new AbortController();
  const operation = ++navigation;
  page = target;
  if (target === "text-tools") { loading = false; render(); return; }
  loading = true;
  render();
  try {
    const route = {workspace:"workspace",files:"files",clipboard:"clipboard/history",settings:"devices"}[target];
    const result = await (await request(route, {signal: controller.signal})).json();
    if (operation !== navigation) return;
    if (target === "workspace") acceptWorkspace(result);
    if (target === "files") {
      files = result.files;
      if (!files.some(file => (file.folder || "/") === fileFolder)) fileFolder = "";
    }
    if (target === "clipboard") {
      history = result.entries;
      try {
        const saved = await readPinnedClips(device, result.entries);
        if (operation === navigation) pinnedClips = saved;
      } catch {
        if (operation === navigation) report("无法读取此浏览器的置顶记录。", true);
      }
    }
    if (target === "settings") devices = result.devices;
  } catch (failure) {
    if (!controller.signal.aborted && operation === navigation) report(failure.message, true);
  } finally {
    if (operation === navigation) {
      loading = false;
      render();
    }
  }
}

function acceptWorkspace(snapshot) {
  if (workspace && snapshot.revision < workspace.revision) return;
  workspace = snapshot;
  const { spaces, activeSpaceId } = workspace.document;
  if (!spaces.some(space => space.id === selected))
    selected = spaces.find(space => space.id === activeSpaceId)?.id ?? spaces[0]?.id ?? null;
}

function category(kind) {
  return {notes:"note",tasks:"todo",whiteboard:"board"}[kind] ?? kind;
}

function visibleComponents(space) {
  return (space?.items ?? []).filter(item => componentKinds.some(([kind]) => kind === category(item.kind)));
}

function componentLabel(item) {
  return componentKinds.find(([kind]) => kind === category(item.kind))?.[1] ?? item.kind;
}

function renderWorkspace() {
  if (!workspace) return pageActions(button("刷新", "refresh"));
  const space = selectedSpace();
  const notice = conflict ? `<div class="toolbar conflict"><span>同步冲突</span>${button("比较版本", "compare-conflict", true)}</div>` : "";
  if (!space) return notice + empty("暂无空间");
  const items = visibleComponents(space);
  const tools = button("添加组件", "add-item", true) + (items.length ? button("定位组件", "locate", "secondary") : "") +
    button("同步", "refresh") + moreActions("空间操作", [["重命名空间", "rename-space"]]);
  const tiles = items.map(item => {
    const data = item.data ?? {};
    const kind = category(item.kind);
    const name = item.title || componentLabel(item);
    const remove = moreActions("组件操作", [["移除组件", "remove-item:" + item.id, true]]);
    if (kind === "note")
      return surface(name, button("编辑", "edit:" + item.id) + remove, `<div class="body note">${escape(data.text || "暂无内容")}</div>`, "tile-note", item.id);
    if (kind === "todo") {
      const entries = data.tasks ?? [];
      const rows = entries.map(task => `<div class="task-row"><label class="task"><input type="checkbox" data-item="${escape(item.id)}"
        data-task="${escape(task.id)}"${task.completed ? " checked" : ""}><span>${escape(task.text)}</span></label>
        <div class="actions">${button("编辑", "edit-task:" + item.id + "/" + task.id)}
        ${moreActions("待办操作", [["删除待办", "delete-task:" + item.id + "/" + task.id, true]])}</div></div>`).join("");
      const done = entries.filter(task=>task.completed).length;
      const progress = entries.length ? `<div class="task-progress"><small>${done} / ${entries.length} 已完成</small><progress aria-label="待办完成率" value="${done}" max="${entries.length}"></progress></div>` : "";
      return surface(name, button("新增待办", "add-task:" + item.id) + remove, progress + (rows || empty("暂无待办")), "tile-todo", item.id);
    }
    return surface(name, button("导出 PNG", "board-export:" + item.id) + remove, boardMarkup(item.id, name), "tile-board", item.id);
  }).join("");
  return `<div class="toolbar workspace-actions" aria-label="空间操作">${tools}</div>` + notice +
    (tiles ? `<div class="workbench">${tiles}</div>` : empty("暂无组件"));
}

function same(first, second) { return JSON.stringify(first) === JSON.stringify(second); }
function edits(before, after, path = [], result = [], hadBefore = true, hasAfter = true) {
  if (hadBefore === hasAfter && same(before, after)) return result;
  if (object(before) && object(after)) {
    for (const key of new Set([...Object.keys(before), ...Object.keys(after)]))
      edits(Object.hasOwn(before,key) ? before[key] : null, Object.hasOwn(after,key) ? after[key] : null,
        [...path,key], result, Object.hasOwn(before,key), Object.hasOwn(after,key));
  } else if (identified(before) && identified(after)) {
    const old = new Map(before.map(item=>[item.id,item]));
    const next = new Map(after.map(item=>[item.id,item]));
    for (const key of new Set([...old.keys(), ...next.keys()]))
      edits(old.get(key) ?? null, next.get(key) ?? null, [...path,key], result, old.has(key), next.has(key));
    const intermediate = [...old.keys()].filter(key=>next.has(key)).concat([...next.keys()].filter(key=>!old.has(key)));
    const wanted = [...next.keys()];
    if (!same(intermediate, wanted)) result.push({ path, hadBefore:true, before:intermediate, hasAfter:true, after:wanted, order:true });
  } else result.push({path, hadBefore, before, hasAfter, after});
  return result;

  function identified(array) {
    return Array.isArray(array) && array.every(item=>object(item)&&typeof item.id==="string"&&item.id.length) &&
      new Set(array.map(item=>item.id)).size===array.length;
  }
}

function redrawWorkspace() {
  const focused = document.activeElement;
  const { item, task, action } = focused?.dataset ?? {};
  render();
  if (task)
    document.querySelector(`input[data-item="${CSS.escape(item)}"][data-task="${CSS.escape(task)}"]`)?.focus({preventScroll:true});
  else if (action?.startsWith("edit-task:") || action === "locate")
    document.querySelector(`[data-action="${CSS.escape(action)}"]`)?.focus({preventScroll:true});
}

function writeWorkspace(change, redraw = true) {
  const section = selected;
  const identity = device;
  const operation = pendingWrite.then(async () => {
    if (!workspace || !device || device !== identity) return;
    if (conflict) throw new Error("先处理现有的同步冲突。");
    const { revision, document: current } = workspace;
    const updated = structuredClone(current);
    change(updated, updated.spaces.find(space => space.id === section));
    const changes = edits(current, updated);
    if (!changes.length) return;
    try {
      const response = await request("workspace/changes", { method: "PATCH", body: { baseRevision: revision, operationId: uuid(), changes } });
      if (device !== identity) return;
      acceptWorkspace(await response.json());
      if (redraw && page === "workspace" && !loading) redrawWorkspace();
    } catch (error) {
      if (!(error instanceof WorkspaceConflict) || device !== identity) throw error;
      const saved = await saveConflict({ local:updated, remote:error.snapshot }, identity);
      if (device === identity && page === "workspace" && !loading) redrawWorkspace();
      throw new Error(saved ? "未提交的修改已保留，请比较冲突版本。" : "无法保存草稿，请在刷新前比较版本并导出。");
    }
  });
  pendingWrite = operation.catch(()=>{});
  return operation;
}

function fileRows() {
  const query = fileQuery.toLowerCase().trim();
  const visible = files.filter(file => (!fileFolder || fileFolder === (file.folder || "/")) &&
    [file.name,file.folder,file.deviceName].some(value=>String(value??"").toLowerCase().includes(query)));
  if (!visible.length) return empty(files.length ? "没有符合条件的文件" : "暂无共享文件");
  const rows = visible.map(file => {
    const actions = [["复制文件名", "file-name:" + file.id]];
    if (/^[0-9a-f]{64}$/i.test(file.sha256))
      actions.push(["复制 SHA-256", "file-checksum:" + file.id],["校验本地文件…", "verify-file:" + file.id]);
    actions.push(["删除文件", "remove-file:" + file.id, true]);
    return `<tr><td><strong>${escape(file.name)}</strong><small>${escape(file.folder || "根目录")}</small></td>
      <td class="file-meta">${sizeText(file.size)}</td><td class="file-meta">${escape(file.deviceName || "")}<small>${escape(localTime(file.modifiedAt))}</small></td>
      <td><div class="actions">${button("下载", "download:" + file.id)}${moreActions("文件操作", actions)}</div></td></tr>`;
  }).join("");
  return `<div class="surface table-wrap"><table><thead><tr><th>名称</th><th class="file-meta">大小</th><th class="file-meta">来源 / 时间</th><th>操作</th></tr></thead><tbody>${rows}</tbody></table></div>`;
}
function renderFiles() {
  const folders = [...new Set(files.map(file => file.folder || "/"))].sort();
  const filters = `<div class="toolbar"><input id="file-query" type="search" aria-label="搜索文件" placeholder="搜索文件" value="${escape(fileQuery)}">
    <select id="file-folder" aria-label="目录"><option value="">所有目录</option>${folders.map(folder => `<option value="${
    escape(folder)}"${folder===fileFolder?" selected":""}>${escape(folder==="/"?"根目录":folder)}</option>`).join("")}</select></div>`;
  return pageActions(button("上传文件", "upload", true) +
    moreActions("共享文件列表操作", [["刷新列表", "refresh"]]), `${files.length} 个文件`) +
    '<div id="transfer-queue">' + transferMarkup() + '</div>' + filters + '<div id="file-rows">' + fileRows() + '</div>';
}

function clipKey(entry) { return String(entry.revision) + "/" + (entry.deviceId ?? ""); }
function clipPreferences() {
  try { return JSON.parse(localStorage.getItem("commandtray.web.clips." + device.id)) ?? { pinned:[], hidden:[] }; }
  catch { return { pinned:[], hidden:[] }; }
}
const clipStorageKey = (identity, id) => ["clip", identity.id, id];
function writeClipPreferences(identity, preferences) {
  localStorage.setItem("commandtray.web.clips." + identity.id, JSON.stringify(preferences));
}
async function readPinnedClips(identity, entries) {
  const { pinned, hidden } = clipPreferences();
  const range = IDBKeyRange.bound(clipStorageKey(identity, ""), clipStorageKey(identity, "\uffff"));
  const archived = await clientStorage("readonly", store => store.getAll(range));
  const saved = new Map(archived.map(entry => [clipKey(entry), entry]));
  const missing = entries.filter(entry => {
    const id = clipKey(entry);
    return pinned.includes(id) && !hidden.includes(id) && !saved.has(id);
  });
  if (missing.length) {
    await clientStorage("readwrite", store => {
      for (const entry of missing) store.put(entry, clipStorageKey(identity, clipKey(entry)));
    });
    for (const entry of missing) saved.set(clipKey(entry), entry);
  }
  return saved;
}
function clipboardEntries() {
  const all = new Map(pinnedClips);
  for (const entry of history) all.set(clipKey(entry), entry);
  return [...all.values()];
}
async function setClipPin(id, pin, hide = false) {
  const identity = device;
  const prefs = clipPreferences();
  if (pin) {
    if (pinnedClips.size >= 256) throw new Error("请先取消一条记录的置顶。");
    const entry = history.find(entry => clipKey(entry) === id);
    if (!entry) return;
    await clientStorage("readwrite", store => store.put(entry, clipStorageKey(identity, id)));
    if (identity === device) pinnedClips.set(id, entry);
  } else {
    if (pinnedClips.has(id))
      await clientStorage("readwrite", store => store.delete(clipStorageKey(identity, id)));
    if (hide || prefs.pinned.includes(id)) {
      prefs.pinned = prefs.pinned.filter(key => key !== id);
      if (hide) prefs.hidden = [id, ...prefs.hidden.filter(key => key !== id)].slice(0, 256);
      writeClipPreferences(identity, prefs);
    }
    if (identity === device) pinnedClips.delete(id);
  }
  if (identity === device) refreshClips();
}
function filterHistory() {
  const prefs = clipPreferences();
  const pinned = entry => pinnedClips.has(clipKey(entry)) || prefs.pinned.includes(clipKey(entry));
  const query = clipQuery.toLowerCase().trim();
  return clipboardEntries().filter(entry=>!prefs.hidden.includes(clipKey(entry)) &&
    (!clipSource || (clipSource === "$pinned" ? pinned(entry) : entry.deviceName===clipSource)) &&
    [entry.text,entry.deviceName].some(value=>String(value??"").toLowerCase().includes(query)))
    .map(entry=>({entry, pinned:pinned(entry)}))
    .sort((a,b)=>Number(b.pinned)-Number(a.pinned) || b.entry.revision-a.entry.revision);
}
function clipPanels() {
  const entries = filterHistory();
  if (!entries.some(({entry})=>clipKey(entry)===clipSelection)) clipSelection = entries[0] ? clipKey(entries[0].entry) : null;
  const selectedEntry = entries.find(({entry})=>clipKey(entry)===clipSelection);
  const list = entries.map(({entry,pinned})=>`<button type="button" class="clip-row" data-action="clip-select:${escape(clipKey(entry))}"
    aria-pressed="${clipKey(entry)===clipSelection}"><span class="clip-meta"><small>${pinned?"置顶 · ":""}${
    escape(entry.deviceName || "未知设备")}</small><small>${escape(localTime(entry.updatedAt))}</small></span>
    <span class="clip-preview">${escape(entry.text.slice(0,280))}${entry.text.length>280?"…":""}</span></button>`).join("") || empty("没有符合条件的记录");
  let detail = empty("请选择记录");
  if (selectedEntry) {
    const {entry,pinned}=selectedEntry, key=clipKey(entry);
    detail = `<div class="body"><div class="toolbar"><small>${pinned ? "置顶 · " : ""}${escape(entry.deviceName || "未知设备")} · ${escape(localTime(entry.updatedAt))}</small></div>
      <div class="clip-text">${escape(entry.text)}</div><div class="toolbar clip-actions">${button("复制","copy:"+key,"secondary")}${
      button(pinned?"取消置顶":"置顶","clip-pin:"+key)}${moreActions("记录操作",[
        ["编辑后发送…","edit-clip:"+key],
        ["处理文本","transform-clip:"+key],
        ["在本机隐藏","clip-hide:"+key]
      ])}</div></div>`;
  }
  return surface("历史", "", list) + surface("内容", "", detail, "clip-inspector");
}
function refreshClips() { if (el("clip-panels")) el("clip-panels").innerHTML = clipPanels(); }
function renderClipboard() {
  const sources = [...new Set(clipboardEntries().map(entry=>entry.deviceName).filter(Boolean))];
  if (clipSource && clipSource!=="$pinned" && !sources.includes(clipSource)) clipSource = "";
  const options = [["","所有记录"],["$pinned","已置顶"],...sources.map(name=>[name,name])];
  return pageActions(button("发送文本", "send-clipboard", true) +
    moreActions("剪贴板历史操作", [["刷新历史", "refresh"]]), `${history.length} 条同步记录`) +
    `<div class="toolbar"><input id="clip-query" type="search" aria-label="搜索剪贴板" placeholder="搜索剪贴板" value="${escape(clipQuery)}">
      <select id="clip-source" aria-label="按设备筛选">${options.map(([id,label])=>`<option value="${escape(id)}"${
      clipSource===id?" selected":""}>${escape(label)}</option>`).join("")}</select></div><div class="split" id="clip-panels">${clipPanels()}</div>`;
}
function renderSettings() {
  const rows = devices.map(entry=>`<div class="setting-row"><div><strong>${escape(entry.name)}</strong><small>${
    escape(localTime(entry.pairedAt))}</small></div>${entry.id === device.id ? "<span>此浏览器</span>" : ""}</div>`).join("");
  return surface("中转与设备", button("刷新设备", "refresh"),
    `<div class="setting-row"><div><strong>中转服务器</strong><small>${escape(location.host)}</small></div>${moreActions("本机配对操作",[["断开配对", "disconnect", true]])}</div>${
    rows}`);
}

const field = (label, control) => `<label class="field">${escape(label)}${control}</label>`;
const input = (label,value="",maxlength=120,required=true) => field(label,
  `<input name="text" value="${escape(value)}" maxlength="${maxlength}" autocomplete="off"${required ? " required" : ""}>`);
const textarea = (label,value="",required=true) => field(label,
  `<textarea name="text" maxlength="1000000"${required ? " required" : ""}>${escape(value)}</textarea>`);
function showDialog(title, content, mode = "action") {
  const dialog = el("dialog");
  dialog.dataset.mode = mode;
  el("dialog-title").textContent = title;
  el("dialog-content").innerHTML = content;
  el("dialog-close").disabled = false;
  if (!dialog.open) dialog.showModal();
  el("dialog-content").querySelector("input, textarea, select, [data-action='close-dialog']")?.focus();
}
const dialogPending = () => el("dialog").querySelector("form[data-pending]") !== null;
function closeUserDialog() { if (!dialogPending()) el("dialog").close(); }

async function copyText(text) {
  try { await navigator.clipboard.writeText(text); report("已复制"); }
  catch {
    showDialog("复制文本",textarea("文本",text,false));
    el("dialog-content").querySelector("textarea").select();
  }
}

const clipboardText = key => (history.find(entry=>clipKey(entry)===key) ?? pinnedClips.get(key))?.text;
function composeClipboard(text = "") {
  return modal(text ? "编辑后发送" : "发送文本", "", textarea("文本",text), "发送", async values => {
    await request("clipboard", { method:"PUT", body:{text:values.get("text")} });
    await open("clipboard");
  });
}

function modal(title, description, fields, submit, action, danger = false) {
  showDialog(title, `<form>${description ? `<p class="dialog-description">${escape(description)}</p>` : ""}${fields}
    <div class="actions">${button("取消", "close-dialog")}<button type="submit" class="${danger ? "danger" : "primary"}">${escape(submit)}</button></div><p class="form-status" role="status"></p></form>`);
  const form = el("dialog-content").querySelector("form");
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const submitButton = form.querySelector("button[type=submit]");
    if (submitButton.disabled) return;
    const cancelButton = form.querySelector("[data-action='close-dialog']");
    const closeButton = el("dialog-close");
    const feedback = form.querySelector(".form-status");
    form.dataset.pending = "";
    submitButton.disabled = true;
    cancelButton.disabled = true;
    closeButton.disabled = true;
    const label = submitButton.textContent;
    submitButton.textContent = "正在处理…";
    feedback.classList.remove("error");
    feedback.textContent = "";
    try {
      await action(new FormData(form));
      if (form.isConnected) el("dialog").close();
    } catch (failure) {
      feedback.classList.add("error");
      feedback.textContent = failure.message;
    } finally {
      delete form.dataset.pending;
      submitButton.disabled = false;
      cancelButton.disabled = false;
      if (form.isConnected) closeButton.disabled = false;
      submitButton.textContent = label;
    }
  });
}
const itemWithId = id => selectedSpace()?.items?.find(item=>item.id===id);
function taskText(values) {
  const text = values.get("text").trim();
  if (!text) throw new Error("请输入待办内容。");
  return text;
}
function compareConflict() {
  if (!conflict) return;
  const summary = document => document.spaces.map(space => `<div class="version-space"><strong>${escape(space.name)}</strong>${
    (space.items ?? []).map(item => `<p>${escape(item.title||item.kind)}</p>${category(item.kind)==="note"?`<small class="note">${
      escape(String(item.data?.text??"").slice(0,400))}</small>` : category(item.kind)==="todo" ? `<small class="note">${
      escape((item.data?.tasks??[]).map(task=>(task.completed?"✓ ":"○ ")+task.text).join("\n").slice(0,400))}</small>` : ""}`).join("")}</div>`).join("");
  showDialog("比较版本", `<div class="split conflict-versions">${surface("本机","","<div class='body'>"+summary(conflict.local)+"</div>")}${
    surface("中转","","<div class='body'>"+summary(conflict.remote.document)+"</div>")}</div>
    <div class="actions">${button("导出两份","conflict-export")}${button("使用中转","conflict-remote")}${button("保留本机","conflict-local",true)}</div>`);
}

function downloadBlob(blob, name) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(()=>URL.revokeObjectURL(url),30_000);
}
function transferMarkup() {
  if (!transfers.length) return "";
  const rows = transfers.map(job => {
    const busy = job.state === "running" || job.state === "verifying";
    const retryable = job.state === "cancelled" || job.state === "failed";
    const dismiss = button("关闭", "transfer-dismiss:" + job.id);
    const actions = job.state === "running" || job.state === "queued" ? button("取消", "transfer-cancel:" + job.id) :
      retryable ? button("重试", "transfer-retry:" + job.id, "secondary") + dismiss : job.state === "done" ? dismiss : "";
    const labels = {queued:"排队中",running:job.upload?"正在上传…":"正在下载…",verifying:"正在校验…",
      done:job.upload?"上传完成":"已交给浏览器下载",cancelled:"已取消",failed:job.error};
    const percent = transferPercent(job);
    const progress = !busy ? "" : `<progress aria-label="传输进度" max="100"${percent === null ? "" : ` value="${percent}"`}></progress>`;
    const tone = {done:"status-success",verifying:"status-warning",failed:"status-error"}[job.state] ?? "";
    return `<div class="setting-row transfer-job" id="transfer-${job.id}"><div class="transfer-body"><strong>${escape(job.name)}</strong>
      <small class="${tone}">${escape(labels[job.state])}<span class="transfer-percent">${busy && percent !== null ? " " + percent + "%" : ""}</span></small>
      ${progress}</div><div class="actions">${actions}</div></div>`;
  });
  return surface("传输队列", "", rows.join(""), "transfer");
}
function drawTransfers() {
  const queue = el("transfer-queue");
  if (queue) queue.innerHTML = transferMarkup();
}
function transferPercent(job) {
  return job.total > 0 ? Math.min(100, Math.round(100 * job.loaded / job.total)) : null;
}
function drawTransferProgress(job) {
  const row = el("transfer-" + job.id);
  const progress = row?.querySelector("progress");
  const percent = transferPercent(job);
  if (!progress || percent === null) return;
  progress.value = percent;
  row.querySelector(".transfer-percent").textContent = " " + percent + "%";
}
function enqueueTransfer(spec) {
  transfers.push({...spec, id:uuid(), state:"queued", loaded:0});
  advanceTransfers();
}
function advanceTransfers() {
  const next = !activeTransfer && device && transfers.find(job => job.state === "queued");
  if (next) startTransfer(next);
  else drawTransfers();
}
function startTransfer(job) {
  const identity = device;
  const xhr = new XMLHttpRequest();
  activeTransfer = job;
  job.state = "running";
  job.xhr = xhr;
  job.loaded = 0;
  xhr.open(job.upload ? "POST" : "GET", "/v1/" + job.path);
  xhr.setRequestHeader(versionHeader, clientVersion);
  xhr.setRequestHeader("Authorization", "Bearer " + identity.token);
  if (job.upload) xhr.setRequestHeader("Content-Type","application/octet-stream");
  else xhr.responseType = "blob";
  xhr.timeout = 180_000;
  const update = event => {
    job.loaded = event.loaded;
    if (event.lengthComputable) job.total = event.total;
    if (activeTransfer === job) drawTransferProgress(job);
  };
  (job.upload?xhr.upload:xhr).addEventListener("progress", update);
  function finish(state, error) {
    if (activeTransfer !== job || device !== identity) return;
    job.state = state;
    job.error = error;
    if (state === "verifying") { drawTransfers(); return; }
    job.xhr = null;
    if (state === "done") job.file = null;
    activeTransfer = null;
    advanceTransfers();
  }
  xhr.addEventListener("abort",()=>finish("cancelled"));
  xhr.addEventListener("timeout",()=>finish("failed","传输超时。"));
  xhr.addEventListener("error",()=>finish("failed","中转连接中断。"));
  xhr.addEventListener("load",async () => {
    if (device !== identity || activeTransfer !== job) return;
    if (xhr.status===401) { dropDevice(); report("配对已失效，请重新连接。",true); return; }
    if (xhr.status<200||xhr.status>=300) {
      finish("failed",xhr.status===413 ? "文件超过 128 MiB。" : "传输失败（"+xhr.status+"）。");
      return;
    }
    try {
      if (!job.upload) {
        finish("verifying");
        const blob = xhr.response;
        if (Number.isFinite(job.size) && job.size!==blob.size) throw new Error("文件大小校验失败。");
        if (!crypto.subtle) throw new Error("请使用 HTTPS 校验并下载文件。");
        const digest = await crypto.subtle.digest("SHA-256",await blob.arrayBuffer());
        const hash = Array.from(new Uint8Array(digest),value=>value.toString(16).padStart(2,"0")).join("");
        if (hash!==job.sha.toLowerCase()) throw new Error("文件校验失败。");
        if (device !== identity || activeTransfer !== job) return;
        downloadBlob(blob, job.name);
      }
      finish("done");
      if (job.upload && page==="files") await open("files");
    } catch (error) { finish("failed",error.message); }
  });
  drawTransfers();
  try { xhr.send(job.upload ? job.file : null); }
  catch (failure) { finish("failed", failure.message); }
}

function search() {
  const options = [
    ...spacesOptions(), ...toolOptions(), {label:pages.settings[0], page:"settings", scope:"设置"},
    ...(workspace?.document?.spaces ?? []).flatMap(space => componentOptions(space, true))
  ];
  showPicker("快速查找", "搜索空间、工具和组件", options, "search");
}

function componentOptions(space, showSpace = false) {
  return visibleComponents(space).map(item => ({
    label: item.title || componentLabel(item), scope: (showSpace ? space.name + " · " : "") + componentLabel(item),
    space: space.id, component: item.id
  }));
}

function showPicker(title, placeholder, options, mode = "picker") {
  showDialog(title, `<input id="search-query" type="search" placeholder="${escape(placeholder)}" aria-label="${escape(title)}"><div id="search-results" class="search-results"></div>`, mode);
  const query = el("search-query"), list = el("search-results");
  let focus = 0;
  function draw() {
    const word=query.value.trim().toLocaleLowerCase();
    const matches=options.filter(option=>(option.label + " " + option.scope).toLocaleLowerCase().includes(word));
    focus = Math.max(0,Math.min(focus,matches.length-1));
    list.innerHTML=matches.map((option,index)=>`<button class="nav-button" aria-pressed="${focus===index}" data-palette="true"
      ${option.page ? `data-page="${option.page}"` : option.space ?
        `data-space="${escape(option.space)}"${option.component ? ` data-locate="${escape(option.component)}"` : ""}` :
        `data-action="${option.action}"`}>
      <span>${escape(option.label)}</span><small>${escape(option.scope)}</small></button>`).join("")||empty("没有结果");
  }
  query.addEventListener("input",()=>{ focus=0; draw(); });
  query.addEventListener("keydown",event=>{
    if (event.key==="ArrowDown"||event.key==="ArrowUp") {
      event.preventDefault();
      focus+=event.key==="ArrowDown"?1:-1;
      draw();
      list.querySelector('[aria-pressed="true"]')?.scrollIntoView({block:"nearest"});
    }
    if (event.key==="Enter") { event.preventDefault(); list.querySelector('[aria-pressed="true"]')?.click(); }
  });
  draw();
  query.focus();
}

function highlightComponent(id) {
  const tile = document.querySelector(`[data-component-id="${CSS.escape(id)}"]`);
  if (!tile) return;
  document.querySelector(".component-located")?.classList.remove("component-located");
  tile.classList.add("component-located");
  tile.focus({preventScroll:true});
  tile.scrollIntoView({block:"nearest", inline:"nearest"});
}

async function perform(operation, argument) {
  switch (operation) {
    case "sidebar": collapsed = !collapsed; render(); return;
    case "search": search(); return;
    case "refresh": return open(page);
    case "locate": return showPicker("定位组件", "搜索当前空间的组件", componentOptions(selectedSpace()));
    case "choose-space": return showPicker("切换空间", "搜索空间", [
      ...spacesOptions(), {label:"新建空间", scope:"操作", action:"new-space"}
    ]);
    case "choose-tool": return showPicker("工具", "搜索工具", toolOptions());
    case "new-space": return modal("新建空间", "", input("名称"), "创建", async values => {
      const id = uuid(), name = values.get("text").trim();
      if (!name) throw new Error("请输入空间名称。");
      await writeWorkspace(document => {
        document.spaces.push({ id, name, items: [] });
        if (!document.activeSpaceId) document.activeSpaceId = id;
      });
      selected = id;
      if (page === "workspace") render();
      else await open("workspace");
    });
    case "rename-space": return modal("重命名空间", "", input("名称", selectedSpace().name), "保存", values =>
      writeWorkspace((_, space) => { space.name = values.get("text").trim() || space.name; }));
    case "add-item": {
      const options = componentKinds.map(([key,label])=>`<option value="${key}">${label}</option>`).join("");
      return modal("添加组件", "", input("名称") + field("类型", '<select name="kind">'+options+'</select>'), "添加", values =>
        writeWorkspace((_, space) => {
          const kind = values.get("kind");
          (space.items ??= []).push({ id:uuid(),kind,title:values.get("text").trim(),x:0,y:0,width:320,height:270,
            data:kind==="note"?{text:""}:kind==="todo"?{tasks:[]}:{strokes:[]} });
        }));
    }
    case "remove-item": return modal("移除组件", itemWithId(argument)?.title, "", "移除组件", () =>
      writeWorkspace((_,space)=>{space.items=space.items.filter(item=>item.id!==argument);}), true);
    case "edit": return modal("编辑便签", "", textarea("正文",itemWithId(argument).data?.text,false), "保存", values =>
      writeWorkspace((_, space) => { (space.items.find(item => item.id === argument).data ??= {}).text = values.get("text"); }));
    case "add-task": return modal("新增待办", "", input("内容", "", 500), "添加", values => {
      const text = taskText(values);
      return writeWorkspace((_, space) => {
        const item = space.items.find(item => item.id === argument);
        ((item.data ??= {}).tasks ??= []).push({ id:uuid(),text,completed:false });
      });
    });
    case "edit-task":
    case "delete-task": {
      const [itemId, taskId] = argument.split("/");
      const task = itemWithId(itemId)?.data?.tasks?.find(entry=>entry.id===taskId);
      if (!task) return;
      if (operation === "delete-task") return modal("删除待办？", task.text, "", "删除待办", () =>
        writeWorkspace((_,space) => {
          const data=space.items.find(item=>item.id===itemId).data;
          data.tasks=data.tasks.filter(entry=>entry.id!==taskId);
        }), true);
      return modal("编辑待办", "", input("内容", task.text, 500), "保存", values => {
        const text = taskText(values);
        return writeWorkspace((_,space) => {
          space.items.find(item=>item.id===itemId).data.tasks.find(entry=>entry.id===taskId).text=text;
        });
      });
    }
    case "board-export": exportWorkspaceBoard(argument); return;
    case "compare-conflict": compareConflict(); return;
    case "conflict-export":
      downloadBlob(new Blob([JSON.stringify(conflict,null,2)],{type:"application/json"}),"workspace-conflict.json");
      return;
    case "conflict-remote":
      acceptWorkspace(conflict.remote); await clearConflict(device); el("dialog").close(); render(); return;
    case "conflict-local":
      return modal("保留本机", "使用本机版本替换中转工作区？", "", "确认", async () => {
        const identity = device;
        try {
          const value = await (await request("workspace", {method:"PUT", body:{baseRevision:conflict.remote.revision,document:conflict.local}})).json();
          await clearConflict(identity);
          if (device === identity) { acceptWorkspace(value); render(); }
        } catch (error) {
          if (error instanceof WorkspaceConflict && device === identity) {
            const saved = await saveConflict({ ...conflict, remote:error.snapshot }, identity);
            compareConflict();
            report(saved ? "中转版本再次更新。" : "无法保存最新冲突，请在刷新前导出。",true);
          } else throw error;
        }
      });
    case "upload": return modal("上传文件", "",
      field("文件", '<input name="file" type="file" required>') +
      field("目录", `<input name="folder" maxlength="800" value="${escape(fileFolder==="/"?"":fileFolder)}">`), "开始上传", values => {
        const file = values.get("file");
        if (file.size > 128 * 1048576) throw new Error("文件超过 128 MiB。");
        const query = new URLSearchParams({ name:file.name, folder:values.get("folder") });
        enqueueTransfer({upload:true,file,name:file.name,path:"files?"+query,total:file.size});
      });
    case "download": {
      const file=files.find(item=>item.id===argument);
      if (file) enqueueTransfer({upload:false,path:"files/"+encodeURIComponent(file.id),name:file.name,size:file.size,total:file.size,sha:file.sha256});
      return;
    }
    case "transfer-cancel":
    case "transfer-retry":
    case "transfer-dismiss": {
      const index = transfers.findIndex(job => job.id === argument);
      if (index < 0) return;
      const job = transfers[index];
      const retryable = job.state === "cancelled" || job.state === "failed";
      if (operation === "transfer-cancel" && job.state === "running") job.xhr?.abort();
      else if (operation === "transfer-cancel" && job.state === "queued") {
        job.state = "cancelled";
        drawTransfers();
      } else if (operation === "transfer-retry" && retryable) {
        transfers.splice(index, 1);
        job.state = "queued";
        job.loaded = 0;
        transfers.push(job);
        advanceTransfers();
      } else if (operation === "transfer-dismiss" && (retryable || job.state === "done")) {
        transfers.splice(index, 1);
        drawTransfers();
      }
      return;
    }
    case "remove-file": return modal("删除共享文件", files.find(file => file.id === argument)?.name, "", "删除文件", async () => {
      await request("files/" + encodeURIComponent(argument), { method:"DELETE" });
      await open("files");
    }, true);
    case "file-name":
    case "file-checksum": {
      const file = files.find(file => file.id === argument);
      const value = operation === "file-name" ? file?.name : file?.sha256;
      if (value) await copyText(value);
      return;
    }
    case "verify-file": {
      const file = files.find(entry=>entry.id===argument);
      if (!file?.sha256) return;
      utilityDraft.expected = file.sha256;
      utilitySection = "checksum";
      await open("text-tools");
      el("checksum-choose")?.focus({preventScroll:true});
      return;
    }
    case "send-clipboard": return composeClipboard();
    case "edit-clip": {
      const text = clipboardText(argument);
      if (text != null) composeClipboard(text);
      return;
    }
    case "transform-clip": {
      const text = clipboardText(argument);
      if (text == null) return;
      textDraft = text;
      utilitySection = "convert";
      await open("text-tools");
      el("text-tool-input")?.focus({preventScroll:true});
      return;
    }
    case "clip-select":
      clipSelection=argument; refreshClips();
      if (matchMedia("(max-width: 700px)").matches) document.querySelector(".clip-inspector")?.scrollIntoView({ block:"start" });
      return;
    case "clip-pin": return setClipPin(argument, !pinnedClips.has(argument) && !clipPreferences().pinned.includes(argument));
    case "clip-hide": return setClipPin(argument, false, true);
    case "copy": {
      const text = clipboardText(argument);
      if (text != null) await copyText(text);
      return;
    }
    case "disconnect": return modal("断开此浏览器配对？", "", "", "断开配对", async () => {
      await request("devices/" + encodeURIComponent(device.id), { method:"DELETE" });
      dropDevice();
    }, true);
  }
}

el("dialog-close").addEventListener("click", closeUserDialog);
el("dialog").addEventListener("cancel", event => { if (dialogPending()) event.preventDefault(); });
document.body.addEventListener("click", async event => {
  const control = event.target.closest("button");
  if (control?.dataset.menu && !control.disabled) { showActionMenu(control); return; }
  closeActionMenu(!!control && el("action-menu").contains(control));
  if (!control || control.disabled) return;
  if (control.dataset.palette) el("dialog").close();
  try {
    if (control.dataset.action === "close-dialog") closeUserDialog();
    else if (control.dataset.page) await open(control.dataset.page);
    else if (control.dataset.space) {
      const next = control.dataset.space, component = control.dataset.locate;
      if (page !== "workspace" || loading) {
        selected = next;
        await open("workspace");
      } else if (selected !== next) {
        selected = next;
        render();
        window.scrollTo(0, 0);
      }
      if (component && page === "workspace" && selected === next) highlightComponent(component);
    } else if (control.dataset.action) {
      const [operation,...arguments_] = control.dataset.action.split(":");
      await perform(operation,arguments_.join(":"));
    }
  } catch (failure) { report(failure.message,true); }
});
document.body.addEventListener("input", event => {
  const control = event.target;
  if (control.id==="file-query") { fileQuery=control.value; el("file-rows").innerHTML=fileRows(); }
  if (control.id==="clip-query") { clipQuery=control.value; refreshClips(); }
});
document.body.addEventListener("change", async event => {
  const control = event.target;
  if (control.id==="file-folder") { fileFolder=control.value; el("file-rows").innerHTML=fileRows(); }
  else if (control.id==="clip-source") { clipSource=control.value; refreshClips(); }
  else if (control.dataset.task) {
    if (control.hasAttribute("aria-disabled")) { control.checked = !control.checked; return; }
    const checked = control.checked;
    control.setAttribute("aria-disabled", "true");
    try {
      await writeWorkspace((_, space) => {
        const task = space.items.find(item => item.id === control.dataset.item).data.tasks.find(task => task.id === control.dataset.task);
        task.completed = checked;
      });
    } catch (failure) {
      control.checked = !checked;
      report(failure.message,true);
    } finally { control.removeAttribute("aria-disabled"); }
  }
});
document.addEventListener("keydown",event=>{
  if (!device) return;
  const menu = el("action-menu");
  if (menuTrigger) {
    if (event.key === "Escape" || event.key === "Tab") {
      closeActionMenu(true);
      if (event.key === "Escape") event.preventDefault();
      return;
    }
    if (menu.contains(event.target) && ["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
      const items = [...menu.children], current = items.indexOf(event.target);
      const next = event.key === "Home" ? 0 : event.key === "End" ? items.length - 1 :
        (current + (event.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
      items[next].focus({preventScroll:true});
      items[next].scrollIntoView({block:"nearest"});
      event.preventDefault();
      return;
    }
  } else if (event.target?.dataset?.menu && ["ArrowDown", "ArrowUp"].includes(event.key)) {
    showActionMenu(event.target, event.key === "ArrowUp");
    event.preventDefault();
    return;
  }
  if (!(event.ctrlKey || event.metaKey)) return;
  if (event.key.toLowerCase()==="k") {
    event.preventDefault();
    if (el("dialog").open) {
      if (el("dialog").dataset.mode === "search") closeUserDialog();
    }
    else { closeActionMenu(true); search(); }
  }
});
window.addEventListener("resize", () => closeActionMenu(true));
document.addEventListener("scroll", event => {
  if (event.target !== el("action-menu")) closeActionMenu(true);
}, true);
if (device?.token && device?.id) restoreWorkspace();
else { setDevice(null); render(); }
