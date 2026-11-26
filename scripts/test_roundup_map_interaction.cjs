/* Regression for RoundUp coverage: pointer input, date visibility, save and manager choice. */
const assert=require("node:assert/strict");
const fs=require("node:fs");
const path=require("node:path");
const vm=require("node:vm");
const createdNodes=[];
class MockElement {
  constructor(tag){this.tag=tag;this.listeners={};this.children=[];this.clientWidth=900;this.clientHeight=700;this.textContent="";this.value="";this.style={};
    this.classList={toggle(){},add(){},remove(){}};createdNodes.push(this);}
  addEventListener(type,fn){(this.listeners[type]??=[]).push(fn);}
  appendChild(child){this.children.push(child);}
  append(...children){this.children.push(...children);}
  remove(){}
  focus(){}
  insertAdjacentElement(_position,node){this.append(node);}
  querySelector(selector){return selector===".sidebar-head"?new MockElement("head"):null;}
  replaceChildren(...children){this.children=children;}
  setAttribute(){}
  getBoundingClientRect(){return {left:0,top:0};}
  fire(type,details={}){
    for(const fn of this.listeners[type]||[]) fn({button:0,isPrimary:true,pointerId:1,clientX:100,
      clientY:100,detail:1,preventDefault(){},stopImmediatePropagation(){},...details});
  }
}
async function main(){
  const ids=["mapCanvas","roundupCoverageOverlay","coveragePolygonBtn","coverageGridBtn",
    "roundupCoverageSummary","coverageCancel","coverageUndo","coverageInstructions"];
  const nodes=Object.fromEntries(ids.map(id=>[id,new MockElement(id)]));
  nodes.mapCanvas.parentElement=new MockElement("mapArea");
  const document={
    getElementById:id=>nodes[id],
    createElement:tag=>new MockElement(tag),
    createElementNS:(_ns,tag)=>new MockElement(tag),
    addEventListener(){}
  };
  const state={currentProjectId:"default",center:{lat:49.84,lng:24.03},zoom:11};
  let manager="ole",managerPromptCount=0,refreshCount=0,stored=[];
  const day="2026-11-12";
  const window={
    RoundUpBridge:{state,toScreen:(lat,lng)=>({x:lng*10,y:lat*10}),
      toGeo:(x,y)=>({lat:49+y/1000,lng:24+x/1000})},
    RoundUpProfiles:{
      activeManager:()=>manager,activeDay:()=>day,selectedRouteDate:()=>"2026-11-26",
      isDrawing:()=>false,allowsCoverage:(owner,date)=>owner==="ole"&&date===day,
      colorFor:()=>"#128a88",refreshControls(){refreshCount++;},
      chooseCoverageManager:cb=>{manager="ole";managerPromptCount++;cb();}
    }
  };
  const fetch=async (_url,options)=>{
    if(options?.method==="PUT"){stored.push(JSON.parse(options.body));return{ok:true};}
    return{ok:true,json:async()=>({version:1,shapes:[],cells:[]})};
  };
  const cache=new Map();
  const localStorage={setItem:(k,v)=>cache.set(k,v),getItem:k=>cache.get(k)};
  const js=fs.readFileSync(path.join(__dirname,"../src/RoundUp.UI/roundup-addon.js"),"utf8");
  vm.runInNewContext(js,{window,document,fetch,setTimeout,clearTimeout,
    setInterval:()=>{},localStorage,alert:message=>{throw Error("Unexpected alert: "+message);},console});
  await new Promise(resolve=>setImmediate(resolve));
  const tap=(x,y)=>{
    nodes.mapCanvas.fire("pointerdown",{clientX:x,clientY:y});
    nodes.mapCanvas.fire("pointerup",{clientX:x,clientY:y});
    nodes.mapCanvas.fire("click",{clientX:x,clientY:y,detail:1});
  };
  nodes.coveragePolygonBtn.fire("click");
  assert.equal(window.RoundUpCoverage.isDrawing(),true,"polygon mode must start");
  tap(100,100);tap(220,100);
  nodes.coverageCancel.fire("click");
  assert.equal(window.RoundUpCoverage.isDrawing(),true,"two points must not be discarded");
  tap(220,220);
  assert.ok(nodes.roundupCoverageOverlay.children.some(el=>el.tag==="polyline"),"draft must be visible");
  nodes.coverageCancel.fire("click");
  assert.equal(window.RoundUpCoverage.isDrawing(),false,"finish must exit polygon mode");
  assert.ok(nodes.roundupCoverageOverlay.children.some(el=>el.tag==="polygon"),"saved polygon stays visible");
  await new Promise(resolve=>setTimeout(resolve,300));
  assert.equal(stored.length,1,"native save must be requested");
  assert.equal(stored[0].shapes[0].points.length,3);
  assert.equal(stored[0].shapes[0].managerId,"ole");
  assert.equal(stored[0].shapes[0].date,day,"active filter date wins over today's default");
  assert.deepEqual([...window.RoundUpCoverage.availableDates("ole")],[day]);
  assert.ok(refreshCount>0,"date selector should refresh");
  manager="all";
  nodes.coveragePolygonBtn.fire("click");
  assert.equal(managerPromptCount,1,"all-managers map should ask who is drawing");
  assert.equal(window.RoundUpCoverage.isDrawing(),true);
  window.RoundUpCoverage.cancelDrawing();
  console.log("PASS: coverage pointer drawing, visible save, correct date, manager selection and recovery.");
}
async function routeTest(){
  const body=new MockElement("body"),canvas=new MockElement("mapCanvas");
  const sidebar=new MockElement("sidebar"),mapArea=new MockElement("mapArea"),mapButton=new MockElement("routeDrawMapBtn");
  const document={body,getElementById:id=>({mapCanvas:canvas,sidebar,mapArea,routeDrawMapBtn:mapButton})[id]||null,
    createElement:tag=>new MockElement(tag),createElementNS:(_ns,tag)=>new MockElement(tag),addEventListener(){}};
  const state={currentProjectId:"default",center:{lat:49.84,lng:24.03},zoom:11,
    sources:new Map(),records:[],library:{items:[]},layer:"osm"};
  const window={RoundUpBridge:{state,rebuild(){},
    toScreen:()=>({x:0,y:0}),toGeo:(x,y)=>({lat:49+y/1000,lng:24+x/1000})},
    RoundUpCoverage:{isDrawing:()=>false,availableDates:()=>[]}};
  const saved=new Map();
  const localStorage={setItem:(k,v)=>saved.set(k,v),getItem:k=>saved.get(k)};
  const fetch=async()=>({ok:true,json:async()=>({version:1,managers:[
    {id:"ole",name:"Олег Львів",color:"#119d78"}],assignments:{},sourceAssignments:{},routes:[]})});
  const js=fs.readFileSync(path.join(__dirname,"../src/RoundUp.UI/roundup-profiles.js"),"utf8");
  vm.runInNewContext(js,{window,document,fetch,setTimeout,clearTimeout,setInterval:()=>{},localStorage,
    alert:message=>{throw Error(message);},confirm:()=>true,console});
  await new Promise(resolve=>setImmediate(resolve));
  mapButton.fire("click");
  const accept=createdNodes.find(node=>node.tag==="button"&&node.textContent==="Почати малювання");
  assert.ok(accept,"route button should open manager/date dialog");
  accept.fire("click");
  assert.equal(window.RoundUpProfiles.isDrawing(),true);
  const tap=(x,y)=>{canvas.fire("pointerdown",{clientX:x,clientY:y});
    canvas.fire("pointerup",{clientX:x,clientY:y});
    canvas.fire("click",{clientX:x,clientY:y,detail:1});};
  tap(100,100);tap(220,140);
  const done=createdNodes.find(node=>node.tag==="button"&&node.textContent==="✓ Готово");
  assert.ok(done,"route drawing needs visible finish button");
  done.fire("click");
  assert.equal(window.RoundUpProfiles.isDrawing(),false);
  assert.equal(window.RoundUpProfiles.activeManager(),"ole");
  const snapshot=JSON.parse(saved.get("RoundUp:profiles:default")||"{}");
  assert.equal(snapshot.routes.length,1,"route must be saved");
  assert.equal(snapshot.routes[0].points.length,2,"two distinct points form a route");
  assert.equal(snapshot.routes[0].managerId,"ole");
  assert.equal(snapshot.routes[0].date,window.RoundUpProfiles.activeDay());
  console.log("PASS: map route button, manager/date dialog, pointer route and persistence.");
}
main().then(routeTest).catch(error=>{console.error(error);process.exitCode=1;});
