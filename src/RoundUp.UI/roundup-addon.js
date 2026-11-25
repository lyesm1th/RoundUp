/* RoundUp coverage tools, stored per project by the local RoundUp host. */
(() => {
  "use strict";
  const bridge = window.RoundUpBridge;
  const canvas = document.getElementById("mapCanvas");
  const overlay = document.getElementById("roundupCoverageOverlay");
  const polygonButton = document.getElementById("coveragePolygonBtn");
  const gridButton = document.getElementById("coverageGridBtn");
  const summary = document.getElementById("roundupCoverageSummary");
  if (!bridge || !canvas || !overlay || !polygonButton || !gridButton) return;
  const SVG = "http://www.w3.org/2000/svg";
  const ZOOM = 15;
  const state = { shapes: [], cells: [], draft: [], mode: "", project: "", loading: false, dirty: false };
  const safeId = x => String(x || "default").replace(/[^a-z0-9_-]/gi, "_").slice(0, 80) || "default";
  const projectId = () => safeId(bridge.state.currentProjectId || "default");
  const tile = (lat, lng) => {
    const n = 2 ** ZOOM, clamped = Math.max(-85, Math.min(85, lat));
    return { x: Math.max(0, Math.min(n - 1, Math.floor((lng + 180) / 360 * n))), y: Math.max(0, Math.min(n - 1, Math.floor((1 - Math.asinh(Math.tan(clamped * Math.PI / 180)) / Math.PI) * n / 2))) };
  };
  const geoTile = (x, y) => ({
    lng: x / (2 ** ZOOM) * 360 - 180,
    lat: Math.atan(Math.sinh(Math.PI * (1 - 2 * y / (2 ** ZOOM)))) * 180 / Math.PI
  });
  const key = cell => cell.x + ":" + cell.y;
  const asXY = point => bridge.toScreen(point[0], point[1]);
  const vertex = point => {
    const v = asXY(point);
    return Math.round(v.x * 10) / 10 + "," + Math.round(v.y * 10) / 10;
  };
  const polyKm2 = points => {
    if (points.length < 3) return 0;
    const latMid = points.reduce((acc,p) => acc+p[0],0) / points.length * Math.PI / 180;
    const scaleX = Math.cos(latMid) * 111.32, scaleY = 111.32;
    return Math.abs(points.reduce((a,p,i) => {
      const next = points[(i+1)%points.length];
      return a + p[1]*scaleX*next[0]*scaleY - next[1]*scaleX*p[0]*scaleY;
    },0)) / 2;
  };
  const svgEl = (tag, attrs) => {
    const n = document.createElementNS(SVG, tag);
    for (const [k,v] of Object.entries(attrs || {})) n.setAttribute(k,String(v));
    return n;
  };
  function render() {
    overlay.setAttribute("viewBox", "0 0 " + Math.max(1,canvas.clientWidth) + " " + Math.max(1,canvas.clientHeight));
    overlay.replaceChildren();
    for (const cell of state.cells) {
      const a=geoTile(cell.x,cell.y), b=geoTile(cell.x+1,cell.y+1);
      const points=[[a.lat,a.lng],[a.lat,b.lng],[b.lat,b.lng],[b.lat,a.lng]];
      overlay.appendChild(svgEl("polygon",{points:points.map(vertex).join(" "),class:"roundup-sector"}));
    }
    for (const shape of state.shapes) overlay.appendChild(svgEl("polygon",{
      points:shape.points.map(vertex).join(" "), class:"roundup-region"
    }));
    if (state.draft.length) {
      overlay.appendChild(svgEl("polyline",{
        points:state.draft.map(vertex).join(" "),class:"roundup-draft"
      }));
      for (const point of state.draft) {
        const at=asXY(point);
        overlay.appendChild(svgEl("circle",{cx:at.x,cy:at.y,r:5,class:"roundup-vertex"}));
      }
    }
    const cellKm2=state.cells.reduce((sum,c)=>{
      const a=geoTile(c.x,c.y),b=geoTile(c.x+1,c.y+1);
      return sum+Math.abs((a.lat-b.lat)*(a.lng-b.lng)*111.32*111.32*Math.cos((a.lat+b.lat)*Math.PI/360));
    },0);
    const km2 = cellKm2+state.shapes.reduce((sum,s)=>sum+polyKm2(s.points),0);
    summary.textContent = state.shapes.length || state.cells.length
      ? "Охоплення ≈ " + km2.toLocaleString("uk-UA",{maximumFractionDigits:2}) + " км² · " +
        state.shapes.length + " ділянок · " + state.cells.length + " секторів"
      : "Охоплення: ще не позначено";
    polygonButton.classList.toggle("coverage-active",state.mode==="polygon");
    gridButton.classList.toggle("coverage-active",state.mode==="sector");
    const instructions=document.getElementById("coverageInstructions");
    if (instructions) instructions.textContent=state.mode==="polygon"
      ? "Клікайте по контуру території. Подвійний клік або Enter — завершити; Esc — скасувати."
      : state.mode==="sector"
      ? "Клік по сектору додає або знімає позначку обстеження (комірка ≈ 0,8 км)."
      : "Позначайте території навіть без фотографій та геоміток об’єктів.";
  }
  let lastView="";
  setInterval(()=>{
    const n=bridge.state;
    const view=[n.center.lat,n.center.lng,n.zoom,canvas.clientWidth,canvas.clientHeight].join("|");
    if (view!==lastView) {lastView=view;render()}
    if(projectId()!==state.project) void load();
  },180);
  const controls=document.createElement("div");
  controls.id="coverageControls";
  controls.className="roundup-coverage-controls";
  controls.innerHTML='<span id="coverageInstructions">Позначайте обстежені території навіть без об’єктів.</span>'+
    '<button type="button" id="coverageUndo" title="Прибрати останню ділянку">Скасувати останнє</button>'+
    '<button type="button" id="coverageCancel" title="Вийти з малювання">Готово</button>';
  canvas.parentElement.appendChild(controls);
  function mode(name) {
    state.mode = state.mode === name ? "" : name;
    state.draft = [];
    canvas.classList.toggle("roundup-drawing",!!state.mode);
    controls.classList.toggle("is-open",!!state.mode);
    render();
  }
  polygonButton.addEventListener("click",()=>mode("polygon"));
  gridButton.addEventListener("click",()=>mode("sector"));
  document.getElementById("coverageCancel").addEventListener("click",()=>{
    if(state.mode==="polygon"&&state.draft.length>=3) finish();
    else mode("");
  });
  document.getElementById("coverageUndo").addEventListener("click",()=>{
    if(state.draft.length) state.draft.pop();
    else if(state.mode==="sector"&&state.cells.length){state.cells.pop();save()}
    else if(state.shapes.length){state.shapes.pop();save()}
    render();
  });
  const fromPointer = ev => {
    const rect=canvas.getBoundingClientRect();
    const p=bridge.toGeo(ev.clientX-rect.left,ev.clientY-rect.top);
    return Number.isFinite(p.lat)&&Number.isFinite(p.lng)?[p.lat,p.lng]:null;
  };
  const finish=()=>{
    if(state.draft.length>=3){
      state.shapes.push({id:Date.now().toString(36)+"-"+Math.random().toString(36).slice(2,6),points:state.draft.slice(),created:new Date().toISOString()});
      save();
    }
    state.draft=[];
    render();
  };
  canvas.addEventListener("click",ev=>{
    if(!state.mode)return;
    const point=fromPointer(ev);
    ev.preventDefault();ev.stopImmediatePropagation();
    if(!point)return;
    if(state.mode==="sector"){
      const t=tile(point[0],point[1]),k=key(t),i=state.cells.findIndex(c=>key(c)===k);
      if(i>=0)state.cells.splice(i,1);
      else state.cells.push(t);
      save();
    }else if(ev.detail===1)state.draft.push(point);
    render();
  },true);
  canvas.addEventListener("dblclick",ev=>{
    if(state.mode!=="polygon")return;
    ev.preventDefault();ev.stopImmediatePropagation();
    finish();
  },true);
  document.addEventListener("keydown",ev=>{
    if(!state.mode)return;
    if(ev.key==="Escape"){mode("");ev.preventDefault()}
    if(ev.key==="Enter"&&state.mode==="polygon"){finish();ev.preventDefault()}
  });
  function sanitize(data) {
    const shapes=Array.isArray(data?.shapes)?data.shapes.filter(s=>
      Array.isArray(s.points)&&s.points.length>=3&&s.points.length<=2000&&
      s.points.every(p=>Array.isArray(p)&&p.length===2&&p.every(Number.isFinite))
    ).slice(0,2000):[];
    const cells=Array.isArray(data?.cells)?data.cells.filter(c=>
      Number.isInteger(c.x)&&Number.isInteger(c.y)&&c.x>=0&&c.y>=0&&c.x<2**ZOOM&&c.y<2**ZOOM
    ).slice(0,25000):[];
    return {shapes,cells};
  }
  async function load() {
    const id=projectId();
    if(state.loading)return;
    state.loading=true;
    state.project=id;
    state.mode="";
    state.draft=[];
    try {
      const response=await fetch("/roundup/coverage?project="+encodeURIComponent(id),{cache:"no-store"});
      if(!response.ok)throw new Error("HTTP "+response.status);
      const next=sanitize(await response.json());
      if(state.project===id&&!state.dirty) {state.shapes=next.shapes;state.cells=next.cells;}
    }catch(ex){
      console.warn("RoundUp coverage: loading local backup",ex.message);
      try{const next=sanitize(JSON.parse(localStorage.getItem("RoundUp:coverage:"+id)||"{}"));state.shapes=next.shapes;state.cells=next.cells;}catch(_){}
    }finally{state.loading=false;render()}
  }
  let saveTimer;
  function save() {
    state.dirty=true;
    localStorage.setItem("RoundUp:coverage:"+state.project,JSON.stringify({shapes:state.shapes,cells:state.cells}));
    clearTimeout(saveTimer);
    const savedProject=state.project;
    const payload={version:1,shapes:state.shapes,cells:state.cells};
    saveTimer=setTimeout(async()=>{
      try{
        const r=await fetch("/roundup/coverage?project="+encodeURIComponent(savedProject),{
          method:"PUT",headers:{"Content-Type":"application/json"},
          body:JSON.stringify(payload)
        });
        if(!r.ok)throw Error("HTTP "+r.status);
        state.dirty=false;
      }catch(ex){console.warn("RoundUp coverage: save queued locally",ex.message);}
    },260);
  }
  void load();
})();