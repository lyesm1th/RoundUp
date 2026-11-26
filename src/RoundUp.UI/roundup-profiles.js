/* RoundUp manager profiles and dated road-trip layers.
   Stored alongside the existing GeoZamir project, NEVER by changing its ZIP/API. */
(() => {
  "use strict";
  const bridge = window.RoundUpBridge;
  if (!bridge || !document.getElementById("mapCanvas")) return;
  const profileColors = ["#119d78","#3586d6","#dc9242","#935ac7","#d95875","#159bad","#8c9943"];
  const dateColors = ["#9b5dd9","#e2a53a","#168aab","#e06b70","#55a268","#d66aaf","#687acc","#b98548"];
  const all = document.createElement.bind(document);
  const blank = () => ({version:1,managers:[],assignments:{},sourceAssignments:{},routes:[]});
  let data = blank(), project = "", loading = false, saving = false, activeManager = "all", activeDay = "", drawing = false, draft = [];
  let lastView = "", lastList = "", lastSaved = "", saveTimer = null;
  const $ = id => document.getElementById(id);
  const getProject = () => String(bridge.state.currentProjectId || "default");
  const endpoint = id => "/roundup/profiles?project=" + encodeURIComponent(id);
  const safeDate = value => /^\d{4}-\d{2}-\d{2}$/.test(String(value || "")) ? String(value) : "";
  const isoToday = () => {
    const d = new Date(), tz = d.getTimezoneOffset() * 60000;
    return new Date(d.getTime() - tz).toISOString().slice(0,10);
  };
  const colorFor = id => {
    const at = data.managers.find(m => m.id === id);
    return at ? at.color : "#97a5ab";
  };
  const dateColor = date => {
    let hash = 0;
    for (let i = 0; i < date.length; i++) hash = ((hash << 5) - hash + date.charCodeAt(i)) | 0;
    return dateColors[Math.abs(hash) % dateColors.length];
  };
  const sanitize = input => {
    if (!input || typeof input !== "object") return blank();
    const managers = (Array.isArray(input.managers) ? input.managers : [])
      .filter(m => m && typeof m.id === "string" && /^[a-zA-Z0-9_-]{1,80}$/.test(m.id))
      .slice(0,100).map((m,i) => ({id:m.id,name:String(m.name || "Менеджер").slice(0,100),
        color:/^#[0-9a-fA-F]{6}$/.test(m.color) ? m.color : profileColors[i % profileColors.length]}));
    const valid = new Set(managers.map(m=>m.id));
    const assignments = {}, sourceAssignments = {};
    for (const [id,manager] of Object.entries(input.assignments || {}))
      if (typeof id === "string" && id.length < 200 && valid.has(manager)) assignments[id] = manager;
    for (const [id,manager] of Object.entries(input.sourceAssignments || {}))
      if (typeof id === "string" && id.length < 300 && valid.has(manager)) sourceAssignments[id] = manager;
    const routes = (Array.isArray(input.routes) ? input.routes : []).filter(r =>
      r && valid.has(r.managerId) && safeDate(r.date) &&
      Array.isArray(r.points) && r.points.length >= 2 && r.points.length <= 5000 &&
      r.points.every(p=>Array.isArray(p) && p.length === 2 && p.every(Number.isFinite) &&
        Math.abs(p[0]) <= 90 && Math.abs(p[1]) <= 180)
    ).map(r=>({id:String(r.id || "r-"+Math.random()).slice(0,70),
      managerId:r.managerId,date:r.date,points:r.points,createdAt:String(r.createdAt || "").slice(0,40)}));
    return {version:1,managers,assignments,sourceAssignments,routes};
  };
  const persist = () => {
    clearTimeout(saveTimer);
    const id = project, snapshot = JSON.stringify(data);
    try { localStorage.setItem("RoundUp:profiles:" + id, snapshot); } catch (_) {}
    if (snapshot === lastSaved || saving) { if (saving) saveTimer=setTimeout(persist,600); return; }
    saveTimer=setTimeout(async()=>{
      if (saving || project !== id) return;
      saving=true;
      try {
        const response=await fetch(endpoint(id), {method:"PUT",headers:{"Content-Type":"application/json","X-RoundUp-Local":"1"},body:snapshot});
        if (!response.ok) throw Error("HTTP " + response.status);
        lastSaved=snapshot;
      } catch(error) {
        console.warn("RoundUp manager data saved locally, desktop write pending:",error);
      } finally {saving=false; if(project===id && JSON.stringify(data)!==snapshot) persist();}
    },350);
  };
  async function openProject() {
    const id=getProject();
    if (loading || id === project) return;
    if (project && JSON.stringify(data)!==lastSaved) {
      clearTimeout(saveTimer);
      try {
        await fetch(endpoint(project),{method:"PUT",headers:{"Content-Type":"application/json","X-RoundUp-Local":"1"},body:JSON.stringify(data)});
      } catch(error){console.warn("RoundUp profiles: project switch write deferred",error);}
    }
    loading=true; project=id; data=blank(); activeManager="all"; activeDay=""; draft=[];drawing=false;lastSaved="";
    try {
      const response=await fetch(endpoint(id),{cache:"no-store"});
      if(!response.ok)throw Error("HTTP "+response.status);
      data=sanitize(await response.json());lastSaved=JSON.stringify(data);
    } catch(error) {
      console.warn("RoundUp profiles: native workspace unavailable; local cache used",error);
      try {data=sanitize(JSON.parse(localStorage.getItem("RoundUp:profiles:"+id)||"{}"));}catch(_){}
    } finally {loading=false; renderControls();renderPaths();bridge.rebuild?.();}
  }
  function managerFor(record) {
    if (!record) return "";
    const source=bridge.state.sources.get(record.sourceKey);
    const archiveId=source && source.desktopId;
    return (archiveId && data.assignments[String(archiveId)]) ||
      data.sourceAssignments[String(record.sourceKey || "")] || "";
  }
  function visitDate(record) {
    // Prefer actual visit/photo date; archive import date is only a fallback.
    const image=record.images && record.images.find(p=>safeDate(String(p.date||"").slice(0,10)));
    for (const candidate of [record.geo?.at,record.exportedAt,image?.date,record.date,record.createdAt]) {
      const day=safeDate(String(candidate||"").slice(0,10));
      if(day) return day;
    }
    const source=bridge.state.sources.get(record.sourceKey);
    return safeDate(String(source?.importedAt||"").slice(0,10));
  }
  function allowsRecord(record) {
    if (loading) return true;
    const m=managerFor(record);
    return (activeManager === "all" || (activeManager === "unassigned" ? !m : m === activeManager)) &&
      (!activeDay || visitDate(record) === activeDay);
  }
  function markerColor(records) {
    const colors=new Set(records.map(record=>managerFor(record)).filter(Boolean));
    return colors.size===1 ? colorFor([...colors][0]) : "#80929a";
  }
  const div = (cls,textContent) => {
    const el=all("div");if(cls)el.className=cls;if(textContent!=null)el.textContent=textContent;return el;
  };
  function button(cls,label,onClick) {
    const b=all("button");b.type="button";b.className=cls;b.textContent=label;b.addEventListener("click",onClick);return b;
  }
  const ui=div("ru-profile-panel");
  const header=div("ru-profile-header");
  header.append(div("ru-profile-title","МЕНЕДЖЕРИ ТА ОБ'ЇЗДИ"));
  const managerSelect=all("select");managerSelect.id="roundupManagerFilter";managerSelect.setAttribute("aria-label","Менеджер");
  managerSelect.addEventListener("change",()=>{activeManager=managerSelect.value;activeDay="";renderControls();bridge.rebuild?.();renderPaths();});
  const daySelect=all("select");daySelect.id="roundupDayFilter";daySelect.setAttribute("aria-label","Дата об'їзду");
  daySelect.addEventListener("change",()=>{activeDay=daySelect.value;renderControls();bridge.rebuild?.();renderPaths();});
  const tools=div("ru-profile-tools");
  const addManager=button("ru-small-btn","＋ Менеджер",()=>showImportModal(null,true));
  const editManager=button("ru-small-btn","✎ Редагувати",editProfile);
  const migrate=button("ru-small-btn","Прив'язати старі архіви",assignUnassigned);
  tools.append(addManager,editManager,migrate);
  const routeTitle=div("ru-profile-title","МАРШРУТИ ТА ПОКРИТТЯ");
  const routeRow=div("ru-profile-route-row");
  const routeDate=all("input");routeDate.type="date";routeDate.id="roundupRouteDate";routeDate.value=isoToday();
  const drawButton=button("ru-small-btn ru-draw-btn","✎ Прокласти шлях",toggleDrawing);
  const finishButton=button("ru-small-btn","✓ Завершити",finishRoute);
  const cancelButton=button("ru-small-btn","Скасувати",()=>stopDrawing());
  routeRow.append(routeDate,drawButton,finishButton,cancelButton);
  const routeHint=div("ru-route-hint","Виберіть менеджера, дату та намалюйте шлях кліками по карті.");
  const routeList=div("ru-route-list");
  const mapRouteBtn=$("routeDrawMapBtn");
  const floating=div("ru-map-route-controls");
  const floatingHint=div("ru-route-map-caption","Малювання маршруту");
  const floatingDate=all("input");floatingDate.type="date";floatingDate.value=routeDate.value;
  floatingDate.setAttribute("aria-label","Дата маршруту");
  const floatingUndo=button("","↶ Точка назад",()=>{
    if(drawing&&draft.length){draft.pop();renderPaths();renderControls();}
  });
  const floatingFinish=button("ru-route-save","✓ Готово",finishRoute);
  const floatingCancel=button("","Скасувати",()=>stopDrawing());
  floating.append(floatingHint,floatingDate,floatingUndo,floatingFinish,floatingCancel);
  $("mapArea")?.append(floating);
  mapRouteBtn?.addEventListener("click",toggleDrawing);
  floatingDate.addEventListener("change",()=>{routeDate.value=floatingDate.value;});
  routeDate.addEventListener("change",()=>{floatingDate.value=routeDate.value;});
  ui.append(header,managerSelect,daySelect,tools,routeTitle,routeRow,routeHint,routeList);
  const container=$("sidebar"),after=container?.querySelector(".sidebar-head");
  if(container&&after)after.insertAdjacentElement("afterend",ui);
  function option(value,label,selected) {
    const o=all("option");o.value=value;o.textContent=label;o.selected=selected;return o;
  }
  function renderControls() {
    const current=activeManager;
    managerSelect.replaceChildren(option("all","Усі менеджери",current==="all"));
    for(const manager of data.managers)
      managerSelect.append(option(manager.id,manager.name,current===manager.id));
    managerSelect.append(option("unassigned","Нерозподілені архіви",current==="unassigned"));
    const days=new Set();
    for(const record of bridge.state.records) {
      if(activeManager!=="all" && (activeManager==="unassigned" ? !!managerFor(record) : managerFor(record)!==activeManager))continue;
      const day=visitDate(record);if(day)days.add(day);
    }
    for(const route of data.routes)if(activeManager==="all"||route.managerId===activeManager)days.add(route.date);
    for(const date of window.RoundUpCoverage?.availableDates?.(activeManager)||[])days.add(date);
    daySelect.replaceChildren(option("","Усі дати",!activeDay));
    for(const d of [...days].sort().reverse())daySelect.append(option(d,d,activeDay===d));
    if(activeDay && !days.has(activeDay)){activeDay="";daySelect.value="";}
    const count=data.routes.filter(routeVisible).length;
    routeHint.textContent=drawing
      ? "Клікайте по дорозі. Подвійний клік або «Завершити» збереже маршрут; Esc — скасувати."
      : count+" маршрутів видно · зелений/синій за менеджером, у профілі — колір за датою.";
    drawButton.classList.toggle("is-active",drawing);
    mapRouteBtn?.classList.toggle("coverage-active",drawing);
    floating.classList.toggle("is-open",drawing);
    floatingHint.textContent=drawing
      ? (data.managers.find(m=>m.id===activeManager)?.name||"Менеджер")+" · "+draft.length+" точок. Клікайте вздовж дороги. Для повороту — нова точка."
      : "Малювання маршруту";
    floatingDate.value=routeDate.value;
    floatingUndo.disabled=!drawing||draft.length===0;
    floatingFinish.disabled=!drawing||draft.length<2;
    finishButton.disabled=!drawing||draft.length<2;
    cancelButton.disabled=!drawing;
    routeList.replaceChildren();
    for(const route of data.routes.filter(routeVisible).slice(-8).reverse()){
      const entry=div("ru-route-item");
      const color=div("ru-route-swatch");color.style.background=colorForRoute(route);
      const name=div("ru-route-label",route.date+" · "+route.points.length+" вузлів");
      const del=button("ru-route-delete","×",()=>{if(!confirm("Видалити цей промальований маршрут? Точки й архіви залишаться."))return;data.routes=data.routes.filter(r=>r.id!==route.id);persist();renderControls();renderPaths();});
      entry.append(color,name,del);routeList.append(entry);
    }
  }
  function makeManager(name,color) {
    name=String(name||"").trim().slice(0,100);
    if(!name)throw Error("Вкажіть назву профілю.");
    const id="m"+Date.now().toString(36)+Math.random().toString(36).slice(2,7);
    data.managers.push({id,name,color:/^#[0-9a-fA-F]{6}$/.test(color||"")?color:profileColors[data.managers.length%profileColors.length]});
    persist();return id;
  }
  let pending=null;
  function showImportModal(files,standalone=false) {
    if(pending)return Promise.resolve({cancelled:true});
    return new Promise(resolve=>{
      const shade=div("ru-profile-modal-backdrop");
      const panel=div("ru-profile-modal");
      const headline=all("h2");headline.textContent=standalone?"Створити профіль":"Прив'язати архіви до менеджера";
      const description=div("",standalone?"Створіть постійний профіль менеджера.":"Обрано "+files.length+" архів(ів). Дані будуть доступні на загальній карті.");
      const choice=all("select");choice.setAttribute("aria-label","Оберіть менеджера");
      for(const m of data.managers)choice.append(option(m.id,m.name,false));
      choice.append(option("__new__","＋ Створити новий профіль",!data.managers.length));
      if(!data.managers.length||standalone)choice.value="__new__";
      const name=all("input");name.type="text";name.maxLength=100;name.placeholder="Наприклад: Олег Львів";
      name.style.display=choice.value==="__new__"?"block":"none";
      choice.addEventListener("change",()=>name.style.display=choice.value==="__new__"?"block":"none");
      const tint=all("input");tint.type="color";tint.value=profileColors[data.managers.length%profileColors.length];tint.title="Колір менеджера";
      tint.style.display=choice.value==="__new__"?"block":"none";
      choice.addEventListener("change",()=>tint.style.display=choice.value==="__new__"?"block":"none");
      const error=div("ru-modal-error","");
      const actions=div("ru-modal-actions");
      const dismiss=()=>{
        shade.remove();pending=null;resolve({cancelled:true});
      };
      const cancel=button("ru-small-btn","Скасувати",dismiss);
      const accept=button("ru-primary-btn",standalone?"Створити":"Імпортувати",()=>{
        try{
          const managerId=choice.value==="__new__"?makeManager(name.value,tint.value):choice.value;
          if(!data.managers.some(m=>m.id===managerId))throw Error("Оберіть менеджера.");
          shade.remove();pending=null;renderControls();resolve({managerId,cancelled:false});
        }catch(ex){error.textContent=ex.message;}
      });
      actions.append(cancel,accept);
      panel.append(headline,description,choice,name,tint,error,actions);shade.append(panel);document.body.append(shade);
      pending=shade;
      if(standalone){
        accept.textContent="Створити";
      }
      shade.addEventListener("click",ev=>{if(ev.target===shade)dismiss();});
      name.addEventListener("keydown",ev=>{if(ev.key==="Enter")accept.click();});
      (choice.value==="__new__"?name:choice).focus();
    }).then(result=>{
      if(standalone){if(result.managerId){activeManager=result.managerId;renderControls();bridge.rebuild?.();}return result;}
      return result;
    });
  }
  async function beforeImport(files) {
    if(project!==getProject())await openProject();
    return showImportModal(files,false);
  }
  async function afterImport(selection, archiveIds, sourceKeys) {
    if(!selection || selection.cancelled || !selection.managerId)return;
    for(const id of archiveIds||[])data.assignments[String(id)]=selection.managerId;
    for(const key of sourceKeys||[])data.sourceAssignments[String(key)]=selection.managerId;
    if((archiveIds||[]).length||(sourceKeys||[]).length)persist();
    activeManager="all";activeDay="";
    renderControls();bridge.rebuild?.();
  }
  function editProfile() {
    const manager=data.managers.find(m=>m.id===activeManager);
    if(!manager){alert("Оберіть конкретний профіль менеджера.");return;}
    const shade=div("ru-profile-modal-backdrop"),panel=div("ru-profile-modal");
    const headline=all("h2");headline.textContent="Редагування менеджера";
    const name=all("input");name.value=manager.name;name.maxLength=100;name.setAttribute("aria-label","Назва менеджера");
    const color=all("input");color.type="color";color.value=manager.color;color.setAttribute("aria-label","Колір на загальній карті");
    const actions=div("ru-modal-actions"),cancel=button("ru-small-btn","Скасувати",()=>shade.remove());
    const save=button("ru-primary-btn","Зберегти",()=>{
      const value=name.value.trim();
      if(!value){name.focus();return;}
      manager.name=value.slice(0,100);manager.color=color.value;persist();shade.remove();
      renderControls();renderPaths();bridge.rebuild?.();
    });
    actions.append(cancel,save);panel.append(headline,name,color,actions);
    shade.append(panel);document.body.append(shade);name.focus();
  }
  function assignUnassigned() {
    const id=activeManager;
    if(!id||id==="all"||id==="unassigned"){
      alert("Спочатку виберіть профіль менеджера, до якого потрібно прив'язати старі архіви.");return;
    }
    const items=(bridge.state.library.items||[]).filter(item=>!data.assignments[String(item.id)]);
    if(!items.length){alert("Нерозподілених архівів немає.");return;}
    if(!confirm("Прив'язати "+items.length+" старих архівів до вибраного менеджера? Фото, геодані та коментарі не змінюватимуться."))return;
    items.forEach(item=>data.assignments[String(item.id)]=id);
    persist();renderControls();bridge.rebuild?.();
  }
  const canvas=$("mapCanvas"),overlay=document.createElementNS("http://www.w3.org/2000/svg","svg");
  overlay.id="roundupRoutesOverlay";overlay.classList.add("ru-route-overlay");
  canvas.append(overlay);
  const svg=(tag,attrs)=>{
    const el=document.createElementNS("http://www.w3.org/2000/svg",tag);
    for(const [name,value] of Object.entries(attrs))el.setAttribute(name,String(value));return el;
  };
  function routeVisible(r) {return (activeManager==="all"||r.managerId===activeManager) && (!activeDay||r.date===activeDay);}
  function colorForRoute(r) {return activeManager==="all" ? colorFor(r.managerId) : dateColor(r.date);}
  function renderPaths() {
    const w=Math.max(1,canvas.clientWidth),h=Math.max(1,canvas.clientHeight);
    overlay.setAttribute("viewBox","0 0 "+w+" "+h);overlay.replaceChildren();
    const visible=data.routes.filter(routeVisible);
    visible.forEach((r,index)=>{
      const coords=r.points.map(p=>bridge.toScreen(p[0],p[1]));
      const shift=index%4===0?-3:index%4===1?3:index%4===2?-6:6;
      const points=coords.map(p=>[p.x,p.y+shift]);
      const poly=svg("polyline",{points:points.map(p=>p[0].toFixed(1)+","+p[1].toFixed(1)).join(" "),
        fill:"none",stroke:colorForRoute(r),"stroke-width":"5","stroke-linecap":"round",
        "stroke-linejoin":"round","vector-effect":"non-scaling-stroke",opacity:"0.9"});
      const title=svg("title",{});title.textContent=(data.managers.find(m=>m.id===r.managerId)?.name||"Менеджер")+" · "+r.date+" · "+r.points.length+" вузлів";
      poly.append(title);overlay.append(poly);
      for(let i=1;i<points.length;i+=Math.max(1,Math.ceil(points.length/7))){
        const a=points[i-1],b=points[i],ang=Math.atan2(b[1]-a[1],b[0]-a[0]);
        const middleX=(a[0]+b[0])/2,middleY=(a[1]+b[1])/2;
        const ar=svg("path",{d:"M -5 -4 L 2 0 L -5 4",fill:"none",stroke:"#fff","stroke-width":"2.5",
          transform:"translate("+middleX+" "+middleY+") rotate("+(ang*180/Math.PI)+")"});
        overlay.append(ar);
      }
    });
    if(draft.length){
      const p=draft.map(v=>bridge.toScreen(v[0],v[1]));
      overlay.append(svg("polyline",{points:p.map(v=>v.x.toFixed(1)+","+v.y.toFixed(1)).join(" "),
        stroke:"#ffffff","stroke-width":"4","stroke-dasharray":"7 5",fill:"none"}));
    }
  }
  function showRouteSetup(onReady=startDrawing,kind="Новий маршрут"){
    if(!data.managers.length){
      alert((kind==="Новий маршрут"?"Для маршруту":"Для позначення території")+" потрібен профіль менеджера. Створіть його у вікні, що відкриється.");
      void showImportModal([],true).then(result=>{if(result?.managerId)onReady();});
      return;
    }
    const shade=div("ru-profile-modal-backdrop"),panel=div("ru-profile-modal");
    const title=all("h2");title.textContent=kind;
    const hint=div("",kind==="Новий маршрут"?"Оберіть менеджера й дату. Потім клікайте по карті вздовж дороги: кожен клік — наступна точка маршруту.":"Оберіть менеджера й дату. Потім позначте на карті контур щонайменше трьома точками (або відмічайте сектори).");
    const choice=all("select");choice.setAttribute("aria-label","Профіль менеджера");
    for(const m of data.managers)choice.append(option(m.id,m.name,m.id===activeManager));
    if(activeManager==="all"||activeManager==="unassigned")choice.value=data.managers[0].id;
    const day=all("input");day.type="date";day.value=activeDay||routeDate.value||isoToday();
    const actions=div("ru-modal-actions");
    const cancel=button("ru-small-btn","Скасувати",()=>shade.remove());
    const accept=button("ru-primary-btn","Почати малювання",()=>{
      if(!safeDate(day.value)){alert("Вкажіть дату виїзду.");return;}
      activeManager=choice.value;activeDay="";routeDate.value=day.value;
      shade.remove();renderControls();bridge.rebuild?.();onReady();
    });
    actions.append(cancel,accept);panel.append(title,hint,choice,day,actions);
    shade.append(panel);document.body.append(shade);choice.focus();
    shade.addEventListener("click",ev=>{if(ev.target===shade)shade.remove();});
  }
  function startDrawing(){
    if(window.RoundUpCoverage?.isDrawing?.()){
      alert("Спочатку завершіть або скасуйте малювання ділянки/секторів.");return;
    }
    if(activeManager==="all"||activeManager==="unassigned"){showRouteSetup();return;}
    drawing=true;draft=[];canvas.classList.add("ru-route-drawing");
    routeDate.value=activeDay||safeDate(routeDate.value)||isoToday();
    floatingDate.value=routeDate.value;
    renderControls();renderPaths();
  }
  function toggleDrawing(){
    if(drawing){finishRoute();return;}
    if(activeManager==="all"||activeManager==="unassigned"){showRouteSetup();return;}
    startDrawing();
  }
  function stopDrawing(){
    drawing=false;draft=[];canvas.classList.remove("ru-route-drawing");renderControls();renderPaths();
  }
  function finishRoute(){
    if(!drawing)return;
    const date=safeDate(routeDate.value);
    if(!date){alert("Вкажіть дату маршруту.");return;}
    const points=draft.filter((p,i)=>i===0||Math.abs(p[0]-draft[i-1][0])+Math.abs(p[1]-draft[i-1][1])>0.000001);
    if(points.length<2){alert("Позначте на карті щонайменше дві різні точки маршруту.");return;}
    data.routes.push({id:"r"+Date.now().toString(36)+Math.random().toString(36).slice(2,7),
      managerId:activeManager,date,points,createdAt:new Date().toISOString()});
    persist();stopDrawing();activeDay=date;renderControls();bridge.rebuild?.();
  }
  const appendPoint=event=>{
    const rect=canvas.getBoundingClientRect(),point=bridge.toGeo(event.clientX-rect.left,event.clientY-rect.top);
    if(!Number.isFinite(point.lat)||!Number.isFinite(point.lng)||draft.length>=5000)return;
    const last=draft[draft.length-1];
    if(!last||Math.abs(last[0]-point.lat)+Math.abs(last[1]-point.lng)>0.000001){
      draft.push([point.lat,point.lng]);renderPaths();renderControls();
    }
  };
  let routePointer=null;
  canvas.addEventListener("pointerdown",event=>{
    if(!drawing||event.button!==0||event.isPrimary===false)return;
    routePointer={id:event.pointerId,x:event.clientX,y:event.clientY};
    event.preventDefault();event.stopImmediatePropagation();
  },true);
  canvas.addEventListener("pointerup",event=>{
    if(!drawing||!routePointer)return;
    const start=routePointer;routePointer=null;
    event.preventDefault();event.stopImmediatePropagation();
    if(start.id!==event.pointerId||Math.hypot(event.clientX-start.x,event.clientY-start.y)>9)return;
    appendPoint(event);
  },true);
  canvas.addEventListener("pointercancel",()=>{routePointer=null;},true);
  canvas.addEventListener("click",event=>{
    if(!drawing)return;
    event.preventDefault();event.stopImmediatePropagation();
    if(event.detail===0)appendPoint(event);
  },true);
  canvas.addEventListener("dblclick",event=>{
    if(!drawing)return;
    event.preventDefault();event.stopImmediatePropagation();finishRoute();
  },true);
  document.addEventListener("keydown",event=>{
    if(drawing&&event.key==="Escape"){event.preventDefault();stopDrawing();}
    else if(drawing&&event.key==="Enter"){event.preventDefault();finishRoute();}
  });
  let lastProject="";
  setInterval(()=>{
    const current=getProject();
    if(current!==lastProject){lastProject=current;void openProject();}
    if(loading)return;
    const v=[bridge.state.center.lat,bridge.state.center.lng,bridge.state.zoom,canvas.clientWidth,canvas.clientHeight,bridge.state.layer,data.routes.length,draft.length,activeManager,activeDay].join("|");
    if(v!==lastView){lastView=v;renderPaths();}
    const fileCount=(bridge.state.library.items||[]).length,recordCount=bridge.state.records.length;
    const listKey=[project,fileCount,recordCount,activeManager,data.managers.length].join("|");
    if(listKey!==lastList){lastList=listKey;renderControls();}
  },350);
  window.RoundUpProfiles={
    beforeImport,afterImport,allowsRecord,markerColor,managerFor,visitDate,
    activeManager:()=>activeManager,activeDay:()=>activeDay,selectedRouteDate:()=>safeDate(routeDate.value)||isoToday(),
    colorFor:(managerId,date)=>!managerId?"#92a6a5":activeManager==="all"?colorFor(managerId):dateColor(date||isoToday()),
    // Keep legacy zones (without manager) visible in muted grey; never assign them implicitly.
    allowsCoverage:(managerId,date)=> !managerId || ((activeManager==="all"||managerId===activeManager)&&(!activeDay||!date||date===activeDay)),
    listManagers:()=>data.managers.slice(),
    isDrawing:()=>drawing,cancelDrawing:stopDrawing,refreshControls:renderControls,
    chooseCoverageManager:onReady=>showRouteSetup(onReady,"Позначити територію")
  };
  void openProject();
})();