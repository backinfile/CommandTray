"use strict";

const boardTools = [
  ["select","选择","M4 3l7 18 2-8 8-2z"], ["hand","抓手","M7 13V6a2 2 0 0 1 4 0v5-7a2 2 0 0 1 4 0v7-5a2 2 0 0 1 4 0v9a6 6 0 0 1-6 6h-1c-2 0-4-1-5-3l-3-4a2 2 0 0 1 3-2l2 2"],
  ["pen","画笔","M4 19l1-5L16 3l5 5-11 11zm10-14 5 5"], ["highlighter","荧光笔","m7 13 9-9 4 4-9 9H7z M7 16l-3 3h8"],
  ["line","直线","M5 19 19 5"], ["arrow","箭头","M5 19 19 5m-7 0h7v7"],
  ["rectangle","矩形","M4 5h16v14H4z"], ["ellipse","椭圆","M21 12a9 6 0 1 1-18 0 9 6 0 0 1 18 0"],
  ["diamond","菱形","M12 3l9 9-9 9-9-9z"],
  ["text","文字","M5 5h14M12 5v15m-4 0h8"], ["erase","橡皮","M3 14 12 4l8 7-8 9H8z M8 9l8 7m-3 4h8"]
];
const boardStates = new Map();
let boardObservers = [];

function boardControl(label, icon, operation) {
  const tip = {hand:"抓手（H 或按住空格拖动）",fit:"适应内容（Shift+1）"}[operation] ?? label;
  return `<button type="button" class="board-button" data-board-action="${operation}" title="${tip}" aria-label="${label}">${svg(icon)}</button>`;
}
function boardMarkup(id, name) {
  return `<div class="board-frame"><div class="board-tools" role="toolbar" aria-label="画板工具">${boardTools.map(([key,label,icon])=>
    boardControl(label,icon,key)).join("")}<span class="board-divider"></span>
    ${boardControl("撤销","M8 5l-5 5 5 5m-4-5h11a5 5 0 0 1 5 5v3","undo")}
    ${boardControl("重做","m16 5 5 5-5 5m4-5H9a5 5 0 0 0-5 5v3","redo")}
    <div class="board-style" role="group" aria-label="画笔样式">
      <input class="board-ink" type="color" value="#8db8ff" aria-label="画笔颜色" title="画笔颜色">
      <select class="board-width" aria-label="线宽"><option value="2">细线</option><option value="5">粗线</option></select>
    </div>
    </div><canvas class="board" data-board="${escape(id)}" aria-label="${escape(name)}" tabindex="0"></canvas>
    <footer class="board-footer"><div class="actions"><small class="board-count"></small>
      <span class="board-selection actions" hidden>
        ${boardControl("复制对象","M8 8h12v12H8z M15 4H4v11","duplicate")}
        ${boardControl("移到顶层","M12 20V9m-5 5 5-5 5 5M5 5h14","front")}
        ${boardControl("移到底层","M12 4v11m-5-5 5 5 5-5M5 19h14","back")}
        ${boardControl("删除对象","M4 6h16 M9 6V3h6v3 M6 6l1 14h10l1-14","delete")}
      </span></div><div class="actions board-view">
      ${boardControl("缩小","M5 12h14","zoom-out")}<small class="board-zoom"></small>
      ${boardControl("放大","M5 12h14M12 5v14","zoom-in")}
      ${boardControl("适应内容","M4 9V4h5m6 0h5v5m0 6v5h-5m-6 0H4v-5M9 9h6v6H9z","fit")}
      ${moreActions("画板操作",[["重置视图","board-action:reset/"+id],["切换网格","board-action:grid/"+id],["清空画板","board-action:clear/"+id,true]])}
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
  const state = boardStates.get(key) ?? { tool:"select", grid:true, ink:"#8db8ff", width:2, undo:[], redo:[], current:stored };
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
  let gesture, sheet, selectedShape, spaceHeld = false;
  const buttons = new Map([...frame.querySelectorAll("[data-board-action]")].map(button=>[button.dataset.boardAction,button]));
  const selection = frame.querySelector(".board-selection");
  const ctx = canvas.getContext("2d");
  const zoomFormat = new Intl.NumberFormat("zh-CN",{style:"percent",maximumFractionDigits:1});
  canvas.boardAction = act;
  function sceneBounds() {
    const points = [];
    for (const stroke of state.current) {
      const geometry = visiblePoints(stroke,false);
      if (!geometry.length) continue;
      const bounds = box(geometry);
      const start = geometry[0];
      if (stroke.text) {
        ctx.font = boardFont;
        points.push(start,{x:start.x+ctx.measureText(stroke.text).width,y:start.y+20});
      } else {
        const padding = strokeWidth(stroke)/2;
        points.push({x:bounds.left-padding,y:bounds.top-padding},{x:bounds.right+padding,y:bounds.bottom+padding});
      }
    }
    return points.length ? box(points) : null;
  }
  const home = {x:320,y:150};
  function fit(initial = false) {
    const rect = canvas.getBoundingClientRect();
    const bounds = sceneBounds();
    if (initial) {
      state.center = bounds?.center ?? home;
      state.zoom = Math.min(1, (rect.width-18)/Math.max(640,(bounds?.right??0)-(bounds?.left??0)),
        (rect.height-18)/Math.max(300,(bounds?.bottom??0)-(bounds?.top??0)));
    } else if (bounds) {
      state.center = bounds.center;
      state.zoom = Math.min(2, Math.max(1,rect.width-38)/Math.max(12,bounds.right-bounds.left),
        Math.max(1,rect.height-38)/Math.max(12,bounds.bottom-bounds.top));
    } else {
      state.center = home;
      state.zoom = Math.min(1,Math.max(1,rect.width-18)/640,Math.max(1,rect.height-18)/300);
    }
  }
  function zoomBy(factor, anchor) {
    if (!sheet) return;
    const {width,height} = canvas.getBoundingClientRect();
    const zoom = Math.max(Math.min(state.zoom,.001),Math.min(4,state.zoom*factor));
    if (anchor) state.center = {
      x:state.center.x+(anchor.x-width/2)*(1/state.zoom-1/zoom),
      y:state.center.y+(anchor.y-height/2)*(1/state.zoom-1/zoom)
    };
    state.zoom = zoom;
  }
  function pan(x,y) {
    state.center = {x:state.center.x-x/state.zoom,y:state.center.y-y/state.zoom};
  }
  function draw() {
    if (!canvas.isConnected) return;
    const rect=canvas.getBoundingClientRect(), ratio=devicePixelRatio||1;
    const w=Math.round(rect.width*ratio), h=Math.round(rect.height*ratio);
    if (rect.width<=18||rect.height<=18) return;
    if (canvas.width!==w||canvas.height!==h) { canvas.width=w; canvas.height=h; }
    if (!state.center) fit(true);
    const scale=state.zoom;
    sheet={scale,left:rect.width/2-state.center.x*scale,top:rect.height/2-state.center.y*scale};
    ctx.setTransform(ratio,0,0,ratio,0,0);
    ctx.fillStyle="#101923";
    ctx.fillRect(0,0,rect.width,rect.height);
    if (state.grid) {
      ctx.fillStyle="#2c3948";
      const spacing=16*2**Math.max(0,Math.ceil(Math.log2(.75/scale)))*scale;
      const mod=value=>(value%spacing+spacing)%spacing;
      for(let x=mod(sheet.left);x<rect.width;x+=spacing)
        for(let y=mod(sheet.top);y<rect.height;y+=spacing) ctx.fillRect(x,y,1.2,1.2);
    }
    ctx.translate(sheet.left,sheet.top);
    ctx.scale(scale,scale);
    ctx.lineCap="round";
    ctx.lineJoin="round";
    for (const stroke of state.current) paintStroke(ctx,stroke,visiblePoints(stroke),stroke===selectedShape);
    if (gesture?.draft) paintStroke(ctx,gesture.draft,boardPoints(gesture.draft),false);
    frame.querySelector(".board-count").textContent=state.busy?"正在保存…":state.current.length+" 个对象";
    frame.querySelector(".board-zoom").textContent=zoomFormat.format(state.zoom);
    const editing=state.busy||!!gesture;
    buttons.get("undo").disabled=editing||!state.undo.length;
    buttons.get("redo").disabled=editing||!state.redo.length;
    selection.hidden=!selectedShape;
    const index=state.current.indexOf(selectedShape);
    buttons.get("front").disabled=editing||index===state.current.length-1;
    buttons.get("back").disabled=editing||index<1;
    buttons.get("duplicate").disabled=buttons.get("delete").disabled=editing;
    canvas.classList.toggle("panning",gesture?.tool==="hand");
    canvas.classList.toggle("hand",state.tool==="hand"||spaceHeld);
    canvas.classList.toggle("drawing",state.tool!=="select"&&state.tool!=="hand"&&!spaceHeld);
    for(const [tool] of boardTools)
      buttons.get(tool).setAttribute("aria-pressed",String(state.tool===tool));
  }
  function visiblePoints(stroke, preview = true) {
    let points = boardPoints(stroke);
    if (stroke.from&&stroke.to) {
      const first=state.current.find(item=>item.id===stroke.from);
      const second=state.current.find(item=>item.id===stroke.to);
      if (first&&second) {
        const a=box(shift(first,boardPoints(first),preview)), b=box(shift(second,boardPoints(second),preview));
        points=arrowPoints(edge(a,b.center),edge(b,a.center));
      }
    }
    return shift(stroke,points,preview);
  }
  function shift(stroke, points, preview) {
    if (!preview||gesture?.moving!==stroke) return points;
    return points.map(point=>({x:point.x+gesture.offset.x,y:point.y+gesture.offset.y}));
  }
  function position(event) {
    const rect=canvas.getBoundingClientRect();
    return {x:Math.max(-100000,Math.min(100000,(event.clientX-rect.left-sheet.left)/sheet.scale)),
      y:Math.max(-100000,Math.min(100000,(event.clientY-rect.top-sheet.top)/sheet.scale))};
  }
  function hit(point) {
    return [...state.current].reverse().find(stroke=>{
      if (stroke.erase) return false;
      const threshold=9/sheet.scale+strokeWidth(stroke)/2;
      const points=visiblePoints(stroke);
      if (!points.length) return false;
      if (stroke.text) {
        const start=points[0];
        ctx.font=boardFont;
        if (point.x>=start.x-threshold&&point.x<=start.x+ctx.measureText(stroke.text).width+threshold&&
          point.y>=start.y-threshold&&point.y<=start.y+20+threshold) return true;
      }
      for(let i=1;i<points.length;i++) if(distanceToLine(point,points[i-1],points[i])<=threshold) return true;
      if (points.length>4 && points[0].x===points.at(-1).x && points[0].y===points.at(-1).y)
        return containsPoint(points,point);
      return points.length===1&&Math.hypot(points[0].x-point.x,points[0].y-point.y)<=threshold;
    });
  }
  function stroke(points) {
    const style = state.tool==="highlighter" ? {width:Math.min(16,state.width*3+4),opacity:.42} : {width:state.width};
    return {id:uuid(),color:state.ink,...style,points};
  }
  async function commit(next, direction = "edit", selectionId = null) {
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
      selectedShape=next.find(stroke=>stroke.id===selectionId);
    } catch (error) { report(error.message,true); throw error; }
    finally { state.busy=false; state.redraw(); }
  }
  function run(next,direction,selectionId) { commit(next,direction,selectionId).catch(()=>{}); }

  function act(action) {
    if (gesture) return;
    if (boardTools.some(item=>item[0]===action)) {
      state.tool=action;
      if (action!=="hand") selectedShape=null;
      canvas.focus({preventScroll:true});
    }
    if (action==="zoom-out") zoomBy(1/1.25);
    if (action==="zoom-in") zoomBy(1.25);
    if (action==="fit") fit();
    if (action==="reset") {state.center=home;state.zoom=1;}
    if (action==="grid") state.grid=!state.grid;
    if (action==="undo"||action==="redo") {
      const history=state[action];
      if(history.length) run(history.at(-1),action);
    }
    if (action==="clear"&&!state.busy) {
      if (state.current.length) modal("清空画板？","清空后可撤销。","","清空画板",()=>commit([]),true);
      else report("画板已空");
    }
    if (selectedShape&&!state.busy) {
      const others=state.current.filter(stroke=>stroke!==selectedShape);
      if (action==="delete") run(others);
      if (action==="duplicate") {
        const copy={...selectedShape,id:uuid(),points:visiblePoints(selectedShape).map(point=>({x:point.x+14,y:point.y+14}))};
        delete copy.from;
        delete copy.to;
        run([...state.current,copy],"edit",copy.id);
      }
      if (action==="front") run([...others,selectedShape],"edit",selectedShape.id);
      if (action==="back") run([selectedShape,...others],"edit",selectedShape.id);
    }
    draw();
  }
  frame.addEventListener("click",event=>{
    const control=event.target.closest("[data-board-action]");
    if(control) act(control.dataset.boardAction);
  });
  ink.addEventListener("input",()=>state.ink=ink.value);
  width.addEventListener("change",()=>state.width=Number(width.value));
  canvas.addEventListener("pointerdown",event=>{
    if (gesture||!sheet||![0,1].includes(event.button)) return;
    const panning=state.tool==="hand"||spaceHeld||event.button===1;
    if (state.busy&&!panning) return;
    event.preventDefault();
    canvas.focus({preventScroll:true});
    if (panning) {
      gesture={pointer:event.pointerId,tool:"hand",x:event.clientX,y:event.clientY};
      canvas.setPointerCapture(event.pointerId);
      draw();
      return;
    }
    const start=position(event);
    if (state.tool==="erase") {
      const target=hit(start);
      if(target) run(state.current.filter(stroke=>stroke!==target));
      return;
    }
    if (state.tool==="text") {
      modal("添加文字","",input("文字"),"添加",values=>commit([...state.current,{...stroke([start]),text:values.get("text").trim()}]));
      return;
    }
    gesture={pointer:event.pointerId,start,offset:{x:0,y:0},tool:state.tool};
    if(state.tool==="select") gesture.moving=selectedShape=hit(start);
    else {
      selectedShape=null;
      gesture.draft=stroke([start]);
      if(state.tool==="arrow") gesture.from=hit(start);
    }
    canvas.setPointerCapture(event.pointerId);
    draw();
  });
  function move(event) {
    if(!gesture||gesture.pointer!==event.pointerId) return false;
    if (gesture.tool==="hand") {
      pan(event.clientX-gesture.x,event.clientY-gesture.y);
      gesture.x=event.clientX;
      gesture.y=event.clientY;
      return true;
    }
    const end=position(event), start=gesture.start;
    gesture.offset={x:end.x-start.x,y:end.y-start.y};
    if (!gesture.draft) return true;
    if(gesture.tool==="pen"||gesture.tool==="highlighter") {
      if(gesture.draft.points.length<4096) gesture.draft.points.push(end);
    } else {
      gesture.draft.points=shapePoints(gesture.tool,start,event.shiftKey?constrain(gesture.tool,start,end):end);
    }
    return true;
  }
  canvas.addEventListener("pointermove",event=>{if(move(event)) draw();});
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
    }),"edit",moving.id);
    draw();
  });
  function cancel(event) {
    if(gesture?.pointer===event.pointerId) {gesture=null;draw();}
  }
  canvas.addEventListener("pointercancel",cancel);
  canvas.addEventListener("lostpointercapture",cancel);
  canvas.addEventListener("wheel",event=>{
    if (!event.ctrlKey&&!event.metaKey) return;
    event.preventDefault();
    if (gesture||!event.deltaY) return;
    const rect=canvas.getBoundingClientRect();
    zoomBy(event.deltaY<0?1.16:1/1.16,{x:event.clientX-rect.left,y:event.clientY-rect.top});
    draw();
  },{passive:false});
  canvas.addEventListener("keyup",event=>{
    if (event.code==="Space") {spaceHeld=false;draw();}
  });
  canvas.addEventListener("blur",()=>{if(spaceHeld){spaceHeld=false;draw();}});
  canvas.addEventListener("keydown",event=>{
    const key=event.key.toLowerCase(), command=event.ctrlKey||event.metaKey;
    let action;
    if (command) {
      action = {z:event.shiftKey?"redo":"undo",y:"redo",d:"duplicate","0":"reset","+":"zoom-in","=":"zoom-in","-":"zoom-out"}[key];
    } else if (!event.altKey) {
      if (key==="escape"&&(gesture||selectedShape)) {
        const pointer=gesture?.pointer;
        gesture=selectedShape=null;
        if (pointer!==undefined&&canvas.hasPointerCapture(pointer)) canvas.releasePointerCapture(pointer);
        event.preventDefault();
        draw();
        return;
      }
      action = {delete:"delete",backspace:"delete",h:"hand",v:"select",p:"pen",e:"erase",g:"grid"}[key];
      if (event.shiftKey&&event.code==="Digit1") action="fit";
      if (event.code==="Space") {event.preventDefault();spaceHeld=true;draw();return;}
      const shift={arrowleft:[1,0],arrowright:[-1,0],arrowup:[0,1],arrowdown:[0,-1]}[key];
      if (shift&&!gesture) {
        event.preventDefault();
        const distance=event.shiftKey?120:36;
        pan(shift[0]*distance,shift[1]*distance);
        draw();
      }
    }
    if (action) {event.preventDefault();act(action);}
  });
  canvas.exportDrawing = exportDrawing;
  function exportDrawing() {
    if (!state.current.length) return;
    const bounds=sceneBounds();
    if (!bounds) return;
    const picture=document.createElement("canvas");
    const scale=Math.min(2,4064/Math.max(1,bounds.right-bounds.left,bounds.bottom-bounds.top));
    picture.width=Math.ceil((bounds.right-bounds.left)*scale)+32;
    picture.height=Math.ceil((bounds.bottom-bounds.top)*scale)+32;
    const output=picture.getContext("2d");
    output.fillStyle="#101923";
    output.fillRect(0,0,picture.width,picture.height);
    output.setTransform(scale,0,0,scale,16-bounds.left*scale,16-bounds.top*scale);
    output.lineCap=output.lineJoin="round";
    for(const stroke of state.current) paintStroke(output,stroke,visiblePoints(stroke,false),false);
    return picture;
  }
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
  if(tool==="diamond") {
    const x=(start.x+end.x)/2, y=(start.y+end.y)/2;
    return [{x,y:start.y},{x:end.x,y},{x,y:end.y},{x:start.x,y},{x,y:start.y}];
  }
  if(tool==="ellipse") return Array.from({length:49},(_,index)=>{
    const angle=index%48*Math.PI*2/48;
    return {x:(start.x+end.x+Math.abs(end.x-start.x)*Math.cos(angle))/2,
      y:(start.y+end.y+Math.abs(end.y-start.y)*Math.sin(angle))/2};
  });
  if(tool==="arrow") return arrowPoints(start,end);
  return [start,end];
}
function constrain(tool,start,end) {
  const x=end.x-start.x, y=end.y-start.y;
  if (tool==="line"||tool==="arrow") {
    const angle=Math.round(Math.atan2(y,x)/(Math.PI/4))*Math.PI/4, length=Math.hypot(x,y);
    return {x:start.x+length*Math.cos(angle),y:start.y+length*Math.sin(angle)};
  }
  const side=Math.max(Math.abs(x),Math.abs(y));
  return {x:start.x+(x<0?-side:side),y:start.y+(y<0?-side:side)};
}
function distanceToLine(point,first,last) {
  const x=last.x-first.x,y=last.y-first.y,length=x*x+y*y;
  const offset=length ? Math.max(0,Math.min(1,((point.x-first.x)*x+(point.y-first.y)*y)/length)) : 0;
  return Math.hypot(point.x-first.x-x*offset,point.y-first.y-y*offset);
}
function containsPoint(points,point) {
  let inside=false;
  for (let i=1;i<points.length;i++) {
    const a=points[i-1],b=points[i];
    if ((a.y>point.y)!==(b.y>point.y)&&point.x<a.x+(point.y-a.y)*(b.x-a.x)/(b.y-a.y)) inside=!inside;
  }
  return inside;
}
const boardFont='16px "Segoe UI","Microsoft YaHei UI",sans-serif';
const strokeWidth=stroke=>stroke.erase?6:typeof stroke.width==="number"?Math.max(1,Math.min(16,stroke.width)):2.5;
function paintStroke(ctx,stroke,points,selected) {
  if(!points.length) return;
  const color=stroke.erase?"#101923":
    (/^#[0-9a-f]{6}$/i.test(stroke.color)&&stroke.color.toLowerCase()!=="#315545"?stroke.color:"#c1ed8b");
  ctx.strokeStyle=color;
  ctx.lineWidth=strokeWidth(stroke);
  ctx.globalAlpha=Number.isFinite(stroke.opacity)?Math.max(0,Math.min(1,stroke.opacity)):1;
  ctx.beginPath();
  const first=points[0];
  if(stroke.text) {
    ctx.fillStyle=color;
    ctx.font=boardFont;
    ctx.textBaseline="top";
    ctx.fillText(stroke.text,first.x,first.y);
  } else {
    ctx.moveTo(first.x,first.y);
    if(points.length===1) ctx.lineTo(first.x+.2,first.y);
    else for(const point of points.slice(1)) ctx.lineTo(point.x,point.y);
    ctx.stroke();
  }
  ctx.globalAlpha=1;
  if(selected) {
    const bounds=box(points);
    ctx.setLineDash([4,4]);
    ctx.strokeStyle="#8db8ff";
    ctx.lineWidth=1;
    ctx.strokeRect(bounds.left-4,bounds.top-4,stroke.text?ctx.measureText(stroke.text).width+8:bounds.right-bounds.left+8,
      stroke.text?24:bounds.bottom-bounds.top+8);
    ctx.setLineDash([]);
  }
}
function exportWorkspaceBoard(id) {
  const board=[...document.querySelectorAll("[data-board]")].find(canvas=>canvas.dataset.board===id);
  const picture=board?.exportDrawing();
  if(picture) picture.toBlob(blob=>{if(blob) downloadBlob(blob,(itemWithId(id)?.title||"画板")+".png");});
  else if (board) report("画板为空");
}
function performBoardAction(action, id) {
  const board=[...document.querySelectorAll("[data-board]")].find(canvas=>canvas.dataset.board===id);
  board?.boardAction(action);
}
