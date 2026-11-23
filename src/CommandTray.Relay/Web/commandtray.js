"use strict";

const pages = {
  workspace: ["工作区", "M4 4h16v16H4z M4 9h16 M10 9v11"],
  files: ["共享文件", "M3 6h6l2 2h10v11H3z"],
  clipboard: ["剪贴板", "M8 5H5v15h14V5h-3 M8 4h8v4H8z M8 12h8 M8 16h6"],
  settings: ["设置", "M12 4v2m0 12v2M4 12h2m12 0h2M6 6l2 2m8 8l2 2M6 18l2-2m8-8l2-2m-2 4a4 4 0 1 1-8 0 4 4 0 0 1 8 0z"]
};
const el = id => document.getElementById(id);
const escape = text => String(text ?? "").replace(/[&<>"']/g, char => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
})[char]);
const svg = path => '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" ' +
  'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="' + path + '"/></svg>';
const button = (label, operation, primary = false) =>
  '<button type="button" data-action="' + escape(operation) + '"' + (primary ? ' class="primary"' : '') + '>' +
  escape(label) + '</button>';
const heading = (label, actions = "") =>
  '<div class="heading"><h1>' + escape(label) + '</h1><div class="actions">' + actions + '</div></div>';
const surface = (label, actions, body) => '<section class="surface"><header class="bar"><h3>' + escape(label) +
  '</h3><div class="actions">' + actions + '</div></header>' + body + '</section>';
const localTime = value => {
  const moment = new Date(value);
  return value && Number.isFinite(moment.valueOf()) ? moment.toLocaleString() : "";
};

const sessionKey = "commandtray.web.device";
let device = null;
try { device = JSON.parse(sessionStorage.getItem(sessionKey)); } catch {}
let page = "workspace";
let workspace = null;
let selected = null;
let files = [];
let history = [];
let loading = false;
let navigation = 0;
let feedbackTimer;

function setDevice(value) {
  device = value;
  try {
    if (value) sessionStorage.setItem(sessionKey, JSON.stringify(value));
    else sessionStorage.removeItem(sessionKey);
  } catch {}
}

async function request(path, { method = "GET", body, file, anonymous = false } = {}) {
  const headers = new Headers();
  if (!anonymous && device) headers.set("Authorization", "Bearer " + device.token);
  if (body !== undefined) headers.set("Content-Type", "application/json");
  if (file) headers.set("Content-Type", "application/octet-stream");
  const reply = await fetch("/v1/" + path, {
    method, headers, body: file ?? (body === undefined ? undefined : JSON.stringify(body)),
    cache: "no-store", signal: AbortSignal.timeout(file ? 180_000 : 30_000)
  });
  if (reply.ok) return reply;
  if (reply.status === 401) {
    if (anonymous) throw new Error("配对码不正确。");
    ++navigation;
    setDevice(null);
    workspace = null;
    selected = null;
    el("dialog").close();
    render();
    throw new Error("配对已失效，请重新连接。");
  }
  if (reply.status === 409 && path === "workspace")
    throw new Error("空间已在另一台设备修改。请保留当前输入，刷新后再提交。");
  const error = await reply.json().catch(() => null);
  throw new Error(error?.error ?? "中转请求失败（" + reply.status + "）。");
}

function report(message, error = false, persistent = false) {
  clearTimeout(feedbackTimer);
  const status = el("status");
  status.textContent = message;
  status.classList.toggle("error", error);
  status.hidden = !message;
  if (message && !persistent) feedbackTimer = setTimeout(() => status.hidden = true, 3400);
}

function selectedSpace() {
  return workspace?.document?.spaces?.find(space => space.id === selected);
}

function navLink(id) {
  return '<button class="nav-button" type="button" data-page="' + id + '"' +
    (page === id ? ' aria-current="page"' : '') + ">" + svg(pages[id][1]) + "<span>" + pages[id][0] + "</span></button>";
}

function render() {
  if (!device) {
    el("top").replaceChildren();
    el("sidebar").replaceChildren();
    el("content").innerHTML = '<div class="login"><h1>连接中转服务器</h1><form id="pair" class="surface">' +
      '<label class="field">设备名称<input name="name" value="Web 浏览器" maxlength="120" required autocomplete="off"></label>' +
      '<label class="field">配对码<input name="code" type="password" minlength="16" maxlength="512" required autocomplete="off"></label>' +
      '<button class="primary" type="submit">配对</button><p class="form-status" role="status"></p></form></div>';
    el("pair").addEventListener("submit", pair);
    return;
  }
  el("top").innerHTML = "<span>" + pages[page][0] + "</span>" +
    (page === "workspace" && selectedSpace() ? '<span class="muted">/</span><strong>' +
      escape(selectedSpace().name) + "</strong>" : "");
  const spaces = (workspace?.document?.spaces ?? []).map(space => '<button type="button" class="nav-button" data-space="' +
    escape(space.id) + '" aria-pressed="' + (space.id === selected && page === "workspace") + '">' +
    svg(pages.workspace[1]) + "<span>" + escape(space.name) + "</span></button>").join("");
  el("sidebar").innerHTML = navLink("workspace") + navLink("files") + navLink("clipboard") +
    (spaces ? '<div class="group"><small>空间</small>' + spaces + "</div>" : "") +
    '<div class="nav-footer"><small class="paired">中转已连接</small>' + navLink("settings") + "</div>";
  el("content").innerHTML = loading ? heading(pages[page][0]) + '<p class="muted" role="status">正在加载…</p>' :
    ({ workspace: renderWorkspace, files: renderFiles, clipboard: renderClipboard, settings: renderSettings }[page])();
  if (!loading && page === "workspace") drawBoards();
}

async function pair(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const submit = form.querySelector("button");
  if (submit.disabled) return;
  const values = new FormData(form);
  submit.disabled = true;
  const status = form.querySelector(".form-status");
  status.classList.remove("error");
  status.textContent = "正在配对…";
  try {
    const name = values.get("name").trim();
    const reply = await request("devices/pair", { method: "POST", anonymous: true,
      body: { deviceName: name, pairingCode: values.get("code") } });
    const paired = await reply.json();
    if (!paired.deviceId || !paired.token) throw new Error("中转未返回有效凭据。");
    setDevice({ id: paired.deviceId, token: paired.token, name });
    await open("workspace");
  } catch (failure) {
    status.classList.add("error");
    status.textContent = failure.message;
  } finally { submit.disabled = false; }
}

async function open(target) {
  const operation = ++navigation;
  page = target;
  loading = true;
  render();
  try {
    if (target === "workspace") {
      const snapshot = await (await request("workspace")).json();
      if (operation !== navigation) return;
      acceptWorkspace(snapshot);
    } else if (target === "files") {
      const result = await (await request("files")).json();
      if (operation !== navigation) return;
      files = result.files;
    } else if (target === "clipboard") {
      const result = await (await request("clipboard/history")).json();
      if (operation !== navigation) return;
      history = result.entries;
    }
  } catch (failure) {
    if (operation === navigation || !device) report(failure.message, true);
  } finally {
    if (operation === navigation) {
      loading = false;
      render();
    }
  }
}

function acceptWorkspace(snapshot) {
  workspace = snapshot;
  const { spaces, activeSpaceId } = workspace.document;
  if (!spaces.some(space => space.id === selected))
    selected = spaces.find(space => space.id === activeSpaceId)?.id ?? spaces[0]?.id ?? null;
}

function renderWorkspace() {
  if (!workspace) return heading("工作区", button("刷新", "refresh"));
  const space = selectedSpace();
  const switcher = !space ? "" : '<select id="space-picker" aria-label="切换空间">' + workspace.document.spaces.map(item =>
    '<option value="' + escape(item.id) + '"' + (item.id === selected ? " selected" : "") + ">" +
    escape(item.name) + "</option>").join("") + "</select>";
  const tools = button("刷新", "refresh") + button("新建空间", "new-space", !space) +
    (space ? button("重命名", "rename-space") + button("添加组件", "add-item", true) : "");
  const header = heading(space?.name ?? "工作区", switcher + tools);
  if (!space) return header + '<p class="muted">暂无空间</p>';
  const tiles = (space.items ?? []).map(item => {
    const data = item.data ?? {};
    const name = item.title || ({ note: "便签", notes: "便签", todo: "待办", tasks: "待办", board: "画板", whiteboard: "画板" })[item.kind] || item.kind;
    if (["note", "notes"].includes(item.kind))
      return surface(name, button("编辑", "edit:" + item.id), '<div class="body note">' + escape(data.text || "空便签") + "</div>");
    if (["todo", "tasks"].includes(item.kind)) {
      const tasks = (data.tasks ?? []).map(task => '<label class="task"><input type="checkbox" data-item="' + escape(item.id) +
        '" data-task="' + escape(task.id) + '"' + (task.completed ? " checked" : "") + "><span>" + escape(task.text) + "</span></label>").join("");
      return surface(name, button("新增待办", "add-task:" + item.id), '<div class="body">' +
        (tasks || '<p class="muted">暂无待办</p>') + "</div>");
    }
    if (["board", "whiteboard"].includes(item.kind))
      return surface(name, "", '<canvas class="board" width="640" height="300" data-board="' + escape(item.id) +
        '" aria-label="' + escape(name) + '"></canvas>');
    return "";
  }).join("");
  return header + (tiles ? '<div class="workbench">' + tiles + '</div>' : '<p class="muted">暂无组件</p>');
}

function drawBoards() {
  document.querySelectorAll("[data-board]").forEach(canvas => {
    const board = selectedSpace()?.items?.find(item => item.id === canvas.dataset.board);
    const strokes = Array.isArray(board?.data?.strokes) ? board.data.strokes : [];
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#101923";
    ctx.fillRect(0, 0, 640, 300);
    ctx.fillStyle = "#2c3948";
    for (let x = 9; x < 640; x += 16) for (let y = 9; y < 300; y += 16) ctx.fillRect(x, y, 1, 1);
    const points = stroke => Array.isArray(stroke?.points) ?
      stroke.points.filter(p => p && Number.isFinite(p.x) && Number.isFinite(p.y)) : [];
    let maxX = 640, maxY = 300;
    for (const stroke of strokes) for (const p of points(stroke)) { maxX = Math.max(maxX,p.x); maxY = Math.max(maxY,p.y); }
    const scale = Math.min(620 / maxX, 280 / maxY);
    ctx.translate((640 - maxX * scale) / 2, (300 - maxY * scale) / 2);
    ctx.scale(scale, scale);
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    for (const stroke of strokes) {
      const series = points(stroke);
      if (!series.length) continue;
      ctx.beginPath();
      ctx.lineWidth = stroke.erase ? 6 : 2.5;
      ctx.strokeStyle = stroke.erase ? "#101923" :
        (/^#[0-9a-f]{6}$/i.test(stroke.color) && stroke.color !== "#315545" ? stroke.color : "#c1ed8b");
      ctx.moveTo(series[0].x, series[0].y);
      if (series.length === 1) ctx.lineTo(series[0].x + .25, series[0].y);
      else for (const point of series.slice(1)) ctx.lineTo(point.x, point.y);
      ctx.stroke();
    }
  });
}

async function writeWorkspace(change) {
  const { revision, document: current } = workspace;
  const updated = structuredClone(current);
  change(updated, updated.spaces.find(space => space.id === selected));
  const reply = await request("workspace", { method: "PUT", body: { baseRevision: revision, document: updated } });
  acceptWorkspace(await reply.json());
  render();
}

function renderFiles() {
  const header = heading("共享文件", button("刷新", "refresh") + button("上传文件", "upload", true));
  if (!files.length) return header + '<p class="muted">暂无共享文件</p>';
  const rows = files.map(file => "<tr><td>" + escape(file.name) + "</td><td>" + escape(file.folder || "—") +
    "</td><td>" + new Intl.NumberFormat(undefined).format(file.size) + " B</td><td>" + escape(localTime(file.modifiedAt)) +
    '</td><td><div class="actions">' + button("下载", "download:" + file.id) + button("删除", "remove-file:" + file.id) +
    "</div></td></tr>").join("");
  return header + '<div class="surface table-wrap"><table><thead><tr><th>名称</th><th>目录</th><th>大小</th><th>更新时间</th>' +
    "<th>操作</th></tr></thead><tbody>" + rows + "</tbody></table></div>";
}

function renderClipboard() {
  const header = heading("剪贴板", button("刷新", "refresh") + button("发送文本", "send-clipboard", true));
  if (!history.length) return header + '<p class="muted">暂无同步文本</p>';
  return header + '<div class="stack">' + history.map((entry,index) => '<section class="surface body clipboard-row"><div>' +
    '<button type="button" data-action="view-clipboard:' + index + '"><span class="clip-preview">' +
    escape(entry.text.slice(0, 280)) + (entry.text.length > 280 ? "…" : "") + "</span></button><small>" +
    escape((entry.deviceName || "未知设备") + " · " + localTime(entry.updatedAt)) + "</small></div>" +
    button("复制", "copy:" + index) + "</section>").join("") + "</div>";
}

function renderSettings() {
  return heading("设置") + surface("中转服务器", button("断开配对", "disconnect"),
    '<div class="body"><strong>' + escape(device.name) + "</strong><br><small>" + escape(location.host) + "</small></div>");
}

function modal(title, description, fields, submit, action) {
  const dialog = el("dialog");
  el("dialog-title").textContent = title;
  el("dialog-content").innerHTML = '<form>' + (description ? "<p>" + escape(description) + "</p>" : "") + fields +
    '<button type="submit" class="primary">' + escape(submit) + '</button><p class="form-status" role="status"></p></form>';
  const form = el("dialog-content").querySelector("form");
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const button = form.querySelector("button[type=submit]");
    if (button.disabled) return;
    const status = form.querySelector(".form-status");
    status.textContent = "正在处理…";
    status.classList.remove("error");
    button.disabled = true;
    try {
      await action(new FormData(form));
      if (form.isConnected && dialog.open) dialog.close();
    } catch (failure) {
      status.textContent = failure.message;
      status.classList.add("error");
    } finally { button.disabled = false; }
  });
  if (!dialog.open) dialog.showModal();
}

const field = (label, control) => '<label class="field">' + escape(label) + control + "</label>";
const input = (label, value = "", limit = 128) =>
  field(label, '<input name="text" value="' + escape(value) + '" maxlength="' + limit + '" required>');
const textarea = (label, value = "") =>
  field(label, '<textarea name="text" maxlength="1000000" required>' + escape(value) + "</textarea>");
const uuid = () => {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
};
const itemWithId = id => selectedSpace()?.items?.find(item => item.id === id);

async function perform(operation, argument, control) {
  switch (operation) {
    case "refresh": return open(page);
    case "new-space": return modal("新建空间", "", input("名称"), "创建", values =>
      writeWorkspace(document => {
        const id = uuid();
        document.spaces.push({ id, name: values.get("text").trim(), items: [] });
        if (!document.activeSpaceId) document.activeSpaceId = id;
      }));
    case "rename-space": return modal("重命名空间", "", input("名称", selectedSpace().name), "保存", values =>
      writeWorkspace((_, space) => space.name = values.get("text").trim()));
    case "add-item": return modal("添加组件", "", input("名称") + field("类型",
      '<select name="kind"><option value="note">便签</option><option value="todo">待办</option></select>'), "添加", values =>
      writeWorkspace((_, space) => {
        const kind = values.get("kind");
        (space.items ??= []).push({ id: uuid(), kind, title: values.get("text").trim(), x: 0, y: 0,
          width: 320, height: 240, data: kind === "note" ? { text: "" } : { tasks: [] } });
      }));
    case "edit": return modal("编辑便签", "", textarea("正文", itemWithId(argument).data?.text), "保存", values =>
      writeWorkspace((_, space) => (space.items.find(item => item.id === argument).data ??= {}).text = values.get("text")));
    case "add-task": return modal("新增待办", "", input("内容", "", 500), "添加", values =>
      writeWorkspace((_, space) => {
        const item = space.items.find(item => item.id === argument);
        ((item.data ??= {}).tasks ??= []).push({ id: uuid(), text: values.get("text").trim(), completed: false });
      }));
    case "upload": return modal("上传文件", "",
      field("文件", '<input name="file" type="file" required>') +
      field("目录", '<input name="folder" maxlength="800">'), "上传", async values => {
        const file = values.get("file");
        const query = new URLSearchParams({ name: file.name, folder: values.get("folder") });
        await request("files?" + query, { method: "POST", file });
        await open("files");
      });
    case "download": {
      const file = files.find(file => file.id === argument);
      control.disabled = true;
      report("正在下载…", false, true);
      try {
        const reply = await request("files/" + encodeURIComponent(argument));
        const url = URL.createObjectURL(await reply.blob());
        const link = document.createElement("a");
        link.href = url;
        link.download = file?.name || "CommandTray";
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 10_000);
        report("已交给浏览器下载");
      } finally { control.disabled = false; }
      return;
    }
    case "remove-file": return modal("删除共享文件", files.find(file => file.id === argument)?.name,
      "", "删除", async () => {
        await request("files/" + encodeURIComponent(argument), { method: "DELETE" });
        await open("files");
      });
    case "send-clipboard": return modal("发送文本", "", textarea("内容"), "发送", async values => {
      await request("clipboard", { method: "PUT", body: { text: values.get("text") } });
      await open("clipboard");
    });
    case "view-clipboard": {
      const entry = history[Number(argument)];
      el("dialog-title").textContent = entry.deviceName || "剪贴板";
      el("dialog-content").innerHTML = '<small>' + escape(localTime(entry.updatedAt)) + '</small><p class="note">' +
        escape(entry.text) + "</p>" + button("复制", "copy:" + argument);
      el("dialog").showModal();
      return;
    }
    case "copy":
      if (!navigator.clipboard) throw new Error("浏览器需要 HTTPS 才能直接复制。");
      await navigator.clipboard.writeText(history[Number(argument)].text);
      report("已复制");
      return;
    case "disconnect": return modal("断开此浏览器配对？", "", "", "断开配对", async () => {
      await request("devices/" + encodeURIComponent(device.id), { method: "DELETE" });
      ++navigation;
      setDevice(null);
      workspace = null;
      selected = null;
      files = [];
      history = [];
      render();
    });
  }
}

el("dialog-close").addEventListener("click", () => el("dialog").close());
document.body.addEventListener("click", async event => {
  const control = event.target.closest("button");
  if (!control || control.disabled) return;
  try {
    if (control.dataset.page) await open(control.dataset.page);
    else if (control.dataset.space) {
      selected = control.dataset.space;
      await open("workspace");
    } else if (control.dataset.action) {
      const [operation, argument] = control.dataset.action.split(":");
      await perform(operation, argument, control);
    }
  } catch (failure) { report(failure.message, true); }
});
document.body.addEventListener("change", async event => {
  const control = event.target;
  if (control.id === "space-picker") {
    selected = control.value;
    render();
  } else if (control.dataset.task) {
    control.disabled = true;
    try {
      await writeWorkspace((_, space) => {
        const task = space.items.find(item => item.id === control.dataset.item).data.tasks.find(task => task.id === control.dataset.task);
        task.completed = control.checked;
      });
    } catch (failure) {
      control.checked = !control.checked;
      report(failure.message, true);
    } finally { control.disabled = false; }
  }
});

if (device?.token && device?.id) open("workspace");
else { setDevice(null); render(); }
