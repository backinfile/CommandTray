"use strict";

const boardTools = [
  ["select","选择","M4 3l7 18 2-8 8-2z"], ["pen","画笔","M4 19l1-5L16 3l5 5-11 11zm10-14 5 5"],
  ["line","直线","M5 19 19 5"], ["arrow","箭头","M5 19 19 5m-7 0h7v7"],
  ["rectangle","矩形","M4 5h16v14H4z"], ["ellipse","椭圆","M21 12a9 6 0 1 1-18 0 9 6 0 0 1 18 0"],
  ["text","文字","M5 5h14M12 5v15m-4 0h8"], ["erase","橡皮","M3 14 12 4l8 7-8 9H8z M8 9l8 7m-3 4h8"]
];
const boardStates = new Map();
let boardObservers = [];

function boardControl(label, icon, operation) {
  return `<button type="button" class="board-button" data-board-action="${operation}" title="${label}" aria-label="${label}">${svg(icon)}</button>`;
}
function boardMarkup(id, name) {
  return `<div class="board-frame"><div class="board-tools" role="toolbar" aria-label="画板工具">${boardTools.map(([key,label,icon])=>
    boardControl(label,icon,key)).join("")}<span class="board-divider"></span>
    ${boardControl("撤销","M8 5l-5 5 5 5m-4-5h11a5 5 0 0 1 5 5v3","undo")}
    ${boardControl("重做","m16 5 5 5-5 5m4-5H9a5 5 0 0 0-5 5v3","redo")}
    <input class="board-ink" type="color" value="#c1ed8b" aria-label="画笔颜色" title="画笔颜色">
    <select class="board-width" aria-label="线宽"><option value="2">细线</option><option value="5">粗线</option></select>
    </div><canvas class="board" data-board="${escape(id)}" aria-label="${escape(name)}" tabindex="0"></canvas>
    <footer class="board-footer"><small class="board-count"></small><div class="actions">
      ${boardControl("缩小","M5 12h14","zoom-out")}<small class="board-zoom"></small>
      ${boardControl("放大","M5 12h14M12 5v14","zoom-in")}${boardControl("显示网格","M3 4h18v16H3z M3 9h18 M10 9v11","grid")}
    </div></footer></div>`;
}

function unmountWorkspaceBoards() {
  for (const observer of boardObservers) observer.disconnect();
  boardObservers = [];
}
function mountWorkspaceBoards() {
  for (const canvas of document.querySelectorAll("canvas[data-board]")) {
    const item = itemWithId(canvas.dataset.board);
    if (!item) continue;
    mountBoard(canvas,item);
  }
}
function mountBoard(canvas, item) {
  const frame = canvas.parentElement;
  const stored = Array.isArray(item.data?.strokes) ? item.data.strokes : [];
  const id = canvas.dataset.board;
  const key = (device?.id ?? "") + "/" + selected + "/" + id;
  const state = boardStates.get(key) ?? { tool:"select", zoom:1, grid:true, ink:"#c1ed8b", width:2, undo:[], redo:[], current:stored };
  boardStates.set(key,state);
  if (!state.busy && !same(state.current,stored)) {
    state.current=stored;
    state.undo=[];
    state.redo=[];
  }
  state.redraw = draw;
  const ink=frame.querySelector(".board-ink"), width=frame.querySelector(".board-width");
  ink.value=state.ink;
  width.value=String(state.width);
  let gesture, sheet, selectedShape;
  function draw() {
    if (!canvas.isConnected) return;
    const rect=canvas.getBoundingClientRect(), ratio=devicePixelRatio||1;
    const w=Math.round(rect.width*ratio), h=Math.round(rect.height*ratio);
    if (w<16||h<16) return;
    if (canvas.width!==w||canvas.height!==h) { canvas.width=w; canvas.height=h; }
    const ctx=canvas.getContext("2d");
    ctx.setTransform(ratio,0,0,ratio,0,0);
    ctx.fillStyle="#101923";
    ctx.fillRect(0,0,rect.width,rect.height);
    if (state.grid) {
      ctx.fillStyle="#2c3948";
      for(let x=8;x<rect.width;x+=16) for(let y=8;y<rect.height;y+=16) ctx.fillRect(x,y,1.2,1.2);
    }
    let left=0,top=0,right=640,bottom=300;
    for (const stroke of state.current) for (const point of boardPoints(stroke)) {
      left=Math.min(left,point.x);top=Math.min(top,point.y);right=Math.max(right,point.x);bottom=Math.max(bottom,point.y);
    }
    const scale=Math.min(4,(rect.width-18)/(right-left),(rect.height-18)/(bottom-top))*state.zoom;
    sheet={scale,left:(rect.width-(right-left)*scale)/2-left*scale,top:(rect.height-(bottom-top)*scale)/2-top*scale};
    ctx.translate(sheet.left,sheet.top);
    ctx.scale(scale,scale);
    ctx.lineCap="round";
    ctx.lineJoin="round";
    for (const stroke of state.current) paintStroke(ctx,stroke,visiblePoints(stroke),stroke===selectedShape);
    if (gesture?.draft) paintStroke(ctx,gesture.draft,boardPoints(gesture.draft),false);
    frame.querySelector(".board-count").textContent=state.busy?"正在保存…":state.current.length+" 个对象";
    frame.querySelector(".board-zoom").textContent=Math.round(state.zoom*100)+"%";
    frame.querySelector('[data-board-action="undo"]').disabled=state.busy||!state.undo.length;
    frame.querySelector('[data-board-action="redo"]').disabled=state.busy||!state.redo.length;
    frame.querySelector('[data-board-action="grid"]').setAttribute("aria-pressed",String(state.grid));
    canvas.classList.toggle("drawing",state.tool!=="select");
    for(const [tool] of boardTools)
      frame.querySelector('[data-board-action="'+tool+'"]').setAttribute("aria-pressed",String(state.tool===tool));
  }
  function visiblePoints(stroke) {
    let points = boardPoints(stroke);
    if (stroke.from&&stroke.to) {
      const first=state.current.find(item=>item.id===stroke.from);
      const second=state.current.find(item=>item.id===stroke.to);
      if (first&&second) {
        const a=box(shift(first,boardPoints(first))), b=box(shift(second,boardPoints(second)));
        points=arrowPoints(edge(a,b.center),edge(b,a.center));
      }
    }
    return shift(stroke,points);
  }
  function shift(stroke, points) {
    if (gesture?.moving!==stroke) return points;
    return points.map(point=>({x:point.x+gesture.offset.x,y:point.y+gesture.offset.y}));
  }
  function position(event) {
    const rect=canvas.getBoundingClientRect();
    return {x:Math.max(-100000,Math.min(100000,(event.clientX-rect.left-sheet.left)/sheet.scale)),
      y:Math.max(-100000,Math.min(100000,(event.clientY-rect.top-sheet.top)/sheet.scale))};
  }
  function hit(point) {
    const threshold=9/sheet.scale;
    return [...state.current].reverse().find(stroke=>{
      if (stroke.erase) return false;
      const points=visiblePoints(stroke);
      if (!points.length) return false;
      if (stroke.text) {
        const start=points[0];
        if (point.x>=start.x-threshold&&point.x<=start.x+stroke.text.length*13+threshold&&
          point.y>=start.y-threshold&&point.y<=start.y+21+threshold) return true;
      }
      for(let i=1;i<points.length;i++) if(distanceToLine(point,points[i-1],points[i])<=threshold) return true;
      const bounds=box(points);
      if (points.length>4 && points[0].x===points.at(-1).x && points[0].y===points.at(-1).y)
        return point.x>=bounds.left&&point.x<=bounds.right&&point.y>=bounds.top&&point.y<=bounds.bottom;
      return points.length===1&&Math.hypot(points[0].x-point.x,points[0].y-point.y)<=threshold;
    });
  }
  function stroke(points) { return {id:uuid(),color:state.ink,width:state.width,points}; }
  async function commit(next, direction = "edit") {
    if (state.busy||same(next,state.current)) return;
    const baseline=state.current;
    state.busy=true;
    draw();
    try {
      await writeWorkspace((_,space)=>{
        const board=space?.items.find(candidate=>candidate.id===id);
        if (!board || !same(board.data?.strokes ?? [],baseline)) throw new Error("画板已被修改，请刷新后重试。");
        (board.data??={}).strokes=next;
      },false);
      if (direction==="undo") { state.redo.push(baseline); state.undo.pop(); }
      else if (direction==="redo") { state.undo.push(baseline); state.redo.pop(); }
      else {
        state.undo.push(baseline);
        if (state.undo.length>30) state.undo.shift();
        state.redo=[];
      }
      state.current=next;
      selectedShape=null;
    } catch (error) { report(error.message,true); throw error; }
    finally { state.busy=false; state.redraw(); }
  }
  function run(next,direction) { commit(next,direction).catch(()=>{}); }

  frame.addEventListener("click",event=>{
    const control=event.target.closest("[data-board-action]");
    if(!control) return;
    const action=control.dataset.boardAction;
    if (boardTools.some(item=>item[0]===action)) { state.tool=action; selectedShape=null; }
    if (action==="zoom-out") state.zoom=Math.max(.6,state.zoom-.1);
    if (action==="zoom-in") state.zoom=Math.min(2,state.zoom+.1);
    if (action==="grid") state.grid=!state.grid;
    if (action==="undo"&&state.undo.length) run(state.undo.at(-1),"undo");
    if (action==="redo"&&state.redo.length) run(state.redo.at(-1),"redo");
    draw();
  });
  ink.addEventListener("input",()=>state.ink=ink.value);
  width.addEventListener("change",()=>state.width=Number(width.value));
  canvas.addEventListener("pointerdown",event=>{
    if (state.busy||gesture||!sheet||event.button!==0) return;
    const start=position(event), target=hit(start);
    if (state.tool==="erase") {
      if(target) run(state.current.filter(stroke=>stroke!==target));
      return;
    }
    if (state.tool==="text") {
      modal("添加文字","",input("文字"),"添加",values=>commit([...state.current,{...stroke([start]),text:values.get("text").trim()}]));
      return;
    }
    gesture={pointer:event.pointerId,start,offset:{x:0,y:0},tool:state.tool};
    if(state.tool==="select") gesture.moving=selectedShape=target;
    else {
      selectedShape=null;
      gesture.draft=stroke([start]);
      if(state.tool==="arrow") gesture.from=target;
    }
    canvas.setPointerCapture(event.pointerId);
    draw();
  });
  function move(event) {
    if(!gesture||gesture.pointer!==event.pointerId) return;
    const end=position(event), start=gesture.start;
    gesture.offset={x:end.x-start.x,y:end.y-start.y};
    if (!gesture.draft) return;
    if(gesture.tool==="pen") {
      if(gesture.draft.points.length<4096) gesture.draft.points.push(end);
    } else {
      if(event.shiftKey&&["line","arrow"].includes(gesture.tool)) {
        if(Math.abs(gesture.offset.x)>Math.abs(gesture.offset.y)) end.y=start.y;
        else end.x=start.x;
      }
      gesture.draft.points=shapePoints(gesture.tool,start,end);
    }
  }
  canvas.addEventListener("pointermove",event=>{move(event);draw();});
  canvas.addEventListener("pointerup",event=>{
    if(!gesture||gesture.pointer!==event.pointerId) return;
    move(event);
    const {draft,moving,offset,from}=gesture;
    if(draft&&from?.id) {
      const to=hit(position(event));
      if(to?.id&&from!==to) {draft.from=from.id;draft.to=to.id;}
    }
    const moved=moving && Math.hypot(offset.x,offset.y)>.5 ? visiblePoints(moving) : null;
    gesture=null;
    if(draft) run([...state.current,draft]);
    else if (moved) run(state.current.map(stroke=>{
      if (stroke!==moving) return stroke;
      const result={...stroke,points:moved};
      delete result.from;
      delete result.to;
      return result;
    }));
    draw();
  });
  function cancel(event) {
    if(gesture?.pointer===event.pointerId) {gesture=null;draw();}
  }
  canvas.addEventListener("pointercancel",cancel);
  canvas.addEventListener("lostpointercapture",cancel);
  canvas.addEventListener("keydown",event=>{
    if(event.key==="Delete"&&selectedShape) { event.preventDefault();run(state.current.filter(stroke=>stroke!==selectedShape)); }
    if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==="z") {
      event.preventDefault();
      const saved=event.shiftKey?state.redo:state.undo;
      if(saved.length) run(saved.at(-1),event.shiftKey?"redo":"undo");
    }
  });
  const observer=new ResizeObserver(draw);
  observer.observe(canvas);
  boardObservers.push(observer);
  draw();
}
function boardPoints(stroke) {
  return Array.isArray(stroke?.points)?stroke.points.filter(p=>p&&Number.isFinite(p.x)&&Number.isFinite(p.y)):[];
}
function box(points) {
  let left=Infinity,top=Infinity,right=-Infinity,bottom=-Infinity;
  for(const point of points){
    left=Math.min(left,point.x); top=Math.min(top,point.y); right=Math.max(right,point.x); bottom=Math.max(bottom,point.y);
  }
  if(!points.length) left=top=right=bottom=0;
  return {left,top,right,bottom,center:{x:(left+right)/2,y:(top+bottom)/2}};
}
function edge(bounds, toward) {
  const {left,top,right,bottom,center}=bounds;
  const x=toward.x-center.x,y=toward.y-center.y;
  const scale=Math.max(Math.abs(x)/Math.max(3,(right-left)/2),Math.abs(y)/Math.max(3,(bottom-top)/2));
  return scale ? {x:center.x+x/scale,y:center.y+y/scale} : center;
}
function arrowPoints(start,end) {
  const x=end.x-start.x,y=end.y-start.y,length=Math.min(18,Math.hypot(x,y)/3),angle=Math.atan2(y,x);
  const wing=a=>({x:end.x-length*Math.cos(a),y:end.y-length*Math.sin(a)});
  return [start,end,wing(angle-.55),end,wing(angle+.55)];
}
function shapePoints(tool,start,end) {
  if(tool==="rectangle") return [start,{x:end.x,y:start.y},end,{x:start.x,y:end.y},start];
  if(tool==="ellipse") return Array.from({length:49},(_,index)=>{
    const angle=index%48*Math.PI*2/48;
    return {x:(start.x+end.x+Math.abs(end.x-start.x)*Math.cos(angle))/2,
      y:(start.y+end.y+Math.abs(end.y-start.y)*Math.sin(angle))/2};
  });
  if(tool==="arrow") return arrowPoints(start,end);
  return [start,end];
}
function distanceToLine(point,first,last) {
  const x=last.x-first.x,y=last.y-first.y,length=x*x+y*y;
  const offset=length ? Math.max(0,Math.min(1,((point.x-first.x)*x+(point.y-first.y)*y)/length)) : 0;
  return Math.hypot(point.x-first.x-x*offset,point.y-first.y-y*offset);
}
function paintStroke(ctx,stroke,points,selected) {
  if(!points.length) return;
  const color=stroke.erase?"#101923":
    (/^#[0-9a-f]{6}$/i.test(stroke.color)&&stroke.color.toLowerCase()!=="#315545"?stroke.color:"#c1ed8b");
  ctx.strokeStyle=color;
  ctx.lineWidth=stroke.erase?6:typeof stroke.width==="number"?Math.max(1,Math.min(16,stroke.width)):2.5;
  ctx.beginPath();
  const first=points[0];
  if(stroke.text) {
    ctx.fillStyle=color;
    ctx.font='16px "Segoe UI","Microsoft YaHei UI",sans-serif';
    ctx.textBaseline="top";
    ctx.fillText(stroke.text,first.x,first.y);
  } else {
    ctx.moveTo(first.x,first.y);
    if(points.length===1) ctx.lineTo(first.x+.2,first.y);
    else for(const point of points.slice(1)) ctx.lineTo(point.x,point.y);
    ctx.stroke();
  }
  if(selected) {
    const bounds=box(points);
    ctx.setLineDash([4,4]);
    ctx.strokeStyle="#c1ed8b";
    ctx.lineWidth=1;
    ctx.strokeRect(bounds.left-4,bounds.top-4,stroke.text?ctx.measureText(stroke.text).width+8:bounds.right-bounds.left+8,
      stroke.text?24:bounds.bottom-bounds.top+8);
    ctx.setLineDash([]);
  }
}
function exportWorkspaceBoard(id) {
  const board=[...document.querySelectorAll("[data-board]")].find(canvas=>canvas.dataset.board===id);
  if(board) board.toBlob(blob=>{if(blob) downloadBlob(blob,(itemWithId(id)?.title||"画板")+".png");});
}
